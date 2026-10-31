using ClassVaneCanvas.Models.Annotations;

namespace ClassVaneCanvas.Sync;

/// <summary>远端（WebDAV 或同步文件夹）上的一个批注文件条目。</summary>
/// <param name="RelativePath">相对同步根的文件名（批注库中即 <c>&lt;sha256&gt;.cvcanno</c>）。</param>
/// <param name="Size">字节数。</param>
/// <param name="ETag">远端版本标识（WebDAV 的 ETag；文件夹实现用最后写入时间戳）。</param>
/// <param name="ModifiedUtc">远端最后修改时间。</param>
public sealed record RemoteEntry(string RelativePath, long Size, string? ETag, DateTimeOffset? ModifiedUtc);

/// <summary>同步动作方向。</summary>
public enum SyncDirection
{
    /// <summary>本机 → 远端。</summary>
    Upload,

    /// <summary>远端 → 本机。</summary>
    Download,

    /// <summary>双方都改过，需要老师决定。</summary>
    Conflict,

    /// <summary>已一致，跳过。</summary>
    Skipped,
}

/// <summary>单个文件的同步动作记录（用于同步结果面板展示）。</summary>
/// <param name="RelativePath">文件名。</param>
/// <param name="Direction">动作方向。</param>
/// <param name="Reason">原因说明（中文，直接展示给老师）。</param>
public sealed record SyncAction(string RelativePath, SyncDirection Direction, string Reason);

/// <summary>需要老师裁决的批注冲突。</summary>
/// <param name="RelativePath">冲突文件名。</param>
/// <param name="Local">本机版本。</param>
/// <param name="Remote">远端版本。</param>
/// <param name="LocalPath">本机批注文件绝对路径。</param>
/// <param name="RemoteETag">远端 ETag（回写时做乐观锁校验）。</param>
public sealed record AnnotationConflict(
    string RelativePath,
    AnnotationDocument Local,
    AnnotationDocument Remote,
    string LocalPath,
    string? RemoteETag);

/// <summary>冲突处理方式。</summary>
public enum AnnotationConflictResolution
{
    /// <summary>保留本地，覆盖远端。</summary>
    KeepLocal,

    /// <summary>保留远端，覆盖本地。</summary>
    KeepRemote,

    /// <summary>合并笔迹，两边内容都保留。</summary>
    Merge,
}

/// <summary>一次同步的结果。</summary>
/// <param name="Actions">全部动作明细。</param>
/// <param name="Uploaded">上传文件数。</param>
/// <param name="Downloaded">下载文件数。</param>
/// <param name="Conflicts">待裁决冲突数。</param>
/// <param name="Skipped">跳过文件数。</param>
/// <param name="PendingConflicts">待裁决冲突明细。</param>
/// <param name="Error">失败原因；成功为 <c>null</c>。</param>
public sealed record SyncReport(
    IReadOnlyList<SyncAction> Actions,
    int Uploaded,
    int Downloaded,
    int Conflicts,
    int Skipped,
    IReadOnlyList<AnnotationConflict> PendingConflicts,
    string? Error = null)
{
    /// <summary>本次同步是否成功。</summary>
    public bool Succeeded => Error is null;

    /// <summary>是否一切正常且没有需要裁决的冲突。</summary>
    public bool IsClean => Succeeded && Conflicts == 0;
}
