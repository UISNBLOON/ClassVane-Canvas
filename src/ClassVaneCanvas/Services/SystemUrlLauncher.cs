using System.Diagnostics;

namespace ClassVaneCanvas.Services;

/// <summary>
/// <see cref="IUrlLauncher"/> 的跨平台实现：
/// Windows 走 ShellExecute，macOS 走 <c>open</c>，Linux / 其它平台走 <c>xdg-open</c>。
/// 任何异常都被吞掉并返回 <c>false</c>，由调用方给出提示，避免 linker 失败影响主流程。
/// </summary>
public sealed class SystemUrlLauncher : IUrlLauncher
{
    public bool TryOpen(string url)
    {
        if (!TryNormalize(url, out var uri))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // UseShellExecute 会在默认浏览器中打开 URL。
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            }

            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            var startInfo = new ProcessStartInfo(opener) { UseShellExecute = false };
            startInfo.ArgumentList.Add(uri.AbsoluteUri);

            // 只负责唤起浏览器，不等待子进程退出。
            Process.Start(startInfo);
            return true;
        }
        catch (Exception)
        {
            // 浏览器 / xdg-open 缺失、无权限、无图形会话等情况都归为“打开失败”。
            return false;
        }
    }

    private static bool TryNormalize(string url, out Uri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        // 只允许交给系统处理的常见协议，避免把任意 scheme 交给 shell。
        var allowed = parsed.Scheme == Uri.UriSchemeHttp
                      || parsed.Scheme == Uri.UriSchemeHttps
                      || parsed.Scheme == Uri.UriSchemeMailto;

        if (!allowed)
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
