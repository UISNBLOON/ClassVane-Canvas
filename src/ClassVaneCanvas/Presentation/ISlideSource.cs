using Avalonia;
using Avalonia.Media;

namespace ClassVaneCanvas.Presentation;

/// <summary>
/// 放映时的幻灯片位图来源（渲染后端可插拔）。
/// 实现方只负责「给第 N 页一张图」，不接触批注数据，也不修改原始课件文件。
/// </summary>
public interface ISlideSource
{
    /// <summary>后端展示名（用于设置页 / 状态栏提示）。</summary>
    string DisplayName { get; }

    /// <summary>当前后端是否可用（例如是否检测到 LibreOffice）。</summary>
    bool IsAvailable { get; }

    /// <summary>课件总页数。</summary>
    int SlideCount { get; }

    /// <summary>页面逻辑尺寸（页面坐标系，默认 1280×720）。</summary>
    Size PageSize { get; }

    /// <summary>加载课件（探测后端、准备缓存）。返回是否成功。</summary>
    Task<bool> InitializeAsync(string coursewarePath, CancellationToken cancellationToken = default);

    /// <summary>取第 pageIndex 页（1 起）的位图；不可用时返回 null。</summary>
    Task<IImage?> GetSlideAsync(int pageIndex, CancellationToken cancellationToken = default);
}
