using Avalonia;
using ClassVaneCanvas.Pptx.Model;

namespace ClassVaneCanvas.Pptx;

/// <summary>
/// 单页在转场过程中的视觉状态：由渲染层负责把两页各画一次。
/// </summary>
/// <param name="Opacity">不透明度（0..1）。</param>
/// <param name="TranslateX">水平位移（逻辑像素）。</param>
/// <param name="TranslateY">垂直位移（逻辑像素）。</param>
/// <param name="ClipProgress">裁剪进度（0..1，1 表示完全不裁剪）。</param>
/// <param name="ClipFrom">裁剪推进方向。</param>
/// <param name="ClipMode">裁剪模式（不裁剪 / 单边擦除 / 拆分）。</param>
public readonly record struct PptxPageVisualState(
    double Opacity,
    double TranslateX,
    double TranslateY,
    double ClipProgress,
    PptxAnimationDirection ClipFrom,
    PptxTransitionClipMode ClipMode)
{
    /// <summary>完全可见、无位移、无裁剪。</summary>
    public static readonly PptxPageVisualState Visible = new(1d, 0d, 0d, 1d, PptxAnimationDirection.None, PptxTransitionClipMode.None);

    /// <summary>完全不可见。</summary>
    public static readonly PptxPageVisualState Hidden = new(0d, 0d, 0d, 1d, PptxAnimationDirection.None, PptxTransitionClipMode.None);
}

/// <summary>
/// 一次转场求值的结果：旧页与新页各自的视觉状态。
/// </summary>
/// <param name="Previous">旧页状态。</param>
/// <param name="Next">新页状态。</param>
/// <param name="Kind">生效的转场类型（已归一化）。</param>
/// <param name="Progress">归一化进度（0..1）。</param>
/// <param name="Degraded">是否由不受支持的转场降级而来。</param>
public sealed record TransitionFrame(
    PptxPageVisualState Previous,
    PptxPageVisualState Next,
    PptxTransitionKind Kind,
    double Progress,
    bool Degraded)
{
    /// <summary>转场完成后的静止帧（只显示新页）。</summary>
    public static readonly TransitionFrame Completed =
        new(PptxPageVisualState.Hidden, PptxPageVisualState.Visible, PptxTransitionKind.None, 1d, false);
}

/// <summary>
/// 页面转场播放器：<see cref="Begin"/> 装载一次转场，<see cref="Evaluate"/> 按进度求值。
///
/// 已实现：淡入淡出（fade）、推进（push，含方向）、覆盖（cover，含方向），
/// 加分项：擦除（wipe，含方向）与拆分（split）。
/// 其它冷门转场（morph / honeycomb / glitter / vortex …）统一降级为淡入淡出，
/// 并在解析阶段记入页面的 <see cref="PptxSlide.UnsupportedFeatures"/>。
/// </summary>
public sealed class SlideTransitionPlayer
{
    /// <summary>默认转场时长（毫秒）。</summary>
    public const double DefaultDurationMs = 500d;

    private Size _pageSize = new(1280, 720);

    /// <summary>当前转场（已归一化）。</summary>
    public PptxTransition Transition { get; private set; } = PptxTransition.None;

    /// <summary>转场时长（毫秒）。</summary>
    public double DurationMs => Transition.Kind == PptxTransitionKind.None ? 0d : Math.Max(1d, Transition.DurationMs);

    /// <summary>是否需要播放转场。</summary>
    public bool IsRunning => Transition.Kind != PptxTransitionKind.None;

    /// <summary>是否由不受支持的转场降级而来。</summary>
    public bool IsDegraded => Transition.IsDegraded;

    /// <summary>装载转场（页面尺寸取默认 1280×720）。</summary>
    public void Begin(PptxTransition transition) => Begin(transition, new Size(1280, 720));

    /// <summary>装载转场（页面尺寸用于计算推进 / 覆盖的位移量）。</summary>
    public void Begin(PptxTransition transition, Size pageSize)
    {
        Transition = Normalize(transition ?? PptxTransition.None);
        _pageSize = pageSize.Width > 0 && pageSize.Height > 0 ? pageSize : new Size(1280, 720);
    }

    /// <summary>装载某页自己的转场。</summary>
    public void Begin(PptxSlide slide)
    {
        ArgumentNullException.ThrowIfNull(slide);
        Begin(slide.Transition, slide.PageSize);
    }

