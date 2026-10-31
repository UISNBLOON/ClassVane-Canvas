using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ClassVaneCanvas.ViewModels;
using ClassVaneCanvas.ViewModels.Presentation;
using ClassVaneCanvas.ViewModels.Settings;
using ClassVaneCanvas.Views.Presentation;
using ClassVaneCanvas.Views.Settings;

namespace ClassVaneCanvas;

/// <summary>
/// ViewModel → View 的显式映射。
/// 使用字典而不是反射拼类型名，便于裁剪（trimming）且新增页面时可读性更好。
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private readonly Dictionary<Type, Func<Control>> _views = new()
    {
        [typeof(SettingsShellViewModel)] = () => new SettingsShellView(),
        [typeof(AboutViewModel)] = () => new AboutView(),
        [typeof(SettingsOptionsPageViewModel)] = () => new SettingsOptionsView(),
        [typeof(PresentationViewModel)] = () => new PresentationView(),
    };

    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        // 数据驱动的设置页共用同一个视图：按基类兜底匹配
        foreach (var (type, factory) in _views)
        {
            if (type.IsInstanceOfType(param))
            {
                return factory();
            }
        }

        return null;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
