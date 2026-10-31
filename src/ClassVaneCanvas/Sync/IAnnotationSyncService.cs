namespace ClassVaneCanvas.Sync;

/// <summary>
/// 批注同步的传输层。只处理 <c>*.cvcanno</c> 文件；
/// 实现方不得读取、上传任何播放进度数据库或其它本机数据。
/// </summary>
public interface IAnnotationSyncTarget
{
    /// <summary>展示名称（例如「WebDAV：dav.example.com」或「同步文件夹」）。</summary>
    string DisplayName { get; }

    /// <summary>测试连接 / 可写性。</summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>列出同步根下的全部批注文件。</summary>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>下载文件内容；不存在返回 <c>null</c>。</summary>
    Task<byte[]?> DownloadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 上传文件内容。传入 <paramref name="expectedETag"/> 时做乐观锁校验（远端已变化则失败）。
    /// 返回新的 ETag。
    /// </summary>
    Task<string?> UploadAsync(
        string relativePath,
        byte[] content,
        string? expectedETag = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除远端文件。</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>批注同步服务。</summary>
public interface IAnnotationSyncService
{
    /// <summary>同步是否启用（默认关闭）。</summary>
    bool IsEnabled { get; }

    /// <summary>执行一次同步。<b>只同步 .cvcanno 批注文件，永远不同步本机播放进度。</b></summary>
    Task<SyncReport> SyncAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>按老师的选择处理一个冲突。</summary>
    Task<SyncReport> ResolveConflictAsync(
        AnnotationConflict conflict,
        AnnotationConflictResolution resolution,
        CancellationToken cancellationToken = default);
}
