using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ClassVaneCanvas.Services;

/// <summary>
/// <see cref="ICoursewareIdentity"/> 的默认实现：流式 SHA256 + 路径级元数据缓存。
///
/// 关键约束：
/// 1. 缓存 key 是<b>规范化绝对路径</b>，value 是（文件长度, 最后写入时间 UTC, 哈希）；
///    只有长度与写入时间都与缓存一致时才算命中，命中时<b>绝不读取文件内容</b>；
/// 2. 哈希一律返回<b>小写十六进制</b>（批注 / 进度的匹配依据，大小写必须统一）；
/// 3. 大文件友好：用 <see cref="SHA256"/> + <see cref="HashAlgorithm.ComputeHashAsync(Stream, CancellationToken)"/>
///    分块读取，不把文件整体载入内存（PPT 动辄上百 MB）；
/// 4. 线程安全：缓存为 <see cref="ConcurrentDictionary{TKey,TValue}"/>，多线程可并发调用；
///    同一路径并发冷启动可能重复计算，但缓存结果始终一致。
/// </summary>
public sealed class CoursewareIdentity : ICoursewareIdentity
{
    /// <summary>文件读取缓冲区大小（64 KiB），避免大文件造成大块内存分配。</summary>
    private const int StreamBufferSize = 64 * 1024;

    /// <summary>
    /// 路径比较规则：Windows / macOS 默认文件系统大小写不敏感，Linux 区分大小写。
    /// 用错规则会让「同一文件的不同写法」产生两条缓存并重复读盘。
    /// </summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(PathComparer);

    /// <inheritdoc />
    public async Task<string> ComputeHashAsync(string coursewarePath, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(coursewarePath, nameof(coursewarePath));
        cancellationToken.ThrowIfCancellationRequested();

        var before = ReadFileStamp(fullPath);
        if (before is null)
        {
            throw new FileNotFoundException("课件文件不存在，无法计算指纹。", fullPath);
        }

        var beforeStamp = before.GetValueOrDefault();

        // 缓存命中：长度与写入时间都一致，直接返回，不再读盘。
        if (_cache.TryGetValue(fullPath, out var cached) && cached.Matches(beforeStamp))
        {
            return cached.Hash;
        }

        var hash = await ComputeFileHashAsync(fullPath, cancellationToken).ConfigureAwait(false);

        // 计算期间文件若被改动（长度 / 写入时间变化），该哈希不对应任何稳定版本：
        // 照常返回给调用方，但不写入缓存，下一次调用会重新计算。
        if (ReadFileStamp(fullPath) is { } after && after.Equals(beforeStamp))
        {
            _cache[fullPath] = new CacheEntry(after.Length, after.LastWriteTimeUtc, hash);
        }

        return hash;
    }

    /// <inheritdoc />
    /// <remarks>纯缓存查询：不访问文件系统，因此文件被删除 / 改名后仍可能返回旧哈希。</remarks>
    public string? TryGetCachedHash(string coursewarePath)
    {
        if (string.IsNullOrWhiteSpace(coursewarePath))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(coursewarePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 非法路径视为「没有缓存」，让调用方走正常的计算 / 报错流程。
            return null;
        }

        return _cache.TryGetValue(fullPath, out var cached) ? cached.Hash : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 文件存在时同时记录当前长度与写入时间，使紧随其后的 <see cref="ComputeHashAsync"/> 直接命中缓存；
    /// 文件不存在时写入哨兵长度 <c>-1</c>，<see cref="TryGetCachedHash"/> 仍可返回该已知哈希，
    /// 但 <see cref="ComputeHashAsync"/> 永远不会误命中（真实文件长度不可能是 -1），一定会重新读取或报错。
    /// </remarks>
    public void Remember(string coursewarePath, string coursewareHash)
    {
        var fullPath = NormalizePath(coursewarePath, nameof(coursewarePath));

        if (string.IsNullOrWhiteSpace(coursewareHash))
        {
            throw new ArgumentException("课件哈希不能为空。", nameof(coursewareHash));
        }

        if (ReadFileStamp(fullPath) is not { } stamp)
        {
            // 文件当前不存在：写入哨兵长度 -1，让 ComputeHashAsync 绝不误命中。
            _cache[fullPath] = new CacheEntry(-1, DateTime.MinValue, coursewareHash);
            return;
        }

        _cache[fullPath] = new CacheEntry(stamp.Length, stamp.LastWriteTimeUtc, coursewareHash);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 期望值忽略首尾空白与大小写；文件不存在时按 <see cref="ComputeHashAsync"/> 的约定抛
    /// <see cref="FileNotFoundException"/>（而不是返回 <c>false</c>），以免把「文件缺失」误判成「课件被改过」。
    /// </remarks>
    public async Task<bool> VerifyAsync(
        string coursewarePath,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actual = await ComputeHashAsync(coursewarePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>清空缓存（数据管理页「重新扫描」等场景使用）。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>流式计算文件 SHA256，返回小写十六进制字符串。</summary>
    private static async Task<string> ComputeFileHashAsync(string fullPath, CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite | Delete：课件可能正被 PowerPoint 等程序打开，尽量以共享方式读取。
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha256 = SHA256.Create();
        var bytes = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>读取文件长度与最后写入时间（UTC）；文件不存在返回 <c>null</c>。</summary>
    private static FileStamp? ReadFileStamp(string fullPath)
    {
        var info = new FileInfo(fullPath);
        info.Refresh();

        return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
    }

    /// <summary>规范化为绝对路径（缓存 key）；空值 / 空白直接判为非法参数。</summary>
    private static string NormalizePath(string coursewarePath, string paramName)
    {
        if (string.IsNullOrWhiteSpace(coursewarePath))
        {
            throw new ArgumentException("课件路径不能为空。", paramName);
        }

        var fullPath = Path.GetFullPath(coursewarePath);
        var root = Path.GetPathRoot(fullPath);

        // 去掉末尾分隔符，避免同一文件出现两种 key（根目录本身除外）。
        return root is not null && PathComparer.Equals(fullPath, root)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>文件元数据指纹：长度 + 最后写入时间（UTC）。</summary>
    private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);

    /// <summary>缓存项：元数据 + 对应哈希。</summary>
    private readonly record struct CacheEntry(long Length, DateTime LastWriteTimeUtc, string Hash)
    {
        /// <summary>长度与写入时间都一致才算命中。</summary>
        public bool Matches(FileStamp stamp)
            => Length == stamp.Length && LastWriteTimeUtc == stamp.LastWriteTimeUtc;
    }
}
