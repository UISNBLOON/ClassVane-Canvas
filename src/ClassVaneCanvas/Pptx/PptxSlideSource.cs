using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassVaneCanvas.Pptx.Model;
using ClassVaneCanvas.Presentation;
using ClassVaneCanvas.Storage;

namespace ClassVaneCanvas.Pptx;

/// <summary>
/// 自研 PPTX 放映后端：把「解析 + 渲染」内核接进现有的 <see cref="ISlideSource"/> 抽象。
///
/// 行为约定：
/// · <see cref="InitializeAsync"/> 只用本内核解析 <c>.pptx</c>，<b>不调用任何外部进程</b>；
///   非 pptx（含老版二进制 <c>.ppt</c>）、文件损坏、路径不存在等一律返回 <c>false</c> 并且不抛异常，
///   失败原因放在 <see cref="LastError"/>，界面可据此提示「保持批注功能可用」；
/// · <see cref="GetSlideAsync"/> 用 <see cref="RenderTargetBitmap"/> 把某一页画成位图，按页缓存；
/// · Avalonia 的位图 / 几何创建必须在 UI 线程：已经在 UI 线程时同步渲染，
///   否则用 <see cref="Dispatcher.UIThread"/> 调度过去，避免「非 UI 线程创建渲染资源」崩溃。
/// </summary>
public sealed class PptxSlideSource : ISlideSource
{
    /// <summary>默认位图倍率（页面逻辑尺寸 × 倍率 = 位图像素尺寸）。</summary>
    public const double DefaultRenderScale = 2d;

    /// <summary>位图缓存上限（页数），超出后按最久未用淘汰。</summary>
    private const int MaxCachedPages = 12;

    private readonly AppPaths _paths;
    private readonly PptxParser _parser;
    private readonly PptxRenderer _renderer;
    private readonly Dictionary<int, Bitmap> _pageCache = new();
    private readonly List<int> _recentPages = new();
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    /// <summary>创建 PPTX 放映后端。</summary>
    /// <param name="paths">本机数据目录（媒体导出走 <see cref="AppPaths.SlideCacheDirectory"/>）。</param>
    /// <param name="parser">解析器；<c>null</c> 时新建。</param>
    /// <param name="renderer">渲染器；<c>null</c> 时新建。</param>
    public PptxSlideSource(AppPaths paths, PptxParser? parser = null, PptxRenderer? renderer = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _parser = parser ?? new PptxParser();
        _renderer = renderer ?? new PptxRenderer();
    }

    /// <summary>渲染倍率（位图像素尺寸 = 页面逻辑尺寸 × 倍率）；默认 2。</summary>
    public double RenderScale { get; set; } = DefaultRenderScale;

    /// <inheritdoc />
    public string DisplayName => IsAvailable
        ? $"自研 PPTX 内核（{SlideCount} 页）"
        : "自研 PPTX 内核（未加载课件）";

    /// <inheritdoc />
    public bool IsAvailable { get; private set; }

    /// <inheritdoc />
    public int SlideCount { get; private set; } = 1;

    /// <inheritdoc />
    public Size PageSize { get; private set; } = new(1280, 720);

    /// <summary>最近一次初始化的失败原因（中文）；成功时为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>解析成功后的文档模型。</summary>
    public PptxDocument? Document { get; private set; }

    /// <summary>已加载的课件路径。</summary>
    public string? CoursewarePath { get; private set; }

    /// <summary>媒体外放服务（视频 / 音频交给系统默认播放器）。</summary>
    public IVideoPlaybackService PlaybackService { get; set; } = ExternalMediaPlaybackService.Instance;

    /// <summary>渲染器（字体替换记录、位图缓存都在这里）。</summary>
    public PptxRenderer Renderer => _renderer;

    /// <summary>解析器。</summary>
    public PptxParser Parser => _parser;

    /// <summary>字体替换记录（请求字体 → 实际使用的字体）。</summary>
    public IReadOnlyDictionary<string, string> FontSubstitutions => _renderer.FontSubstitutions;

    /// <summary>
    /// 全部降级提示：文档级 + 各页级 + 字体缺失提示（去重后按出现顺序返回）。
    /// </summary>
    public IReadOnlyList<string> UnsupportedFeatures
    {
        get
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string message)
            {
                if (seen.Add(message))
                {
                    result.Add(message);
                }
            }

            if (Document is { } document)
            {
                foreach (var message in document.UnsupportedFeatures)
                {
                    Add(message);
                }

                foreach (var slide in document.Slides)
                {
                    foreach (var message in slide.UnsupportedFeatures)
                    {
                        Add(message);
                    }
                }
            }

            foreach (var pair in _renderer.FontSubstitutions)
            {
                Add($"字体 {pair.Key} 缺失，已回退为 {pair.Value}");
            }

