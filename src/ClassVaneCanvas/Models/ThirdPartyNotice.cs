namespace ClassVaneCanvas.Models;

/// <summary>
/// 「开源项目致谢」列表中的一项。
/// 新增依赖时只需在 <c>AboutViewModel.Acknowledgements</c> 中追加一个实例。
/// </summary>
public sealed class ThirdPartyNotice
{
    public ThirdPartyNotice(string name, string repositoryUrl, string licenseName, string summary = "")
    {
        Name = name;
        RepositoryUrl = repositoryUrl;
        LicenseName = licenseName;
        Summary = summary;
    }

    /// <summary>项目名称，例如 "Avalonia UI"。</summary>
    public string Name { get; }

    /// <summary>仓库地址，点击后调用系统浏览器打开。</summary>
    public string RepositoryUrl { get; }

    /// <summary>许可证名称，例如 "MIT"。</summary>
    public string LicenseName { get; }

    /// <summary>一句话说明该依赖在项目中的用途（可为空）。</summary>
    public string Summary { get; }

    /// <summary>是否显示用途说明。</summary>
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
}
