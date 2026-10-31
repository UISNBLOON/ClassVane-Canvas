using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClassVaneCanvas.Services;

/// <summary>
/// 静态资源访问入口。三张 Logo 各有分工：
///
/// · <c>logo.png</c>（570×96，含文字标识）—— 关于页顶部的完整 Logo；
/// · <c>logo-1.png</c>（264×264）—— 应用 / 窗口图标（任务栏、Dock）；
/// · <c>logo-2.png</c>（60×60）—— 最小尺寸版：侧边栏左上角徽标 + 托盘图标。
///
/// 资源缺失时返回 <c>null</c>，界面自动降级为占位（不会因为缺图而启动失败）。
/// </summary>
public static class AppAssets
{
    /// <summary>完整 Logo（含文字）资源 URI。</summary>
    public const string FullLogoUri = "avares://ClassVaneCanvas/Assets/logo.png";

    /// <summary>应用图标资源 URI。</summary>
    public const string AppIconUri = "avares://ClassVaneCanvas/Assets/logo-1.png";

    /// <summary>最小尺寸 Logo（徽标 / 托盘）资源 URI。</summary>
    public const string SmallLogoUri = "avares://ClassVaneCanvas/Assets/logo-2.png";

    /// <summary>Logo 在项目中的相对路径（文档用）。</summary>
    public const string LogoRelativePath = "src/ClassVaneCanvas/Assets/logo.png";

    /// <summary>加载完整 Logo（关于页顶部）；资源缺失时返回 <c>null</c>。</summary>
    public static IImage? TryLoadFullLogo() => TryLoad(FullLogoUri);

    /// <summary>加载应用图标（窗口 / 任务栏）。</summary>
    public static IImage? TryLoadAppIcon() => TryLoad(AppIconUri);

    /// <summary>加载最小尺寸 Logo（侧边栏徽标 / 托盘图标）。</summary>
    public static IImage? TryLoadSmallLogo() => TryLoad(SmallLogoUri);

    private static IImage? TryLoad(string uri)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri(uri)));
        }
        catch (Exception)
        {
            // 资源缺失或解码失败：走占位分支
            return null;
        }
    }
}
