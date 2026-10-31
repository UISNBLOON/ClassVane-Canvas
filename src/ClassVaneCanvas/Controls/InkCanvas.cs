using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Controls;

/// <summary>
/// 板书批注画布（自研笔迹渲染 + 输入采集）。
///
/// 职责边界：
/// · 只负责**绘制**与**输入采集**，不改动批注数据 —— 一笔写完通过
///   <see cref="InkCanvasState.StrokeCompleted"/> 交回 ViewModel（撤销栈、自动保存都在那边）；
/// · 笔迹全部绘制在页面坐标系内，由画布矩阵统一缩放平移，
///   因此缩放 / 漫游后笔迹位置不变、形状不变形，任意倍率下保持矢量清晰；
/// · 输入源区分：**手写笔与鼠标始终书写**，手指默认走手势（单指翻页、双指缩放漫游），
///   手写笔落笔期间忽略所有触摸点（防误触 / 手掌抑制）。
/// </summary>
public sealed class InkCanvas : Control
{
    /// <summary>缩放下限 / 上限。</summary>
    public const double MinZoom = 0.5;
    public const double MaxZoom = 8.0;

    /// <summary>画布状态（由 ViewModel 提供）。</summary>
    public static readonly StyledProperty<InkCanvasState?> StateProperty =
        AvaloniaProperty.Register<InkCanvas, InkCanvasState?>(nameof(State));

    private readonly InkGeometryCache _geometryCache = new();
    private readonly List<InkPoint> _livePoints = new();
    private readonly Dictionary<int, Point> _touches = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IPointer? _inkPointer;
    private IPointer? _erasePointer;
    private long _strokeStartMs;
    private bool _swipeActive;
    private Point _swipeStart;
    private bool _pinchActive;
    private double _pinchLastDistance;
    private Point _pinchLastCentroid;

    static InkCanvas()
    {
        // 状态变化（换页、缩放、切工具…）都要重绘
        AffectsRender<InkCanvas>(StateProperty);
    }

