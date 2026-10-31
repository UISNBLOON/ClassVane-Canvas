using Avalonia;
using ClassVaneCanvas.Pptx.Model;

namespace ClassVaneCanvas.Pptx;

/// <summary>
/// 一次动画求值的结果：每个参与动画的节点对应一个 <see cref="PptxNodeVisualState"/>。
/// 未参与动画（或本轮不需要变换）的节点不在字典里，取值时按恒等变换处理。
/// </summary>
/// <param name="States">形状 id → 视觉状态。</param>
/// <param name="ElapsedMs">本次求值使用的「页面已放映毫秒数」。</param>
/// <param name="ActiveClickGroup">当前已解锁的最大单击组序号（-1 表示一组都还没开始）。</param>
/// <param name="IsComplete">是否全部动画都播完且没有待单击的后续组。</param>
public sealed record PptxAnimationFrame(
    IReadOnlyDictionary<int, PptxNodeVisualState> States,
    double ElapsedMs,
    int ActiveClickGroup,
    bool IsComplete)
{
    /// <summary>恒等帧（没有任何动画生效）。</summary>
    public static readonly PptxAnimationFrame Identity =
        new(new Dictionary<int, PptxNodeVisualState>(), 0d, -1, true);

    /// <summary>取某节点的状态；缺失时返回恒等状态。</summary>
    public PptxNodeVisualState StateFor(int nodeId)
        => States.TryGetValue(nodeId, out var state) ? state : PptxNodeVisualState.Identity;
}

/// <summary>
/// 进入 / 退出动画播放器。
///
/// 语义：
/// · <see cref="Evaluate"/> 的输入是「页面开始放映至今的毫秒数」，与是否单击无关；
/// · 需要单击触发的组由 <see cref="Advance"/> 解锁，解锁时刻取最近一次 <see cref="Evaluate"/> 的时间；
/// · <c>withEffect</c>（与上一项同时）与上一项同时开始，<c>afterEffect</c>（上一项之后）在上一项结束后开始，
///   两者都不需要单击；
/// · 处于等待状态的进入动画节点按「起始状态」输出（通常是全透明 / 起始位移），
///   等待中的退出动画节点保持恒等（仍然可见）；
/// · 不受支持的效果（路径动画、复杂强调、SmartArt 动画）按恒等变换处理。
/// </summary>
public sealed class SlideAnimationPlayer
{
    /// <summary>飞入 / 缩放等效果的默认时长（毫秒），XML 未给出时使用。</summary>
    private const double DefaultDurationMs = 500d;

    private readonly Dictionary<int, NodeGeometry> _nodeGeometry = new();
    private readonly Dictionary<int, double> _stepStart = new();
    private readonly Dictionary<int, double> _groupActivation = new();
    private PptxSlide _slide = new();
    private int _activeClickGroup = -1;
    private double _lastElapsed;

    /// <summary>当前页面的动画时间轴。</summary>
    public PptxAnimationTimeline Timeline { get; private set; } = PptxAnimationTimeline.Empty;

    /// <summary>单击组总数（含自动开始的第一组）。</summary>
    public int ClickGroupCount => Timeline.ClickGroupCount;

    /// <summary>是否还有需要单击才能继续的动画。</summary>
    public bool CanAdvance => _activeClickGroup < Timeline.ClickGroupCount - 1;

    /// <summary>当前已解锁的最大单击组序号（-1 表示尚未开始）。</summary>
    public int ActiveClickGroup => _activeClickGroup;

    /// <summary>本页是否存在动画。</summary>
    public bool HasAnimation => Timeline.Steps.Count > 0;

    /// <summary>全部动画播完所需时间（毫秒，不含等待单击的时间）。</summary>
    public double TotalDurationMs { get; private set; }

    /// <summary>创建播放器（不绑定页面，需再调用 <see cref="Begin"/>）。</summary>
    public SlideAnimationPlayer()
    {
    }

    /// <summary>创建播放器并立即绑定页面。</summary>
    /// <param name="slide">页面模型。</param>
    /// <param name="autoStartFirstGroup">是否让第一组动画随页面出现自动开始（PowerPoint 常见行为）。</param>
    public SlideAnimationPlayer(PptxSlide slide, bool autoStartFirstGroup = true)
        => Begin(slide, autoStartFirstGroup);

