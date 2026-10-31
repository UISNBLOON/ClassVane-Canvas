using Avalonia.Controls;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Services;

namespace ClassVaneCanvas.Views.Dialogs;

/// <summary>
/// 「恢复上次播放位置」弹窗。
/// 三个选项：跳转至该页 / 从头开始 / 不再提示此文件。
/// </summary>
public partial class ResumePromptDialog : Window
{
    public ResumePromptDialog() => InitializeComponent();

    /// <summary>按课件进度填充弹窗文案（也便于预览与测试）。</summary>
    public void ApplyProgress(PlaybackProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        MessageText.Text = ResumePromptText.FormatMessage(progress);
    }

    /// <summary>弹出对话框并等待老师选择。</summary>
    public static async Task<PlaybackResumeChoice> ShowAsync(Window? owner, PlaybackProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var dialog = new ResumePromptDialog();
        dialog.ApplyProgress(progress);

        var completion = new TaskCompletionSource<PlaybackResumeChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Choose(PlaybackResumeChoice choice)
        {
            completion.TrySetResult(choice);
            dialog.Close();
        }

        dialog.JumpButton.Click += (_, _) => Choose(PlaybackResumeChoice.JumpToPage);
        dialog.StartOverButton.Click += (_, _) => Choose(PlaybackResumeChoice.StartFromBeginning);
        dialog.NeverPromptButton.Click += (_, _) => Choose(PlaybackResumeChoice.NeverPromptAgain);
        dialog.Closed += (_, _) => completion.TrySetResult(PlaybackResumeChoice.Dismiss);

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
