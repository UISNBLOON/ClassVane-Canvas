using ClassVaneCanvas.Models;

namespace ClassVaneCanvas.Services;

/// <summary>恢复播放位置弹窗的选项。</summary>
public enum PlaybackResumeChoice
{
    /// <summary>跳转至上次页码。</summary>
    JumpToPage,

    /// <summary>从头开始放映。</summary>
    StartFromBeginning,

    /// <summary>不再提示此文件。</summary>
    NeverPromptAgain,

    /// <summary>直接关闭弹窗（等价于从头开始，但不改设置）。</summary>
    Dismiss,
}

/// <summary>
/// 恢复播放位置的文案。
/// 固定格式：<c>上次在本机播放到第 X / Y 页（上次放映：YYYY-MM-DD HH:mm），是否跳转至该页面？</c>
/// </summary>
public static class ResumePromptText
{
    /// <summary>弹窗标题。</summary>
    public const string Title = "恢复上次播放位置";

    /// <summary>按课件进度生成弹窗正文（时间取本机时区）。</summary>
    public static string FormatMessage(PlaybackProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var total = Math.Max(progress.SlideCount, progress.LastSlideIndex);
        return $"上次在本机播放到第 {progress.LastSlideIndex} / {total} 页" +
               $"（上次放映：{progress.FormatLastPlayedLocal()}），是否跳转至该页面？";
    }
}