    /// <summary>画布状态。</summary>
    public InkCanvasState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>当前是否正在书写（供工具栏显示状态）。</summary>
    public bool IsDrawing => _livePoints.Count > 0;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StateProperty)
        {
            Unsubscribe(change.GetOldValue<InkCanvasState?>());
            Subscribe(change.GetNewValue<InkCanvasState?>());
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(State);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe(State);
    }

    private void Subscribe(InkCanvasState? state)
    {
        if (state is null)
        {
            return;
        }

        state.PropertyChanged += OnStatePropertyChanged;
        state.InkChanged += OnStateInkChanged;
    }

    private void Unsubscribe(InkCanvasState? state)
    {
        if (state is null)
        {
            return;
        }

        state.PropertyChanged -= OnStatePropertyChanged;
        state.InkChanged -= OnStateInkChanged;
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnStateInkChanged(object? sender, EventArgs e) => InvalidateVisual();

    // ---------------------------------------------------------------- 渲染

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var state = State;
        var bounds = new Rect(Bounds.Size);
        if (state is null || bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        var page = NormalizePageSize(state.PageSize);
        var matrix = BuildPageMatrix(state, bounds, page);

        using (context.PushTransform(matrix))
        {
            var pageRect = new Rect(0, 0, page.Width, page.Height);

            DrawBackground(context, state, pageRect);
            DrawStrokes(context, state);
            DrawLiveStroke(context, state);

            // 页面描边：区分「画面区域」与画布留白
            context.DrawRectangle(
                null,
                new Pen(new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)), 1.4),
                pageRect);
        }
    }

    private static void DrawBackground(DrawingContext context, InkCanvasState state, Rect pageRect)
    {
        switch (state.DisplayMode)
        {
            case SlideDisplayMode.Black:
                // 黑屏只遮住幻灯片，板书继续可见（讲课时暂停画面但保留书写）
                context.FillRectangle(Brushes.Black, pageRect);
                return;

            case SlideDisplayMode.White:
                context.FillRectangle(Brushes.White, pageRect);
                return;
        }

        // 逐帧动画 / 转场：整页画面交给自定义渲染器，静态页图不再重复绘制。
        // 绘制顺序仍是「背景 → 笔迹 → 实时笔迹」，所以板书始终在最上层且位置不变。
        if (state.FrameRenderer is { } frameRenderer)
        {
            frameRenderer.Render(context, pageRect);
            return;
        }

        if (state.SlideImage is { } image)
        {
            context.DrawImage(image, pageRect);
            return;
        }

        DrawPlaceholder(context, state, pageRect);
    }

    /// <summary>渲染后端不可用时的占位画面（页面坐标系内绘制）。</summary>
    private static void DrawPlaceholder(DrawingContext context, InkCanvasState state, Rect pageRect)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF9)), pageRect);

        var title = new FormattedText(
            $"第 {state.PageIndex} / {Math.Max(state.SlideCount, 1)} 页",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            46,
            new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));

        var hint = new FormattedText(
            state.IsSlideLoading ? "正在渲染幻灯片…" : "未检测到幻灯片渲染后端",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            26,
            new SolidColorBrush(Color.FromRgb(0xB4, 0xBC, 0xC8)));

        context.DrawText(title, new Point((pageRect.Width - title.Width) / 2, (pageRect.Height / 2) - 70));
        context.DrawText(hint, new Point((pageRect.Width - hint.Width) / 2, (pageRect.Height / 2) + 6));
    }

    private void DrawStrokes(DrawingContext context, InkCanvasState state)
    {
        if (!state.ShowInk || state.Document is not { } document)
        {
            return;
        }

        var page = document.FindPage(state.PageIndex);
        if (page is null || page.Strokes.Count == 0)
        {
            return;
        }

        foreach (var stroke in page.Strokes)
        {
            InkRenderer.Draw(context, stroke, state.Settings, _geometryCache);
        }
    }

    private void DrawLiveStroke(DrawingContext context, InkCanvasState state)
    {
        if (_livePoints.Count == 0 || !state.ShowInk)
        {
            return;
        }

        InkRenderer.DrawLive(context, _livePoints, state.Tool, state.ColorArgb, state.BaseWidth, state.Settings);
    }

    // ---------------------------------------------------------------- 输入

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var state = State;
        if (state is null)
        {
            return;
        }

        var screen = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touches[e.Pointer.Id] = screen;

            // 手写笔 / 橡皮正在工作：忽略一切触摸（防误触、手掌抑制）
            if (state.Settings.IgnoreTouchWhilePenDown && (_inkPointer is not null || _erasePointer is not null))
            {
                e.Handled = true;
                return;
            }

            if (_touches.Count == 1)
            {
                _swipeActive = true;
                _swipeStart = screen;

                // 手指书写（默认关闭：手指只做手势）
                if (state.IsInkEnabled && state.Settings.FingerDraws)
                {
                    BeginStroke(e.Pointer, screen, properties, state);
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                // 第二指落下：取消可能存在的单指笔画，转为缩放漫游
                _swipeActive = false;
                CancelLiveStroke();
                BeginPinch();
            }

            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!state.IsInkEnabled)
        {
            return;
        }

        // 橡皮擦：点擦笔迹
        if (state.Tool == InkTool.Eraser)
        {
            _erasePointer = e.Pointer;
            e.Pointer.Capture(this);
            PerformErase(screen, state);
            e.Handled = true;
            return;
        }

        // 手写笔 / 鼠标：落笔即书写，绝不触发页面移动
        BeginStroke(e.Pointer, screen, properties, state);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var state = State;
        if (state is null)
        {
            return;
        }

        var screen = e.GetPosition(this);

        if (_erasePointer is not null && ReferenceEquals(e.Pointer, _erasePointer))
        {
            PerformErase(screen, state);
            e.Handled = true;
            return;
        }

        if (_inkPointer is not null && ReferenceEquals(e.Pointer, _inkPointer))
        {
            var properties = e.GetCurrentPoint(this).Properties;
            AppendLivePoint(screen, properties, state);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Pointer.Type != PointerType.Touch || !_touches.ContainsKey(e.Pointer.Id))
        {
            return;
        }

        _touches[e.Pointer.Id] = screen;

        if (_touches.Count >= 2)
        {
            UpdatePinch();
        }

        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        var state = State;
        if (state is null)
        {
            return;
        }

        if (_inkPointer is not null && ReferenceEquals(e.Pointer, _inkPointer))
        {
            CompleteStroke(state);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_erasePointer is not null && ReferenceEquals(e.Pointer, _erasePointer))
        {
            _erasePointer = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        _touches.Remove(e.Pointer.Id);

        if (_touches.Count == 0)
        {
            _pinchActive = false;

            if (_swipeActive)
            {
                EvaluateSwipe(e.GetPosition(this), state);
            }

            _swipeActive = false;
        }
        else if (_touches.Count == 1)
        {
            // 从双指退回单指：重新开始跟踪滑动，避免误判翻页
            _swipeActive = false;
            _pinchActive = false;
        }

        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_inkPointer is not null && ReferenceEquals(e.Pointer, _inkPointer))
        {
            CancelLiveStroke();
        }

        if (_erasePointer is not null && ReferenceEquals(e.Pointer, _erasePointer))
        {
            _erasePointer = null;
        }

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touches.Remove(e.Pointer.Id);
            if (_touches.Count == 0)
            {
                _swipeActive = false;
                _pinchActive = false;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var state = State;
        if (state is null)
        {
            return;
        }

        var accelerate = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (accelerate)
        {
            ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1.12 : 1 / 1.12, state);
        }
        else
        {
            // 滚轮翻页（外接鼠标 / 翻页笔常用）
            state.RaisePageTurnRequested(e.Delta.Y < 0 ? 1 : -1);
        }

        e.Handled = true;
    }

    // ---------------------------------------------------------------- 书写

    private void BeginStroke(IPointer pointer, Point screen, PointerPointProperties properties, InkCanvasState state)
    {
        _inkPointer = pointer;
        pointer.Capture(this);

        _livePoints.Clear();
        _strokeStartMs = _clock.ElapsedMilliseconds;
        AppendLivePoint(screen, properties, state);

        InvalidateVisual();
    }

    private void AppendLivePoint(Point screen, PointerPointProperties properties, InkCanvasState state)
    {
        if (!TryToPage(screen, state, out var page))
        {
            return;
        }

        var elapsed = (int)Math.Clamp(_clock.ElapsedMilliseconds - _strokeStartMs, 0, int.MaxValue);

        _livePoints.Add(new InkPoint(
            (float)page.X,
            (float)page.Y,
            properties.Pressure,
            properties.XTilt,
            properties.YTilt,
            elapsed));
    }

    private void CompleteStroke(InkCanvasState state)
    {
        var points = new List<InkPoint>(_livePoints);
        _livePoints.Clear();
        _inkPointer = null;
        InvalidateVisual();

        if (points.Count == 0)
        {
            return;
        }

        var stroke = InkStroke.Create(state.Tool, state.ColorArgb, state.BaseWidth, points, state.DeviceId);

        // 交回 ViewModel：入撤销栈 + 标脏 + 自动保存
        state.RaiseStrokeCompleted(stroke);
    }

    private void CancelLiveStroke()
    {
        if (_livePoints.Count == 0 && _inkPointer is null)
        {
            return;
        }

        _livePoints.Clear();
        _inkPointer = null;
        InvalidateVisual();
    }

    private void PerformErase(Point screen, InkCanvasState state)
    {
        if (state.Document is not { } document || !TryToPage(screen, state, out var page))
        {
            return;
        }

        var strokes = document.FindPage(state.PageIndex)?.Strokes;
        if (strokes is null || strokes.Count == 0)
        {
            return;
        }

        // 擦除半径跟随笔宽（细笔也能擦到）
        var radius = Math.Max(8d, state.BaseWidth * 2.2);
        var hits = InkHitTester.HitTest(strokes, page.X, page.Y, radius, state.Settings);
        if (hits.Count > 0)
        {
            state.RaiseEraseRequested(new EraseRequest(page.X, page.Y, radius, hits));
        }
    }

    // ---------------------------------------------------------------- 手势

    private void EvaluateSwipe(Point end, InkCanvasState state)
    {
        if (!state.Settings.SwipeTurnsPage)
        {
            return;
        }

        var dx = end.X - _swipeStart.X;
        var dy = end.Y - _swipeStart.Y;
        var threshold = Math.Max(24f, state.Settings.SwipeTurnThreshold);

        if (Math.Abs(dx) < threshold && Math.Abs(dy) < threshold)
        {
            return;
        }

        // 横向为主：左滑下一页、右滑上一页；纵向为主：上滑下一页、下滑上一页
        var forward = Math.Abs(dx) >= Math.Abs(dy) ? dx < 0 : dy < 0;
        state.RaisePageTurnRequested(forward ? 1 : -1);
    }

    private void BeginPinch()
    {
        if (_touches.Count < 2)
        {
            return;
        }

        var points = _touches.Values.ToArray();
        _pinchActive = true;
        _pinchLastDistance = Distance(points[0], points[1]);
        _pinchLastCentroid = Centroid(points);
    }

    private void UpdatePinch()
    {
        var state = State;
        if (state is null || !_pinchActive || _touches.Count < 2)
        {
            return;
        }

        var points = _touches.Values.ToArray();
        var centroid = Centroid(points);
        var distance = Distance(points[0], points[1]);

        if (_pinchLastDistance > 1d && distance > 1d)
        {
            var factor = distance / _pinchLastDistance;
            var sensitivity = Math.Clamp(state.Settings.PinchSensitivity, 0.2f, 3f);
            if (Math.Abs(sensitivity - 1f) > 0.001f)
            {
                factor = Math.Pow(factor, sensitivity);
            }

            ZoomAt(centroid, factor, state);
        }

        // 双指拖拽 = 漫游画布
        state.PanX += centroid.X - _pinchLastCentroid.X;
        state.PanY += centroid.Y - _pinchLastCentroid.Y;

        _pinchLastDistance = distance;
        _pinchLastCentroid = centroid;

        InvalidateVisual();
    }

    /// <summary>以某个屏幕锚点为中心缩放（锚点下的内容保持不动）。</summary>
    public void ZoomAt(Point anchor, double factor, InkCanvasState? state = null)
    {
        state ??= State;
        if (state is null || factor <= 0)
        {
            return;
        }

        var bounds = new Rect(Bounds.Size);
        var newZoom = Math.Clamp(state.Zoom * factor, MinZoom, MaxZoom);
        var applied = newZoom / state.Zoom;
        if (Math.Abs(applied - 1d) < 1e-6)
        {
            return;
        }

        state.PanX = (state.PanX * applied) + ((1 - applied) * (anchor.X - (bounds.Width / 2)));
        state.PanY = (state.PanY * applied) + ((1 - applied) * (anchor.Y - (bounds.Height / 2)));
        state.Zoom = newZoom;

        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 坐标变换

    private static Size NormalizePageSize(Size page)
        => page.Width > 1 && page.Height > 1 ? page : InkCanvasState.DefaultPageSize;

    /// <summary>页面坐标系 → 屏幕坐标系的矩阵（渲染与输入换算共用同一套逻辑）。</summary>
    private static Matrix BuildPageMatrix(InkCanvasState state, Rect bounds, Size page)
    {
        var fit = Math.Min(bounds.Width / page.Width, bounds.Height / page.Height);
        var scale = Math.Max(0.01, fit * state.Zoom);

        var offsetX = ((bounds.Width - (page.Width * scale)) / 2) + state.PanX;
        var offsetY = ((bounds.Height - (page.Height * scale)) / 2) + state.PanY;

        return Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);
    }

    /// <summary>屏幕坐标 → 页面坐标。</summary>
    private bool TryToPage(Point screen, InkCanvasState state, out Point page)
    {
        page = default;

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return false;
        }

        var matrix = BuildPageMatrix(state, bounds, NormalizePageSize(state.PageSize));
        if (!matrix.TryInvert(out var inverse))
        {
            return false;
        }

        page = inverse.Transform(screen);
        return true;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Point Centroid(IReadOnlyList<Point> points)
    {
        double x = 0, y = 0;
        foreach (var point in points)
        {
            x += point.X;
            y += point.Y;
        }

        return new Point(x / points.Count, y / points.Count);
    }
}
