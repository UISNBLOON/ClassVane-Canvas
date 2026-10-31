using Avalonia.Threading;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Presentation;

namespace ClassVaneCanvas.Export;

/// <summary>
/// 「幻灯片 + 板书批注」导出实现：单页 PNG 与整册 PDF，全部本机离线完成。
///
/// 渲染约定（与放映画布完全一致）：
/// · 笔迹坐标是**页面坐标系**（默认 1280×720），导出时先按目标位图算出等比缩放与居中偏移，
///   再 <c>PushTransform(scale * translate)</c>，然后直接复用 <see cref="InkRenderer.Draw"/>；
/// · 幻灯片位图按「保持宽高比居中适配」画进同一块画布，因此导出的批注与幻灯片严格对齐；
/// · 幻灯片后端不可用（<c>ISlideSource.GetSlideAsync</c> 返回 <c>null</c>）时输出白底 + 批注，
///   老师仍然拿得到一份可用的板书图片。
///
/// PDF 由本文件内的最小写入器直接生成（每页一个 <c>/XObject /Image</c>，FlateDecode 压缩），
/// 不依赖任何第三方 PDF 库。
///
/// 线程模型：Avalonia 的几何 / 画笔对象是**线程亲和**的，因此渲染必须回到调用方的
/// 同步上下文（放映界面就是 UI 线程）继续执行，所有 await 都保留默认续体行为；
/// 只有纯字节运算（Flate 压缩）下放到线程池，避免导出大课件时界面长时间无响应。
/// </summary>
public sealed class AnnotatedExportService : IAnnotatedExportService
{
    /// <summary>页面坐标系默认尺寸（与放映画布一致）。</summary>
    private static readonly Size DefaultPageSize = new(1280, 720);

    /// <summary>渲染 DPI：位图按 1:1 像素输出，不跟随系统缩放。</summary>
    private static readonly Vector RenderDpi = new(96, 96);

    /// <summary>PDF 中每页固定占用 3 个间接对象：页字典 / 图像 XObject / 内容流。</summary>
    private const int PdfObjectsPerPage = 3;

    /// <summary>PDF 固定占用 3 个对象：1 文档目录、2 页树、3 文档信息。</summary>
    private const int PdfFixedObjectCount = 3;

    /// <summary>单页像素数上限，防止误传超大尺寸把内存打满（约等于 8000×5000）。</summary>
    private const long MaxPixelsPerPage = 40_000_000L;

    private readonly InkEngineSettings _settings;

    /// <summary>
    /// 创建导出服务。
    /// </summary>
    /// <param name="settings">笔迹渲染参数；传 <c>null</c> 使用与放映一致的默认参数。</param>
    public AnnotatedExportService(InkEngineSettings? settings = null)
        => _settings = settings ?? new InkEngineSettings();

