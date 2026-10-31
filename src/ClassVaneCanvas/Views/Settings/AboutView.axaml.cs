using Avalonia.Controls;

namespace ClassVaneCanvas.Views.Settings;

/// <summary>
/// 关于页（设置模块的默认页面）。
///
/// 这里刻意保持空代码后置：打开链接、返回设置等行为都通过命令绑定到
/// <see cref="ViewModels.Settings.AboutViewModel"/>，由 IUrlLauncher / ISettingsNavigator 完成，
/// 视图层不直接调用 Process.Start，便于跨平台与单元测试。
/// Logo 是纯静态展示元素，没有 Click / PointerPressed 之类的处理逻辑。
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();
}
