using ClassVaneCanvas.Models;

namespace ClassVaneCanvas.Services;

/// <summary>全局主题服务：深色 / 浅色 / 跟随系统。</summary>
public interface IThemeService
{
    /// <summary>当前主题设置。</summary>
    AppTheme Current { get; }

    /// <summary>应用主题设置；<see cref="AppTheme.System"/> 表示跟随操作系统。</summary>
    void Apply(AppTheme theme);
}
