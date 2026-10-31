using System.Security.Cryptography;
using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Storage;

namespace ClassVaneCanvas.Sync;

/// <summary>
/// 批注同步服务：把本机批注库里的 <c>*.cvcanno</c> 与老师自持的同步目标（文件夹 / WebDAV）对齐。
///
/// <b>三条不可逾越的红线</b>（实现与测试都以此为准）：
/// 1. 只处理 <c>*.cvcanno</c>：本机播放进度库 <see cref="AppPaths.PlaybackProgressDatabase"/>、
///    设置文件、同步状态文件永远不读、不传、不下载；
/// 2. 同步默认关闭，且只支持自持目标（老师的 WebDAV 或自己的文件夹 / NAS），不连任何第三方公共服务；
/// 3. 任何网络 / IO 异常都转成 <see cref="SyncReport.Error"/>（中文可读），不向 UI 抛异常，
///    只有老师主动取消（<see cref="OperationCanceledException"/>）才向外传递。
///
/// 冲突检测依据「上次同步哈希 + 上次同步 ETag」判断两侧各自的改动，判定不出来才交给老师裁决。
/// </summary>
public sealed class AnnotationSyncService : IAnnotationSyncService
{
    private readonly AppPaths _paths;
    private readonly IAnnotationPackageCodec _codec;
    private readonly IAnnotationMerger _merger;
    private readonly IAnnotationSyncTarget _target;
    private readonly SyncStateStore _state;

    /// <param name="paths">应用目录布局（只使用批注库目录与同步状态文件）。</param>
    /// <param name="codec">.cvcanno 读写实现。</param>
    /// <param name="merger">笔迹合并实现（冲突处理选「合并」时使用）。</param>
    /// <param name="target">同步目标（文件夹 / WebDAV）。</param>
    /// <param name="state">同步状态存储。</param>
    /// <param name="enabled">是否启用同步（默认关闭）。</param>
    /// <param name="defaultResolution">
    /// 冲突默认处理方式；传 <c>null</c>（默认）表示「询问老师」——冲突只收集到
    /// <see cref="SyncReport.PendingConflicts"/>，不自动改动任何一侧。
    /// </param>
    public AnnotationSyncService(
        AppPaths paths,
        IAnnotationPackageCodec codec,
        IAnnotationMerger merger,
        IAnnotationSyncTarget target,
        SyncStateStore state,
        bool enabled = false,
        AnnotationConflictResolution? defaultResolution = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _state = state ?? throw new ArgumentNullException(nameof(state));

        IsEnabled = enabled;
        DefaultResolution = defaultResolution;
    }

    /// <inheritdoc />
    public bool IsEnabled { get; set; }

    /// <summary>冲突默认处理方式；<c>null</c> = 每次都由老师裁决。</summary>
    public AnnotationConflictResolution? DefaultResolution { get; set; }

    /// <summary>当前同步目标的展示名称（用于界面提示）。</summary>
    public string TargetDisplayName => _target.DisplayName;

