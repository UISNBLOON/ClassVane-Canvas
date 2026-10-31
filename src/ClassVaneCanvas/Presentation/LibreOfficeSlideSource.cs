using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using ClassVaneCanvas.Storage;

namespace ClassVaneCanvas.Presentation;

/// <summary>
/// LibreOffice 幻灯片后端（探测式，本机没装就自动降级）。
///
/// 工作流程：
/// 1. <b>探测</b>：依次尝试 <c>soffice</c> / <c>libreoffice</c> 命令（macOS 额外尝试 .app 内的可执行文件），
///    执行 <c>--version</c> 且限时 10 秒；找不到时 <see cref="IsAvailable"/> 保持 <c>false</c>，
///    并且 <see cref="DisplayName"/> 显示「LibreOffice（未安装）」；
/// 2. <b>转 PDF</b>：<c>soffice --headless --convert-to pdf --outdir &lt;缓存目录&gt; &lt;课件&gt;</c>，
///    缓存文件名为课件 SHA256 前缀，同一份课件只转一次（缓存目录可随时清空重建）；
/// 3. <b>出图</b>：PDF → 位图需要一个 PDF 光栅化器。本项目不引入任何新的原生依赖，
///    因此这里把「PDF 第 N 页 → 位图」抽象为构造函数注入的委托
///    <c>Func&lt;pdfPath, pageIndex, cancellationToken, Task&lt;IImage?&gt;&gt;</c>；
///    <b>委托为空时 <see cref="GetSlideAsync"/> 直接返回 <c>null</c></b>，放映画布于是走占位画面。
///    将来接入 pdfium / mupdf（或自研光栅化）时，只需在构造本类时把该委托传进来，
///    本文件不需要任何改动，也不需要新增 NuGet 包。
///
/// 健壮性约定：所有外部进程调用与文件 IO 都包了 try/catch，任何失败都降级为
/// 「返回 null / IsAvailable 保持 false」——放映与批注流程绝不会因为渲染后端崩溃。
/// 本类只读取课件、只写自己的缓存目录，绝不修改原始课件文件。
/// </summary>
public sealed class LibreOfficeSlideSource : ISlideSource
{
    /// <summary>未探测到 LibreOffice 时的展示名。</summary>
    private const string MissingDisplayName = "LibreOffice（未安装）";

    /// <summary>探测超时：10 秒。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>转 PDF 超时：大课件转换较慢，给足 2 分钟。</summary>
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 可执行文件候选：PATH 里的 <c>soffice</c> / <c>libreoffice</c>，
    /// 外加 macOS 应用包内的可执行文件（很多 Mac 用户只装了 .app，没有命令行软链）。
    /// </summary>
    private static readonly string[] ExecutableCandidates =
    {
        "soffice",
        "libreoffice",
        "/Applications/LibreOffice.app/Contents/MacOS/soffice",
    };

    /// <summary>页面坐标系默认尺寸，与放映画布一致。</summary>
    private static readonly Size DefaultPageSize = new(1280, 720);

