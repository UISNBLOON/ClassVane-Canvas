using Avalonia;
using Avalonia.Media;
using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Models.Ink;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassVaneCanvas.Controls;

/// <summary>橡皮擦请求（页面坐标系）。</summary>
/// <param name="X">擦除点横坐标。</param>
/// <param name="Y">擦除点纵坐标。</param>
/// <param name="Radius">擦除半径。</param>
/// <param name="StrokeIds">命中的笔迹 Id（最上层优先）。</param>
public sealed record EraseRequest(double X, double Y, double Radius, IReadOnlyList<string> StrokeIds);

/// <summary>
/// 自定义幻灯片背景渲染（逐帧动画 / 转场用）。
///
/// 约定：
/// · <see cref="Render"/> 在<b>页面坐标系</b>内绘制，绘制范围由 <c>pageRect</c> 给出，
///   调用方（<see cref="InkCanvas"/>）已经推入「页面 → 屏幕」的缩放平移矩阵；
/// · 背景只负责画面本身，<b>笔迹由画布在其之后绘制</b>，因此板书永远压在最上层且位置不变；
/// · <see cref="IsAnimating"/> 用于判断是否还需要继续重绘（放映结束应回到静态整页渲染）。
/// </summary>
public interface ISlideFrameRenderer
{
    /// <summary>在页面坐标系 <paramref name="pageRect"/> 内绘制当前帧。</summary>
    void Render(DrawingContext context, Rect pageRect);

    /// <summary>是否仍在播放（用于判断是否需要继续重绘）。</summary>
    bool IsAnimating { get; }
}

/// <summary>
/// 画布状态：由 ViewModel 持有并驱动 <see cref="InkCanvas"/> 渲染，
/// 画布本身只负责绘制与输入采集，所有数据变更通过事件回到 ViewModel（便于撤销与自动保存）。
/// </summary>
public sealed partial class InkCanvasState : ObservableObject
{
    /// <summary>默认页面尺寸（16:9，页面坐标系）。</summary>
    public static readonly Size DefaultPageSize = new(1280, 720);

    /// <summary>当前课件的批注文档。</summary>
    [ObservableProperty]
    private AnnotationDocument? _document;

    /// <summary>当前页码（1 起）。</summary>
    [ObservableProperty]
    private int _pageIndex = 1;

    /// <summary>总页数。</summary>
    [ObservableProperty]
    private int _slideCount = 1;

    /// <summary>当前页面尺寸。</summary>
    [ObservableProperty]
    private Size _pageSize = DefaultPageSize;

    /// <summary>当前幻灯片图像（渲染后端提供；为空时显示占位画面）。</summary>
    [ObservableProperty]
    private IImage? _slideImage;

    /// <summary>自定义背景渲染（逐帧动画 / 转场用）。设置后优先于 <see cref="SlideImage"/>。</summary>
    private ISlideFrameRenderer? _frameRenderer;

    /// <summary>
    /// 自定义背景渲染（逐帧动画 / 转场用）。设置后优先于 <see cref="SlideImage"/>；
    /// 为空时回落到静态页图 / 黑屏白屏 / 占位画面。
    /// </summary>
    public ISlideFrameRenderer? FrameRenderer
    {
        get => _frameRenderer;
        set
        {
            if (ReferenceEquals(_frameRenderer, value))
            {
                return;
            }

            _frameRenderer = value;

            // 让画布立即重绘（换后端 / 退出放映时画面要跟着换）
            OnPropertyChanged(nameof(FrameRenderer));
        }
    }

    /// <summary>当前页码对应的幻灯片是否仍在渲染中。</summary>
    [ObservableProperty]
    private bool _isSlideLoading;

    /// <summary>显示模式（正常 / 黑屏 / 白屏）。</summary>
    [ObservableProperty]
    private SlideDisplayMode _displayMode = SlideDisplayMode.Normal;

    /// <summary>当前笔触类型。</summary>
    [ObservableProperty]
    private InkTool _tool = InkTool.HardPen;

    /// <summary>当前笔色（ARGB）。</summary>
    [ObservableProperty]
    private uint _colorArgb = 0xFFE53935;

    /// <summary>当前基准笔宽（页面逻辑像素）。</summary>
    [ObservableProperty]
    private float _baseWidth = 3.2f;

    /// <summary>笔迹引擎参数。</summary>
    [ObservableProperty]
    private InkEngineSettings _settings = new();

    /// <summary>本机设备标识（写入笔迹，冲突排查时区分来源）。</summary>
    [ObservableProperty]
    private string _deviceId = string.Empty;

    /// <summary>是否允许书写（关闭时只做手势翻页 / 缩放）。</summary>
    [ObservableProperty]
    private bool _isInkEnabled = true;

    /// <summary>是否显示本页笔迹（切页时短暂隐藏）。</summary>
    [ObservableProperty]
    private bool _showInk = true;

    /// <summary>画布缩放倍率（1 = 适配窗口）。</summary>
    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>画布横向平移（屏幕像素）。</summary>
    [ObservableProperty]
    private double _panX;

    /// <summary>画布纵向平移（屏幕像素）。</summary>
    [ObservableProperty]
    private double _panY;

    /// <summary>一笔书写完成（ViewModel 负责入栈撤销、标脏、自动保存）。</summary>
    public event EventHandler<InkStroke>? StrokeCompleted;

    /// <summary>橡皮擦命中（ViewModel 负责删除并记录撤销）。</summary>
    public event EventHandler<EraseRequest>? EraseRequested;

    /// <summary>请求翻页（正数向后、负数向前）。</summary>
    public event EventHandler<int>? PageTurnRequested;

    /// <summary>手指落笔但「手指书写」未开启时的提示。</summary>
    public event EventHandler? FingerInkBlocked;

    /// <summary>画布变换（缩放 / 漫游）变化。</summary>
    public event EventHandler? CanvasTransformChanged;

    /// <summary>本页笔迹数据发生变化（新增 / 擦除 / 撤销 / 重做 / 换页），画布收到后重绘。</summary>
    public event EventHandler? InkChanged;

    /// <summary>通知画布重绘（数据是就地修改的，不经过属性通知）。</summary>
    public void RaiseInkChanged()
    {
        InkChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(CurrentPageStrokeCount));
    }

    /// <summary>本页已有的笔迹条数。</summary>
    public int CurrentPageStrokeCount => Document?.FindPage(PageIndex)?.Strokes.Count ?? 0;

    internal void RaiseStrokeCompleted(InkStroke stroke) => StrokeCompleted?.Invoke(this, stroke);

    internal void RaiseEraseRequested(EraseRequest request) => EraseRequested?.Invoke(this, request);

    internal void RaisePageTurnRequested(int delta) => PageTurnRequested?.Invoke(this, delta);

    internal void RaiseFingerInkBlocked() => FingerInkBlocked?.Invoke(this, EventArgs.Empty);

    internal void RaiseCanvasTransformChanged() => CanvasTransformChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>重置缩放与漫游（回到适配窗口）。</summary>
    public void ResetView()
    {
        Zoom = 1;
        PanX = 0;
        PanY = 0;
    }

    partial void OnZoomChanged(double value) => RaiseCanvasTransformChanged();

    partial void OnPanXChanged(double value) => RaiseCanvasTransformChanged();

    partial void OnPanYChanged(double value) => RaiseCanvasTransformChanged();
}
