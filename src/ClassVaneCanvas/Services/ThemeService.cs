using Avalonia;
using Avalonia.Styling;
using ClassVaneCanvas.Models;

namespace ClassVaneCanvas.Services;

/// <summary>
/// 通过 <see cref="Application.RequestedThemeVariant"/> 应用主题。
/// 页面全部使用 {DynamicResource}，因此切换主题后所有颜色即时刷新。
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly Application _application;

    public ThemeService(Application application)
        => _application = application ?? throw new ArgumentNullException(nameof(application));

    public AppTheme Current { get; private set; } = AppTheme.System;

    public void Apply(AppTheme theme)
    {
        Current = theme;

        _application.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
