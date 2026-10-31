using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Controls;
using ClassVaneCanvas.Export;
using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Models.Ink;
using ClassVaneCanvas.Pptx;
using ClassVaneCanvas.Presentation;
using ClassVaneCanvas.Services;
using ClassVaneCanvas.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassVaneCanvas.ViewModels.Presentation;

/// <summary>缩略图导航项。</summary>
public sealed partial class SlideThumbnailViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _pageIndex;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _hasAnnotations;

    [ObservableProperty]
    private IImage? _image;

    /// <summary>页码文本。</summary>
    public string Label => PageIndex.ToString();
}

/// <summary>
/// 放映视图模型：把课件、批注、笔迹引擎、播放进度、导出串起来。
///
/// 三条数据链严格分开：
/// · 幻灯片内容：只读，绝不修改原始 PPT 文件；
/// · 板书批注：全部写进外置 <c>.cvcanno</c>（按 PPT 的 SHA256 绑定），自动保存；
/// · 播放进度：只写本机 SQLite，不跨设备同步、不写进批注文件。
/// </summary>
public sealed partial class PresentationViewModel : ViewModelBase
{
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ICoursewareIdentity _identity;
    private readonly IAnnotationStore _annotationStore;
    private readonly IPlaybackProgressStore _progressStore;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _fileDialogs;
    private readonly IAnnotatedExportService _export;
    private readonly DispatcherTimer _autosaveTimer;

    private AnnotationDocument? _document;
    private ISlideSource? _slideSource;
    private SlidePlaybackController? _playback;
    private string _coursewarePath = string.Empty;
    private string _coursewareHash = string.Empty;
    private bool _isDirty;

    public PresentationViewModel(
        AppPaths paths,
        ISettingsService settings,
        InkEngineSettings inkSettings,
        ICoursewareIdentity identity,
        IAnnotationStore annotationStore,
        IPlaybackProgressStore progressStore,
        IDialogService dialogs,
        IFileDialogService fileDialogs,
        IAnnotatedExportService export)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(inkSettings);
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _annotationStore = annotationStore ?? throw new ArgumentNullException(nameof(annotationStore));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _export = export ?? throw new ArgumentNullException(nameof(export));

        Canvas = new InkCanvasState
        {
            DeviceId = _settings.Current.DeviceId,
            Tool = InkTool.HardPen,
            ColorArgb = 0xFFE53935,

            // 与「书写设置」页共用同一个笔迹引擎参数实例：调完手感立即生效
            Settings = inkSettings,
        };
        _settings.Current.ApplyTo(Canvas.Settings);

        // 设置变化（例如滑杆调整）后立即作用于画布并重绘
        _settings.Changed += (_, _) =>
        {
            _settings.Current.ApplyTo(Canvas.Settings);
            Canvas.RaiseInkChanged();
        };

        Canvas.StrokeCompleted += OnStrokeCompleted;
        Canvas.EraseRequested += OnEraseRequested;
        Canvas.PageTurnRequested += (_, delta) => TurnPage(delta);
        Canvas.FingerInkBlocked += (_, _) => StatusText = "手指默认用于手势翻页 / 缩放；如需手指书写请在「书写设置」中开启。";
        Canvas.CanvasTransformChanged += (_, _) => ZoomText = $"{Canvas.Zoom * 100:0}%";

