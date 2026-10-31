namespace ClassVaneCanvas.Services;

/// <summary>
/// 设置模块内部的页面导航。由设置外壳（SettingsShellViewModel）实现并注入到各页面 ViewModel，
/// 这样页面本身不需要知道自己是嵌在外壳里、还是被弹窗承载。
/// </summary>
public interface ISettingsNavigator
{
    /// <summary>切换到指定设置页；页面不在当前导航列表中时忽略。</summary>
    void NavigateTo(ViewModels.Settings.SettingsPageViewModel page);

    /// <summary>回到设置主页（关于页）。若已经位于主页，则由外壳决定是否请求关闭宿主窗口。</summary>
    void NavigateHome();
}