    /// <summary>绑定页面并从头开始。</summary>
    /// <param name="slide">页面模型。</param>
    /// <param name="autoStartFirstGroup">是否让第一组动画随页面出现自动开始。</param>
    public void Begin(PptxSlide slide, bool autoStartFirstGroup = true)
    {
        _slide = slide ?? new PptxSlide();
        Timeline = _slide.Animation;
        _nodeGeometry.Clear();
        _stepStart.Clear();
        _groupActivation.Clear();
        _activeClickGroup = autoStartFirstGroup && Timeline.ClickGroupCount > 0 ? 0 : -1;
        _lastElapsed = 0d;

        if (_activeClickGroup >= 0)
        {
            _groupActivation[0] = 0d;
        }

        foreach (var node in _slide.Flatten())
        {
            _nodeGeometry[node.Id] = new NodeGeometry(node.Rect, _slide.PageSize);
        }

        ComputeStepStarts();
    }

    /// <summary>重新从头播放（保留绑定的页面）。</summary>
    public void Reset(bool autoStartFirstGroup = true) => Begin(_slide, autoStartFirstGroup);

    /// <summary>
    /// 推进到下一个单击组（相当于用户点了一下）。
    /// </summary>
    /// <returns>解锁成功返回 <c>true</c>；已经没有后续组返回 <c>false</c>。</returns>
    public bool Advance()
    {
        if (!CanAdvance)
        {
            return false;
        }

        _activeClickGroup++;
        _groupActivation[_activeClickGroup] = _lastElapsed;
        return true;
    }

    /// <summary>
    /// 按「页面已放映毫秒数」求值。
    /// </summary>
    /// <param name="elapsedMs">页面开始放映至今的毫秒数（负值按 0 处理）。</param>
    /// <returns>本帧各节点的视觉状态。</returns>
    public PptxAnimationFrame Evaluate(double elapsedMs)
    {
        elapsedMs = Math.Max(0d, elapsedMs);
        _lastElapsed = elapsedMs;

        if (Timeline.Steps.Count == 0)
        {
            return PptxAnimationFrame.Identity with { ElapsedMs = elapsedMs };
        }

        var states = new Dictionary<int, PptxNodeVisualState>(Timeline.Steps.Count);
        var allComplete = true;

        for (var index = 0; index < Timeline.Steps.Count; index++)
        {
            var step = Timeline.Steps[index];
            var activated = _groupActivation.TryGetValue(step.ClickGroupIndex, out var activation);

            if (!activated)
            {
                allComplete = false;

                // 等待中的进入动画先按起始状态显示（不可见 / 起始位移）；等待中的退出动画保持原样。
                if (!step.IsExit && !states.ContainsKey(step.TargetId))
                {
                    states[step.TargetId] = EvaluateStep(step, 0d);
                }

                continue;
            }

            var start = activation + (_stepStart.TryGetValue(index, out var offset) ? offset : 0d);
            var duration = Math.Max(1d, step.DurationMs);
            var progress = Math.Clamp((elapsedMs - start) / duration, 0d, 1d);
            if (progress < 1d)
            {
                allComplete = false;
            }

            states[step.TargetId] = EvaluateStep(step, progress);
        }

        return new PptxAnimationFrame(states, elapsedMs, _activeClickGroup, allComplete && !CanAdvance);
    }

    /// <summary>计算每个步骤在所属单击组内的时间偏移。</summary>
    private void ComputeStepStarts()
    {
        var groupCursor = 0d;
        var previousStart = 0d;
        var currentGroup = -1;
        var total = 0d;

        for (var index = 0; index < Timeline.Steps.Count; index++)
        {
            var step = Timeline.Steps[index];
            if (step.ClickGroupIndex != currentGroup)
            {
                currentGroup = step.ClickGroupIndex;
                groupCursor = 0d;
                previousStart = 0d;
            }

            var duration = step.DurationMs > 0 ? step.DurationMs : DefaultDurationMs;
            var start = step.Trigger == PptxAnimationTrigger.WithPrevious
                ? previousStart + step.DelayMs
                : groupCursor + step.DelayMs;

            _stepStart[index] = start;
            previousStart = start;
            groupCursor = start + duration;
            total = Math.Max(total, groupCursor);
        }

        TotalDurationMs = total;
    }

