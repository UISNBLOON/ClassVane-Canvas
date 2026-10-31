using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ClassVaneCanvas.Models.Annotations;

namespace ClassVaneCanvas.Sync;

/// <summary>
/// WebDAV 目标：老师自建的 WebDAV 服务（Nextcloud / 群晖 / 坚果云自建 / 自搭 NAS 均可）。
///
/// 约定：
/// · <b>只允许自持服务器</b>，本项目不内置、不推荐任何第三方公共服务器地址；
/// · 只收发 <c>*.cvcanno</c> 文件，相对路径先做目录穿越与扩展名校验；
/// · Basic 认证，账号密码只用于当次请求头，不写日志、不落盘；
/// · 每个请求 30 秒超时并透传 <see cref="CancellationToken"/>。
///
/// ETag 一律<b>原样透传</b>远端返回的字符串（可能是 <c>"abc"</c> 也可能带 <c>W/</c> 前缀），
/// 因为回写时要把它原封不动放进 <c>If-Match</c> 头做乐观锁。
/// </summary>
public sealed class WebDavSyncTarget : IAnnotationSyncTarget, IDisposable
{
    /// <summary>单次请求超时时间。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod MkcolMethod = new("MKCOL");

    /// <summary>PROPFIND 请求体：只要这几个属性，够同步用且体积最小。</summary>
    private const string PropFindRequestBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<D:propfind xmlns:D=\"DAV:\"><D:prop>" +
        "<D:resourcetype/><D:getcontentlength/><D:getetag/><D:getlastmodified/>" +
        "</D:prop></D:propfind>";

    private static readonly XNamespace Dav = "DAV:";

    private readonly Uri _baseUri;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _authorizationParameter;
    private bool _disposed;

    /// <param name="baseUrl">WebDAV 目录地址（例如 <c>https://nas.local/dav/annotations</c>），末尾斜杠可有可无。</param>
    /// <param name="userName">账号。</param>
    /// <param name="password">密码 / 应用专用密码。</param>
    /// <param name="displayName">展示名称；为空时按主机名自动生成。</param>
    /// <param name="httpClient">可选注入的 <see cref="HttpClient"/>（测试用）；为空时内部创建并负责释放。</param>
    public WebDavSyncTarget(
        string baseUrl,
        string userName,
        string password,
        string? displayName = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("WebDAV 地址不能为空。", nameof(baseUrl));
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("WebDAV 地址必须是 http:// 或 https:// 开头的完整地址。", nameof(baseUrl));
        }

