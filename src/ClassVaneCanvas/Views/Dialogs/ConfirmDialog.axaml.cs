using Avalonia.Controls;

namespace ClassVaneCanvas.Views.Dialogs;

/// <summary>通用确认弹窗（清除数据等破坏性操作）。</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    /// <summary>弹出确认框；确认返回 <c>true</c>。</summary>
    public static async Task<bool> ShowAsync(
        Window? owner,
        string title,
        string message,
        string confirmText,
        string cancelText)
        => await ShowChoiceAsync(owner, title, message, confirmText, cancelText) == true;

    /// <summary>
    /// 弹出「主按钮 / 次按钮」二选一弹窗。
    /// </summary>
    /// <param name="owner">宿主窗口；<c>null</c> 时非模态显示。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文（支持换行）。</param>
    /// <param name="primaryText">主按钮文案。</param>
    /// <param name="secondaryText">次按钮文案。</param>
    /// <returns>主按钮 <c>true</c>；次按钮 <c>false</c>；直接关窗（右上角 / Esc）<c>null</c>。</returns>
    public static async Task<bool?> ShowChoiceAsync(
        Window? owner,
        string title,
        string message,
        string primaryText,
        string secondaryText)
    {
        var dialog = new ConfirmDialog();
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = primaryText;
        dialog.CancelButton.Content = secondaryText;

        var completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);

        dialog.ConfirmButton.Click += (_, _) =>
        {
            completion.TrySetResult(true);
            dialog.Close();
        };

        dialog.CancelButton.Click += (_, _) =>
        {
            completion.TrySetResult(false);
            dialog.Close();
        };

        // 直接关窗：没有做出选择
        dialog.Closed += (_, _) => completion.TrySetResult(null);

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        return await completion.Task;
    }
}
