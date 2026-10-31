namespace ClassVaneCanvas.Services;

/// <summary>
/// 用系统默认浏览器 / 默认程序打开链接。
/// 抽成接口是为了让 ViewModel 不直接依赖 <see cref="System.Diagnostics.Process"/>，便于单元测试。
/// </summary>
public interface IUrlLauncher
{
    /// <summary>尝试打开链接，成功返回 <c>true</c>；链接非法或系统调用失败返回 <c>false</c>。</summary>
    bool TryOpen(string url);
}
