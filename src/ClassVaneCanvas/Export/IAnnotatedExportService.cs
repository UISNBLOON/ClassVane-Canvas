using Avalonia;
using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Presentation;

namespace ClassVaneCanvas.Export;

/// <summary>把「幻灯片 + 板书批注」导出为图片 / PDF。</summary>
public interface IAnnotatedExportService
{
    /// <summary>渲染某一页（含批注）为 PNG 字节；幻灯片不可用时只输出批注与白底。</summary>
    Task<byte[]> RenderPagePngAsync(
        AnnotationDocument document,
        int pageIndex,
        ISlideSource? slideSource,
        PixelSize size,
        CancellationToken cancellationToken = default);

    /// <summary>导出某一页为 PNG 文件。</summary>
    Task ExportPagePngAsync(
        AnnotationDocument document,
        int pageIndex,
        ISlideSource? slideSource,
        PixelSize size,
        string targetPath,
        CancellationToken cancellationToken = default);

    /// <summary>导出整个课件（含批注）为 PDF，返回写出的页数。</summary>
    Task<int> ExportPdfAsync(
        AnnotationDocument document,
        ISlideSource? slideSource,
        PixelSize pageSize,
        string targetPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
