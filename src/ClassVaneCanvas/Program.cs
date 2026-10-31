using Avalonia;

namespace ClassVaneCanvas;

internal static class Program
{
    // 初始化前不要访问任何 Avalonia / 依赖 AvaloniaLocator 的 API。
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // 供 Avalonia 设计器（预览器）复用的构建入口。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
