namespace ClassVaneCanvas.Models;

/// <summary>
/// 某份课件在**本机**的放映进度。
///
/// 硬性约束：这条记录只写入本机 SQLite（playback-progress.db），
/// 不写入 .cvcanno 批注文件、不随批注同步、不跨设备传递。
/// </summary>
/// <param name="CoursewareHash">课件文件 SHA256（小写十六进制），作为唯一标识。</param>
/// <param name="CoursewarePath">最近一次打开时的绝对路径（仅本机展示用）。</param>
/// <param name="CoursewareFileName">课件文件名（展示用）。</param>
/// <param name="SlideCount">上次已知总页数；0 表示未知。</param>
/// <param name="LastSlideIndex">上次停留的页码（1 起）。</param>
/// <param name="LastPlayedUtc">上次放映时间（UTC 存储，展示时转本地时间）。</param>
/// <param name="PromptSuppressed">是否「不再提示此文件」。</param>
public sealed record PlaybackProgress(
    string CoursewareHash,
    string CoursewarePath,
    string CoursewareFileName,
    int SlideCount,
    int LastSlideIndex,
    DateTimeOffset LastPlayedUtc,
    bool PromptSuppressed)
{
    /// <summary>格式化为弹窗中的「上次放映」时间（本机时区，YYYY-MM-DD HH:mm）。</summary>
    public string FormatLastPlayedLocal() => LastPlayedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
