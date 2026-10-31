using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Media;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>
/// 设置模块默认页面（关于页）的 ViewModel。
///
/// 约定：
/// · 版本号 / 版权信息全部来自程序集元数据（csproj 的 &lt;Version&gt;、&lt;Copyright&gt;），
///   界面里不出现任何硬编码版本号；
/// · 打开链接统一走 <see cref="IUrlLauncher"/>，由 ViewModel 触发系统浏览器；
/// · Logo 只是静态展示元素，这里不提供也不绑定任何点击命令。
/// </summary>
public partial class AboutViewModel : SettingsPageViewModel
{
    /// <summary>GNU GPLv3 许可证正文地址。</summary>
    public const string GplLicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.en.html";

    /// <summary>项目主页地址。</summary>
    public const string ProjectHomepageUrl = "https://github.com/NoxBlaze/ClassVaneCanvas";

    private readonly IUrlLauncher _urlLauncher;
    private readonly ISettingsNavigator _navigator;

    /// <summary>链接打开失败时的提示文本；正常时为空字符串。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public AboutViewModel(IUrlLauncher urlLauncher, ISettingsNavigator navigator)
        : base("关于", "版本信息、开源致谢与许可证声明")
    {
        _urlLauncher = urlLauncher ?? throw new ArgumentNullException(nameof(urlLauncher));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));

        VersionText = $"Version {ResolveVersion()}";
        CopyrightText = $"Copyright © {ResolveCopyrightHolder()}";

        // 关于页顶部使用完整 Logo（含文字标识），静态展示、无点击事件。
        LogoImage = AppAssets.TryLoadFullLogo();

        Acknowledgements = new ObservableCollection<ThirdPartyNotice>
        {
            new(
                "Avalonia UI",
                "https://github.com/AvaloniaUI/Avalonia",
                "MIT",
                "跨平台 .NET UI 框架，提供窗口、控件、布局与渲染后端。"),
            new(
                "CommunityToolkit.Mvvm",
                "https://github.com/CommunityToolkit/dotnet",
                "MIT",
                "MVVM 源生成器，提供 ObservableObject、RelayCommand 等基础设施。"),

            new(
                "Microsoft.Data.Sqlite",
                "https://github.com/dotnet/efcore",
                "MIT",
                "本机播放进度数据库访问（进度只存本机，绝不参与批注同步）。"),
            new(
                "SQLite",
                "https://sqlite.org",
                "Public Domain",
                "嵌入式数据库引擎，承载本机播放进度记录。"),

            // 预留位置：后续新增依赖直接在此追加一项即可，界面会自动渲染出新条目。
        };
    }

    /// <summary>
    /// 软件 Logo。当前为预留状态，资源尚未提供时为 <c>null</c>，界面展示占位框。
    /// Logo 只是静态展示元素，没有对应的点击命令。
    /// </summary>
    public IImage? LogoImage { get; }

    /// <summary>是否已经提供 Logo 图片。</summary>
    public bool HasLogo => LogoImage is not null;

    /// <summary>形如 "Version 0.1.0"，由程序集元数据动态生成。</summary>
    public string VersionText { get; }

    /// <summary>形如 "Copyright © NoxBlaze"。</summary>
    public string CopyrightText { get; }

    /// <summary>开源致谢列表（垂直排列、可滚动）。</summary>
    public ObservableCollection<ThirdPartyNotice> Acknowledgements { get; }

    /// <summary>是否显示链接打开失败的提示。</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>「查看 GPLv3 许可证」。</summary>
    [RelayCommand]
    private void OpenLicense() => OpenUrl(GplLicenseUrl, "GPLv3 许可证");

    /// <summary>「项目主页」。</summary>
    [RelayCommand]
    private void OpenHomepage() => OpenUrl(ProjectHomepageUrl, "项目主页");

    /// <summary>点击致谢条目中的仓库链接。</summary>
    [RelayCommand]
    private void OpenRepository(ThirdPartyNotice? notice)
    {
        if (notice is null)
        {
            return;
        }

        OpenUrl(notice.RepositoryUrl, notice.Name);
    }

    /// <summary>
    /// 「返回设置」：交给上层导航容器处理 —— 回到设置主页；若已在主页，则由外壳决定是否关闭宿主窗口。
    /// </summary>
    [RelayCommand]
    private void GoBack() => _navigator.NavigateHome();

    private void OpenUrl(string url, string target)
        => StatusMessage = _urlLauncher.TryOpen(url)
            ? string.Empty
            : $"无法调用系统浏览器打开{target}，请手动访问：{url}";

    /// <summary>
    /// 读取版本号：优先用 csproj &lt;Version&gt; 生成的 InformationalVersion（并去掉 SourceLink 的 +commit 后缀），
    /// 读不到时回退到 AssemblyVersion。
    /// </summary>
    private static string ResolveVersion()
    {
        var assembly = typeof(AboutViewModel).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            if (plus >= 0)
            {
                informational = informational[..plus];
            }

            if (informational.Length > 0)
            {
                return informational;
            }
        }

        var version = assembly.GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }

        if (version.Build < 0)
        {
            return $"{version.Major}.{version.Minor}";
        }

        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>读取版权持有者，取不到时回退到开发者署名。</summary>
    private static string ResolveCopyrightHolder()
    {
        var copyright = typeof(AboutViewModel).Assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

        return string.IsNullOrWhiteSpace(copyright) ? "NoxBlaze" : copyright.Trim();
    }
}
