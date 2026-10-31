using ClassVaneCanvas.ViewModels.Presentation;
using ClassVaneCanvas.ViewModels.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassVaneCanvas.ViewModels;

/// <summary>
/// 主窗口视图模型：在「设置」与「放映」两个界面之间切换。
/// 启动后默认进入设置视图，而设置视图默认选中关于页。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _current;

    public MainWindowViewModel(SettingsShellViewModel settings, PresentationViewModel presentation)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));

        _current = settings;

        settings.PresentationRequested += (_, _) => Current = Presentation;
        presentation.SettingsRequested += (_, _) => Current = Settings;
    }

    /// <summary>设置界面。</summary>
    public SettingsShellViewModel Settings { get; }

    /// <summary>放映界面。</summary>
    public PresentationViewModel Presentation { get; }

    /// <summary>切回设置。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowSettings() => Current = Settings;

    /// <summary>进入放映。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowPresentation() => Current = Presentation;
}
