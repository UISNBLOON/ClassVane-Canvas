using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using ClassVaneCanvas.Controls;
using ClassVaneCanvas.Pptx;
using ClassVaneCanvas.Pptx.Model;

namespace ClassVaneCanvas.Presentation;

/// <summary>
/// 放映时的逐帧播放控制器：把「静态页图」升级为「按时间轴逐帧绘制的页面」。
///
/// 职责：
/// · 维护当前页 / 上一页（转场旧页快照）的节点树与各自的动画状态；
/// · 页面内动画走 <see cref="SlideAnimationPlayer"/>（进入 / 退出、单击组、with/after 时序）；
/// · 换页转场走 <see cref="SlideTransitionPlayer"/>（淡入淡出 / 推进 / 覆盖 / 擦除 / 拆分）；
/// · 用一个约 60fps 的 <see cref="DispatcherTimer"/> 在播放期间请求重绘
///   （通过 <see cref="InkCanvasState.RaiseInkChanged"/> 触发画布 <c>InvalidateVisual</c>）。
///
/// 关键约定：
/// · <b>画面永远按真实时钟求值</b>——每次 <see cref="Render"/> / <see cref="IsAnimating"/> 都会先把
///   内部时钟推进到「现在」，因此即使计时器被系统节流（后台窗口、无头环境），
///   画面也不会停在错误的帧；计时器只负责“请求重绘”，不负责“推进时间”。
/// · 绘制全部发生在<b>页面坐标系</b>：由 <see cref="InkCanvas"/> 统一压入缩放平移矩阵，
///   笔迹在该矩阵之后绘制，所以板书始终压在最上层、位置与缩放完全不受动画影响。
/// · Avalonia 的几何 / 位图创建必须在 UI 线程：<see cref="ShowPageAsync"/> 会把工作调度到 UI 线程，
///   其余入口（<see cref="Render"/>、计时器回调）天然在 UI 线程。
/// · 播放结束后不再有逐帧状态变化，<see cref="Render"/> 直接走整页静态渲染
///   （无动画页面传 <c>null</c> 状态表，与 <c>GetSlideAsync</c> 的静态页图完全同源、不会互相打架）。
/// </summary>
public sealed class SlidePlaybackController : ISlideFrameRenderer
{
    /// <summary>逐帧重绘间隔（约 60fps）。</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>判定「某一步骤已播完」的时间余量（毫秒），避免浮点误差让最后一步迟迟不结束。</summary>
    private const double StepCompletionEpsilonMs = 0.01d;

    /// <summary>动画时长缺省值（与 <see cref="SlideAnimationPlayer"/> 保持一致）。</summary>
    private const double DefaultStepDurationMs = 500d;

    private readonly PptxSlideSource _source;
    private readonly PptxRenderer _renderer;
    private readonly InkCanvasState _canvas;
    private readonly Stopwatch _clock = new();

    /// <summary>本页每个动画步骤的结束时刻（毫秒，相对<b>所属单击组激活时刻</b>），按时间轴顺序。</summary>
    private readonly List<double> _stepEnds = new();

    /// <summary>本页每个动画步骤所属的单击组序号（与 <see cref="_stepEnds"/> 一一对应）。</summary>
    private readonly List<int> _stepGroups = new();

    /// <summary>各单击组的激活时刻（毫秒，相对动画起点）：与 <see cref="SlideAnimationPlayer"/> 同步维护。</summary>
    private readonly List<double> _groupActivation = new();

    private DispatcherTimer? _timer;

    /// <summary>「当前页已经画出第一帧」的信号（兼容性提示要等字体替换记录登记完成）。</summary>
    private TaskCompletionSource<bool>? _firstFrame;

    /// <summary>快进量（毫秒）：<see cref="Advance"/> 把画面直接推到某一步骤结束时累加到这里。</summary>
    private double _clockOffsetMs;

    /// <summary>页内动画的起点相对「本页开始放映」的偏移（有转场时为转场时长）。</summary>
    private double _animationOffsetMs;