    /// <inheritdoc />
    public async Task<SyncReport> SyncAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return Failure("批注同步未启用（默认关闭），请先在设置中开启同步。");
        }

        var actions = new List<SyncAction>();
        var pending = new List<AnnotationConflict>();
        var uploaded = 0;
        var downloaded = 0;
        var skipped = 0;

        try
        {
            var library = _paths.AnnotationLibraryDirectory;
            Directory.CreateDirectory(library);

            progress?.Report($"开始同步批注（{_target.DisplayName}）…");

            // 远端清单 → 只接受合法的 .cvcanno 文件名，其它一律忽略（防目录穿越 / 防误传本机数据）。
            var remoteMap = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
            foreach (var entry in await _target.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (SyncPathRules.TryGetFlatFileName(entry.RelativePath, out var remoteName))
                {
                    remoteMap[remoteName] = entry;
                }
                else
                {
                    actions.Add(new SyncAction(
                        entry.RelativePath,
                        SyncDirection.Skipped,
                        "远端条目不是合法的批注文件名，已忽略。"));
                    skipped++;
                }
            }

            // 逐个本机批注做双向比对；被本地认领过的远端条目从 remoteMap 移出。
            foreach (var localPath in EnumerateLocalAnnotations(library))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(localPath);
                remoteMap.Remove(name, out var remoteEntry);
                Accumulate(await SyncFileAsync(name, localPath, remoteEntry, progress, cancellationToken)
                    .ConfigureAwait(false));
            }

            // 远端有、本机没有的批注 → 下载进批注库。
            foreach (var entry in remoteMap.Values.OrderBy(static e => e.RelativePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await _target.DownloadAsync(entry.RelativePath, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    actions.Add(new SyncAction(
                        entry.RelativePath,
                        SyncDirection.Skipped,
                        "远端批注在同步过程中消失，已忽略。"));
                    skipped++;
                    continue;
                }

                Accumulate(DownloadToLocal(
                    entry.RelativePath,
                    bytes,
                    ComputeSha256(bytes),
                    entry.ETag,
                    "远端有本机没有的批注，已下载到批注库。",
                    DescribeAnnotation(entry.RelativePath, bytes),
                    progress));
            }

            _state.Save();
            progress?.Report(
                $"同步完成：上传 {uploaded} 个，下载 {downloaded} 个，跳过 {skipped} 个，待处理冲突 {pending.Count} 个。");

            return new SyncReport(actions, uploaded, downloaded, pending.Count, skipped, pending);
        }
        catch (OperationCanceledException)
        {
            // 老师主动取消了同步：已完成的动作状态先落盘，异常继续向外传（UI 自行提示）。
            TrySaveState();
            throw;
        }
        catch (Exception ex)
        {
            TrySaveState();
            return new SyncReport(
                actions,
                uploaded,
                downloaded,
                pending.Count,
                skipped,
                pending,
                $"{_target.DisplayName}：{DescribeError(ex)}");
        }

        void Accumulate(FileOutcome outcome)
        {
            actions.Add(new SyncAction(outcome.RelativePath, outcome.Direction, outcome.Reason));
            if (outcome.IsUpload)
            {
                uploaded++;
            }

            if (outcome.IsDownload)
            {
                downloaded++;
            }

            if (outcome.IsSkipped)
            {
                skipped++;
            }

            if (outcome.Pending is not null)
            {
                pending.Add(outcome.Pending);
            }
        }
    }

    /// <inheritdoc />
    public async Task<SyncReport> ResolveConflictAsync(
        AnnotationConflict conflict,
        AnnotationConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        if (conflict is null)
        {
            return Failure("冲突记录为空，无法处理。");
        }

        try
        {
            // 冲突记录可能来自界面缓存，回写前必须重新校验：路径只能在批注库内、且是 .cvcanno。
            var name = SyncPathRules.RequireFlatFileName(conflict.RelativePath);
            var localPath = Path.GetFullPath(conflict.LocalPath);
            if (!SyncPathRules.IsInside(_paths.AnnotationLibraryDirectory, localPath))
            {
                throw new ArgumentException($"本机批注路径「{conflict.LocalPath}」不在批注库目录内，已拒绝写入。", nameof(conflict));
            }

            if (!string.Equals(Path.GetFileName(localPath), name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("冲突记录里的文件名与本机路径不一致，已拒绝写入。", nameof(conflict));
            }

            var outcome = await ApplyResolutionAsync(
                conflict with { RelativePath = name, LocalPath = localPath },
                resolution,
                progress: null,
                cancellationToken).ConfigureAwait(false);

            _state.Save();
            return new SyncReport(
                new[] { new SyncAction(name, outcome.Direction, outcome.Reason) },
                outcome.IsUpload ? 1 : 0,
                outcome.IsDownload ? 1 : 0,
                0,
                outcome.IsSkipped ? 1 : 0,
                Array.Empty<AnnotationConflict>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(DescribeError(ex));
        }
    }

    /// <summary>单个本机批注的同步判定（需求里的 7 条规则）。</summary>
    private async Task<FileOutcome> SyncFileAsync(
        string name,
        string localPath,
        RemoteEntry? remote,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var localBytes = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        var localHash = ComputeSha256(localBytes);
        var state = _state.Get(name);
        var lastHash = state?.LastSyncedHash;
        var display = DescribeAnnotation(name, localBytes);

        // 规则 1：远端不存在 → 直接上传本机版本。
        if (remote is null)
        {
            return await UploadAsync(
                name, localBytes, localHash, null, display,
                "远端还没有这份批注，已上传本机版本。", progress, cancellationToken).ConfigureAwait(false);
        }

        var localChanged = !string.Equals(lastHash, localHash, StringComparison.OrdinalIgnoreCase);
        var remoteChanged = !string.Equals(state?.RemoteETag, remote.ETag, StringComparison.Ordinal);

        // 两侧都没动 → 跳过。
        if (!localChanged && !remoteChanged)
        {
            return Skipped(name, "本机与远端都没有变化。");
        }

        // 规则 2：只有远端变了 → 下载覆盖本机。
        if (!localChanged)
        {
            var remoteBytes = await _target.DownloadAsync(name, cancellationToken).ConfigureAwait(false);
            if (remoteBytes is null)
            {
                // 列目录与下载之间被别的设备删掉了：按「远端不存在」处理，把本机版本重新推上去。
                return await UploadAsync(
                    name, localBytes, localHash, null, display,
                    "远端批注在同步过程中消失，已重新上传本机版本。", progress, cancellationToken).ConfigureAwait(false);
            }

            var remoteHash = ComputeSha256(remoteBytes);
            if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
            {
                UpdateState(name, localHash, remote.ETag);
                return Skipped(name, "两侧内容已经一致，只更新了同步记录。");
            }

            if (string.Equals(remoteHash, lastHash, StringComparison.OrdinalIgnoreCase))
            {
                // 远端只是换了 ETag（例如服务器重写了元数据），内容仍是上次同步的那一版。
                UpdateState(name, localHash, remote.ETag);
                return Skipped(name, "远端仅版本标识变化，内容未变。");
            }

            return DownloadToLocal(
                name, remoteBytes, remoteHash, remote.ETag,
                "远端批注有更新，已下载到本机。", display, progress);
        }

        // 规则 3：只有本机变了 → 带乐观锁上传，远端真被改过就报错让老师重来。
        if (!remoteChanged)
        {
            return await UploadAsync(
                name, localBytes, localHash, remote.ETag, display,
                "本机批注有改动，已上传到远端。", progress, cancellationToken).ConfigureAwait(false);
        }

        // 规则 5：两侧都变了（含本机从未同步过）→ 先比内容，再看上次同步版本，最后才判冲突。
        var bothBytes = await _target.DownloadAsync(name, cancellationToken).ConfigureAwait(false);
        if (bothBytes is null)
        {
            return await UploadAsync(
                name, localBytes, localHash, null, display,
                "远端批注在同步过程中消失，已重新上传本机版本。", progress, cancellationToken).ConfigureAwait(false);
        }

        var bothHash = ComputeSha256(bothBytes);
        if (string.Equals(bothHash, localHash, StringComparison.OrdinalIgnoreCase))
        {
            UpdateState(name, localHash, remote.ETag);
            return Skipped(name, "两侧内容已经一致，只更新了同步记录。");
        }

        if (string.Equals(bothHash, lastHash, StringComparison.OrdinalIgnoreCase))
        {
            // 远端内容 == 上次同步版本 ⇒ 只有本机改过，直接上传。
            return await UploadAsync(
                name, localBytes, localHash, remote.ETag, display,
                "远端仍是上次同步的版本，已上传本机改动。", progress, cancellationToken).ConfigureAwait(false);
        }

        // 真正冲突：两侧都改成了「不是上次同步版本」的不同内容。
        var localDocument = TryReadDocument(localBytes);
        var remoteDocument = TryReadDocument(bothBytes);
        if (localDocument is null || remoteDocument is null)
        {
            return Skipped(name, "批注文件内容无法解析，已跳过以避免覆盖（可能由更新版本写入）。");
        }

        var conflict = new AnnotationConflict(name, localDocument, remoteDocument, localPath, remote.ETag);
        if (DefaultResolution is not { } resolution)
        {
            progress?.Report($"检测到 {display} 的批注冲突，等待老师选择处理方式…");
            return new FileOutcome(
                name,
                SyncDirection.Conflict,
                "本机与远端都修改过这份批注，需要老师选择保留本机 / 保留远端 / 合并笔迹。",
                Pending: conflict);
        }

        return await ApplyResolutionAsync(conflict, resolution, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按老师（或默认策略）的选择处理一个冲突。</summary>
    private async Task<FileOutcome> ApplyResolutionAsync(
        AnnotationConflict conflict,
        AnnotationConflictResolution resolution,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        switch (resolution)
        {
            case AnnotationConflictResolution.KeepLocal:
            {
                var bytes = await File.ReadAllBytesAsync(conflict.LocalPath, cancellationToken).ConfigureAwait(false);
                var hash = ComputeSha256(bytes);
                progress?.Report($"正在上传 {DescribeAnnotation(conflict.RelativePath, bytes)} 的批注（保留本机版本）…");
                var etag = await _target
                    .UploadAsync(conflict.RelativePath, bytes, conflict.RemoteETag, cancellationToken)
                    .ConfigureAwait(false);
                UpdateState(conflict.RelativePath, hash, etag);
                return new FileOutcome(
                    conflict.RelativePath, SyncDirection.Upload,
                    "冲突处理：保留本机批注，已覆盖远端。", IsUpload: true);
            }

            case AnnotationConflictResolution.KeepRemote:
            {
                var bytes = await _target.DownloadAsync(conflict.RelativePath, cancellationToken).ConfigureAwait(false)
                            ?? throw new IOException($"远端批注「{conflict.RelativePath}」已不存在，无法保留远端版本。");
                var hash = ComputeSha256(bytes);

                // 用远端原始字节覆盖本机，保证两侧字节完全一致。
                SyncPathRules.WriteFileAtomic(conflict.LocalPath, bytes);
                UpdateState(
                    conflict.RelativePath,
                    hash,
                    await ResolveCurrentETagAsync(conflict.RelativePath, conflict.RemoteETag, cancellationToken)
                        .ConfigureAwait(false));
                return new FileOutcome(
                    conflict.RelativePath, SyncDirection.Download,
                    "冲突处理：保留远端批注，已覆盖本机。", IsDownload: true);
            }

            case AnnotationConflictResolution.Merge:
            {
                var merged = _merger.Merge(ReadLocalDocument(conflict), conflict.Remote)
                             ?? throw new InvalidOperationException("合并批注失败：合并结果为空。");

                byte[] bytes;
                using (var buffer = new MemoryStream())
                {
                    _codec.Write(buffer, merged);
                    bytes = buffer.ToArray();
                }

                var hash = ComputeSha256(bytes);
                SyncPathRules.WriteFileAtomic(conflict.LocalPath, bytes);
                progress?.Report($"正在上传 {DescribeAnnotation(conflict.RelativePath, bytes)} 的合并批注…");
                var etag = await _target
                    .UploadAsync(conflict.RelativePath, bytes, conflict.RemoteETag, cancellationToken)
                    .ConfigureAwait(false);
                UpdateState(conflict.RelativePath, hash, etag);
                return new FileOutcome(
                    conflict.RelativePath, SyncDirection.Upload,
                    $"冲突处理：已合并两侧笔迹（合计 {merged.TotalStrokeCount} 条），本机与远端均更新。", IsUpload: true);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "未知的冲突处理方式。");
        }
    }

    /// <summary>上传并记录同步状态；<paramref name="expectedETag"/> 非空时启用乐观锁。</summary>
    private async Task<FileOutcome> UploadAsync(
        string name,
        byte[] content,
        string contentHash,
        string? expectedETag,
        string displayName,
        string reason,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"正在上传 {displayName} 的批注…");
        var etag = await _target.UploadAsync(name, content, expectedETag, cancellationToken).ConfigureAwait(false);
        UpdateState(name, contentHash, etag);
        return new FileOutcome(name, SyncDirection.Upload, reason, IsUpload: true);
    }

    /// <summary>用远端字节覆盖本机批注文件（保持字节一致）并记录同步状态。</summary>
    private FileOutcome DownloadToLocal(
        string name,
        byte[] content,
        string contentHash,
        string? remoteETag,
        string reason,
        string displayName,
        IProgress<string>? progress)
    {
        progress?.Report($"正在下载 {displayName} 的批注…");
        SyncPathRules.WriteFileAtomic(ResolveLibraryPath(name), content);
        UpdateState(name, contentHash, remoteETag);
        return new FileOutcome(name, SyncDirection.Download, reason, IsDownload: true);
    }

    /// <summary>批注库里某个文件的绝对路径（越界 / 非 .cvcanno 直接拒绝）。</summary>
    private string ResolveLibraryPath(string name)
        => SyncPathRules.ResolveInsideRoot(_paths.AnnotationLibraryDirectory, name);

    /// <summary>更新并立即落盘同步状态，避免同步中途崩溃导致下次误判。</summary>
    private void UpdateState(string name, string contentHash, string? remoteETag)
    {
        _state.Set(name, contentHash, remoteETag);
        _state.Save();
    }

    private void TrySaveState()
    {
        try
        {
            _state.Save();
        }
        catch (Exception)
        {
            // 状态落盘失败不影响已经完成的同步结果，下次同步会重新比对内容。
        }
    }

    /// <summary>列出批注库里的 <c>*.cvcanno</c>（只认这个扩展名，播放进度库 / 设置文件永远不在其中）。</summary>
    private static IReadOnlyList<string> EnumerateLocalAnnotations(string libraryDirectory)
    {
        if (!Directory.Exists(libraryDirectory))
        {
            return Array.Empty<string>();
        }

        var files = Directory
            .EnumerateFiles(libraryDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => SyncPathRules.IsAnnotationFileName(Path.GetFileName(path)))
            .ToList();

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>尽力读出课件名用于进度提示；读不出来就退回文件名，绝不因此中断同步。</summary>
    private string DescribeAnnotation(string name, byte[]? content)
    {
        if (content is not null)
        {
            try
            {
                using var stream = new MemoryStream(content, writable: false);
                var document = _codec.Read(stream);
                if (!string.IsNullOrWhiteSpace(document?.CoursewareFileName))
                {
                    return document!.CoursewareFileName;
                }
            }
            catch (Exception)
            {
                // 文件损坏 / 版本更新：忽略，用短名代替。
            }
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length > 8 ? string.Concat(stem.AsSpan(0, 8), "…") : stem;
    }

    /// <summary>尝试解析批注文档（冲突裁决需要文档对象；解析不了返回 <c>null</c>）。</summary>
    private AnnotationDocument? TryReadDocument(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            return _codec.Read(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 取本地文档用于合并：优先重新读盘（老师可能在冲突提示期间又写了新笔迹），
    /// 读不出来时退回冲突记录里的快照。
    /// </summary>
    private AnnotationDocument ReadLocalDocument(AnnotationConflict conflict)
    {
        try
        {
            return _codec.ReadFile(conflict.LocalPath);
        }
        catch (Exception)
        {
            return conflict.Local;
        }
    }

    /// <summary>回写后刷新远端 ETag（尽力而为，取不到就沿用旧值，下次同步会自行纠正）。</summary>
    private async Task<string?> ResolveCurrentETagAsync(
        string relativePath,
        string? fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var entry in await _target.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(entry.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.ETag;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 尽力而为。
        }

        return fallback;
    }

    /// <summary>把异常翻译成老师看得懂的中文说明。</summary>
    private string DescribeError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "没有访问权限，请检查同步文件夹权限或 WebDAV 账号密码。",
        TimeoutException => $"连接同步服务器超时：{exception.Message}",
        HttpRequestException => $"无法连接同步服务器：{exception.Message}",
        InvalidDataException => $"批注文件格式无法识别：{exception.Message}",
        IOException => $"同步过程中读写失败：{exception.Message}",
        ArgumentException => $"同步参数不合法：{exception.Message}",
        _ => $"同步失败：{exception.Message}",
    };

    private static string ComputeSha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static FileOutcome Skipped(string name, string reason)
        => new(name, SyncDirection.Skipped, reason, IsSkipped: true);

    private static SyncReport Failure(string message)
        => new(
            Array.Empty<SyncAction>(),
            0,
            0,
            0,
            0,
            Array.Empty<AnnotationConflict>(),
            message);

    /// <summary>单个文件的同步结论。</summary>
    private sealed record FileOutcome(
        string RelativePath,
        SyncDirection Direction,
        string Reason,
        bool IsUpload = false,
        bool IsDownload = false,
        bool IsSkipped = false,
        AnnotationConflict? Pending = null);
}
