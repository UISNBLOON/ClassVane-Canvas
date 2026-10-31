using Avalonia.Controls;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Services;
using ClassVaneCanvas.Sync;
using ClassVaneCanvas.Views.Dialogs;

namespace ClassVaneCanvas.Views;

/// <summary>
/// 弹窗服务实现：用当前主窗口作为宿主。
/// 弹窗本身是视图层实现，ViewModel 只依赖 <see cref="IDialogService"/> / <see cref="IConfirmService"/>。
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly Func<Window?> _hostAccessor;

    public DialogService(Func<Window?> hostAccessor)
        => _hostAccessor = hostAccessor ?? throw new ArgumentNullException(nameof(hostAccessor));

    /// <inheritdoc />
    public Task<PlaybackResumeChoice> ShowResumePromptAsync(
        PlaybackProgress progress,
        CancellationToken cancellationToken = default)
        => ResumePromptDialog.ShowAsync(_hostAccessor(), progress);

    /// <inheritdoc />
    public Task<AnnotationConflictResolution?> ShowAnnotationConflictAsync(
        AnnotationConflict conflict,
        CancellationToken cancellationToken = default)
        => AnnotationConflictDialog.ShowAsync(_hostAccessor(), conflict);

    /// <inheritdoc />
    /// <remarks>
    /// 沿用 <see cref="ConfirmDialog"/>（两个按钮）：主按钮「知道了」、
    /// 次按钮「不再提示此文件」，直接关窗按「知道了」处理。
    /// </remarks>
    public async Task<CompatibilityNoticeChoice> ShowCompatibilityNoticeAsync(
        CompatibilityNotice notice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        var choice = await ConfirmDialog.ShowChoiceAsync(
            _hostAccessor(),
            CompatibilityNotice.Title,
            notice.ToMessage(),
            CompatibilityNotice.AcknowledgeText,
            CompatibilityNotice.SuppressText);

        return choice == false
            ? CompatibilityNoticeChoice.SuppressForThisFile
            : CompatibilityNoticeChoice.Acknowledge;
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        CancellationToken cancellationToken = default)
        => ConfirmDialog.ShowAsync(_hostAccessor(), title, message, confirmText, cancelText);
}