    private PptxSlide? _currentSlide;
    private PptxSlide? _previousSlide;
    private SlideAnimationPlayer? _animation;
    private SlideTransitionPlayer? _transition;
    private PptxAnimationFrame? _frame;
    private IReadOnlyDictionary<int, PptxNodeVisualState>? _currentStates;
    private IReadOnlyDictionary<int, PptxNodeVisualState>? _previousStates;
    private double _transitionDurationMs;
    private bool _transitionRunning;
    private bool _animationComplete = true;
    private bool _disposed;

    /// <summary>创建逐帧播放控制器。</summary>
    /// <param name="source">已初始化的自研 PPTX 后端（提供页面模型、动画 / 转场播放器与渲染器）。</param>
    /// <param name="canvas">画布状态：播放期间由它触发重绘。</param>
    public SlidePlaybackController(PptxSlideSource source, InkCanvasState canvas)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _renderer = source.Renderer;
    }

    /// <summary>当前已装载的页码（1 起）；尚未装载时为 0。</summary>
    public int CurrentPageIndex { get; private set; }

    /// <summary>是否已经装载了页面（未装载时画布应回落到静态页图 / 占位画面）。</summary>
    public bool HasContent => _currentSlide is not null;

    /// <summary>已渲染的帧数（诊断 / 验证用）。</summary>
    public int RenderedFrameCount { get; private set; }

    /// <summary>本页动画步骤总数（无动画为 0）。</summary>
    public int AnimationStepCount => _animation?.Timeline.Steps.Count ?? 0;

    /// <summary>本页剩余的单击组数（0 表示后续动画不再需要单击）。</summary>
    public int RemainingClickGroups
        => _animation is null ? 0 : Math.Max(0, _animation.ClickGroupCount - 1 - _animation.ActiveClickGroup);

    /// <summary>是否正在播放转场。</summary>
    public bool IsTransitionRunning
    {
        get
        {
            Refresh();
            return _transitionRunning;
        }
    }

    /// <summary>本页已放映的毫秒数（含转场时间）。</summary>
    public double PageElapsedMs
    {
        get
        {
            Refresh();
            return CurrentElapsedMs;
        }
    }

    /// <summary>
    /// 是否仍在播放（转场未结束，或页内动画还没播完）。
    /// 读取时会先把内部时钟推进到「现在」，因此它始终反映真实画面状态。
    /// </summary>
    public bool IsAnimating
    {
        get
        {
            if (_disposed || _currentSlide is null)
            {
                return false;
            }

            Refresh();
            return _transitionRunning || !_animationComplete;
        }
    }

    /// <summary>
    /// 是否还能推进动画（本页还有没播完的动画步骤）。
    /// 转场播放期间固定返回 <c>false</c>：此时一次点击的语义是「把转场放完」。
    /// </summary>
    public bool CanAdvance
    {
        get
        {
            if (_disposed || _currentSlide is null || _animation is null)
            {
                return false;
            }

            Refresh();
            return !_transitionRunning && !_animationComplete;
        }
    }

    /// <summary>
    /// 等待「当前页已经画出第一帧」。
    /// 兼容性提示需要等首帧渲染完成（字体替换记录在渲染文本时才登记）。
    /// </summary>
    /// <param name="timeoutMs">最长等待毫秒数（无头 / 未上屏时不会永远等下去）。</param>
    /// <returns>是否在超时前画出了第一帧。</returns>
    public async Task<bool> WaitForFirstFrameAsync(int timeoutMs)
    {
        var pending = _firstFrame;
        if (pending is null)
        {
            return false;
        }

        if (pending.Task.IsCompleted)
        {
            return true;
        }

        var timeout = Task.Delay(Math.Max(1, timeoutMs));
        var finished = await Task.WhenAny(pending.Task, timeout);
        return finished == pending.Task && pending.Task.IsCompleted;
    }

    /// <summary>切换到第 <paramref name="pageIndex"/> 页（1 起）。</summary>
    /// <param name="pageIndex">目标页码，越界自动收敛到有效范围。</param>
    /// <param name="playTransition">是否播放该页自带的转场（首次放映 / 直接跳页可传 <c>false</c>）。</param>
    public async Task ShowPageAsync(int pageIndex, bool playTransition)
    {
        if (_disposed)
        {
            return;
        }

        // Avalonia 的几何 / 位图创建必须在 UI 线程；调用方通常已在 UI 线程，这里只做兜底调度。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowPageCore(pageIndex, playTransition));
            return;
        }

        ShowPageCore(pageIndex, playTransition);
    }

    /// <summary>
    /// 推进一次（相当于老师点了一下）。
    ///
    /// 语义：
    /// 1) 还有「等待单击」的后续动画组：解锁下一组，让它按时间轴正常播放；
    /// 2) 已经在顺序播放的步骤：把画面快进到「下一个尚未播完的步骤」的结束时刻
    ///    （课堂上连点两下时，先把手头这一步放完，而不是直接翻页）；
    /// 3) 没有可推进的内容：返回 <c>false</c>（此时「下一页」应当翻页）。
    /// </summary>
    /// <returns>本次调用是否推进了动画。</returns>
    public bool Advance()
    {
        if (_disposed || _currentSlide is null || _animation is null)
        {
            return false;
        }

        Refresh();

        // 转场还没放完：本次点击语义是「放完转场」，不算推进动画。
        if (_transitionRunning)
        {
            return false;
        }

        if (_animation.CanAdvance)
        {
            // 解锁下一组：激活时刻取最近一次求值的时间（即“现在”）。
            _animation.Advance();
            RecordClickGroupActivation(_animation.ActiveClickGroup);
            Refresh();
            EnsureTimer();
            _canvas.RaiseInkChanged();
            return true;
        }

        var target = NextIncompleteStepEnd();
        if (target is null)
        {
            return false;
        }

        FastForwardTo(target.Value + StepCompletionEpsilonMs);
        Refresh();
        EnsureTimer();
        _canvas.RaiseInkChanged();
        return true;
    }

    /// <summary>清空播放状态并断开计时器（退出放映 / 换课件时调用）。</summary>
    public void Reset()
    {
        DisposeTimer();

        _clock.Reset();
        _clockOffsetMs = 0d;
        _animationOffsetMs = 0d;
        _transitionDurationMs = 0d;
        _transitionRunning = false;
        _animationComplete = true;

        _currentSlide = null;
        _previousSlide = null;
        _animation = null;
        _transition = null;
        _frame = null;
        _currentStates = null;
        _previousStates = null;
        _firstFrame = null;
        _stepEnds.Clear();
        _stepGroups.Clear();
        _groupActivation.Clear();

        CurrentPageIndex = 0;
        RenderedFrameCount = 0;
    }

    /// <summary>永久停用本控制器（内部会断开计时器）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reset();
    }

    /// <inheritdoc />
    public void Render(DrawingContext context, Rect pageRect)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_disposed || _currentSlide is null || pageRect.Width <= 0d || pageRect.Height <= 0d)
        {
            return;
        }

        // 首帧信号：字体替换记录是在真正渲染文本时登记的，兼容性提示要等它。
        _firstFrame?.TrySetResult(true);

        // 关键：先按真实时钟求值，再画。这样即使计时器一次都没触发，画面也是“此刻应有的样子”。
        Refresh();
        RenderedFrameCount++;

        var page = _currentSlide.PageSize;
        var scaleX = page.Width > 0d ? pageRect.Width / page.Width : 1d;
        var scaleY = page.Height > 0d ? pageRect.Height / page.Height : 1d;
        var scaled = Math.Abs(scaleX - 1d) > 1e-9 || Math.Abs(scaleY - 1d) > 1e-9;
        var translated = Math.Abs(pageRect.X) > 1e-9 || Math.Abs(pageRect.Y) > 1e-9;

        // 页面坐标系绘制 + 裁剪到页面矩形：转场时两页会平移出画面，必须裁掉，
        // 否则「推入 / 覆盖」会把内容画到画布留白上。
        using (context.PushClip(pageRect))
        {
            if (scaled || translated)
            {
                var matrix = Matrix.CreateScale(scaleX, scaleY) * Matrix.CreateTranslation(pageRect.X, pageRect.Y);
                using (context.PushTransform(matrix))
                {
                    DrawFrame(context);
                }
            }
            else
            {
                DrawFrame(context);
            }
        }
    }

    // ---------------------------------------------------------------- 装载页面

    /// <summary>真正的装载（调用方保证在 UI 线程）。</summary>
    private void ShowPageCore(int pageIndex, bool playTransition)
    {
        var slide = _source.GetSlideModel(pageIndex);
        if (slide is null)
        {
            Reset();
            _canvas.RaiseInkChanged();
            return;
        }

        var index = Math.Max(1, slide.Index);

        // 旧页快照：换页瞬间把上一页「当前看到的样子」（含已播动画的中间态）冻结下来，
        // 供转场期间绘制；同一页重复装载不产生快照。
        var keepPrevious = _currentSlide is not null && CurrentPageIndex != index;
        _previousSlide = keepPrevious ? _currentSlide : null;
        _previousStates = keepPrevious ? _currentStates : null;

        _currentSlide = slide;
        CurrentPageIndex = index;
        _firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 第一组动画随页面自动开始；单击组由 Advance() 解锁。
        _animation = _source.CreateAnimationPlayer(index);
        ComputeStepEnds(slide, _stepEnds, _stepGroups);

        // 与播放器同步：Begin(autoStartFirstGroup: true) 时第 0 组在动画起点即被激活。
        _groupActivation.Clear();
        if (_animation is { Timeline.ClickGroupCount: > 0 })
        {
            _groupActivation.Add(0d);
        }

        _transition = null;
        _transitionRunning = false;
        _transitionDurationMs = 0d;
        _animationOffsetMs = 0d;

        if (playTransition && _previousSlide is not null)
        {
            var player = _source.CreateTransitionPlayer(index);
            if (player is { IsRunning: true })
            {
                _transition = player;
                _transitionDurationMs = player.DurationMs;

                // 转场未结束前画面属于转场；页内动画从转场结束时刻开始。
                _transitionRunning = true;
                _animationOffsetMs = _transitionDurationMs;
            }
        }

        _clockOffsetMs = 0d;
        _clock.Restart();

        Refresh();
        _canvas.RaiseInkChanged();

        if (IsAnimating)
        {
            EnsureTimer();
        }
    }

    /// <summary>
    /// 按时间轴推算每个动画步骤的结束时刻（相对所属单击组的激活时刻）。
    /// <see cref="SlideAnimationPlayer"/> 只公开总时长与单击组，不公开每步时序，
    /// 这里按与内核一致的规则（withEffect 与上一项同时开始、afterEffect 等上一项结束）重算，仅用于快进定位。
    /// </summary>
    private static void ComputeStepEnds(PptxSlide slide, List<double> ends, List<int> groups)
    {
        ends.Clear();
        groups.Clear();

        var steps = slide.Animation.Steps;
        if (steps.Count == 0)
        {
            return;
        }

        var groupCursor = 0d;
        var previousStart = 0d;
        var currentGroup = -1;

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.ClickGroupIndex != currentGroup)
            {
                currentGroup = step.ClickGroupIndex;
                groupCursor = 0d;
                previousStart = 0d;
            }

            var duration = step.DurationMs > 0d ? step.DurationMs : DefaultStepDurationMs;
            var start = step.Trigger == PptxAnimationTrigger.WithPrevious
                ? previousStart + step.DelayMs
                : groupCursor + step.DelayMs;

            previousStart = start;
            groupCursor = start + duration;
            ends.Add(groupCursor);
            groups.Add(currentGroup);
        }
    }

    /// <summary>记录某个单击组的激活时刻（与动画播放器内部状态保持同步）。</summary>
    private void RecordClickGroupActivation(int groupIndex)
    {
        if (groupIndex < 0)
        {
            return;
        }

        while (_groupActivation.Count <= groupIndex)
        {
            _groupActivation.Add(0d);
        }

        _groupActivation[groupIndex] = Math.Max(0d, CurrentElapsedMs - _animationOffsetMs);
    }

    // ---------------------------------------------------------------- 时间

    /// <summary>本页已放映毫秒数（真实时钟 + 快进量）。</summary>
    private double CurrentElapsedMs
        => _clockOffsetMs + (_clock.IsRunning ? _clock.Elapsed.TotalMilliseconds : 0d);

    /// <summary>把画面快进到指定的「本页已放映毫秒数」。</summary>
    private void FastForwardTo(double elapsedMs)
    {
        var delta = elapsedMs - CurrentElapsedMs;
        if (delta > 0d)
        {
            _clockOffsetMs += delta;
        }
    }

    /// <summary>
    /// 按真实时钟推进一步：结束已完成的转场、求出当前动画帧。
    /// 所有对外成员在读取状态前都会先调用它。
    /// </summary>
    private void Refresh()
    {
        if (_disposed || _currentSlide is null)
        {
            _animationComplete = true;
            _transitionRunning = false;
            _frame = null;
            return;
        }

        var elapsed = CurrentElapsedMs;

        if (_transitionRunning && elapsed >= _transitionDurationMs)
        {
            _transitionRunning = false;

            // 转场结束：旧页快照不再需要，及时松手（页面模型本身仍在文档 / 页缓存里）。
            _previousSlide = null;
            _previousStates = null;
        }

        if (_animation is null)
        {
            _frame = null;
            _animationComplete = true;
            return;
        }

        var animationElapsed = Math.Max(0d, elapsed - _animationOffsetMs);
        _frame = _animation.Evaluate(animationElapsed);
        _animationComplete = _frame.IsComplete;

        // 无动画页面：状态表保持为 null，渲染走「静态整页」路径，与 GetSlideAsync 的页图同源。
        _currentStates = _animation.HasAnimation ? _frame.States : null;
    }

    /// <summary>下一个尚未播完的动画步骤的结束时刻（相对动画起点）；都已播完返回 <c>null</c>。</summary>
    private double? NextIncompleteStepEnd()
    {
        var elapsed = Math.Max(0d, CurrentElapsedMs - _animationOffsetMs);
        double? best = null;

        for (var index = 0; index < _stepEnds.Count; index++)
        {
            var group = _stepGroups[index];
            if (group < 0 || group >= _groupActivation.Count)
            {
                // 该组还没被解锁：本轮不参与快进（调用方会先走「解锁下一组」分支）。
                continue;
            }

            var end = _groupActivation[group] + _stepEnds[index];
            if (end > elapsed + StepCompletionEpsilonMs && (best is null || end < best.Value))
            {
                best = end;
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- 渲染

    /// <summary>绘制当前帧（调用方已压入页面矩阵并裁到页面矩形）。</summary>
    private void DrawFrame(DrawingContext context)
    {
        var slide = _currentSlide;
        if (slide is null)
        {
            return;
        }

        try
        {
            if (_transitionRunning && _previousSlide is not null && _transition is not null)
            {
                var progress = _transitionDurationMs <= 0d
                    ? 1d
                    : Math.Clamp(CurrentElapsedMs / _transitionDurationMs, 0d, 1d);

                var frame = _transition.Evaluate(progress);

                // 旧页（快照状态）与新页（当前动画帧）各画一次，透明度 / 位移 / 裁剪由转场给出。
                _renderer.RenderTransition(context, _previousSlide, slide, frame, _previousStates, _currentStates);
                return;
            }

            // 播放结束 / 无动画：整页静态渲染（状态表为 null 时与静态页图完全一致）。
            _renderer.RenderSlide(context, slide, _currentStates);
        }
        catch (Exception)
        {
            // 单帧渲染失败不能把整块画布搞崩（损坏课件已在解析阶段降级，这里是最后一道防线）。
        }
    }

    // ---------------------------------------------------------------- 计时器

    /// <summary>启动（或复用）逐帧计时器。</summary>
    private void EnsureTimer()
    {
        if (_disposed)
        {
            return;
        }

        if (_timer is null)
        {
            // 渲染优先级：与 Avalonia 的渲染循环同档，动画更跟手。
            _timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, OnTick);
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>计时器回调：只负责请求重绘，时间由真实时钟给出。</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            DisposeTimer();
            return;
        }

        Refresh();
        _canvas.RaiseInkChanged();

        if (!IsAnimating)
        {
            // 播放结束：停表，画面回到静态整页（最后一次重绘已在上面发出）。
            DisposeTimer();
        }
    }

    /// <summary>停止并彻底断开计时器（避免 DispatcherTimer 被调度器长期持有）。</summary>
    private void DisposeTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }
}
