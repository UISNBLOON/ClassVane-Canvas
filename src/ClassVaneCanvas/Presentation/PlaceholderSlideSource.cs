using Avalonia;
using Avalonia.Media;

namespace ClassVaneCanvas.Presentation;

/// <summary>
/// 占位幻灯片来源：永远可用的兜底后端。
///
/// 它**不画任何东西**——<see cref="GetSlideAsync"/> 恒返回 <c>null</c>，
/// 由放映画布自己铺白底并画「未接入渲染后端」的提示画面，
/// 因此本类不依赖任何外部程序、不读取课件文件，也永远不会抛异常。
/// 本机没装 LibreOffice（或用户选择纯批注模式）时就走这里，书写与批注功能完全不受影响。
/// </summary>
public sealed class PlaceholderSlideSource : ISlideSource
{
    /// <summary>页面坐标系默认尺寸，与放映画布一致。</summary>
    private static readonly Size DefaultPageSize = new(1280, 720);

    /// <summary>
    /// 创建占位来源。
    /// </summary>
    /// <param name="slideCount">课件总页数，至少 1 页。</param>
    /// <param name="pageSize">页面逻辑尺寸；传 <c>null</c> 或非正值时使用 1280×720。</param>
    public PlaceholderSlideSource(int slideCount = 1, Size? pageSize = null)
    {
        SlideCount = Math.Max(1, slideCount);
        PageSize = pageSize is { Width: > 0, Height: > 0 } size ? size : DefaultPageSize;
    }

    /// <inheritdoc />
    public string DisplayName => "占位画面（未接入渲染后端）";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public int SlideCount { get; }

    /// <inheritdoc />
    public Size PageSize { get; }

    /// <inheritdoc />
    /// <remarks>占位实现没有课件可加载，直接报告成功。</remarks>
    public Task<bool> InitializeAsync(string coursewarePath, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    /// <remarks>恒返回 <c>null</c>：占位实现不画图，放映画布会自绘占位画面。</remarks>
    public Task<IImage?> GetSlideAsync(int pageIndex, CancellationToken cancellationToken = default)
        => Task.FromResult<IImage?>(null);
}