    /// <inheritdoc />
    public async Task<byte[]> RenderPagePngAsync(
        AnnotationDocument document,
        int pageIndex,
        ISlideSource? slideSource,
        PixelSize size,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var bitmap = await RenderPageAsync(document, pageIndex, slideSource, size, cancellationToken);

        try
        {
            using var buffer = new MemoryStream();
            bitmap.Save(buffer); // Avalonia 自带 PNG 编码
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"PNG 编码失败（第 {pageIndex} 页）：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task ExportPagePngAsync(
        AnnotationDocument document,
        int pageIndex,
        ISlideSource? slideSource,
        PixelSize size,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("导出路径不能为空。", nameof(targetPath));
        }

        var bytes = await RenderPagePngAsync(document, pageIndex, slideSource, size, cancellationToken);

        try
        {
            EnsureDirectory(targetPath);
            await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"写入 PNG 文件失败：{targetPath}（{ex.Message}）", ex);
        }
    }

    /// <inheritdoc />
    public async Task<int> ExportPdfAsync(
        AnnotationDocument document,
        ISlideSource? slideSource,
        PixelSize pageSize,
        string targetPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("导出路径不能为空。", nameof(targetPath));
        }

        ValidateSize(pageSize, nameof(pageSize));

        // 页数取「课件页数 / 后端页数 / 有批注的最大页码」三者的最大值，保证批注不会被漏掉
        var pageCount = ResolvePageCount(document, slideSource);
        var totalObjects = PdfFixedObjectCount + (pageCount * PdfObjectsPerPage);
        var catalogObjectId = 1;
        var pagesObjectId = 2;
        var infoObjectId = 3;

        try
        {
            EnsureDirectory(targetPath);

            await using var stream = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1 << 16,
                useAsync: true);

            var writer = new MinimalPdfWriter(stream, totalObjects);

            // 1) 文件头 + 二进制标记
            writer.WriteHeader();

            // 2) 文档目录 / 页树 / 文档信息；页对象编号预先算好，可前向引用
            var pageObjectIds = new int[pageCount];
            for (var index = 0; index < pageCount; index++)
            {
                pageObjectIds[index] = PdfFixedObjectCount + 1 + (index * PdfObjectsPerPage);
            }

            writer.WriteDocumentObjects(catalogObjectId, pagesObjectId, infoObjectId, pageObjectIds);

            // 3) 逐页：渲染 → 取像素 → Flate 压缩 → 写 页/图像/内容流 三个对象
            for (var page = 1; page <= pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageObjectId = PdfFixedObjectCount + 1 + ((page - 1) * PdfObjectsPerPage);
                var imageObjectId = pageObjectId + 1;
                var contentObjectId = pageObjectId + 2;

                using var bitmap = await RenderPageAsync(document, page, slideSource, pageSize, cancellationToken);

                var rgb = ExtractRgb(bitmap, pageSize); // BGRA → 原始 RGB

                // 压缩是纯 CPU 计算（不碰 Avalonia 对象），放到线程池，导出大课件时界面不卡
                var compressed = await Task.Run(() => CompressRgb(rgb), cancellationToken);

                writer.WriteImagePage(
                    pageObjectId,
                    imageObjectId,
                    contentObjectId,
                    pageSize.Width,
                    pageSize.Height,
                    compressed);

                progress?.Report(page);
            }

            // 4) 交叉引用表 + trailer
            writer.WriteTrailer(catalogObjectId, infoObjectId);
            await stream.FlushAsync(cancellationToken);
            return pageCount;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new IOException($"写入 PDF 文件失败：{targetPath}（{ex.Message}）", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"没有权限写入 PDF 文件：{targetPath}", ex);
        }
        catch (InvalidOperationException)
        {
            // 渲染层已经带了中文说明，不再套一层
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"导出 PDF 失败（{pageCount} 页）：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 渲染单页（白底 + 幻灯片 + 批注）到新的 <see cref="RenderTargetBitmap"/>。
    /// 调用方负责释放返回的位图。
    /// </summary>
    private async Task<RenderTargetBitmap> RenderPageAsync(
        AnnotationDocument document,
        int pageIndex,
        ISlideSource? slideSource,
        PixelSize size,
        CancellationToken cancellationToken)
    {
        if (pageIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "页码从 1 开始。");
        }

        ValidateSize(size, nameof(size));
        EnsureRenderThread();
        cancellationToken.ThrowIfCancellationRequested();

        // 页面逻辑尺寸：优先用幻灯片后端声明的坐标系，保证批注与幻灯片对齐
        var pageSize = slideSource is { PageSize.Width: > 0, PageSize.Height: > 0 } source
            ? source.PageSize
            : DefaultPageSize;

        // 等比缩放 + 居中（contain），保证幻灯片不变形
        var fitScale = Math.Min(size.Width / pageSize.Width, size.Height / pageSize.Height);
        var drawWidth = pageSize.Width * fitScale;
        var drawHeight = pageSize.Height * fitScale;
        var offsetX = (size.Width - drawWidth) / 2d;
        var offsetY = (size.Height - drawHeight) / 2d;

        // 幻灯片位图：取不到就只导出批注（白底），绝不让导出失败
        IImage? slide = null;
        if (slideSource is not null)
        {
            try
            {
                slide = await slideSource.GetSlideAsync(pageIndex, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                slide = null;
            }
        }

        // Avalonia 的几何 / 位图对象只能在 UI 线程创建：统一切回 UI 线程渲染，
        // 这样从后台线程调用导出也不会抛 "Call from invalid thread"。
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var bitmap = new RenderTargetBitmap(size, RenderDpi);
            try
            {
                using (var context = bitmap.CreateDrawingContext())
                {
                    // 1) 白底：批注文件里没有幻灯片时，导出的是一张干净的板书白纸
                    context.FillRectangle(Brushes.White, new Rect(0, 0, size.Width, size.Height));

                    // 2) 幻灯片：保持宽高比居中适配
                    if (slide is not null)
                    {
                        context.DrawImage(slide, new Rect(offsetX, offsetY, drawWidth, drawHeight));
                    }

                    // 3) 板书批注：页面坐标系 → 位图坐标系（先缩放后平移），再交给笔迹渲染器
                    using (context.PushTransform(
                        Matrix.CreateScale(fitScale, fitScale) * Matrix.CreateTranslation(offsetX, offsetY)))
                    {
                        var page = document.FindPage(pageIndex);
                        if (page is not null)
                        {
                            foreach (var stroke in page.Strokes)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                InkRenderer.Draw(context, stroke, _settings);
                            }
                        }
                    }
                }

                return bitmap;
            }
            catch (OperationCanceledException)
            {
                bitmap.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                bitmap.Dispose();
                throw new InvalidOperationException($"渲染第 {pageIndex} 页失败：{ex.Message}", ex);
            }
        });
    }

    /// <summary>
    /// 把渲染结果读成 PDF 需要的「原始 RGB」（每像素 3 字节，行间无填充）。
    ///
    /// 像素读取方式：渲染结果是 <see cref="RenderTargetBitmap"/>，它本身不暴露像素指针，
    /// 因此先转存进一个自己创建、格式可控的 <see cref="WriteableBitmap"/>
    /// （BGRA8888 + 非预乘），锁定帧缓冲后用 <see cref="Marshal.Copy(IntPtr, byte[], int, int)"/>
    /// 拷出字节，再逐像素丢掉 A 通道并按需反预乘，得到 RGB 缓冲。
    /// （导出页始终先铺白底，α 处处为 255，因此预乘 / 非预乘的差异不会影响结果，
    /// 下面仍保留反预乘分支以防后端返回半透明像素。）
    /// </summary>
    private static byte[] ExtractRgb(RenderTargetBitmap bitmap, PixelSize size)
    {
        var width = size.Width;
        var height = size.Height;
        var rowBytes = width * 4;
        var bgra = new byte[rowBytes * height];

        using (var staging = new WriteableBitmap(size, RenderDpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul))
        {
            using (var locked = staging.Lock())
            {
                bitmap.CopyPixels(locked, AlphaFormat.Unpremul);
                rowBytes = locked.RowBytes; // 可能含行尾填充，按实际跨度读取
                bgra = new byte[rowBytes * locked.Size.Height];
                Marshal.Copy(locked.Address, bgra, 0, bgra.Length);
            }
        }

        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var source = y * rowBytes;
            var target = y * width * 3;

            for (var x = 0; x < width; x++, source += 4, target += 3)
            {
                var alpha = bgra[source + 3];
                switch (alpha)
                {
                    case 255:
                        // 不透明（本导出铺了白底，绝大多数像素走这条分支）
                        rgb[target] = bgra[source + 2];
                        rgb[target + 1] = bgra[source + 1];
                        rgb[target + 2] = bgra[source];
                        break;

                    case 0:
                        // 全透明：PDF 页没有透明通道，按白底补齐
                        rgb[target] = 255;
                        rgb[target + 1] = 255;
                        rgb[target + 2] = 255;
                        break;

                    default:
                        // 半透明：反预乘（整数近似）并夹紧，避免溢出成异常颜色
                        var a = (int)alpha;
                        rgb[target] = (byte)Math.Min(255, (bgra[source + 2] * 255) / a);
                        rgb[target + 1] = (byte)Math.Min(255, (bgra[source + 1] * 255) / a);
                        rgb[target + 2] = (byte)Math.Min(255, (bgra[source] * 255) / a);
                        break;
                }
            }
        }

        return rgb;
    }

    /// <summary>用 zlib（<see cref="ZLibStream"/>）压缩原始 RGB，对应 PDF 的 <c>/Filter /FlateDecode</c>。</summary>
    private static byte[] CompressRgb(byte[] rgb)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(rgb, 0, rgb.Length);
        }

        return output.ToArray();
    }

    /// <summary>计算导出页数：课件页数、后端页数、有批注的最大页码三者取最大。</summary>
    private static int ResolvePageCount(AnnotationDocument document, ISlideSource? slideSource)
    {
        var count = document.SlideCount;

        if (slideSource is not null && slideSource.SlideCount > count)
        {
            count = slideSource.SlideCount;
        }

        foreach (var page in document.Pages)
        {
            if (page.PageIndex > count)
            {
                count = page.PageIndex;
            }
        }

        return Math.Max(1, count);
    }

    /// <summary>
    /// 校验当前是否在 Avalonia UI 线程上：笔迹几何（StreamGeometry）与画笔（Pen / Brush）
    /// 都是 <c>AvaloniaObject</c>，只能在其所属线程创建，否则会抛 "Call from invalid thread"。
    /// 提前给出中文提示，避免调用方从后台线程发起导出时拿到莫名其妙的英文异常。
    /// </summary>
    private static void EnsureRenderThread()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                "导出渲染必须在 UI 线程调用：Avalonia 的笔迹几何与画笔对象有线程亲和性。");
        }
    }

    /// <summary>校验导出尺寸；非法尺寸给出中文提示。</summary>
    private static void ValidateSize(PixelSize size, string parameterName)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "导出尺寸必须为正数（宽高都要大于 0）。");
        }

        if ((long)size.Width * size.Height > MaxPixelsPerPage)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"导出尺寸过大（{size.Width}×{size.Height}），单页最多 {MaxPixelsPerPage} 像素。");
        }
    }

    /// <summary>确保目标文件所在目录存在。</summary>
    private static void EnsureDirectory(string targetPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// 最小 PDF 写入器（自研，不引入任何第三方库）。写出的是一个合法 PDF 1.7 文件。
    ///
    /// 文件结构（<c>%</c> 开头的是 PDF 注释，可用文本编辑器直接检查）：
    /// <code>
    /// %PDF-1.7
    /// %<4 个高位字节>                                       —— 二进制标记
    /// 1 0 obj &lt;&lt; /Type /Catalog /Pages 2 0 R &gt;&gt;                    —— 文档目录
    /// 2 0 obj &lt;&lt; /Type /Pages /Count N /Kids [4 0 R 7 0 R ...] &gt;&gt;  —— 页树，/Count 必须等于页数
    /// 3 0 obj &lt;&lt; /Producer (ClassVane Canvas) ... &gt;&gt;              —— 文档信息
    /// 之后每页 3 个对象（第 i 页，1 起）：
    ///   3i+1 &lt;&lt; /Type /Page /Parent 2 0 R /MediaBox [0 0 W H]
    ///          /Resources &lt;&lt; /XObject &lt;&lt; /Im0 (3i+2) 0 R &gt;&gt; &gt;&gt; /Contents (3i+3) 0 R &gt;&gt;
    ///   3i+2 &lt;&lt; /Type /XObject /Subtype /Image /Width w /Height h /ColorSpace /DeviceRGB
    ///          /BitsPerComponent 8 /Filter /FlateDecode /Length L &gt;&gt; stream &lt;L 字节 zlib 数据&gt; endstream
    ///   3i+3 &lt;&lt; /Length M &gt;&gt; stream "q W 0 0 H 0 0 cm /Im0 Do Q" endstream   —— 把图像铺满整页
    /// xref（每项固定 20 字节）+ trailer + startxref + %%EOF
    /// </code>
    ///
    /// 说明：
    /// · 页对象号在写第一页之前就已算好，允许前向引用（PDF 规范允许），无需回填 /Kids；
    /// · 图像数据按「自上而下」的行序存放，配合 <c>W 0 0 H 0 0 cm</c> 正好正立显示；
    /// · 页面大小直接用像素值当 PDF 用户单位（1 像素 = 1 点）。要出 A4 就传 A4@96dpi ≈ 794×1123。
    /// </summary>
    private sealed class MinimalPdfWriter
    {
        private readonly Stream _stream;
        private readonly int _objectCount;

        /// <summary>对象号 → 该对象在文件中的字节偏移（写 xref 用）。</summary>
        private readonly Dictionary<int, long> _offsets = new();

        public MinimalPdfWriter(Stream stream, int objectCount)
        {
            _stream = stream;
            _objectCount = objectCount;
        }

        /// <summary>写文件头与二进制标记。</summary>
        public void WriteHeader()
        {
            WriteAscii("%PDF-1.7\n");

            // 高位字节的注释行：让阅读器 / 版本控制把本文件识别为二进制，避免被按文本换行改写
            _stream.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);
        }

        /// <summary>写文档目录、页树与文档信息三个固定对象。</summary>
        public void WriteDocumentObjects(
            int catalogObjectId,
            int pagesObjectId,
            int infoObjectId,
            IReadOnlyList<int> pageObjectIds)
        {
            // 文档目录
            BeginObject(catalogObjectId);
            WriteAscii(FormattableString.Invariant($"<< /Type /Catalog /Pages {pagesObjectId} 0 R >>\n"));
            EndObject();

            // 页树：/Count 必须等于 /Kids 的数量
            var kids = new StringBuilder();
            foreach (var id in pageObjectIds)
            {
                kids.Append(FormattableString.Invariant($"{id} 0 R "));
            }

            BeginObject(pagesObjectId);
            WriteAscii(FormattableString.Invariant(
                $"<< /Type /Pages /Count {pageObjectIds.Count} /Kids [{kids.ToString().TrimEnd()}] >>\n"));
            EndObject();

            // 文档信息（纯 ASCII，避免 PDF 头部的编码歧义）
            BeginObject(infoObjectId);
            WriteAscii("<< /Producer (ClassVane Canvas) /Creator (ClassVane Canvas) " +
                       "/Title (Annotated slides) >>\n");
            EndObject();
        }

        /// <summary>写一页：页字典 + 图像 XObject + 内容流。</summary>
        public void WriteImagePage(
            int pageObjectId,
            int imageObjectId,
            int contentObjectId,
            int width,
            int height,
            byte[] compressedRgb)
        {
            // 页字典
            BeginObject(pageObjectId);
            WriteAscii(FormattableString.Invariant(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im0 {imageObjectId} 0 R >> /ProcSet [/PDF /ImageC] >> /Contents {contentObjectId} 0 R >>\n"));
            EndObject();

            // 图像 XObject：原始 RGB + FlateDecode
            BeginObject(imageObjectId);
            WriteAscii(FormattableString.Invariant(
                $"<</Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressedRgb.Length}>>\n"));
            WriteAscii("stream\n");
            _stream.Write(compressedRgb, 0, compressedRgb.Length);
            WriteAscii("\nendstream\n");
            EndObject();

            // 内容流：q ... Q 之间用 cm 把单位正方形放大成整页，再绘制图像
            var content = FormattableString.Invariant($"q\n{width} 0 0 {height} 0 0 cm\n/Im0 Do\nQ\n");
            var contentBytes = Encoding.ASCII.GetBytes(content);

            BeginObject(contentObjectId);
            WriteAscii(FormattableString.Invariant($"<< /Length {contentBytes.Length} >>\n"));
            WriteAscii("stream\n");
            _stream.Write(contentBytes, 0, contentBytes.Length);
            WriteAscii("endstream\n");
            EndObject();
        }

        /// <summary>写 xref 交叉引用表、trailer、startxref 与文件结束标记。</summary>
        public void WriteTrailer(int rootObjectId, int infoObjectId)
        {
            var xrefOffset = _stream.Position;

            WriteAscii("xref\n");
            WriteAscii(FormattableString.Invariant($"0 {_objectCount + 1}\n"));

            // 对象 0 是固定表头项：偏移 0、世代 65535、空闲
            WriteAscii("0000000000 65535 f \n");

            for (var id = 1; id <= _objectCount; id++)
            {
                var offset = _offsets.TryGetValue(id, out var value) ? value : 0L;

                // 每项严格 20 字节：10 位偏移 + 空格 + 5 位世代 + 空格 + 类型 + 2 字节换行
                WriteAscii(FormattableString.Invariant($"{offset:D10} 00000 n \n"));
            }

            WriteAscii(FormattableString.Invariant(
                $"trailer\n<< /Size {_objectCount + 1} /Root {rootObjectId} 0 R /Info {infoObjectId} 0 R >>\n"));
            WriteAscii(FormattableString.Invariant($"startxref\n{xrefOffset}\n"));
            WriteAscii("%%EOF\n");
        }

        private void BeginObject(int objectId)
        {
            _offsets[objectId] = _stream.Position;
            WriteAscii(FormattableString.Invariant($"{objectId} 0 obj\n"));
        }

        private void EndObject() => WriteAscii("endobj\n");

        private void WriteAscii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            _stream.Write(bytes, 0, bytes.Length);
        }
    }
}