    /// <summary>
    /// 按进度求值。
    /// </summary>
    /// <param name="progress">进度（0 = 旧页完全可见，1 = 新页完全可见）。</param>
    /// <returns>新旧两页各自的视觉状态。</returns>
    public TransitionFrame Evaluate(double progress)
    {
        var p = Math.Clamp(progress, 0d, 1d);
        var width = _pageSize.Width;
        var height = _pageSize.Height;

        switch (Transition.Kind)
        {
            case PptxTransitionKind.None:
                return new TransitionFrame(
                    p >= 1d ? PptxPageVisualState.Hidden : PptxPageVisualState.Visible,
                    p >= 1d ? PptxPageVisualState.Visible : PptxPageVisualState.Hidden,
                    PptxTransitionKind.None,
                    p,
                    false);

            case PptxTransitionKind.Fade:
                return new TransitionFrame(
                    new PptxPageVisualState(1d - p, 0d, 0d, 1d, PptxAnimationDirection.None, PptxTransitionClipMode.None),
                    new PptxPageVisualState(p, 0d, 0d, 1d, PptxAnimationDirection.None, PptxTransitionClipMode.None),
                    PptxTransitionKind.Fade,
                    p,
                    Transition.IsDegraded);

            case PptxTransitionKind.Push:
            {
                var (dx, dy) = PushVector(Transition.Direction, width, height);
                return new TransitionFrame(
                    new PptxPageVisualState(1d, dx * p, dy * p, 1d, PptxAnimationDirection.None, PptxTransitionClipMode.None),
                    new PptxPageVisualState(1d, -dx * (1d - p), -dy * (1d - p), 1d, PptxAnimationDirection.None, PptxTransitionClipMode.None),
                    PptxTransitionKind.Push,
                    p,
                    false);
            }

            case PptxTransitionKind.Cover:
            {
                var (dx, dy) = PushVector(Transition.Direction, width, height);
                return new TransitionFrame(
                    PptxPageVisualState.Visible,
                    new PptxPageVisualState(1d, -dx * (1d - p), -dy * (1d - p), 1d, PptxAnimationDirection.None, PptxTransitionClipMode.None),
                    PptxTransitionKind.Cover,
                    p,
                    false);
            }

            case PptxTransitionKind.Wipe:
            {
                var revealFrom = Transition.Direction switch
                {
                    PptxTransitionDirection.Left => PptxAnimationDirection.FromRight,
                    PptxTransitionDirection.Right => PptxAnimationDirection.FromLeft,
                    PptxTransitionDirection.Up => PptxAnimationDirection.FromBottom,
                    PptxTransitionDirection.Down => PptxAnimationDirection.FromTop,
                    _ => PptxAnimationDirection.FromLeft,
                };

                return new TransitionFrame(
                    PptxPageVisualState.Visible,
                    new PptxPageVisualState(1d, 0d, 0d, p, revealFrom, PptxTransitionClipMode.Edge),
                    PptxTransitionKind.Wipe,
                    p,
                    false);
            }

            case PptxTransitionKind.Split:
            {
                var mode = Transition.SplitOrientation.Equals("vert", StringComparison.OrdinalIgnoreCase)
                    ? PptxTransitionClipMode.SplitOut
                    : PptxTransitionClipMode.SplitOut;

                return new TransitionFrame(
                    PptxPageVisualState.Visible,
                    new PptxPageVisualState(1d, 0d, 0d, p, PptxAnimationDirection.None, mode),
                    PptxTransitionKind.Split,
                    p,
                    false);
            }

            default:
                return TransitionFrame.Completed;
        }
    }

    /// <summary>把不受支持的转场归一化为淡入淡出（解析阶段已做过一次，这里再兜底一次）。</summary>
    public static PptxTransition Normalize(PptxTransition transition)
    {
        if (transition.Kind is PptxTransitionKind.None
            or PptxTransitionKind.Fade
            or PptxTransitionKind.Push
            or PptxTransitionKind.Cover
            or PptxTransitionKind.Wipe
            or PptxTransitionKind.Split)
        {
            return transition.DurationMs > 0 ? transition : transition with { DurationMs = DefaultDurationMs };
        }

        return transition with
        {
            Kind = PptxTransitionKind.Fade,
            IsDegraded = true,
            DurationMs = transition.DurationMs > 0 ? transition.DurationMs : DefaultDurationMs,
        };
    }

    /// <summary>推进 / 覆盖的基准位移向量（旧页移动方向）。</summary>
    private static (double X, double Y) PushVector(PptxTransitionDirection direction, double width, double height)
        => direction switch
        {
            PptxTransitionDirection.Left => (-width, 0d),
            PptxTransitionDirection.Right => (width, 0d),
            PptxTransitionDirection.Up => (0d, -height),
            PptxTransitionDirection.Down => (0d, height),
            _ => (-width, 0d),
        };
}
