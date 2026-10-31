using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassVaneCanvas.Pptx.Model;

namespace ClassVaneCanvas.Pptx;

/// <summary>
/// 幻灯片渲染器：把 <see cref="PptxSlide"/> 的节点树画到 Avalonia <see cref="DrawingContext"/> 上。
///
/// 约定：
/// · 一切绘制都在<b>页面坐标系</b>（默认 1280×720 逻辑像素）内完成，调用方自己 <c>PushTransform</c> 做缩放；
/// · 文本用 <see cref="FormattedText"/>，字体缺失时按回退链替换，并把替换关系记入 <see cref="FontSubstitutions"/>；
/// · 图片按部件名缓存解码结果，逐帧渲染不会重复解码；
/// · 旋转绕节点中心、透明度用 <c>PushOpacity</c>、擦除动画用 <c>PushClip</c> 实现；
/// · 文本排版结果按（文本框 + 宽度）缓存，缓存命中后整页渲染不重复排版。
/// </summary>
public sealed class PptxRenderer
{
    /// <summary>字体回退链（按优先级排列）。</summary>
    public const string FallbackFontChain =
        "PingFang SC, Microsoft YaHei UI, Hiragino Sans GB, Source Han Sans SC, Noto Sans CJK SC, Segoe UI";

    /// <summary>解析字体回退链得到的候选字族名。</summary>
    private static readonly string[] FallbackFonts =
    {
        "PingFang SC",
        "Microsoft YaHei UI",
        "Hiragino Sans GB",
        "Source Han Sans SC",
        "Noto Sans CJK SC",
        "Segoe UI",
    };

    private readonly ConcurrentDictionary<string, Bitmap?> _imageCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fontSubstitutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontFamily> _fontFamilyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _fontAvailabilityCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TextCacheKey, FormattedText[]> _textCache = new();
    private readonly Dictionary<GeometryCacheKey, Geometry> _geometryCache = new();
    private readonly object _sync = new();

    /// <summary>字体替换记录（请求字体 → 实际使用字体）。</summary>
    public IReadOnlyDictionary<string, string> FontSubstitutions
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_fontSubstitutions, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>已解码图片数量（诊断 / 验证用）。</summary>
    public int CachedImageCount => _imageCache.Count;

    /// <summary>清空字体替换记录。</summary>
    public void ClearFontSubstitutions()
    {
        lock (_sync)
        {
            _fontSubstitutions.Clear();
        }
    }

    /// <summary>清空位图 / 排版 / 几何缓存（切换课件时调用）。</summary>
    public void ClearCaches()
    {
        foreach (var bitmap in _imageCache.Values)
        {
            bitmap?.Dispose();
        }

        _imageCache.Clear();
        lock (_sync)
        {
            _textCache.Clear();
            _geometryCache.Clear();
        }
    }

    /// <summary>渲染整页（含页面背景）。</summary>
    /// <param name="context">绘制上下文。</param>
    /// <param name="slide">页面模型。</param>
    /// <param name="frame">动画帧；<c>null</c> 表示静态渲染。</param>
    public void RenderSlide(DrawingContext context, PptxSlide slide, PptxAnimationFrame? frame = null)
        => RenderSlide(context, slide, frame?.States);

    /// <summary>渲染整页（含页面背景）。</summary>
    public void RenderSlide(
        DrawingContext context,
        PptxSlide slide,
        IReadOnlyDictionary<int, PptxNodeVisualState>? states)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(slide);

        RenderBackground(context, slide);