        // 目录地址必须以 / 结尾，否则 Uri 相对解析会把最后一段当成文件名。
        _baseUri = new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/");
        _authorizationParameter = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));

        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = RequestTimeout };

        DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"WebDAV：{_baseUri.Host}" : displayName;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <summary>同步根地址（始终以 <c>/</c> 结尾）。</summary>
    public Uri BaseUri => _baseUri;

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 1) 先探测根目录是否存在且可读。
            using (var response = await PropFindAsync(_baseUri, "0", cancellationToken).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (!IsMissing(response.StatusCode))
                {
                    return false;
                }
            }

            // 2) 根目录还不存在：尝试建目录（已存在时多数服务器回 405，同样算通过）。
            using (var response = await SendAsync(MkcolMethod, _baseUri, null, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MethodNotAllowed)
                {
                    return false;
                }
            }

            // 3) 再探测一次，确认目录真的可用。
            using var confirm = await PropFindAsync(_baseUri, "0", cancellationToken).ConfigureAwait(false);
            return confirm.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await PropFindAsync(_baseUri, "1", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<RemoteEntry>();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IOException(
                $"列出 WebDAV 目录失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{_baseUri}）。");
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseMultiStatus(xml);
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var uri = BuildFileUri(relativePath);
        using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IOException(
                $"下载批注失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{Path.GetFileName(uri.LocalPath)}）。");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> UploadAsync(
        string relativePath,
        byte[] content,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var uri = BuildFileUri(relativePath);
        using var response = await SendAsync(HttpMethod.Put, uri, request =>
        {
            var body = new ByteArrayContent(content);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = body;

            // 乐观锁：远端版本与老师裁决时看到的不一致，服务器会回 412，绝不盲目覆盖。
            if (!string.IsNullOrEmpty(expectedETag))
            {
                request.Headers.TryAddWithoutValidation("If-Match", expectedETag);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new IOException(
                $"远端批注「{Path.GetFileName(uri.LocalPath)}」已被其它设备修改，已取消本次上传。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IOException(
                $"上传批注失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{Path.GetFileName(uri.LocalPath)}）。");
        }

        return ReadETagHeader(response);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var uri = BuildFileUri(relativePath);
        using var response = await SendAsync(HttpMethod.Delete, uri, null, cancellationToken).ConfigureAwait(false);

        // 远端本来就没有 = 目标状态已达成。
        if (response.StatusCode == HttpStatusCode.NotFound || response.IsSuccessStatusCode)
        {
            return;
        }

        throw new IOException(
            $"删除远端批注失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{Path.GetFileName(uri.LocalPath)}）。");
    }

    /// <summary>释放内部创建的 <see cref="HttpClient"/>（注入的客户端由调用方负责）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>发起一次带认证与超时的请求；调用方负责释放返回的响应。</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorizationParameter);
        configure?.Invoke(request);

        try
        {
            return await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"连接 WebDAV 服务器超时（{RequestTimeout.TotalSeconds:0} 秒）：{method.Method} {uri}。");
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"访问 WebDAV 服务器失败（{method.Method} {uri}）：{ex.Message}", ex);
        }
    }

    /// <summary>发一次 PROPFIND 请求（<paramref name="depth"/> 为 0 或 1）。</summary>
    private Task<HttpResponseMessage> PropFindAsync(Uri uri, string depth, CancellationToken cancellationToken)
        => SendAsync(PropFindMethod, uri, request =>
        {
            request.Headers.TryAddWithoutValidation("Depth", depth);
            request.Content = new StringContent(PropFindRequestBody, Encoding.UTF8, "application/xml");
        }, cancellationToken);

    /// <summary>解析 207 多状态响应，只保留同步根下的 <c>*.cvcanno</c> 文件。</summary>
    private IReadOnlyList<RemoteEntry> ParseMultiStatus(string xml)
    {
        var entries = new List<RemoteEntry>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return entries;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new IOException($"WebDAV 返回的目录列表无法解析：{ex.Message}", ex);
        }

        foreach (var response in document.Descendants(Dav + "response"))
        {
            var relativePath = ToRelativePath(response.Element(Dav + "href")?.Value);
            if (relativePath is null)
            {
                continue;
            }

            var properties = SelectProperties(response);
            if (properties is null)
            {
                continue;
            }

            // 目录本身（含根目录）不参与同步。
            if (properties.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null)
            {
                continue;
            }

            if (!SyncPathRules.IsAnnotationFileName(Path.GetFileName(relativePath)))
            {
                continue;
            }

            var sizeText = properties.Element(Dav + "getcontentlength")?.Value?.Trim();
            var size = long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;

            var etag = properties.Element(Dav + "getetag")?.Value?.Trim();

            DateTimeOffset? modifiedUtc = null;
            var modifiedText = properties.Element(Dav + "getlastmodified")?.Value;
            if (DateTimeOffset.TryParse(
                    modifiedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var modified))
            {
                modifiedUtc = modified;
            }

            entries.Add(new RemoteEntry(
                relativePath,
                size,
                string.IsNullOrWhiteSpace(etag) ? null : etag,
                modifiedUtc));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return entries;
    }

    /// <summary>取 <c>propstat</c> 里的属性集合：优先 200 的那组，只有一个时直接用它。</summary>
    private static XElement? SelectProperties(XElement response)
    {
        var propStats = response.Elements(Dav + "propstat").ToList();
        if (propStats.Count == 0)
        {
            return response.Element(Dav + "prop");
        }

        foreach (var propStat in propStats)
        {
            var status = propStat.Element(Dav + "status")?.Value;
            if (status is not null && status.Contains(" 200", StringComparison.Ordinal))
            {
                return propStat.Element(Dav + "prop");
            }
        }

        return propStats.Count == 1 ? propStats[0].Element(Dav + "prop") : null;
    }

    /// <summary>
    /// 把响应里的 <c>href</c> 还原成同步根下的相对路径；
    /// 不是本目录下的文件（含目录自身、子目录、越界条目）一律返回 <c>null</c>。
    /// </summary>
    private string? ToRelativePath(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        if (!Uri.TryCreate(_baseUri, href.Trim(), out var absolute))
        {
            return null;
        }

        if (!string.Equals(absolute.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var basePath = _baseUri.AbsolutePath;
        var path = absolute.AbsolutePath;
        if (!path.StartsWith(basePath, StringComparison.Ordinal))
        {
            return null;
        }

        var relative = path[basePath.Length..];

        // 空 = 根目录自身；以 / 结尾或含分隔符 = 子目录 / 子目录里的文件，同步根是平铺目录，不支持。
        if (relative.Length == 0
            || relative.EndsWith('/')
            || relative.Contains('/')
            || relative.Contains('\\'))
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(relative);
        return SyncPathRules.TryGetFlatFileName(decoded, out var name) ? name : null;
    }

    /// <summary>按校验后的文件名拼出请求地址（校验失败抛 <see cref="ArgumentException"/>）。</summary>
    private Uri BuildFileUri(string relativePath)
    {
        var name = SyncPathRules.RequireFlatFileName(relativePath);
        return new Uri(_baseUri, Uri.EscapeDataString(name));
    }

    /// <summary>原样读取响应头里的 <c>ETag</c>（HttpClient 解析失败时也能拿到原始值）。</summary>
    private static string? ReadETagHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("ETag", out var values))
        {
            var raw = values.FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }

        return null;
    }

    private static bool IsMissing(HttpStatusCode status)
        => status is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone;
}
