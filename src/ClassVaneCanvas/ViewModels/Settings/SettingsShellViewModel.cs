using System.Collections.ObjectModel;
using Avalonia.Media;
using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Services;
using ClassVaneCanvas.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>
/// 设置模块的外壳：左侧为设置项导航，右侧为内容区。
///
/// 关键行为：构造完成后默认选中「关于」页（<see cref="HomePage"/>），
/// 因此进入设置视图时展示的就是关于页，侧边导航可以切换到其它设置项。
/// 同时实现 <see cref="ISettingsNavigator"/>，供各页面回调导航（例如关于页的「返回设置」）。
/// </summary>
public partial class SettingsShellViewModel : ViewModelBase, ISettingsNavigator
{
    private readonly IThemeService _themeService;

    [ObservableProperty]
    private SettingsPageViewModel _selectedPage;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    public SettingsShellViewModel(
        IUrlLauncher urlLauncher,
        IThemeService themeService,
        ISettingsService settings,
        InkEngineSettings inkEngineSettings,
        AppPaths paths,
        IAnnotationStore annotationStore,
        IPlaybackProgressStore progressStore,
        IAnnotationPackageCodec codec,
        IFileDialogService fileDialogs,
        IDialogService dialogs,
        SyncServiceFactory syncFactory)
    {
        ArgumentNullException.ThrowIfNull(urlLauncher);
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inkEngineSettings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(annotationStore);
        ArgumentNullException.ThrowIfNull(progressStore);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(fileDialogs);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(syncFactory);

        // 设置主页即关于页。
        HomePage = new AboutViewModel(urlLauncher, this);

        Pages = new ObservableCollection<SettingsPageViewModel>
        {
            // 第一项为默认页面：关于
            HomePage,
            new WritingSettingsViewModel(settings, inkEngineSettings),
            new PresentationSettingsViewModel(settings, progressStore, annotationStore, syncFactory),
            new GestureSettingsViewModel(settings),
            new DataManagementViewModel(paths, progressStore, annotationStore, codec, fileDialogs, dialogs),
        };

        ThemeOptions = new[]
        {
            new ThemeOption(AppTheme.System, "跟随系统"),
            new ThemeOption(AppTheme.Light, "浅色"),
            new ThemeOption(AppTheme.Dark, "深色"),
        };

        _selectedPage = HomePage;          // 进入设置视图：默认加载并选中关于页
        _selectedTheme = ThemeOptions[0];  // 默认跟随全局主题

        // 侧边栏左上角徽标用最小尺寸 Logo（logo-2）。
        LogoImage = AppAssets.TryLoadSmallLogo();
        HasLogo = LogoImage is not null;
    }

    /// <summary>宿主窗口请求关闭（关于页在主页再次点「返回设置」时触发）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>请求进入放映界面（由主窗口切换视图）。</summary>
    public event EventHandler? PresentationRequested;

    /// <summary>设置主页（关于页）。</summary>
    public SettingsPageViewModel HomePage { get; }

    /// <summary>侧边导航数据源。</summary>
    public ObservableCollection<SettingsPageViewModel> Pages { get; }

    /// <summary>外观主题下拉框数据源。</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    /// <summary>侧边栏 Logo（预留位，未上传时为 <c>null</c>）。</summary>
    public IImage? LogoImage { get; }

    /// <summary>是否已经提供徽标图片。</summary>
    public bool HasLogo { get; }

    /// <summary>进入放映界面。</summary>
    [RelayCommand]
    private void OpenPresentation() => PresentationRequested?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void NavigateTo(SettingsPageViewModel page)
    {
        if (page is not null && Pages.Contains(page))
        {
            SelectedPage = page;
        }
    }

    /// <inheritdoc />
    public void NavigateHome()
    {
        if (!ReferenceEquals(SelectedPage, HomePage))
        {
            SelectedPage = HomePage;
            return;
        }

        // 已经停留在设置主页：交给宿主窗口决定是否关闭（作为主界面时是空操作）。
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedThemeChanged(ThemeOption value) => _themeService.Apply(value.Value);
}