        foreach (var node in slide.Nodes)
        {
            var state = ResolveState(states, node.Id);
            RenderNode(context, node, state);
        }
    }

    /// <summary>只画页面背景。</summary>
    public void RenderBackground(DrawingContext context, PptxSlide slide)
    {
        var pageRect = new Rect(0, 0, slide.PageSize.Width, slide.PageSize.Height);
        if (slide.BackgroundFill is { } fill && fill is not PptxNoFill)
        {
            context.DrawRectangle(CreateBrush(fill, pageRect), null, pageRect);
        }
        else
        {
            context.DrawRectangle(new SolidColorBrush(slide.Background), null, pageRect);
        }
    }

    /// <summary>
    /// 渲染一次转场：由调用方给出新旧两页各自的视觉状态（透明度 / 位移 / 裁剪）。
    /// </summary>
    public void RenderTransition(
        DrawingContext context,
        PptxSlide previous,
        PptxSlide next,
        TransitionFrame frame,
        IReadOnlyDictionary<int, PptxNodeVisualState>? previousStates = null,
        IReadOnlyDictionary<int, PptxNodeVisualState>? nextStates = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frame);

        RenderPageWithState(context, previous, frame.Previous, previousStates);
        RenderPageWithState(context, next, frame.Next, nextStates);
    }

    /// <summary>按页面级视觉状态渲染一页。</summary>
    public void RenderPageWithState(
        DrawingContext context,
        PptxSlide slide,
        PptxPageVisualState state,
        IReadOnlyDictionary<int, PptxNodeVisualState>? states)
    {
        if (slide is null || state.Opacity <= 1e-6 || state.ClipProgress <= 1e-6)
        {
            return;
        }

        var clip = CreateClip(new Rect(slide.PageSize), state.ClipProgress, state.ClipMode, state.ClipFrom);
        if (clip is { } clipRect)
        {
            using (context.PushClip(clipRect))
            {
                DrawPage();
            }
        }
        else
        {
            DrawPage();
        }

        void DrawPage()
        {
            var matrix = Matrix.CreateTranslation(state.TranslateX, state.TranslateY);
            if (!matrix.IsIdentity)
            {
                using (context.PushTransform(matrix))
                {
                    DrawWithOpacity();
                }
            }
            else
            {
                DrawWithOpacity();
            }
        }

        void DrawWithOpacity()
        {
            if (state.Opacity < 1d - 1e-6)
            {
                using (context.PushOpacity(Math.Clamp(state.Opacity, 0d, 1d)))
                {
                    RenderSlide(context, slide, states);
                }
            }
            else
            {
                RenderSlide(context, slide, states);
            }
        }
    }

    /// <summary>渲染单个节点（含动画变换）。</summary>
    public void RenderNode(DrawingContext context, PptxNode node, PptxNodeVisualState state)
    {
        if (node.Hidden || state.IsInvisible)
        {
            return;
        }

        if (node.Rect.Width <= 0d && node.Rect.Height <= 0d && node is not PptxLineNode)
        {
            return;
        }

        // 擦除动画：先在页面坐标系里裁出可见区域。
        var clip = CreateClip(node.Rect, state.ClipProgress, PptxTransitionClipMode.Edge, state.ClipFrom);
        if (clip is { } clipRect)
        {
            using (context.PushClip(clipRect))
            {
                RenderWithTransform();
            }
        }
        else
        {
            RenderWithTransform();
        }

        void RenderWithTransform()
        {
            var center = node.Rect.Center;
            var matrix = Matrix.CreateTranslation(-center.X, -center.Y)
                         * Matrix.CreateScale(state.Scale, state.Scale)
                         * Matrix.CreateRotation(node.Rotation * Math.PI / 180d)
                         * Matrix.CreateTranslation(
                             center.X + state.TranslateX,
                             center.Y + state.TranslateY);

            if (matrix.IsIdentity)
            {
                RenderWithOpacity();
            }
            else
            {
                using (context.PushTransform(matrix))
                {
                    RenderWithOpacity();
                }
            }
        }

        void RenderWithOpacity()
        {
            var opacity = node.Opacity * state.Opacity;
            if (opacity < 1d - 1e-6)
            {
                using (context.PushOpacity(Math.Clamp(opacity, 0d, 1d)))
                {
                    RenderNodeCore(context, node);
                }
            }
            else
            {
                RenderNodeCore(context, node);
            }
        }
    }

    /// <summary>按节点类型分发绘制（不含动画变换）。</summary>
    private void RenderNodeCore(DrawingContext context, PptxNode node)
    {
        switch (node)
        {
            case PptxGroupNode group:
                RenderGroup(context, group);
                break;
            case PptxShapeNode shape:
                RenderShape(context, shape);
                break;
            case PptxTextBoxNode textBox:
                RenderTextBox(context, textBox);
                break;
            case PptxPictureNode picture:
                RenderPicture(context, picture);
                break;
            case PptxLineNode line:
                RenderLine(context, line);
                break;
            case PptxTableNode table:
                RenderTable(context, table);
                break;
            case PptxSmartArtNode smartArt:
                RenderPlaceholderBox(context, smartArt.Rect, smartArt.PlaceholderText, Color.FromRgb(0x8A, 0x6D, 0x3B));
                break;
            case PptxVideoNode video:
                RenderVideo(context, video);
                break;
            case PptxAudioNode audio:
                RenderAudio(context, audio);
                break;
            case PptxUnknownNode unknown:
                RenderPlaceholderBox(context, unknown.Rect, unknown.PlaceholderText, Color.FromRgb(0x77, 0x77, 0x77));
                break;
        }
    }

    // ───────────────────────── 各类节点 ─────────────────────────

    /// <summary>组合：绕组合中心旋转后依次绘制子节点。</summary>
    private void RenderGroup(DrawingContext context, PptxGroupNode group)
    {
        if (Math.Abs(group.Rotation) < 1e-6)
        {
            foreach (var child in group.Children)
            {
                RenderNode(context, child, PptxNodeVisualState.Identity);
            }

            return;
        }

        var center = group.Rect.Center;
        var matrix = Matrix.CreateTranslation(-center.X, -center.Y)
                     * Matrix.CreateRotation(group.Rotation * Math.PI / 180d)
                     * Matrix.CreateTranslation(center.X, center.Y);

        using (context.PushTransform(matrix))
        {
            foreach (var child in group.Children)
            {
                RenderNode(context, child, PptxNodeVisualState.Identity);
            }
        }
    }

    /// <summary>自选图形：填充 + 描边 + 文本。</summary>
    private void RenderShape(DrawingContext context, PptxShapeNode shape)
    {
        var geometry = GetGeometry(shape);
        var brush = shape.Fill is PptxNoFill ? null : CreateBrush(shape.Fill, shape.Rect);
        var pen = CreatePen(shape.Stroke);

        if (geometry is not null)
        {
            context.DrawGeometry(brush, pen, geometry);
        }
        else
        {
            // 自定义几何：路径数据位于「0..宽 / 0..高」的路径空间，平移到节点左上角即可。
            context.DrawRectangle(brush, pen, shape.Rect);
        }

        if (shape.Text is { } text)
        {
            DrawTextBody(context, text, shape.Rect);
        }
    }

    /// <summary>文本框。</summary>
    private void RenderTextBox(DrawingContext context, PptxTextBoxNode textBox)
    {
        var brush = textBox.Fill is PptxNoFill ? null : CreateBrush(textBox.Fill, textBox.Rect);
        var pen = CreatePen(textBox.Stroke);
        if (brush is not null || pen is not null)
        {
            context.DrawRectangle(brush, pen, textBox.Rect);
        }

        DrawTextBody(context, textBox.Text, textBox.Rect, null);
    }

    /// <summary>图片：按部件名缓存解码结果，支持 srcRect 裁剪。</summary>
    private void RenderPicture(DrawingContext context, PptxPictureNode picture)
    {
        var bitmap = GetBitmap(picture.PartName, picture.Data);
        if (bitmap is null)
        {
            RenderPlaceholderBox(context, picture.Rect, "图片解码失败", Color.FromRgb(0x99, 0x33, 0x33));
            return;
        }

        var destination = picture.Rect;
        var crop = picture.Crop;
        var hasCrop = crop.Left > 0 || crop.Top > 0 || crop.Right > 0 || crop.Bottom > 0;

        if (!hasCrop)
        {
            context.DrawImage(bitmap, destination);
            return;
        }

        var pixelWidth = bitmap.PixelSize.Width;
        var pixelHeight = bitmap.PixelSize.Height;
        var source = new Rect(
            crop.Left * pixelWidth,
            crop.Top * pixelHeight,
            Math.Max(1d, pixelWidth * (1d - crop.Left - crop.Right)),
            Math.Max(1d, pixelHeight * (1d - crop.Top - crop.Bottom)));

        context.DrawImage(bitmap, source, destination);
    }

    /// <summary>直线 / 连接线（含虚线、箭头）。</summary>
    private void RenderLine(DrawingContext context, PptxLineNode line)
    {
        if (line.Stroke.IsNone)
        {
            return;
        }

        var brush = new SolidColorBrush(line.Stroke.Color);
        var pen = new Pen(brush, Math.Max(0.5d, line.Stroke.WidthPx), MapDash(line.Stroke.DashStyle), PenLineCap.Flat);

        var start = line.Start;
        var end = line.End;
        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        if (length < 1e-6)
        {
            return;
        }

        // 箭头按线宽缩放，三角形直接画在端点外侧。
        var dx = (end.X - start.X) / length;
        var dy = (end.Y - start.Y) / length;
        var headLength = Math.Max(6d, line.Stroke.WidthPx * 4d);

        var lineStart = line.HeadEnd == PptxArrowHead.None ? start : new Point(start.X + dx * headLength, start.Y + dy * headLength);
        var lineEnd = line.TailEnd == PptxArrowHead.None ? end : new Point(end.X - dx * headLength, end.Y - dy * headLength);

        context.DrawLine(pen, lineStart, lineEnd);

        if (line.HeadEnd != PptxArrowHead.None)
        {
            DrawArrowHead(context, brush, start, dx, dy, headLength, line.HeadEnd);
        }

        if (line.TailEnd != PptxArrowHead.None)
        {
            DrawArrowHead(context, brush, end, -dx, -dy, headLength, line.TailEnd);
        }
    }

    /// <summary>画箭头。</summary>
    private static void DrawArrowHead(
        DrawingContext context,
        IBrush brush,
        Point tip,
        double dx,
        double dy,
        double length,
        PptxArrowHead kind)
    {
        var halfWidth = length * 0.45d;
        var baseX = tip.X - dx * length;
        var baseY = tip.Y - dy * length;
        var normalX = -dy;
        var normalY = dx;

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            switch (kind)
            {
                case PptxArrowHead.Oval:
                    stream.BeginFigure(new Point(tip.X - dx * length * 0.5d, tip.Y - dy * length * 0.5d), true);
                    stream.LineTo(new Point(tip.X - dx * length * 0.5d + normalX * halfWidth, tip.Y - dy * length * 0.5d + normalY * halfWidth));
                    stream.LineTo(new Point(tip.X + dx * length * 0.5d, tip.Y + dy * length * 0.5d));
                    stream.LineTo(new Point(tip.X - dx * length * 0.5d - normalX * halfWidth, tip.Y - dy * length * 0.5d - normalY * halfWidth));
                    break;
                case PptxArrowHead.Diamond:
                    stream.BeginFigure(tip, true);
                    stream.LineTo(new Point(baseX + normalX * halfWidth, baseY + normalY * halfWidth));
                    stream.LineTo(new Point(baseX - dx * length * 0.5d, baseY - dy * length * 0.5d));
                    stream.LineTo(new Point(baseX - normalX * halfWidth, baseY - normalY * halfWidth));
                    break;
                case PptxArrowHead.Arrow:
                    stream.BeginFigure(tip, true);
                    stream.LineTo(new Point(baseX + normalX * halfWidth, baseY + normalY * halfWidth));
                    stream.LineTo(new Point(baseX + dx * length * 0.25d, baseY + dy * length * 0.25d));
                    stream.LineTo(new Point(baseX - normalX * halfWidth, baseY - normalY * halfWidth));
                    break;
                case PptxArrowHead.Stealth:
                    stream.BeginFigure(tip, true);
                    stream.LineTo(new Point(baseX + normalX * halfWidth, baseY + normalY * halfWidth));
                    stream.LineTo(new Point(baseX, baseY));
                    stream.LineTo(new Point(baseX - normalX * halfWidth, baseY - normalY * halfWidth));
                    break;
                default:
                    stream.BeginFigure(tip, true);
                    stream.LineTo(new Point(baseX + normalX * halfWidth, baseY + normalY * halfWidth));
                    stream.LineTo(new Point(baseX - normalX * halfWidth, baseY - normalY * halfWidth));
                    break;
            }

            stream.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    /// <summary>表格：底色 + 边框 + 单元格文字（合并单元格由解析层给出跨行列矩形）。</summary>
    private void RenderTable(DrawingContext context, PptxTableNode table)
    {
        foreach (var row in table.Rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.IsMergeContinuation)
                {
                    continue; // 被合并覆盖的从属单元格不重复绘制
                }

                if (cell.Fill is not PptxNoFill)
                {
                    context.DrawRectangle(CreateBrush(cell.Fill, cell.Rect), null, cell.Rect);
                }

                DrawCellBorder(context, cell.BorderTop, new Point(cell.Rect.X, cell.Rect.Y), new Point(cell.Rect.Right, cell.Rect.Y));
                DrawCellBorder(context, cell.BorderBottom, new Point(cell.Rect.X, cell.Rect.Bottom), new Point(cell.Rect.Right, cell.Rect.Bottom));
                DrawCellBorder(context, cell.BorderLeft, new Point(cell.Rect.X, cell.Rect.Y), new Point(cell.Rect.X, cell.Rect.Bottom));
                DrawCellBorder(context, cell.BorderRight, new Point(cell.Rect.Right, cell.Rect.Y), new Point(cell.Rect.Right, cell.Rect.Bottom));

                if (cell.Text is { } text)
                {
                    var contentRect = new Rect(
                        cell.Rect.X + cell.MarginLeftPx,
                        cell.Rect.Y + cell.MarginTopPx,
                        Math.Max(0d, cell.Rect.Width - cell.MarginLeftPx - cell.MarginRightPx),
                        Math.Max(0d, cell.Rect.Height - cell.MarginTopPx - cell.MarginBottomPx));

                    DrawTextBody(context, text, contentRect, null, cell.Anchor);
                }
            }
        }
    }

    /// <summary>画单元格边框。</summary>
    private static void DrawCellBorder(DrawingContext context, PptxStroke? stroke, Point start, Point end)
    {
        if (stroke is null || stroke.IsNone || stroke.Color.A == 0)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(stroke.Color), Math.Max(0.5d, stroke.WidthPx), MapDash(stroke.DashStyle));
        context.DrawLine(pen, start, end);
    }

    /// <summary>视频占位框：本内核不内嵌解码，只画提示。</summary>
    private void RenderVideo(DrawingContext context, PptxVideoNode video)
    {
        var rect = video.Rect;
        if (video.PosterData is { Length: > 0 } poster && video.PosterPartName is { } posterPart)
        {
            var bitmap = GetBitmap(posterPart, poster);
            if (bitmap is not null)
            {
                context.DrawImage(bitmap, rect);
            }
            else
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, rect);
            }
        }
        else
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, rect);
        }

        var center = rect.Center;
        var size = Math.Max(12d, Math.Min(rect.Width, rect.Height) * 0.18d);
        var triangle = new StreamGeometry();
        using (var stream = triangle.Open())
        {
            stream.BeginFigure(new Point(center.X - size * 0.4d, center.Y - size * 0.6d), true);
            stream.LineTo(new Point(center.X + size * 0.7d, center.Y));
            stream.LineTo(new Point(center.X - size * 0.4d, center.Y + size * 0.6d));
            stream.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, triangle);

        var label = string.IsNullOrEmpty(video.Media.PartName)
            ? "视频（未找到媒体部件）"
            : $"视频：{System.IO.Path.GetFileName(video.Media.PartName)}（使用系统播放器打开）";

        // 提示文字压在封面上时可能看不清：先铺一条半透明底栏，再写白字。
        var barHeight = Math.Clamp(rect.Height * 0.22d, 18d, 40d);
        var bar = new Rect(rect.X, rect.Bottom - barHeight, rect.Width, barHeight);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, bar);
        DrawLabel(context, bar, label, Colors.White);
    }

    /// <summary>音频提示：小图标 + 文件名。</summary>
    private void RenderAudio(DrawingContext context, PptxAudioNode audio)
    {
        var rect = audio.Rect;
        var iconSize = Math.Max(10d, Math.Min(rect.Width, rect.Height));
        var iconRect = new Rect(rect.X, rect.Y + (rect.Height - iconSize) / 2d, iconSize, iconSize);

        context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2C, 0x6F, 0xB8)), null, iconRect);

        var glyph = new FormattedText(
            "♪",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            ResolveTypeface(null, false, false),
            iconSize * 0.62d,
            Brushes.White);

        context.DrawText(glyph, new Point(iconRect.X + iconSize * 0.26d, iconRect.Y + iconSize * 0.12d));

        var name = string.IsNullOrEmpty(audio.Media.PartName)
            ? "音频（未找到媒体部件）"
            : $"音频：{System.IO.Path.GetFileName(audio.Media.PartName)}";

        var text = new FormattedText(
            name,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            ResolveTypeface(null, false, false),
            Math.Max(9d, iconSize * 0.4d),
            new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));

        context.DrawText(text, new Point(iconRect.Right + 6d, iconRect.Y + (iconSize - text.Height) / 2d));
    }

    /// <summary>降级占位框（虚线边框 + 居中提示文字）。</summary>
    private void RenderPlaceholderBox(DrawingContext context, Rect rect, string message, Color accent)
    {
        if (rect.Width <= 0d || rect.Height <= 0d)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(accent), 1.2d, new DashStyle(new double[] { 4d, 3d }, 0d));
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B)), pen, rect);
        DrawLabel(context, rect, message, accent);
    }

    /// <summary>在矩形内画一行居中提示文字。</summary>
    private void DrawLabel(DrawingContext context, Rect rect, string message, Color color)
    {
        var fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.12d, 10d, 18d);
        var text = new FormattedText(
            message,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            ResolveTypeface(null, false, false),
            fontSize,
            new SolidColorBrush(color))
        {
            MaxTextWidth = Math.Max(10d, rect.Width - 8d),
            TextAlignment = TextAlignment.Center,
        };

        context.DrawText(text, new Point(rect.X + 4d, rect.Center.Y - text.Height / 2d));
    }

    // ───────────────────────── 文本 ─────────────────────────

    /// <summary>
    /// 绘制文本框内容：逐段排版、按段对齐、按 run 应用字体 / 字号 / 颜色 / 下划线。
    /// </summary>
    /// <param name="context">绘制上下文。</param>
    /// <param name="body">文本框内容。</param>
    /// <param name="rect">文本框矩形（已含内边距之外的内容区）。</param>
    /// <param name="fallbackBody">备用内容（当前未使用，保留给调用方扩展）。</param>
    /// <param name="anchorOverride">垂直锚点覆盖（表格单元格用）。</param>
    public void DrawTextBody(
        DrawingContext context,
        PptxTextBody body,
        Rect rect,
        PptxTextBody? fallbackBody = null,
        PptxTextAnchor? anchorOverride = null)
    {
        _ = fallbackBody;
        if (body.Paragraphs.Count == 0)
        {
            return;
        }

        var scale = body.FontScale is > 0 and <= 1 ? body.FontScale : 1d;
        var contentRect = new Rect(
            rect.X + body.InsetLeftPx * scale,
            rect.Y + body.InsetTopPx * scale,
            Math.Max(1d, rect.Width - (body.InsetLeftPx + body.InsetRightPx) * scale),
            Math.Max(1d, rect.Height - (body.InsetTopPx + body.InsetBottomPx) * scale));

        var paragraphs = GetFormattedParagraphs(body, contentRect.Width);
        var heights = new double[paragraphs.Length];
        var totalHeight = 0d;

        for (var i = 0; i < paragraphs.Length; i++)
        {
            var paragraph = body.Paragraphs[i];
            heights[i] = paragraph.SpaceBeforePx * scale + Math.Max(paragraphs[i].Height, 1d) + paragraph.SpaceAfterPx * scale;
            totalHeight += heights[i];
        }

        var anchor = anchorOverride ?? body.Anchor;
        var y = anchor switch
        {
            PptxTextAnchor.Middle => contentRect.Y + Math.Max(0d, (contentRect.Height - totalHeight) / 2d),
            PptxTextAnchor.Bottom => contentRect.Y + Math.Max(0d, contentRect.Height - totalHeight),
            _ => contentRect.Y,
        };

        var numberCounters = new Dictionary<int, int>();

        for (var i = 0; i < paragraphs.Length; i++)
        {
            var paragraph = body.Paragraphs[i];
            var formatted = paragraphs[i];
            y += paragraph.SpaceBeforePx * scale;

            var indent = paragraph.IndentPx * scale;
            var hanging = paragraph.HangingIndentPx * scale;
            var textX = contentRect.X + indent;
            var textWidth = Math.Max(1d, contentRect.Width - indent);

            if (paragraph.Bullet.Kind != PptxBulletKind.None)
            {
                DrawBullet(context, paragraph, formatted, contentRect.X + indent + hanging, y, scale, numberCounters);
            }

            // 段落级对齐：FormattedText 的 MaxTextWidth 已经按缩进后的宽度设置。
            if (Math.Abs(textWidth - contentRect.Width) > 0.5d)
            {
                formatted.MaxTextWidth = textWidth;
            }

            context.DrawText(formatted, new Point(textX, y));
            y += heights[i] - paragraph.SpaceBeforePx * scale;
        }
    }

    /// <summary>画项目符号 / 编号。</summary>
    private void DrawBullet(
        DrawingContext context,
        PptxParagraph paragraph,
        FormattedText paragraphText,
        double x,
        double y,
        double scale,
        Dictionary<int, int> counters)
    {
        var bullet = paragraph.Bullet;
        var run = paragraph.Runs.FirstOrDefault();
        var fontSize = (run?.FontSizePx ?? 24d) * scale;
        var bulletSize = fontSize * Math.Clamp(bullet.SizePercent / 100d, 0.5d, 1.4d);
        var color = bullet.Color ?? run?.Color ?? Colors.Black;

        string glyph;
        if (bullet.Kind == PptxBulletKind.Number)
        {
            counters.TryGetValue(paragraph.Level, out var index);
            index++;
            counters[paragraph.Level] = index;
            glyph = FormatNumber(bullet.StartAt + index - 1, bullet.NumberType);
        }
        else
        {
            glyph = string.IsNullOrEmpty(bullet.Character) ? "•" : bullet.Character;
        }

        var formatted = new FormattedText(
            glyph,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            ResolveTypeface(run?.FontFamily, run?.Bold ?? false, run?.Italic ?? false),
            bulletSize,
            new SolidColorBrush(color));

        // 符号基线大致与首行基线对齐。
        var baseline = paragraphText.Baseline > 0 ? paragraphText.Baseline : bulletSize;
        context.DrawText(formatted, new Point(x, y + Math.Max(0d, baseline - formatted.Baseline)));
    }

    /// <summary>编号格式化。</summary>
    private static string FormatNumber(int value, string type)
    {
        var suffix = type switch
        {
            "arabicPeriod" => ".",
            "arabicParenR" => ")",
            "arabicParenBoth" => ")",
            "romanUcPeriod" => ".",
            "romanLcPeriod" => ".",
            _ => ".",
        };

        var text = type.StartsWith("roman", StringComparison.OrdinalIgnoreCase)
            ? ToRoman(value, type.Contains("Uc", StringComparison.Ordinal))
            : value.ToString(CultureInfo.InvariantCulture);

        return type switch
        {
            "arabicParenBoth" => $"({text})",
            "arabicParenR" => $"{text})",
            "arabicPlain" => text,
            _ => text + suffix,
        };
    }

    /// <summary>十进制 → 罗马数字。</summary>
    private static string ToRoman(int value, bool upper)
    {
        if (value is <= 0 or > 3999)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var values = new[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        var symbols = new[] { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (value >= values[i])
            {
                builder.Append(symbols[i]);
                value -= values[i];
            }
        }

        var text = builder.ToString();
        return upper ? text : text.ToLowerInvariant();
    }

    /// <summary>
    /// 段落排版（带缓存）：key = 文本框引用 + 可用宽度。
    /// </summary>
    private FormattedText[] GetFormattedParagraphs(PptxTextBody body, double width)
    {
        var key = new TextCacheKey(body, Math.Round(width, 2));
        lock (_sync)
        {
            if (_textCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var result = BuildFormattedParagraphs(body, width);
        lock (_sync)
        {
            _textCache[key] = result;
        }

        return result;
    }

    /// <summary>真正构建 <see cref="FormattedText"/>（含 run 级样式覆盖）。</summary>
    private FormattedText[] BuildFormattedParagraphs(PptxTextBody body, double width)
    {
        var result = new FormattedText[body.Paragraphs.Count];

        for (var index = 0; index < body.Paragraphs.Count; index++)
        {
            var paragraph = body.Paragraphs[index];
            var firstRun = paragraph.Runs.FirstOrDefault();
            var text = paragraph.Text;
            if (string.IsNullOrEmpty(text))
            {
                text = " ";
            }

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                ResolveTypeface(firstRun?.FontFamily, firstRun?.Bold ?? false, firstRun?.Italic ?? false),
                firstRun?.FontSizePx ?? 24d,
                new SolidColorBrush(firstRun?.Color ?? Colors.Black));

            if (body.WordWrap)
            {
                formatted.MaxTextWidth = Math.Max(1d, width);
            }

            formatted.TextAlignment = paragraph.Alignment switch
            {
                PptxTextAlignment.Center => TextAlignment.Center,
                PptxTextAlignment.Right => TextAlignment.Right,
                PptxTextAlignment.Justify => TextAlignment.Justify,
                _ => TextAlignment.Left,
            };

            var lineHeight = paragraph.LineSpacingPx
                             ?? ((paragraph.LineSpacingFactor > 1.0001d || paragraph.LineSpacingFactor < 0.9999d)
                                 ? (firstRun?.FontSizePx ?? 24d) * paragraph.LineSpacingFactor
                                 : (double?)null);
            if (lineHeight is > 0)
            {
                formatted.LineHeight = lineHeight.Value;
            }

            ApplyRunStyles(formatted, paragraph);
            result[index] = formatted;
        }

        return result;
    }

    /// <summary>把 run 级样式套用到排版结果的字符区间上。</summary>
    private void ApplyRunStyles(FormattedText formatted, PptxParagraph paragraph)
    {
        var offset = 0;
        foreach (var run in paragraph.Runs)
        {
            var length = run.Text.Length;
            if (length <= 0)
            {
                continue;
            }

            formatted.SetForegroundBrush(new SolidColorBrush(run.Color), offset, length);

            if (run.FontFamily is { Length: > 0 } family)
            {
                formatted.SetFontFamily(family, offset, length);
            }

            formatted.SetFontSize(run.FontSizePx, offset, length);
            formatted.SetFontWeight(run.Bold ? FontWeight.Bold : FontWeight.Normal, offset, length);
            formatted.SetFontStyle(run.Italic ? FontStyle.Italic : FontStyle.Normal, offset, length);

            if (run.Underline || run.Strikethrough)
            {
                var decorations = new TextDecorationCollection();
                if (run.Underline)
                {
                    decorations.AddRange(TextDecorations.Underline);
                }

                if (run.Strikethrough)
                {
                    decorations.AddRange(TextDecorations.Strikethrough);
                }

                formatted.SetTextDecorations(decorations, offset, length);
            }

            offset += length;
        }
    }

    /// <summary>
    /// 解析字体族：优先使用请求字体，缺失时按 <see cref="FallbackFontChain"/> 回退，
    /// 并把「请求字体 → 实际字体」记入 <see cref="FontSubstitutions"/>。
    /// </summary>
    private Typeface ResolveTypeface(string? requestedFamily, bool bold, bool italic)
    {
        var family = ResolveFontFamily(requestedFamily);
        return new Typeface(family, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal);
    }

    /// <summary>解析字体族（带回退链与替换记录）。</summary>
    private FontFamily ResolveFontFamily(string? requestedFamily)
    {
        var requested = string.IsNullOrWhiteSpace(requestedFamily) ? null : requestedFamily.Trim();
        var chainKey = requested is null ? FallbackFontChain : $"{requested}|{FallbackFontChain}";

        lock (_sync)
        {
            if (_fontFamilyCache.TryGetValue(chainKey, out var cached))
            {
                return cached;
            }
        }

        FontFamily family;
        if (requested is null)
        {
            family = FontFamily.Parse(FallbackFontChain);
        }
        else if (IsFontAvailable(requested))
        {
            family = FontFamily.Parse($"{requested}, {FallbackFontChain}");
        }
        else
        {
            var fallback = FallbackFonts.FirstOrDefault(IsFontAvailable) ?? FontManager.Current.DefaultFontFamily.Name;
            family = FontFamily.Parse($"{fallback}, {FallbackFontChain}");
            lock (_sync)
            {
                _fontSubstitutions[requested] = fallback;
            }
        }

        lock (_sync)
        {
            _fontFamilyCache[chainKey] = family;
        }

        return family;
    }

    /// <summary>字体是否可用（本机是否真的存在该字族）。</summary>
    private bool IsFontAvailable(string name)
    {
        lock (_sync)
        {
            if (_fontAvailabilityCache.TryGetValue(name, out var cached))
            {
                return cached;
            }
        }

        var available = false;
        try
        {
            var manager = FontManager.Current;
            foreach (var systemFont in manager.SystemFonts)
            {
                if (string.Equals(systemFont.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    available = true;
                    break;
                }
            }

            if (!available
                && manager.TryGetGlyphTypeface(new Typeface(new FontFamily(name)), out var glyphTypeface)
                && string.Equals(glyphTypeface.FamilyName, name, StringComparison.OrdinalIgnoreCase))
            {
                available = true;
            }
        }
        catch
        {
            available = false;
        }

        lock (_sync)
        {
            _fontAvailabilityCache[name] = available;
        }

        return available;
    }

    // ───────────────────────── 画笔 / 几何 ─────────────────────────

    /// <summary>把填充模型转成画刷。</summary>
    private IBrush CreateBrush(PptxFill fill, Rect rect)
    {
        switch (fill)
        {
            case PptxSolidFill solid:
                return new SolidColorBrush(solid.Color);
            case PptxGradientFill gradient when gradient.Stops.Count > 0:
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5 - Math.Cos(gradient.AngleDegrees * Math.PI / 180d) / 2d,
                                                   0.5 - Math.Sin(gradient.AngleDegrees * Math.PI / 180d) / 2d,
                                                   RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5 + Math.Cos(gradient.AngleDegrees * Math.PI / 180d) / 2d,
                                                 0.5 + Math.Sin(gradient.AngleDegrees * Math.PI / 180d) / 2d,
                                                 RelativeUnit.Relative),
                };

                foreach (var stop in gradient.Stops)
                {
                    brush.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
                }

                return brush;
            default:
                return new SolidColorBrush(Colors.Transparent);
        }
    }

    /// <summary>把描边模型转成画笔。</summary>
    private static IPen? CreatePen(PptxStroke? stroke)
    {
        if (stroke is null || stroke.IsNone || stroke.Color.A == 0)
        {
            return null;
        }

        return new Pen(new SolidColorBrush(stroke.Color), Math.Max(0.4d, stroke.WidthPx), MapDash(stroke.DashStyle));
    }

    /// <summary>虚线样式映射。</summary>
    private static IDashStyle? MapDash(PptxDashStyle style) => style switch
    {
        PptxDashStyle.Solid => null,
        PptxDashStyle.Dash or PptxDashStyle.SystemDash => DashStyle.Dash,
        PptxDashStyle.Dot => DashStyle.Dot,
        PptxDashStyle.DashDot => DashStyle.DashDot,
        PptxDashStyle.LongDash => new DashStyle(new double[] { 6d, 3d }, 0d),
        PptxDashStyle.LongDashDot => new DashStyle(new double[] { 6d, 3d, 1d, 3d }, 0d),
        PptxDashStyle.RoundDot or PptxDashStyle.SystemDot => new DashStyle(new double[] { 1d, 2d }, 0d),
        _ => null,
    };

    /// <summary>取得节点几何（带缓存）。</summary>
    private Geometry? GetGeometry(PptxShapeNode shape)
    {
        if (shape.Geometry == PptxGeometryKind.Custom)
        {
            return CreateCustomGeometry(shape);
        }

        var key = new GeometryCacheKey(shape.Id, shape.Rect, shape.Geometry, AdjustmentsFingerprint(shape.Adjustments));
        lock (_sync)
        {
            if (_geometryCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var geometry = CreatePresetGeometry(shape.Rect, shape.Geometry, shape.Adjustments);
        lock (_sync)
        {
            _geometryCache[key] = geometry;
        }

        return geometry;
    }

    /// <summary>自定义几何：路径数据已在「0..宽 / 0..高」空间，平移后直接使用。</summary>
    private static Geometry? CreateCustomGeometry(PptxShapeNode shape)
    {
        if (string.IsNullOrWhiteSpace(shape.CustomPathData))
        {
            return null;
        }

        try
        {
            var geometry = StreamGeometry.Parse(shape.CustomPathData);
            var group = new GeometryGroup { Transform = new TranslateTransform(shape.Rect.X, shape.Rect.Y) };
            group.Children.Add(geometry);
            return group;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>调整值指纹（用于几何缓存键）。</summary>
    private static string AdjustmentsFingerprint(IReadOnlyDictionary<string, double> adjustments)
    {
        if (adjustments.Count == 0)
        {
            return string.Empty;
        }

        var parts = adjustments.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value:0.#####}");
        return string.Join(";", parts);
    }

    /// <summary>按预置几何生成路径。</summary>
    private static Geometry CreatePresetGeometry(
        Rect rect,
        PptxGeometryKind kind,
        IReadOnlyDictionary<string, double> adjustments)
    {
        switch (kind)
        {
            case PptxGeometryKind.Rectangle:
            case PptxGeometryKind.Unsupported:
            case PptxGeometryKind.Line:
                return new RectangleGeometry(rect);
            case PptxGeometryKind.Ellipse:
                return new EllipseGeometry(rect);
            case PptxGeometryKind.RoundRectangle:
                var radiusPercent = adjustments.TryGetValue("adj", out var adj) ? adj : 0.16667d;
                var radius = Math.Max(0d, Math.Min(radiusPercent, 0.5d) * Math.Min(rect.Width, rect.Height));
                return new RectangleGeometry(rect, radius, radius);
        }

        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            switch (kind)
            {
                case PptxGeometryKind.Triangle:
                    Polygon(stream, rect, (0.5, 0), (1, 1), (0, 1));
                    break;
                case PptxGeometryKind.RightTriangle:
                    Polygon(stream, rect, (0, 0), (0, 1), (1, 1));
                    break;
                case PptxGeometryKind.Diamond:
                    Polygon(stream, rect, (0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5));
                    break;
                case PptxGeometryKind.Parallelogram:
                    Polygon(stream, rect, (0.25, 0), (1, 0), (0.75, 1), (0, 1));
                    break;
                case PptxGeometryKind.Trapezoid:
                    Polygon(stream, rect, (0.25, 0), (0.75, 0), (1, 1), (0, 1));
                    break;
                case PptxGeometryKind.Pentagon:
                    RegularPolygon(stream, rect, 5, -90d);
                    break;
                case PptxGeometryKind.Hexagon:
                    Polygon(stream, rect, (0.25, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0.25, 1), (0, 0.5));
                    break;
                case PptxGeometryKind.Octagon:
                    Polygon(stream, rect, (0.29, 0), (0.71, 0), (1, 0.29), (1, 0.71), (0.71, 1), (0.29, 1), (0, 0.71), (0, 0.29));
                    break;
                case PptxGeometryKind.Star5:
                    Star(stream, rect, 5, 0.382d);
                    break;
                case PptxGeometryKind.Star4:
                    Star(stream, rect, 4, 0.35d);
                    break;
                case PptxGeometryKind.Star6:
                    Star(stream, rect, 6, 0.577d);
                    break;
                case PptxGeometryKind.ArrowRight:
                    Polygon(stream, rect, (0, 0.3), (0.65, 0.3), (0.65, 0), (1, 0.5), (0.65, 1), (0.65, 0.7), (0, 0.7));
                    break;
                case PptxGeometryKind.ArrowLeft:
                    Polygon(stream, rect, (1, 0.3), (0.35, 0.3), (0.35, 0), (0, 0.5), (0.35, 1), (0.35, 0.7), (1, 0.7));
                    break;
                case PptxGeometryKind.ArrowUp:
                    Polygon(stream, rect, (0.3, 1), (0.3, 0.35), (0, 0.35), (0.5, 0), (1, 0.35), (0.7, 0.35), (0.7, 1));
                    break;
                case PptxGeometryKind.ArrowDown:
                    Polygon(stream, rect, (0.3, 0), (0.3, 0.65), (0, 0.65), (0.5, 1), (1, 0.65), (0.7, 0.65), (0.7, 0));
                    break;
                case PptxGeometryKind.Chevron:
                    Polygon(stream, rect, (0, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0, 1), (0.25, 0.5));
                    break;
                case PptxGeometryKind.Cross:
                    Polygon(stream, rect, (0.35, 0), (0.65, 0), (0.65, 0.35), (1, 0.35), (1, 0.65), (0.65, 0.65), (0.65, 1), (0.35, 1), (0.35, 0.65), (0, 0.65), (0, 0.35), (0.35, 0.35));
                    break;
                case PptxGeometryKind.FlowChartProcess:
                    Polygon(stream, rect, (0, 0), (1, 0), (1, 1), (0, 1));
                    break;
                case PptxGeometryKind.FlowChartDecision:
                    Polygon(stream, rect, (0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5));
                    break;
                case PptxGeometryKind.FlowChartData:
                    Polygon(stream, rect, (0.2, 0), (1, 0), (0.8, 1), (0, 1));
                    break;
                default:
                    Polygon(stream, rect, (0, 0), (1, 0), (1, 1), (0, 1));
                    break;
            }
        }

        return geometry;
    }

    /// <summary>按归一化坐标画多边形。</summary>
    private static void Polygon(StreamGeometryContext stream, Rect rect, params (double X, double Y)[] points)
    {
        for (var i = 0; i < points.Length; i++)
        {
            var point = new Point(rect.X + points[i].X * rect.Width, rect.Y + points[i].Y * rect.Height);
            if (i == 0)
            {
                stream.BeginFigure(point, true);
            }
            else
            {
                stream.LineTo(point);
            }
        }

        stream.EndFigure(true);
    }

    /// <summary>正多边形。</summary>
    private static void RegularPolygon(StreamGeometryContext stream, Rect rect, int sides, double startAngleDegrees)
    {
        var center = rect.Center;
        var radiusX = rect.Width / 2d;
        var radiusY = rect.Height / 2d;

        for (var i = 0; i < sides; i++)
        {
            var angle = (startAngleDegrees + i * 360d / sides) * Math.PI / 180d;
            var point = new Point(center.X + Math.Cos(angle) * radiusX, center.Y + Math.Sin(angle) * radiusY);
            if (i == 0)
            {
                stream.BeginFigure(point, true);
            }
            else
            {
                stream.LineTo(point);
            }
        }

        stream.EndFigure(true);
    }

    /// <summary>星形多边形（<paramref name="innerRatio"/> 为内半径比例）。</summary>
    private static void Star(StreamGeometryContext stream, Rect rect, int points, double innerRatio)
    {
        var center = rect.Center;
        var radiusX = rect.Width / 2d;
        var radiusY = rect.Height / 2d;

        for (var i = 0; i < points * 2; i++)
        {
            var ratio = i % 2 == 0 ? 1d : innerRatio;
            var angle = (-90d + i * 180d / points) * Math.PI / 180d;
            var point = new Point(center.X + Math.Cos(angle) * radiusX * ratio, center.Y + Math.Sin(angle) * radiusY * ratio);
            if (i == 0)
            {
                stream.BeginFigure(point, true);
            }
            else
            {
                stream.LineTo(point);
            }
        }

        stream.EndFigure(true);
    }

    // ───────────────────────── 位图缓存 ─────────────────────────

    /// <summary>按部件名解码并缓存位图（解码失败缓存 <c>null</c>，避免反复重试）。</summary>
    public Bitmap? GetBitmap(string partName, byte[] data)
    {
        if (string.IsNullOrEmpty(partName) || data.Length == 0)
        {
            return null;
        }

        if (_imageCache.TryGetValue(partName, out var cached))
        {
            return cached;
        }

        Bitmap? bitmap = null;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            bitmap = new Bitmap(stream);
        }
        catch
        {
            bitmap = null;
        }

        _imageCache[partName] = bitmap;
        return bitmap;
    }

    /// <summary>图片缓存（供外部预热 / 诊断）。</summary>
    public IReadOnlyDictionary<string, Bitmap?> ImageCache => _imageCache;

    // ───────────────────────── 内部小类型 ─────────────────────────

    /// <summary>动画帧取状态；缺省为恒等变换。</summary>
    private static PptxNodeVisualState ResolveState(
        IReadOnlyDictionary<int, PptxNodeVisualState>? states,
        int nodeId)
        => states is not null && states.TryGetValue(nodeId, out var state) ? state : PptxNodeVisualState.Identity;

    /// <summary>按方向 / 进度生成裁剪矩形；返回 <c>null</c> 表示不裁剪。</summary>
    private static Rect? CreateClip(
        Rect rect,
        double progress,
        PptxTransitionClipMode mode,
        PptxAnimationDirection direction)
    {
        if (progress >= 1d - 1e-6 || mode == PptxTransitionClipMode.None || direction == PptxAnimationDirection.None)
        {
            return null;
        }

        var clamped = Math.Clamp(progress, 0d, 1d);
        if (mode is PptxTransitionClipMode.SplitOut or PptxTransitionClipMode.SplitIn)
        {
            // 拆分：按对称的中间区域露出（SplitOut 由中心向两侧、SplitIn 由两侧向中心，观感上同为同心展开）。
            var visibleWidth = rect.Width * clamped;
            return new Rect(rect.X + (rect.Width - visibleWidth) / 2d, rect.Y, Math.Max(0d, visibleWidth), rect.Height);
        }

        return direction switch
        {
            PptxAnimationDirection.FromLeft => new Rect(rect.X, rect.Y, rect.Width * clamped, rect.Height),
            PptxAnimationDirection.FromRight => new Rect(rect.Right - rect.Width * clamped, rect.Y, rect.Width * clamped, rect.Height),
            PptxAnimationDirection.FromTop => new Rect(rect.X, rect.Y, rect.Width, rect.Height * clamped),
            PptxAnimationDirection.FromBottom => new Rect(rect.X, rect.Bottom - rect.Height * clamped, rect.Width, rect.Height * clamped),
            PptxAnimationDirection.FromTopLeft => new Rect(rect.X, rect.Y, rect.Width * clamped, rect.Height * clamped),
            PptxAnimationDirection.FromTopRight => new Rect(rect.Right - rect.Width * clamped, rect.Y, rect.Width * clamped, rect.Height * clamped),
            PptxAnimationDirection.FromBottomLeft => new Rect(rect.X, rect.Bottom - rect.Height * clamped, rect.Width * clamped, rect.Height * clamped),
            PptxAnimationDirection.FromBottomRight => new Rect(rect.Right - rect.Width * clamped, rect.Bottom - rect.Height * clamped, rect.Width * clamped, rect.Height * clamped),
            _ => null,
        };
    }

    /// <summary>文本排版缓存键（按文本框引用 + 宽度）。</summary>
    private readonly struct TextCacheKey : IEquatable<TextCacheKey>
    {
        private readonly PptxTextBody _body;
        private readonly double _width;

        public TextCacheKey(PptxTextBody body, double width)
        {
            _body = body;
            _width = width;
        }

        public bool Equals(TextCacheKey other)
            => ReferenceEquals(_body, other._body) && Math.Abs(_width - other._width) < 0.01d;

        public override bool Equals(object? obj) => obj is TextCacheKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_body), Math.Round(_width, 1));
    }

    /// <summary>几何缓存键。</summary>
    private readonly struct GeometryCacheKey : IEquatable<GeometryCacheKey>
    {
        private readonly int _id;
        private readonly Rect _rect;
        private readonly PptxGeometryKind _kind;
        private readonly string _adjustments;

        public GeometryCacheKey(int id, Rect rect, PptxGeometryKind kind, string adjustments)
        {
            _id = id;
            _rect = rect;
            _kind = kind;
            _adjustments = adjustments;
        }

        public bool Equals(GeometryCacheKey other)
            => _id == other._id && _rect.Equals(other._rect) && _kind == other._kind
               && string.Equals(_adjustments, other._adjustments, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is GeometryCacheKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_id, _rect, (int)_kind, _adjustments);
    }
}