        History.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        };

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            SaveAnnotations();
        };
    }

    /// <summary>请求返回设置界面（由主窗口切换视图）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>画布状态（与 <c>InkCanvas</c> 绑定）。</summary>
    public InkCanvasState Canvas { get; }

    /// <summary>撤销 / 重做栈。</summary>
    public InkHistory History { get; } = new();

    /// <summary>缩略图导航。</summary>
    public ObservableCollection<SlideThumbnailViewModel> Thumbnails { get; } = new();

    /// <summary>工具栏可选颜色。</summary>
    public IReadOnlyList<uint> Palette { get; } = new uint[]
    {
        0xFFE53935, 0xFF1E88E5, 0xFF43A047, 0xFFFDD835, 0xFF8E24AA, 0xFF1B1D21, 0xFFFFFFFF,
    };

    [ObservableProperty]
    private string _coursewareName = "未打开课件";

    [ObservableProperty]
    private string _statusText = "点击「打开课件」选择 PPT 开始放映";

    [ObservableProperty]
    private bool _hasCourseware;

    [ObservableProperty]
    private int _pageIndex = 1;

    [ObservableProperty]
    private int _slideCount = 1;

    [ObservableProperty]
    private string _pageText = "1 / 1";

    [ObservableProperty]
    private string _zoomText = "100%";

    [ObservableProperty]
    private bool _isBlackScreen;

    [ObservableProperty]
    private bool _isWhiteScreen;

    [ObservableProperty]
    private bool _hasAnnotationsOnPage;

    /// <summary>页码跳转输入框内容。</summary>
    [ObservableProperty]
    private string _jumpPageText = "1";

    /// <summary>是否有未保存的批注改动。</summary>
    public bool IsDirty => _isDirty;

    public bool CanUndo => History.CanUndo;

    public bool CanRedo => History.CanRedo;

    /// <summary>当前工具（用于工具栏高亮）。</summary>
    public InkTool CurrentTool => Canvas.Tool;

    public bool IsHardPenSelected => Canvas.Tool == InkTool.HardPen;

    public bool IsMarkerSelected => Canvas.Tool == InkTool.Marker;

    public bool IsHighlighterSelected => Canvas.Tool == InkTool.Highlighter;

    public bool IsEraserSelected => Canvas.Tool == InkTool.Eraser;

    /// <summary>当前颜色。</summary>
    public uint CurrentColor => Canvas.ColorArgb;

    // ---------------------------------------------------------------- 打开课件

    /// <summary>选择并打开课件。</summary>
    [RelayCommand]
    private async Task OpenCoursewareAsync()
    {
        var path = await _fileDialogs.PickOpenFileAsync("选择要放映的 PPT", ".pptx");
        if (string.IsNullOrWhiteSpace(path))
        {
            // 允许手工选择 .ppt（部分老课件）
            path = await _fileDialogs.PickOpenFileAsync("选择要放映的 PPT", ".ppt");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenAsync(path);
    }

    /// <summary>打开指定课件（同时加载批注、渲染后端、播放进度提示）。</summary>
    public async Task OpenAsync(string coursewarePath)
    {
        SaveAnnotations();

        _coursewarePath = coursewarePath;
        CoursewareName = Path.GetFileName(coursewarePath);

        try
        {
            _coursewareHash = await _identity.ComputeHashAsync(coursewarePath);
        }
        catch (Exception ex)
        {
            StatusText = $"无法读取课件：{ex.Message}";
            return;
        }

        // 批注：按 SHA256 绑定，哈希不一致会抛 InvalidDataException
        try
        {
            _document = _annotationStore.Load(coursewarePath, _coursewareHash);
        }
        catch (InvalidDataException ex)
        {
            StatusText = $"批注加载失败：{ex.Message}（已跳过批注，课件正常放映）";
            _document = AnnotationDocument.CreateFor(_coursewareHash, CoursewareName, 0, _settings.Current.DeviceId);
        }

        History.Clear();
        _isDirty = false;
        OnPropertyChanged(nameof(IsDirty));

        await InitializeSlideSourceAsync(coursewarePath);

        SlideCount = Math.Max(_slideSource?.SlideCount ?? 1, GetMaxAnnotatedPage(_document));
        Canvas.Document = _document;
        Canvas.SlideCount = SlideCount;
        Canvas.PageSize = _slideSource?.PageSize ?? InkCanvasState.DefaultPageSize;

        BuildThumbnails();
        HasCourseware = true;
        GoToPage(1);

        StatusText = _slideSource switch
        {
            PptxSlideSource kernel
                => $"已加载 {CoursewareName}（渲染后端：{kernel.DisplayName}，动画与转场逐帧播放）",
            { IsAvailable: true } source when source.DisplayName.Contains("LibreOffice")
                => $"已加载 {CoursewareName}（渲染后端：{source.DisplayName}）",
            _ => $"已加载 {CoursewareName}（未检测到幻灯片渲染后端，仅批注与手势可用）",
        };

        await PromptResumeAsync();

        // 降级提示：内核报告的降级 / 不支持项 + 字体替换记录。
        // 字体替换记录是「渲染文本时」才登记的，所以先等首帧画出来（最多等 0.5 秒，绝不卡住放映），
        // 再汇总提示；整个过程是 async 的，逐帧动画继续播放，失败只写 StatusText。
        await YieldToBackgroundAsync();
        await PromptCompatibilityAsync();

        SaveProgress();
    }

    /// <summary>让出一轮 UI 工作（含渲染）后再继续；没有 UI 线程时直接返回。</summary>
    private static async Task YieldToBackgroundAsync()
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
        catch (Exception)
        {
            // 无 UI 线程（单元测试 / 无头运行）时不影响主流程
        }
    }

    /// <summary>
    /// 打开课件后提示「动画与字体兼容性提示」。不阻塞放映：
    /// · 只在内核后端报告了降级项 / 字体替换时才会弹；
    /// · 老师可选「不再提示此文件」，按课件 SHA256 记入本机设置；
    /// · 任何异常只写 <see cref="StatusText"/>，绝不影响放映。
    /// </summary>
    private async Task PromptCompatibilityAsync()
    {
        if (_slideSource is not PptxSlideSource pptx || string.IsNullOrEmpty(_coursewareHash))
        {
            return;
        }

        if (_settings.Current.SuppressedCompatibilityWarnings.Contains(_coursewareHash))
        {
            return;
        }

        // 等当前页第一帧画出来再读字体替换记录（无头 / 未上屏时最多等 0.5 秒）
        if (_playback is { } playback)
        {
            try
            {
                await playback.WaitForFirstFrameAsync(500);
            }
            catch (Exception)
            {
                // 等不到首帧也要照常提示（动画 / 转场类降级在解析阶段就已经记录好了）
            }
        }

        CompatibilityNotice? notice;
        try
        {
            notice = CompatibilityNotice.FromSlideSource(pptx);
        }
        catch (Exception ex)
        {
            StatusText = $"兼容性提示生成失败：{ex.Message}";
            return;
        }

        if (notice is null || notice.IsEmpty)
        {
            return;
        }

        try
        {
            var choice = await _dialogs.ShowCompatibilityNoticeAsync(notice);
            if (choice == CompatibilityNoticeChoice.SuppressForThisFile)
            {
                _settings.Current.SuppressedCompatibilityWarnings.Add(_coursewareHash);
                _settings.NotifyChanged();
                StatusText = "已设置「不再提示此文件」的兼容性提示。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"兼容性提示弹窗失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 选择幻灯片渲染后端，优先级：
    /// 1) 自研 PPTX 内核（无外部依赖，直接解析 pptx 并用 Skia 绘制；动画 / 转场走逐帧控制器）；
    /// 2) LibreOffice（装了才可用，转 PDF 后取页图）；
    /// 3) 占位画面（批注、手势、导出仍然可用）。
    /// </summary>
    private async Task InitializeSlideSourceAsync(string coursewarePath)
    {
        // 换课件 / 重新打开前先断开旧控制器（同时停掉逐帧计时器，避免泄漏）
        DetachPlayback();

        var pptx = new PptxSlideSource(_paths);
        if (await pptx.InitializeAsync(coursewarePath))
        {
            _slideSource = pptx;

            // 内核后端：整页画面由逐帧控制器绘制（进入动画 + 换页转场），
            // 静态页图只留给缩略图 / 导出（GetSlideAsync 缓存与逐帧渲染互不干扰）。
            _playback = new SlidePlaybackController(pptx, Canvas);
            Canvas.FrameRenderer = _playback;
            return;
        }

        var libreOffice = new LibreOfficeSlideSource(_paths);
        if (await libreOffice.InitializeAsync(coursewarePath))
        {
            _slideSource = libreOffice;
            return;
        }

        var placeholder = new PlaceholderSlideSource();
        await placeholder.InitializeAsync(coursewarePath);
        _slideSource = placeholder;
    }

    /// <summary>断开逐帧控制器：停表、清空播放状态、把画布还给静态页图。</summary>
    private void DetachPlayback()
    {
        _playback?.Reset();
        _playback = null;
        Canvas.FrameRenderer = null;
    }

    private async Task PromptResumeAsync()
    {
        if (!_settings.Current.PromptResumePlayback)
        {
            return;
        }

        var progress = _progressStore.Find(_coursewareHash);
        if (progress is null || progress.PromptSuppressed || progress.LastSlideIndex <= 1)
        {
            return;
        }

        var choice = await _dialogs.ShowResumePromptAsync(progress);
        switch (choice)
        {
            case PlaybackResumeChoice.JumpToPage:
                GoToPage(progress.LastSlideIndex);
                StatusText = $"已跳转到上次播放位置：第 {progress.LastSlideIndex} 页";
                break;

            case PlaybackResumeChoice.NeverPromptAgain:
                _progressStore.SuppressPrompt(_coursewareHash);
                StatusText = "已设置「不再提示此文件」，下次打开将从第 1 页开始。";
                break;

            case PlaybackResumeChoice.StartFromBeginning:
            case PlaybackResumeChoice.Dismiss:
            default:
                break;
        }
    }

    // ---------------------------------------------------------------- 翻页

    [RelayCommand]
    private void NextPage() => TurnPage(1);

    [RelayCommand]
    private void PreviousPage() => TurnPage(-1);

    [RelayCommand]
    private void GoToPage(int pageIndex) => SetPage(pageIndex);

    /// <summary>按输入框里的页码跳转。</summary>
    [RelayCommand]
    private void JumpToTypedPage()
    {
        if (int.TryParse(JumpPageText, out var page))
        {
            SetPage(page);
        }
    }

    /// <summary>
    /// 翻页（正数向后、负数向前）。
    /// 课堂习惯：<b>下一页先播完本页动画</b>——只要本页还有没播完的动画步骤，
    /// 第一次「下一页」推进动画（页码不变），动画播完后再点才真正翻页；「上一页」直接翻页。
    /// </summary>
    private void TurnPage(int delta)
    {
        if (delta > 0 && _playback is { CanAdvance: true } playback)
        {
            playback.Advance();
            StatusText = "已推进本页动画（动画播完后再点「下一页」即翻页）。";
            return;
        }

        SetPage(PageIndex + delta);
    }

    private void SetPage(int pageIndex)
    {
        if (!HasCourseware)
        {
            return;
        }

        var target = Math.Clamp(pageIndex, 1, Math.Max(SlideCount, 1));

        // 走内核后端时，还要确认目标页确实已经装载（首次打开课件时页码同为 1，但画面尚未装载）。
        var alreadyShown = target == PageIndex && Canvas.PageIndex == target
                           && (_playback is null || _playback.CurrentPageIndex == target);
        if (alreadyShown)
        {
            return;
        }

        // 切页前保存当前页笔迹
        SaveProgress();

        PageIndex = target;
        PageText = $"{target} / {Math.Max(SlideCount, 1)}";
        JumpPageText = target.ToString();
        Canvas.PageIndex = target;
        Canvas.ResetView();

        foreach (var thumbnail in Thumbnails)
        {
            thumbnail.IsCurrent = thumbnail.PageIndex == target;
        }

        HasAnnotationsOnPage = _document?.FindPage(target)?.HasInk ?? false;
        Canvas.ShowInk = true;

        if (_playback is { } playback)
        {
            // 内核后端：换页交给逐帧控制器（转场 + 页内动画）；静态页图置空避免重复绘制。
            // 缩略图与导出仍然走 PptxSlideSource.GetSlideAsync 的位图缓存，两条路径互不打架。
            Canvas.SlideImage = null;
            Canvas.IsSlideLoading = false;
            Canvas.RaiseInkChanged();
            _ = playback.ShowPageAsync(target, playTransition: true);
            return;
        }

        Canvas.RaiseInkChanged();
        _ = LoadSlideAsync(target);
    }

    private async Task LoadSlideAsync(int pageIndex)
    {
        if (_slideSource is null)
        {
            return;
        }

        Canvas.IsSlideLoading = true;
        try
        {
            Canvas.SlideImage = await _slideSource.GetSlideAsync(pageIndex);
        }
        catch (Exception)
        {
            Canvas.SlideImage = null;
        }
        finally
        {
            Canvas.IsSlideLoading = false;
        }
    }

    private void BuildThumbnails()
    {
        Thumbnails.Clear();
        for (var i = 1; i <= Math.Max(SlideCount, 1); i++)
        {
            Thumbnails.Add(new SlideThumbnailViewModel
            {
                PageIndex = i,
                IsCurrent = i == PageIndex,
                HasAnnotations = _document?.FindPage(i)?.HasInk ?? false,
            });
        }
    }

    // ---------------------------------------------------------------- 笔迹

    private void OnStrokeCompleted(object? sender, InkStroke stroke)
    {
        if (_document is null)
        {
            return;
        }

        _document.GetOrCreatePage(PageIndex).Strokes.Add(stroke);
        History.Record(new AddStrokeEdit(PageIndex, stroke));
        MarkDirty();
    }

    private void OnEraseRequested(object? sender, EraseRequest request)
    {
        if (_document is null || request.StrokeIds.Count == 0)
        {
            return;
        }

        var page = _document.FindPage(PageIndex);
        if (page is null)
        {
            return;
        }

        var removed = new List<(int Index, InkStroke Stroke)>();
        var ids = request.StrokeIds.ToHashSet(StringComparer.Ordinal);
        for (var i = page.Strokes.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(page.Strokes[i].Id))
            {
                removed.Add((i, page.Strokes[i]));
                page.Strokes.RemoveAt(i);
            }
        }

        if (removed.Count == 0)
        {
            return;
        }

        History.Record(new EraseStrokesEdit(PageIndex, removed));
        MarkDirty();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_document is not null && History.Undo(_document))
        {
            AfterDocumentMutation();
        }
    }

    [RelayCommand]
    private void Redo()
    {
        if (_document is not null && History.Redo(_document))
        {
            AfterDocumentMutation();
        }
    }

    [RelayCommand]
    private void ClearPageAnnotations()
    {
        if (_document is null)
        {
            return;
        }

        var page = _document.FindPage(PageIndex);
        if (page is null || page.Strokes.Count == 0)
        {
            StatusText = "本页没有批注。";
            return;
        }

        History.Record(new ClearPageEdit(PageIndex, page.Strokes));
        page.Strokes.Clear();
        AfterDocumentMutation();
        StatusText = $"已清空第 {PageIndex} 页批注（可撤销）。";
    }

    private void AfterDocumentMutation()
    {
        HasAnnotationsOnPage = _document?.FindPage(PageIndex)?.HasInk ?? false;
        var thumbnail = Thumbnails.FirstOrDefault(t => t.PageIndex == PageIndex);
        if (thumbnail is not null)
        {
            thumbnail.HasAnnotations = HasAnnotationsOnPage;
        }

        MarkDirty();
        Canvas.RaiseInkChanged();
    }

    private void MarkDirty()
    {
        _isDirty = true;
        OnPropertyChanged(nameof(IsDirty));

        // 自动保存：停笔 1.5 秒后写盘，退出放映时立即写盘
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    /// <summary>立即把批注写进 .cvcanno。</summary>
    [RelayCommand]
    public void SaveAnnotations()
    {
        if (_document is null || !HasCourseware)
        {
            return;
        }

        _autosaveTimer.Stop();

        try
        {
            _annotationStore.Save(_coursewarePath, _document);
            _isDirty = false;
            OnPropertyChanged(nameof(IsDirty));
            StatusText = $"批注已保存（{_document.TotalStrokeCount} 条笔迹）";
        }
        catch (Exception ex)
        {
            StatusText = $"批注保存失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 工具与画面

    [RelayCommand]
    private void SelectTool(InkTool tool)
    {
        Canvas.Tool = tool;
        Canvas.IsInkEnabled = true;
        OnPropertyChanged(nameof(CurrentTool));
        OnPropertyChanged(nameof(IsHardPenSelected));
        OnPropertyChanged(nameof(IsMarkerSelected));
        OnPropertyChanged(nameof(IsHighlighterSelected));
        OnPropertyChanged(nameof(IsEraserSelected));
    }

    [RelayCommand]
    private void SelectColor(uint colorArgb)
    {
        Canvas.ColorArgb = colorArgb;
        OnPropertyChanged(nameof(CurrentColor));
    }

    [RelayCommand]
    private void ToggleBlackScreen()
    {
        var enable = !IsBlackScreen;
        IsBlackScreen = enable;
        IsWhiteScreen = false;
        Canvas.DisplayMode = enable ? SlideDisplayMode.Black : SlideDisplayMode.Normal;
    }

    [RelayCommand]
    private void ToggleWhiteScreen()
    {
        var enable = !IsWhiteScreen;
        IsWhiteScreen = enable;
        IsBlackScreen = false;
        Canvas.DisplayMode = enable ? SlideDisplayMode.White : SlideDisplayMode.Normal;
    }

    [RelayCommand]
    private void ResetView() => Canvas.ResetView();

    [RelayCommand]
    private void ToggleFingerDraw()
    {
        _settings.Current.FingerDraws = !_settings.Current.FingerDraws;
        _settings.Current.ApplyTo(Canvas.Settings);
        _settings.NotifyChanged();
        StatusText = _settings.Current.FingerDraws ? "手指书写已开启" : "手指书写已关闭（手指只做手势）";
    }

    // ---------------------------------------------------------------- 导出

    [RelayCommand]
    private async Task ScreenshotAsync()
    {
        if (_document is null || !HasCourseware)
        {
            return;
        }

        var target = await _fileDialogs.PickSaveFileAsync(
            "导出当前页（含板书）",
            $"{Path.GetFileNameWithoutExtension(CoursewareName)}-第{PageIndex}页.png",
            ".png");

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            await _export.ExportPagePngAsync(
                _document,
                PageIndex,
                _slideSource,
                new Avalonia.PixelSize(2560, 1440),
                target);

            StatusText = $"已导出当前页：{target}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (_document is null || !HasCourseware)
        {
            return;
        }

        var target = await _fileDialogs.PickSaveFileAsync(
            "导出带批注的 PDF",
            $"{Path.GetFileNameWithoutExtension(CoursewareName)}-带批注.pdf",
            ".pdf");

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            StatusText = "正在导出 PDF…";
            var pages = await _export.ExportPdfAsync(
                _document,
                _slideSource,
                new Avalonia.PixelSize(1920, 1080),
                target);

            StatusText = $"已导出 PDF（{pages} 页）：{target}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出 PDF 失败：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 退出

    /// <summary>退出放映：保存批注与播放进度。</summary>
    [RelayCommand]
    public void Exit()
    {
        SaveAnnotations();
        SaveProgress();

        // 断开逐帧控制器（停表 + 清空播放状态），避免 DispatcherTimer 泄漏
        DetachPlayback();

        History.Clear();
        _document = null;
        _slideSource = null;
        _coursewareHash = string.Empty;
        _coursewarePath = string.Empty;
        HasCourseware = false;
        CoursewareName = "未打开课件";
        Thumbnails.Clear();
        Canvas.Document = null;
        Canvas.SlideImage = null;
        Canvas.ResetView();
        StatusText = "已退出放映；批注已保存，播放进度已记录在本机。";

        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>记录本机播放进度（只写本机 SQLite）。</summary>
    public void SaveProgress()
    {
        if (!HasCourseware || string.IsNullOrEmpty(_coursewareHash))
        {
            return;
        }

        var existing = _progressStore.Find(_coursewareHash);
        _progressStore.Save(new PlaybackProgress(
            _coursewareHash,
            _coursewarePath,
            CoursewareName,
            SlideCount,
            PageIndex,
            DateTimeOffset.UtcNow,
            existing?.PromptSuppressed ?? false));
    }

    private static int GetMaxAnnotatedPage(AnnotationDocument? document)
        => document is null || document.Pages.Count == 0 ? 1 : document.Pages.Max(p => p.PageIndex);
}
