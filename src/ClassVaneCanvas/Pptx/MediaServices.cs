using System.Diagnostics;

namespace ClassVaneCanvas.Pptx;

/// <summary>
/// 媒体（视频 / 音频）外放服务。
///
/// 本内核<b>不内嵌解码</b>视频：解析阶段只把媒体部件导出到本机缓存目录，
/// 播放阶段交给系统默认播放器。这样既不需要任何第三方解码库，
/// 也不会因为解码器崩溃拖垮放映流程。
/// </summary>
public interface IVideoPlaybackService
{
    /// <summary>
    /// 用系统默认播放器打开指定媒体文件。
    /// </summary>
    /// <param name="path">媒体文件绝对路径（通常是 <see cref="Model.PptxMediaFile.LocalPath"/>）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>成功拉起播放器返回 <c>true</c>；文件不存在或拉起失败返回 <c>false</c>（不抛异常）。</returns>
    Task<bool> PlayExternalAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IVideoPlaybackService"/> 的默认实现：调用系统默认播放器。
///
/// · Windows：<c>Process.Start(path)</c> 且 <c>UseShellExecute = true</c>（走文件关联）；
/// · macOS：<c>open &lt;path&gt;</c>；
/// · Linux：<c>xdg-open &lt;path&gt;</c>。
///
/// 所有分支都包了 try/catch：播放器缺失、权限不足、文件被删等情况一律返回 <c>false</c>，
/// 绝不把异常抛给放映流程。
/// </summary>
public sealed class ExternalMediaPlaybackService : IVideoPlaybackService
{
    /// <summary>无状态，可安全共享的单例。</summary>
    public static ExternalMediaPlaybackService Instance { get; } = new();

    /// <inheritdoc />
    public Task<bool> PlayExternalAsync(string path, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Task.FromResult(false);
            }

            var startInfo = CreateStartInfo(path);
            using var process = Process.Start(startInfo);
            return Task.FromResult(process is not null);
        }
        catch
        {
            // 系统播放器不可用（无 GUI 会话、无 xdg-open、文件关联缺失…）时静默失败。
            return Task.FromResult(false);
        }
    }

    /// <summary>按当前操作系统构造拉起命令。</summary>
    private static ProcessStartInfo CreateStartInfo(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo(path) { UseShellExecute = true };
        }

        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            return psi;
        }

        var linux = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        linux.ArgumentList.Add(path);
        return linux;
    }
}

/// <summary>
/// 永远返回 <c>false</c> 的播放服务：无头测试 / 纯批注模式下使用，不接触任何外部进程。
/// </summary>
public sealed class NullMediaPlaybackService : IVideoPlaybackService
{
    /// <summary>无状态单例。</summary>
    public static NullMediaPlaybackService Instance { get; } = new();

    /// <inheritdoc />
    public Task<bool> PlayExternalAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// 媒体部件导出器：把 PPTX 包内的视频 / 音频部件写到本机缓存目录。
///
/// 目录布局：<c>&lt;缓存根&gt;/&lt;课件哈希前 16 位&gt;/&lt;部件文件名&gt;</c>。
/// 缓存文件可随时删除重建，不承载任何用户数据；同一部件重复解析时直接复用已有文件。
/// </summary>
public static class MediaCacheExporter
{
    /// <summary>哈希目录名长度（SHA256 前 16 位十六进制）。</summary>
    private const int HashDirectoryLength = 16;

    /// <summary>
    /// 导出媒体部件。
    /// </summary>
    /// <param name="cacheDirectory">缓存根目录（通常是 <c>AppPaths.SlideCacheDirectory</c>）。</param>
    /// <param name="documentHash">课件 SHA256（小写十六进制）。</param>
    /// <param name="partName">包内部件名（例如 <c>ppt/media/media1.mp4</c>）。</param>
    /// <param name="data">部件字节。</param>
    /// <returns>导出后的绝对路径；失败返回 <c>null</c>（不抛异常）。</returns>
    public static string? Export(string? cacheDirectory, string documentHash, string partName, byte[] data)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cacheDirectory) || data.Length == 0)
            {
                return null;
            }

            var hashPart = string.IsNullOrWhiteSpace(documentHash)
                ? "unknown"
                : documentHash.Length > HashDirectoryLength
                    ? documentHash[..HashDirectoryLength]
                    : documentHash;

            var directory = Path.Combine(cacheDirectory, hashPart);
            Directory.CreateDirectory(directory);

            var fileName = SanitizeFileName(Path.GetFileName(partName));
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            var target = Path.Combine(directory, fileName);
            if (File.Exists(target) && new FileInfo(target).Length == data.Length)
            {
                return target;
            }

            // 先写临时文件再替换，避免放映过程中读到半截文件。
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>去掉文件名里的非法字符，防止部件名穿越目录。</summary>
    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
