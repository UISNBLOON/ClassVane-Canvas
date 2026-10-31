using Avalonia.Controls;
using ClassVaneCanvas.ViewModels.Settings;

namespace ClassVaneCanvas.Views.Settings;

/// <summary>
/// 设置外壳视图：左侧设置项导航 + 右侧内容区。
/// 默认页面由 <see cref="SettingsShellViewModel.SelectedPage"/> 决定（构造时已选中关于页）。
/// </summary>
public partial class SettingsShellView : UserControl
{
    public SettingsShellView() => InitializeComponent();
}
