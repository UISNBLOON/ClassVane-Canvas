using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Export;
using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Services;
using ClassVaneCanvas.Storage;
using ClassVaneCanvas.ViewModels;
using ClassVaneCanvas.ViewModels.Presentation;
using ClassVaneCanvas.ViewModels.Settings;
using ClassVaneCanvas.Views;

namespace ClassVaneCanvas;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ---------------------------------------------------------- 组合根
            var paths = new AppPaths();
            paths.EnsureCreated();

            var settings = new JsonSettingsService(paths);
            settings.Load();

            // 全局唯一一份笔迹引擎参数：书写设置页调完，放映画布立即生效
            var inkSettings = new InkEngineSettings();
            settings.Current.ApplyTo(inkSettings);

            var codec = new CvcannoCodec();
            var merger = new AnnotationMerger();
            var annotationStore = new AnnotationStore(paths, settings.Current.AnnotationStorageMode, codec);
            var progressStore = new SqlitePlaybackProgressStore(paths);
            var coursewareIdentity = new CoursewareIdentity();
            var export = new AnnotatedExportService(inkSettings);
            var syncFactory = new SyncServiceFactory(paths, codec, merger, settings);
            var urlLauncher = new SystemUrlLauncher();
            var themeService = new ThemeService(this);

            Window? HostWindow()
                => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            var dialogs = new DialogService(HostWindow);
            var fileDialogs = new StorageProviderFileDialogService(HostWindow);

            // 启动即进入设置视图；SettingsShellViewModel 内部默认选中「关于」页。
            var settingsShell = new SettingsShellViewModel(
                urlLauncher,
                themeService,
                settings,
                inkSettings,
                paths,
                annotationStore,
                progressStore,
                codec,
                fileDialogs,
                dialogs,
                syncFactory);

            var presentation = new PresentationViewModel(
                paths,
                settings,
                inkSettings,
                coursewareIdentity,
                annotationStore,
                progressStore,
                dialogs,
                fileDialogs,
                export);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(settingsShell, presentation),
            };

            // 托盘图标使用最小尺寸 Logo（logo-2）；资源缺失时静默跳过
            if (AppAssets.TryLoadSmallLogo() is Bitmap trayBitmap)
            {
                var showItem = new NativeMenuItem("显示主窗口");
                showItem.Click += (_, _) =>
                {
                    if (desktop.MainWindow is { } window)
                    {
                        window.Show();
                        window.Activate();
                    }
                };

                var exitItem = new NativeMenuItem("退出 ClassVane Canvas");
                exitItem.Click += (_, _) => desktop.Shutdown();

                var trayMenu = new NativeMenu();
                trayMenu.Items.Add(showItem);
                trayMenu.Items.Add(new NativeMenuItemSeparator());
                trayMenu.Items.Add(exitItem);

                TrayIcon.SetIcons(this, new TrayIcons
                {
                    new TrayIcon
                    {
                        Icon = new WindowIcon(trayBitmap),
                        ToolTipText = "ClassVane Canvas",
                        IsVisible = true,
                        Menu = trayMenu,
                    },
                });
            }

            // 退出时保存批注（.cvcanno）与本机播放进度
            desktop.ShutdownRequested += (_, _) => presentation.Exit();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
