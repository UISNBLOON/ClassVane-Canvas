using System.Collections.ObjectModel;

namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>
/// 数据驱动设置页的基类：页面 = 一串设置项（分组标题 / 开关 / 滑杆 / 下拉 / 输入 / 按钮 / 信息）。
/// 所有页面共用同一个视图 <c>SettingsOptionsView</c>，新增设置项不需要写新的 XAML。
/// </summary>
public abstract class SettingsOptionsPageViewModel : SettingsPageViewModel
{
    protected SettingsOptionsPageViewModel(string title, string description)
        : base(title, description)
        => Options = new ObservableCollection<Options.SettingsOptionViewModel>();

    /// <summary>页面上的全部设置项（按顺序渲染）。</summary>
    public ObservableCollection<Options.SettingsOptionViewModel> Options { get; }

    /// <summary>追加一个设置项并返回它（便于页面构造时链式书写）。</summary>
    protected T Add<T>(T option)
        where T : Options.SettingsOptionViewModel
    {
        Options.Add(option);
        return option;
    }
}