            return result;
        }
    }

    /// <summary>
    /// 加载课件。任何失败都只返回 <c>false</c>（老版 <c>.ppt</c>、损坏 zip、非 pptx、无权限……），绝不抛异常。
    /// </summary>
    /// <param name="coursewarePath">课件路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task<bool> InitializeAsync(string coursewarePath, CancellationToken cancellationToken = default)
    {
        Reset();

        if (string.IsNullOrWhiteSpace(coursewarePath))
        {
            LastError = "课件路径为空。";
            return false;
        }

        try
        {
            var options = new PptxParseOptions { MediaCacheDirectory = _paths.SlideCacheDirectory };
            var document = await Task.Run(
                () => _parser.Parse(coursewarePath, options),
                cancellationToken).ConfigureAwait(false);

            Document = document;
            CoursewarePath = coursewarePath;
            SlideCount = Math.Max(1, document.Slides.Count);
            PageSize = document.PageSize;
            IsAvailable = true;
            LastError = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "已取消加载。";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsAvailable = false;
            SlideCount = 1;
            PageSize = new Size(1280, 720);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IImage?> GetSlideAsync(int pageIndex, CancellationToken cancellationToken = default)
        => await GetSlideBitmapAsync(pageIndex, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 取第 <paramref name="pageIndex"/> 页（1 起）的位图；不可用或越界返回 <c>null</c>。
    /// </summary>
    public async Task<Bitmap?> GetSlideBitmapAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || Document is null)
        {
            return null;
        }

        var index = Math.Clamp(pageIndex, 1, Math.Max(1, Document.Slides.Count));

        lock (_pageCache)
        {
            if (_pageCache.TryGetValue(index, out var cached))
            {
                Touch(index);
                return cached;
            }
        }

        await _renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_pageCache)
            {
                if (_pageCache.TryGetValue(index, out var cached))
                {
                    Touch(index);
                    return cached;
                }
            }

            var bitmap = await RenderPageAsync(index, cancellationToken).ConfigureAwait(false);
            if (bitmap is null)
            {
                return null;
            }

            lock (_pageCache)
            {
                _pageCache[index] = bitmap;
                Touch(index);
                TrimCache();
            }

            return bitmap;
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>取某页的模型（动画 / 转场播放器由此驱动）。</summary>
    public PptxSlide? GetSlideModel(int pageIndex)
    {
        if (Document is null || Document.Slides.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp(pageIndex, 1, Document.Slides.Count);
        return Document.Slides[index - 1];
    }

    /// <summary>为某页创建动画播放器（第一组动画随页面自动开始）。</summary>
    public SlideAnimationPlayer? CreateAnimationPlayer(int pageIndex)
    {
        var slide = GetSlideModel(pageIndex);
        return slide is null ? null : new SlideAnimationPlayer(slide);
    }

    /// <summary>为某页创建转场播放器。</summary>
    public SlideTransitionPlayer? CreateTransitionPlayer(int pageIndex)
    {
        var slide = GetSlideModel(pageIndex);
        if (slide is null)
        {
            return null;
        }

        var player = new SlideTransitionPlayer();
        player.Begin(slide);
        return player;
    }

    /// <summary>播放页面内的一段媒体（视频 / 音频），交给系统默认播放器。</summary>
    /// <param name="media">媒体部件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public Task<bool> PlayMediaAsync(PptxMediaFile media, CancellationToken cancellationToken = default)
        => media?.LocalPath is { Length: > 0 } path
            ? PlaybackService.PlayExternalAsync(path, cancellationToken)
            : Task.FromResult(false);

    /// <summary>清空位图缓存并释放位图。</summary>
    public void ClearCache()
    {
        lock (_pageCache)
        {
            foreach (var bitmap in _pageCache.Values)
            {
                bitmap.Dispose();
            }

            _pageCache.Clear();
            _recentPages.Clear();
        }

        _renderer.ClearCaches();
    }

    /// <summary>重置为「未加载」状态。</summary>
    private void Reset()
    {
        ClearCache();
        IsAvailable = false;
        Document = null;
        CoursewarePath = null;
        SlideCount = 1;
        PageSize = new Size(1280, 720);
    }

    /// <summary>在 UI 线程上渲染一页。</summary>
    private async Task<Bitmap?> RenderPageAsync(int pageIndex, CancellationToken cancellationToken)
    {
        var slide = GetSlideModel(pageIndex);
        if (slide is null)
        {
            return null;
        }

        // 创建 RenderTargetBitmap / 几何必须发生在 UI 线程。
        if (Dispatcher.UIThread.CheckAccess())
        {
            return RenderCore(slide);
        }

        return await Dispatcher.UIThread.InvokeAsync(() => RenderCore(slide));
    }

    /// <summary>真正的渲染（调用方保证在 UI 线程）。</summary>
    private Bitmap? RenderCore(PptxSlide slide)
    {
        try
        {
            var scale = RenderScale is > 0 and <= 8 ? RenderScale : DefaultRenderScale;
            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Round(slide.PageSize.Width * scale)),
                Math.Max(1, (int)Math.Round(slide.PageSize.Height * scale)));

            // 位图 DPI 直接设成 96×倍率：Avalonia 的绘图上下文会按 Dpi/96 自动缩放，
            // 因此这里按页面逻辑坐标绘制即可得到 2 倍像素，且位图的 DIP 尺寸仍是 1280×720。
            // 切勿再额外 PushTransform(scale)：那会与 DPI 缩放叠加成 4 倍（形状会被画到画布外）。
            var target = new RenderTargetBitmap(pixelSize, new Vector(96d * scale, 96d * scale));
            using (var context = target.CreateDrawingContext())
            {
                _renderer.RenderSlide(context, slide);
            }

            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>更新最近使用顺序。</summary>
    private void Touch(int pageIndex)
    {
        _recentPages.Remove(pageIndex);
        _recentPages.Add(pageIndex);
    }

    /// <summary>按上限淘汰最久未使用的页。</summary>
    private void TrimCache()
    {
        while (_recentPages.Count > MaxCachedPages)
        {
            var oldest = _recentPages[0];
            _recentPages.RemoveAt(0);
            if (_pageCache.Remove(oldest, out var bitmap))
            {
                bitmap.Dispose();
            }
        }
    }
}