    /// <summary>按进度求单个步骤的视觉状态。</summary>
    private PptxNodeVisualState EvaluateStep(PptxAnimationStep step, double progress)
    {
        var p = Math.Clamp(progress, 0d, 1d);

        switch (step.Effect)
        {
            case PptxAnimationEffect.Unsupported:
                return PptxNodeVisualState.Identity;

            case PptxAnimationEffect.Appear:
                // 出现 = 瞬时可见。进度为 0 时仍算「尚未出现」（全透明），
                // 这样「等待中的进入动画」与「已开始的第一瞬间」都不会提前泄题。
                return step.IsExit
                    ? (p >= 1d ? new PptxNodeVisualState(0d, 0d, 0d, 1d, 1d) : PptxNodeVisualState.Identity)
                    : new PptxNodeVisualState(p > 0d ? 1d : 0d, 0d, 0d, 1d, 1d);

            case PptxAnimationEffect.Fade:
                return new PptxNodeVisualState(step.IsExit ? 1d - p : p, 0d, 0d, 1d, 1d);

            case PptxAnimationEffect.Fly:
            {
                var eased = EaseInOut(p);
                var distance = FlyDistance(step);
                var factor = step.IsExit ? eased : 1d - eased;
                var (dx, dy) = DirectionVector(step.Direction, distance.X, distance.Y);
                var opacity = step.IsExit
                    ? Math.Clamp(1d - p / 0.3d, 0d, 1d)
                    : Math.Clamp(p / 0.3d, 0d, 1d);
                return new PptxNodeVisualState(opacity, dx * factor, dy * factor, 1d, 1d);
            }

            case PptxAnimationEffect.Zoom:
            {
                var eased = EaseInOut(p);
                var scale = step.IsExit ? 1d - 0.95d * eased : 0.05d + 0.95d * eased;
                var opacity = step.IsExit ? Math.Clamp(1d - p, 0d, 1d) : Math.Clamp(p, 0d, 1d);
                return new PptxNodeVisualState(opacity, 0d, 0d, Math.Max(0.01d, scale), 1d);
            }

            case PptxAnimationEffect.Wipe:
            {
                var direction = step.Direction == PptxAnimationDirection.None
                    ? PptxAnimationDirection.FromLeft
                    : step.Direction;
                var clip = step.IsExit ? 1d - p : p;
                return new PptxNodeVisualState(1d, 0d, 0d, 1d, clip, direction);
            }

            default:
                return PptxNodeVisualState.Identity;
        }
    }

    /// <summary>飞入 / 飞出的起始位移（把节点完整推到页面外）。</summary>
    private (double X, double Y) FlyDistance(PptxAnimationStep step)
    {
        if (!_nodeGeometry.TryGetValue(step.TargetId, out var geometry))
        {
            geometry = new NodeGeometry(new Rect(0, 0, 200, 100), _slide.PageSize);
        }

        const double margin = 24d;
        var left = geometry.Rect.X + geometry.Rect.Width + margin;
        var right = geometry.PageSize.Width - geometry.Rect.X + margin;
        var top = geometry.Rect.Y + geometry.Rect.Height + margin;
        var bottom = geometry.PageSize.Height - geometry.Rect.Y + margin;

        return step.Direction switch
        {
            PptxAnimationDirection.FromLeft => (left, 0d),
            PptxAnimationDirection.FromRight => (-right, 0d),
            PptxAnimationDirection.FromTop => (0d, top),
            PptxAnimationDirection.FromBottom => (0d, -bottom),
            PptxAnimationDirection.FromTopLeft => (left, top),
            PptxAnimationDirection.FromTopRight => (-right, top),
            PptxAnimationDirection.FromBottomLeft => (left, -bottom),
            PptxAnimationDirection.FromBottomRight => (-right, -bottom),
            _ => (left, 0d),
        };
    }

    /// <summary>方向对应的位移符号。</summary>
    private static (double X, double Y) DirectionVector(PptxAnimationDirection direction, double x, double y)
        => direction switch
        {
            PptxAnimationDirection.FromLeft => (-x, 0d),
            PptxAnimationDirection.FromRight => (x, 0d),
            PptxAnimationDirection.FromTop => (0d, -y),
            PptxAnimationDirection.FromBottom => (0d, y),
            PptxAnimationDirection.FromTopLeft => (-x, -y),
            PptxAnimationDirection.FromTopRight => (x, -y),
            PptxAnimationDirection.FromBottomLeft => (-x, y),
            PptxAnimationDirection.FromBottomRight => (x, y),
            _ => (-x, 0d),
        };

    /// <summary>三次缓入缓出。</summary>
    private static double EaseInOut(double t)
        => t < 0.5d ? 4d * t * t * t : 1d - Math.Pow(-2d * t + 2d, 3d) / 2d;

    /// <summary>节点几何（用于计算飞出页面的位移）。</summary>
    private readonly record struct NodeGeometry(Rect Rect, Size PageSize);
}
