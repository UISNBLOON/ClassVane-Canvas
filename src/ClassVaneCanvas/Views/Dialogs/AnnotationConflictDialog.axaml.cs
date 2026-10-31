using Avalonia.Controls;
using ClassVaneCanvas.Sync;

namespace ClassVaneCanvas.Views.Dialogs;

/// <summary>
/// 批注冲突处理弹窗：同一课件在多台设备上都被修改时出现。
/// 三个选项：保留本地 / 保留远端 / 合并笔迹；直接关窗表示本次不同步该文件。
/// </summary>
public partial class AnnotationConflictDialog : Window
{
    public AnnotationConflictDialog() => InitializeComponent();

    /// <summary>按冲突内容填充弹窗（也便于预览与测试）。</summary>
    public void ApplyConflict(AnnotationConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        MessageText.Text = $"课件「{conflict.Local.CoursewareFileName}」的批注在多台设备上都被修改过。";
        LocalDetailText.Text = Describe(conflict.Local);
        RemoteDetailText.Text = Describe(conflict.Remote);
    }

    /// <summary>弹出对话框并等待老师选择；关窗返回 <c>null</c>。</summary>
    public static async Task<AnnotationConflictResolution?> ShowAsync(Window? owner, AnnotationConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        var dialog = new AnnotationConflictDialog();
        dialog.ApplyConflict(conflict);

        var completion = new TaskCompletionSource<AnnotationConflictResolution?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Choose(AnnotationConflictResolution resolution)
        {
            completion.TrySetResult(resolution);
            dialog.Close();
        }

        dialog.KeepLocalButton.Click += (_, _) => Choose(AnnotationConflictResolution.KeepLocal);
        dialog.KeepRemoteButton.Click += (_, _) => Choose(AnnotationConflictResolution.KeepRemote);
        dialog.MergeButton.Click += (_, _) => Choose(AnnotationConflictResolution.Merge);
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

    private static string Describe(Models.Annotations.AnnotationDocument document)
        => $"{document.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {document.TotalStrokeCount} 条笔迹";

    /// <summary>格式化冲突描述（同步结果面板也会用到）。</summary>
    public static string FormatSummary(AnnotationConflict conflict)
        => $"「{conflict.Local.CoursewareFileName}」本机 {conflict.Local.TotalStrokeCount} 条 / " +
           $"远端 {conflict.Remote.TotalStrokeCount} 条笔迹";
}