    /// <summary>页对象匹配：<c>/Type /Page</c>（负向断言排除 <c>/Type /Pages</c>）。</summary>
    private static readonly Regex PageObjectRegex =
        new(@"/Type\s*/Page(?![a-zA-Z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>页树节点上的页数声明：<c>/Count 12</c>。</summary>
    private static readonly Regex DeclaredCountRegex =
        new(@"/Count\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AppPaths _paths;
    private readonly Func<string, int, CancellationToken, Task<IImage?>>? _rasterizer;

    /// <summary>探测到的可执行文件；为 <c>null</c> 表示本后端不可用。</summary>
    private string? _executablePath;

    /// <summary>课件转换出的 PDF 缓存路径；为 <c>null</c> 表示尚未成功初始化。</summary>
    private string? _pdfPath;

    /// <summary>
    /// 创建 LibreOffice 后端。
    /// </summary>
    /// <param name="paths">本机数据目录（<see cref="AppPaths.SlideCacheDirectory"/> 用作转换缓存）。</param>
    /// <param name="rasterizer">
    /// 可选：PDF 光栅化器 <c>(pdfPath, pageIndex, cancellationToken) =&gt; 位图</c>。
    /// 传 <c>null</c>（默认）时 <see cref="GetSlideAsync"/> 恒返回 <c>null</c>，放映走占位画面；
    /// 接入 pdfium / mupdf 等实现时把本委托传进来即可，无需修改本类。
    /// </param>
    public LibreOfficeSlideSource(
        AppPaths paths,
        Func<string, int, CancellationToken, Task<IImage?>>? rasterizer = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _rasterizer = rasterizer;
    }

    /// <inheritdoc />
    /// <remarks>探测成功前显示「未安装」，避免在设置页给出没把握的承诺。</remarks>
    public string DisplayName => _executablePath is null
        ? MissingDisplayName
        : $"LibreOffice（{Path.GetFileName(_executablePath)}）";

    /// <inheritdoc />
    /// <remarks>探测是异步的（要起进程跑 <c>--version</c>），因此构造后先为 <c>false</c>，探测成功后转 <c>true</c>。</remarks>
    public bool IsAvailable => _executablePath is not null;

    /// <inheritdoc />
    /// <remarks>转 PDF 成功前按 1 页兜底，避免界面出现 0 页导致无法翻页。</remarks>
    public int SlideCount { get; private set; } = 1;

    /// <inheritdoc />
    public Size PageSize => DefaultPageSize;

    /// <summary>
    /// 探测 LibreOffice 是否可用（幂等，成功一次后不再重复起进程）。
    /// </summary>
    /// <returns>是否找到可用的 LibreOffice 命令行。</returns>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_executablePath is not null)
        {
            return true;
        }

        foreach (var candidate in ExecutableCandidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var result = await RunProcessAsync(
                    candidate,
                    new[] { "--version" },
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Started && result.ExitCode == 0)
            {
                _executablePath = candidate;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> InitializeAsync(string coursewarePath, CancellationToken cancellationToken = default)
    {
        _pdfPath = null;
        SlideCount = 1;

        // 课件不存在（被移动 / 删除 / 路径为空）时直接降级，不抛异常
        if (string.IsNullOrWhiteSpace(coursewarePath) || !File.Exists(coursewarePath))
        {
            return false;
        }

        try
        {
            if (!await ProbeAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var cacheKey = await ComputeCacheKeyAsync(coursewarePath, cancellationToken).ConfigureAwait(false);
            if (cacheKey.Length == 0)
            {
                return false;
            }

            var cacheDirectory = _paths.SlideCacheDirectory;
            Directory.CreateDirectory(cacheDirectory);

            // 缓存文件名 = 课件 SHA256 前缀：同一份课件命中缓存，不同课件不会互相覆盖
            var pdfPath = Path.Combine(cacheDirectory, cacheKey + ".pdf");
            if (!File.Exists(pdfPath) &&
                !await ConvertToPdfAsync(coursewarePath, pdfPath, cacheKey, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (!File.Exists(pdfPath))
            {
                return false;
            }

            _pdfPath = pdfPath;
            SlideCount = Math.Max(1, CountPdfPages(pdfPath));
            return true;
        }
        catch (Exception)
        {
            // 目录不可写、缓存被占用、文件转换中被删除……一律降级为「本后端不可用」
            _pdfPath = null;
            SlideCount = 1;
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 没有注入光栅化器、还没初始化、页码越界、光栅化器内部报错，统统返回 <c>null</c>，绝不抛异常。
    /// </remarks>
    public async Task<IImage?> GetSlideAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            if (pageIndex < 1 || _rasterizer is null)
            {
                return null;
            }

            var pdfPath = _pdfPath;
            if (pdfPath is null || !File.Exists(pdfPath))
            {
                // 未初始化或缓存已被清理：本次放映按「无幻灯片」处理，重新 InitializeAsync 即可恢复
                return null;
            }

            return await _rasterizer(pdfPath, pageIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // PDF 损坏、原生光栅化库加载失败等一律按「本页无图」处理
            return null;
        }
    }

    /// <summary>课件内容 SHA256 的前 16 个十六进制字符，用作缓存键。</summary>
    private static async Task<string> ComputeCacheKeyAsync(string coursewarePath, CancellationToken cancellationToken)
    {
        try
        {
            // FileShare.ReadWrite：课件正被 PowerPoint / WPS 打开时也能读取
            await using var stream = new FileStream(
                coursewarePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1 << 16,
                useAsync: true);

            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>调用 LibreOffice 把课件转成 PDF 缓存。</summary>
    private async Task<bool> ConvertToPdfAsync(
        string coursewarePath,
        string pdfPath,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        // 每份课件一个独立输出目录 + 独立用户配置目录：
        // 既不会和用户正在使用的 LibreOffice 实例抢锁，也不会让同名课件互相覆盖。
        var outputDirectory = Path.Combine(_paths.SlideCacheDirectory, cacheKey);
        var profileDirectory = Path.Combine(_paths.SlideCacheDirectory, "lo-profile-" + cacheKey);
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(profileDirectory);

        // ArgumentList 逐项传参：课件路径含空格 / 中文时不会被拆散
        var arguments = new[]
        {
            "--headless",
            "--norestore",
            "--nolockcheck",
            "-env:UserInstallation=" + new Uri(profileDirectory).AbsoluteUri,
            "--convert-to",
            "pdf",
            "--outdir",
            outputDirectory,
            coursewarePath,
        };

        var result = await RunProcessAsync(_executablePath!, arguments, ConvertTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started || result.ExitCode != 0)
        {
            return false;
        }

        // LibreOffice 输出的是「源文件名.pdf」，这里改名为 <哈希>.pdf 作为稳定缓存键
        return await Task.Run(
            () =>
            {
                try
                {
                    var produced = Directory
                        .EnumerateFiles(outputDirectory, "*.pdf")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();

                    if (produced is null)
                    {
                        return false;
                    }

                    File.Move(produced, pdfPath, overwrite: true);
                    return File.Exists(pdfPath);
                }
                catch (Exception)
                {
                    return false;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 统计 PDF 页数：优先数页对象（<c>/Type /Page</c>，已排除 <c>/Pages</c>），
    /// 退化时读页树的 <c>/Count N</c>。
    /// 两者都读不到（例如页对象被压进对象流）时按 1 页处理——页数只用于界面提示与翻页上限，
    /// 估错不会破坏任何批注数据；真要精确页数时应由光栅化器回报。
    /// </summary>
    private static int CountPdfPages(string pdfPath)
    {
        try
        {
            // PDF 语法是字节级的：用 Latin1 逐字节映射成字符串，不会因编码问题抛异常
            var text = File.ReadAllText(pdfPath, Encoding.Latin1);

            var pageObjects = PageObjectRegex.Matches(text).Count;
            if (pageObjects > 0)
            {
                return pageObjects;
            }

            var declared = DeclaredCountRegex.Match(text);
            if (declared.Success &&
                int.TryParse(declared.Groups[1].Value, out var count) &&
                count > 0)
            {
                return count;
            }

            return 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>
    /// 启动外部进程并在超时内等待退出。任何失败（可执行文件不存在、超时、无权限、进程崩溃）
    /// 都以返回值表达，绝不向调用方抛异常；超时会杀掉进程，不留下后台 soffice。
    /// </summary>
    /// <returns>是否成功启动、退出码、合并后的标准输出与错误输出。</returns>
    private static async Task<(bool Started, int ExitCode, string Output)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process? process = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, -1, string.Empty);
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            // 先挂上读取再等待退出，避免子进程写满管道缓冲区后互相卡死
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 探测/转换超时或调用方取消：结束进程并按失败返回
                TryKill(process);
                return (true, -1, string.Empty);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (true, process.ExitCode, (stdout + stderr).Trim());
        }
        catch (Exception)
        {
            // Win32Exception：命令不存在；其余为权限 / 句柄等异常，统一按「没装 / 起不来」处理
            TryKill(process);
            return (false, -1, string.Empty);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>尽力结束进程；进程已退出或无权结束时忽略异常。</summary>
    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 结束失败不影响上层：进程的父进程退出后由系统回收
        }
    }
}
