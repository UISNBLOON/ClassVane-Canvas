namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>设置侧边导航中一个可选中页面的基类。</summary>
public abstract class SettingsPageViewModel : ViewModelBase
{
    protected SettingsPageViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    /// <summary>侧边导航中显示的标题。</summary>
    public string Title { get; }

    /// <summary>侧边导航中显示的一句话说明。</summary>
    public string Description { get; }
}
