using ClassVaneCanvas.Models.Annotations;

namespace ClassVaneCanvas.Sync;

/// <summary>
/// 「同步文件夹」目标：本地目录、U 盘，或老师自己挂载的 NAS 共享目录。
///
/// 特点：
/// · 完全自持，不依赖任何第三方服务器；老师用系统自带的挂载方式把 NAS 挂成本地目录即可；
/// · 只认 <c>*.cvcanno</c> 扩展名，目录里的其它文件（播放进度库、设置、课件本身）一律不读不写；
/// · 所有路径先做目录穿越校验，越出同步根的相对路径直接拒绝；
/// · 乐观锁用「长度-最后写入 UTC ticks」组成的 ETag 模拟，值不一致即视为远端已被其它设备改动。
/// </summary>
public sealed class FolderSyncTarget : IAnnotationSyncTarget
{
    private readonly string _rootDirectory;

    /// <param name="directory">同步根目录（不存在时自动创建）。</param>
    /// <param name="displayName">展示名称。</param>
    public FolderSyncTarget(string directory, string displayName = "同步文件夹")
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("同步文件夹路径不能为空。", nameof(directory));
        }

        _rootDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_rootDirectory);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "同步文件夹" : displayName;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <summary>同步根目录绝对路径。</summary>
    public string RootDirectory => _rootDirectory;

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_rootDirectory);

            // 写一个探测文件再删掉：既能验证「可写」，又不会污染批注列表（刻意不用 .cvcanno 扩展名）。
            var probePath = Path.Combine(_rootDirectory, $".classvane-sync-probe-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(probePath, new byte[] { 0x43, 0x56 }, cancellationToken).ConfigureAwait(false);
            File.Delete(probePath);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(_rootDirectory))
        {
            // 目录在构造之后消失，通常是 NAS 掉线 / U 盘被拔出，必须如实报错而不是当成「远端为空」。
            throw new IOException($"同步文件夹「{_rootDirectory}」不存在或尚未挂载。");
        }

        var entries = new List<RemoteEntry>();
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            if (!SyncPathRules.IsAnnotationFileName(name))
            {
                continue;
            }

            var info = new FileInfo(path);
            entries.Add(new RemoteEntry(
                name,
                info.Length,
                ComputeETag(info),
                new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc))));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return Task.FromResult<IReadOnlyList<RemoteEntry>>(entries);
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = SyncPathRules.ResolveInsideRoot(_rootDirectory, relativePath);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc />
    public async Task<string?> UploadAsync(
        string relativePath,
        byte[] content,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = SyncPathRules.ResolveInsideRoot(_rootDirectory, relativePath);

        // 乐观锁：调用方认为远端还是 expectedETag 那一版，实际不是就拒绝写入。
        if (!string.IsNullOrEmpty(expectedETag))
        {
            var current = File.Exists(path) ? ComputeETag(new FileInfo(path)) : null;
            if (!string.Equals(current, expectedETag, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"远端批注「{Path.GetFileName(path)}」已被其它设备修改（期望版本 {expectedETag}，实际 {(current ?? "不存在")}），已取消本次上传。");
            }
        }

        Directory.CreateDirectory(_rootDirectory);

        // 先写临时文件再原子替换：中途失败也不会留下半个批注包。
        var temporaryPath = Path.Combine(_rootDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        return ComputeETag(new FileInfo(path));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = SyncPathRules.ResolveInsideRoot(_rootDirectory, relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>ETag 规则：<c>{字节长度}-{最后写入 UTC ticks}</c>；文件不存在返回 <c>null</c>。</summary>
    private static string? ComputeETag(FileInfo info)
        => info.Exists ? $"{info.Length}-{info.LastWriteTimeUtc.Ticks}" : null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响主流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}

/// <summary>
/// 同步模块内部共用的路径规则（随 <see cref="FolderSyncTarget"/> 一起声明，不额外新增文件）。
///
/// 只做一件事：把「相对同步根的批注文件名」卡死在 <c>*.cvcanno</c> 上，并确保解析结果仍在根目录内。
/// 这既是目录穿越防护，也是「同步永不触碰播放进度库 / 设置文件」的硬保障。
/// </summary>
internal static class SyncPathRules
{
    /// <summary>判断文件名是否为批注包（只认 <c>.cvcanno</c>，大小写不敏感）。</summary>
    internal static bool IsAnnotationFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && fileName.Length > CvcannoFormat.Extension.Length
           && fileName.EndsWith(CvcannoFormat.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把相对路径校验为「同步根下的平铺文件名」：不允许分隔符、<c>..</c>、根路径与其它扩展名。
    /// 不合法时抛 <see cref="ArgumentException"/>。
    /// </summary>
    internal static string RequireFlatFileName(string relativePath, string parameterName = "relativePath")
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("批注文件相对路径不能为空。", parameterName);
        }

        var name = relativePath.Trim();
        if (name.Contains('\0')
            || name.Contains('/')
            || name.Contains('\\')
            || name is "." or ".."
            || Path.IsPathRooted(name))
        {
            throw new ArgumentException($"批注文件相对路径「{relativePath}」不是同步根下的合法文件名，已拒绝。", parameterName);
        }

        if (!IsAnnotationFileName(name))
        {
            throw new ArgumentException($"只允许同步 {CvcannoFormat.Extension} 批注文件，已拒绝「{relativePath}」。", parameterName);
        }

        return name;
    }

    /// <summary>尝试把相对路径校验为合法的批注文件名（用于忽略远端返回的非法条目）。</summary>
    internal static bool TryGetFlatFileName(string? relativePath, out string fileName)
    {
        try
        {
            fileName = RequireFlatFileName(relativePath!);
            return true;
        }
        catch (ArgumentException)
        {
            fileName = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// 把相对路径解析为根目录内的绝对路径；越界或不合法时抛 <see cref="ArgumentException"/>。
    /// </summary>
    internal static string ResolveInsideRoot(string rootDirectory, string relativePath, string parameterName = "relativePath")
    {
        var name = RequireFlatFileName(relativePath, parameterName);
        var root = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(root, name));
        if (!IsInside(root, fullPath))
        {
            throw new ArgumentException($"批注文件路径「{relativePath}」越出同步根目录，已拒绝。", parameterName);
        }

        return fullPath;
    }

    /// <summary>判断某个绝对路径是否严格位于指定目录之内（目录本身不算）。</summary>
    internal static bool IsInside(string directory, string fullPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var target = Path.GetFullPath(fullPath);
        if (string.Equals(root, target, StringComparison.Ordinal))
        {
            return false;
        }

        return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || target.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>把内容原子地写入指定文件（临时文件 + 替换）。</summary>
    internal static void WriteFileAtomic(string path, byte[] content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // 临时文件清理失败不影响主流程。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}
