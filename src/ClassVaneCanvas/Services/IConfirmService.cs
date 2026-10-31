namespace ClassVaneCanvas.Services;

/// <summary>确认对话框（用于清除数据等破坏性操作）。</summary>
public interface IConfirmService
{
    /// <summary>弹出确认框；老师确认返回 <c>true</c>。</summary>
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        CancellationToken cancellationToken = default);
}
