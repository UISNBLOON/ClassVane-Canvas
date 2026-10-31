using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Models.Annotations;

/// <summary>.cvcanno 批注包格式常量。</summary>
public static class CvcannoFormat
{
    /// <summary>格式标识（写入文件头，导入时校验）。</summary>
    public const string Identifier = "classvane-canvas/cvcanno";

    /// <summary>当前格式版本。</summary>
    public const int Version = 1;

    /// <summary>批注包扩展名（含点）。</summary>
    public const string Extension = ".cvcanno";
}

/// <summary>
/// 一份课件对应的批注文档，序列化后即 <c>*.cvcanno</c> 独立批注包。
///
/// 硬性约束：
/// · 批注全部外置在此文件中，**绝不修改原始 PPT / pptx 压缩包、不写 CustomXml**；
/// · 通过 <see cref="CoursewareHash"/>（PPT 文件 SHA256）与课件绑定，哈希不一致不加载；
/// · 文件中**不包含任何播放进度信息**（进度只存本机 SQLite）。
/// </summary>
public sealed class AnnotationDocument
{
    /// <summary>格式标识，固定为 <see cref="CvcannoFormat.Identifier"/>。</summary>
    public string Format { get; set; } = CvcannoFormat.Identifier;

    /// <summary>格式版本。</summary>
    public int FormatVersion { get; set; } = CvcannoFormat.Version;

    /// <summary>绑定的课件 SHA256（小写十六进制）。加载前必须与当前课件比对。</summary>
    public string CoursewareHash { get; set; } = string.Empty;

    /// <summary>课件文件名（仅展示用，不参与匹配）。</summary>
    public string CoursewareFileName { get; set; } = string.Empty;

    /// <summary>课件总页数（用于页数不一致时的提示）。</summary>
    public int SlideCount { get; set; }

    /// <summary>最后修改时间（UTC）。同步冲突检测与合并取较晚值。</summary>
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>创建该批注包的设备标识。</summary>
    public string OriginDeviceId { get; set; } = string.Empty;

    /// <summary>分页笔迹。</summary>
    public List<InkPage> Pages { get; set; } = new();

    /// <summary>笔迹总数。</summary>
    public int TotalStrokeCount => Pages.Sum(p => p.Strokes.Count);

    /// <summary>是否没有任何笔迹。</summary>
    public bool IsEmpty => TotalStrokeCount == 0;

    /// <summary>有笔迹的页数。</summary>
    public int AnnotatedPageCount => Pages.Count(p => p.HasInk);

    /// <summary>按课件信息创建一个空批注文档。</summary>
    public static AnnotationDocument CreateFor(
        string coursewareHash,
        string coursewareFileName,
        int slideCount,
        string deviceId)
        => new()
        {
            CoursewareHash = coursewareHash,
            CoursewareFileName = coursewareFileName,
            SlideCount = slideCount,
            OriginDeviceId = deviceId,
            ModifiedUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>取指定页（1 起），不存在则创建。</summary>
    public InkPage GetOrCreatePage(int pageIndex)
    {
        var page = Pages.FirstOrDefault(p => p.PageIndex == pageIndex);
        if (page is null)
        {
            page = new InkPage { PageIndex = pageIndex };
            Pages.Add(page);
            Pages.Sort((a, b) => a.PageIndex.CompareTo(b.PageIndex));
        }

        return page;
    }

    /// <summary>取指定页，不存在返回 <c>null</c>。</summary>
    public InkPage? FindPage(int pageIndex) => Pages.FirstOrDefault(p => p.PageIndex == pageIndex);

    /// <summary>深拷贝。</summary>
    public AnnotationDocument Clone() => new()
    {
        Format = Format,
        FormatVersion = FormatVersion,
        CoursewareHash = CoursewareHash,
        CoursewareFileName = CoursewareFileName,
        SlideCount = SlideCount,
        ModifiedUtc = ModifiedUtc,
        OriginDeviceId = OriginDeviceId,
        Pages = Pages.Select(p => p.Clone()).ToList(),
    };
}
