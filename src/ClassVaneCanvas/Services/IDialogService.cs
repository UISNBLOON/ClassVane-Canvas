using ClassVaneCanvas.Models;
using ClassVaneCanvas.Sync;

namespace ClassVaneCanvas.Services;

/// <summary>兼容性提示弹窗的按钮选择。</summary>
public enum CompatibilityNoticeChoice
{
    /// <summary>知道了（本次提示，下次打开仍然提示）。</summary>
    Acknowledge,

    /// <summary>不再提示此文件（按课件 SHA256 记入本机设置）。</summary>
    SuppressForThisFile,
}

/// <summary>放映与同步相关的弹窗。</summary>
public interface IDialogService : IConfirmService
{
    /// <summary>询问是否恢复本机上次播放位置。</summary>
    Task<PlaybackResumeChoice> ShowResumePromptAsync(
        PlaybackProgress progress,
        CancellationToken cancellationToken = default);

    /// <summary>批注冲突处理；老师直接关窗返回 <c>null</c>（本次不同步该文件）。</summary>
    Task<AnnotationConflictResolution?> ShowAnnotationConflictAsync(
        AnnotationConflict conflict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 「动画与字体兼容性提示」：列出内核降级项，老师可直接关窗。
    /// 关窗按「知道了」处理（这次不看了，但不写入「不再提示此文件」）。
    /// </summary>
    Task<CompatibilityNoticeChoice> ShowCompatibilityNoticeAsync(
        CompatibilityNotice notice,
        CancellationToken cancellationToken = default);
}
