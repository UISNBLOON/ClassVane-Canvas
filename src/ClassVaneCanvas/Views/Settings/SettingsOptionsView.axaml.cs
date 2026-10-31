using Avalonia.Controls;

namespace ClassVaneCanvas.Views.Settings;

/// <summary>
/// 数据驱动设置页的统一视图（书写设置 / 放映设置 / 手势设置 / 数据管理共用）。
/// 页面内容由 <see cref="ViewModels.Settings.SettingsOptionsPageViewModel.Options"/> 决定。
/// </summary>
public partial class SettingsOptionsView : UserControl
{
    public SettingsOptionsView() => InitializeComponent();
}
