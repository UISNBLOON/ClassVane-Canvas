using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using ClassVaneCanvas.Pptx.Model;
using ClassVaneCanvas.Storage;

namespace ClassVaneCanvas.Pptx;

/// <summary>解析选项。</summary>
public sealed record PptxParseOptions
{
    /// <summary>默认选项。</summary>
    public static PptxParseOptions Default { get; } = new();

    /// <summary>媒体（视频 / 音频）导出根目录；<c>null</c> 时使用 <see cref="AppPaths.SlideCacheDirectory"/>。</summary>
    public string? MediaCacheDirectory { get; init; }

    /// <summary>是否把媒体部件导出到本机缓存目录。</summary>
    public bool ExportMedia { get; init; } = true;

    /// <summary>解析出的媒体缓存根目录（未设置时取本机数据目录下的幻灯片缓存目录）。</summary>
    public string ResolveMediaCacheDirectory()
        => MediaCacheDirectory ?? new AppPaths().SlideCacheDirectory;
}

/// <summary>
/// 自研 PPTX（OOXML）解析器：把 <c>.pptx</c> 包解析成不可变的页面节点树。
///
/// 设计要点：
/// · 只用 BCL：<see cref="ZipArchive"/> 读包 + <see cref="XDocument"/> 手写 OpenXML 解析，不依赖任何第三方渲染库、不起外部进程；
/// · 长度换算统一走 <see cref="PptxUnits"/>（EMU / 9525 = 逻辑像素）；
/// · <b>容错优先</b>：单个部件 / 单个形状出错只记入 <see cref="PptxSlide.UnsupportedFeatures"/> 并跳过，
///   只有「整份文件打不开」才抛中文异常；
/// · 老版 <c>.ppt</c>（OLE 复合二进制）直接抛 <see cref="NotSupportedException"/>。
/// </summary>
public sealed class PptxParser
{
    /// <summary>SmartArt 降级提示。</summary>
    public const string SmartArtNotice = "SmartArt 图形暂不支持（仅显示占位）";

    /// <summary>路径动画降级提示。</summary>
    public const string MotionPathNotice = "复杂路径动画暂不支持";

    /// <summary>矢量图位图回退提示。</summary>
    public const string VectorFallbackNotice = "矢量图片已用位图回退";

    /// <summary>解析本地 <c>.pptx</c> 文件。</summary>
    /// <param name="filePath">课件路径。</param>
    /// <returns>解析结果（不可变模型）。</returns>
    /// <exception cref="ArgumentException">路径为空。</exception>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="NotSupportedException">不是 <c>.pptx</c> / <c>.pptm</c>（例如老版二进制 <c>.ppt</c>）。</exception>
    /// <exception cref="InvalidDataException">文件损坏或缺少必需的 OOXML 部件。</exception>
    public PptxDocument Parse(string filePath)
        => Parse(filePath, PptxParseOptions.Default);

    /// <summary>解析本地 <c>.pptx</c> 文件（可指定解析选项）。</summary>
    public PptxDocument Parse(string filePath, PptxParseOptions options)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("课件路径不能为空。", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("课件文件不存在。", fullPath);
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is ".ppt" or ".pps" or ".pot")
        {
            throw new NotSupportedException(
                $"暂不支持老版二进制 PowerPoint 格式（{extension}），请在 PowerPoint 中另存为 .pptx 后再打开。");
        }

        if (extension is not (".pptx" or ".pptm"))
        {
            throw new NotSupportedException($"只支持 .pptx 格式的课件，当前文件类型：{extension}。");
        }

        string hash;
        using (var hashStream = File.OpenRead(fullPath))
        {
            hash = HashStream(hashStream);
        }

        using var stream = File.OpenRead(fullPath);
        return Parse(stream, fullPath, hash, options);
    }

    /// <summary>
    /// 解析 PPTX 数据流。
    /// </summary>
    /// <param name="stream">可寻址的包数据流（调用方负责释放）。</param>
    /// <param name="fileName">用于诊断的文件名 / 路径。</param>
    /// <param name="fileHash">课件 SHA256（用于媒体缓存目录）；<c>null</c> 时由调用方后续补齐。</param>
    /// <param name="options">解析选项。</param>
    public PptxDocument Parse(
        Stream stream,
        string fileName,
        string? fileHash = null,
        PptxParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("课件文件已损坏或不是有效的 .pptx（ZIP）文件，无法打开。", ex);
        }

        using (archive)
        {
            var session = new ParseSession(archive, fileName ?? string.Empty, fileHash ?? string.Empty, options ?? PptxParseOptions.Default);
            return session.Run();
        }
    }

    /// <summary>
    /// 容错解析：任何失败都只返回 <c>false</c> 与中文错误说明，绝不抛异常。
    /// 供放映流程「打不开就降级」使用。
    /// </summary>
    /// <param name="filePath">课件路径。</param>
    /// <param name="document">成功时的解析结果。</param>
    /// <param name="error">失败原因（中文）。</param>
    /// <returns>是否解析成功。</returns>
    public bool TryParse(string filePath, out PptxDocument? document, out string? error)
    {
        try
        {
            document = Parse(filePath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            document = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>计算数据流 SHA256（小写十六进制）。</summary>
    private static string HashStream(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 一次解析会话：持有包、主题与版式继承缓存，负责把整份演示文稿装配成模型。
    /// </summary>
    private sealed class ParseSession
    {
        private readonly OpcPackage _package;
        private readonly PptxParseOptions _options;
        private readonly string _filePath;
        private readonly string _fileHash;
        private readonly List<string> _documentNotes = new();
        private readonly HashSet<string> _documentNoteSet = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SlideInheritance> _inheritanceCache = new(StringComparer.OrdinalIgnoreCase);
        private XElement? _defaultTextStyle;
        private Size _pageSize = new(1280, 720);
        private int _syntheticIdSeed = 900_000;

        public ParseSession(ZipArchive archive, string filePath, string fileHash, PptxParseOptions options)
        {
            _package = new OpcPackage(archive);
            _filePath = filePath;
            _fileHash = fileHash;
            _options = options;
        }

        /// <summary>生成一个不会与包内形状冲突的合成形状 id。</summary>
        public int NextId() => _syntheticIdSeed++;

        /// <summary>执行解析。</summary>
        public PptxDocument Run()
        {
            var presentation = _package.LoadXml(OpcPackage.PresentationPart)
                ?? throw new InvalidDataException("课件缺少 ppt/presentation.xml，可能不是有效的 .pptx 文件。");

            var root = presentation.Root
                ?? throw new InvalidDataException("课件 ppt/presentation.xml 内容为空。");

            _pageSize = ReadPageSize(root);
            _defaultTextStyle = root.Element(Ns.P + "defaultTextStyle");

            var slideParts = ReadSlideParts(root)
                ?? throw new InvalidDataException("课件中没有找到任何幻灯片页面（ppt/slides）。");

            var slides = new List<PptxSlide>(slideParts.Count);
            for (var i = 0; i < slideParts.Count; i++)
            {
                slides.Add(ParseSlide(slideParts[i], i + 1));
            }

            return new PptxDocument
            {
                FilePath = _filePath,
                FileHash = _fileHash,
                PageSize = _pageSize,
                Slides = slides,
                UnsupportedFeatures = _documentNotes.ToArray(),
                Title = ReadCoreTitle(),
            };
        }

        // ───────────────────────── 演示文稿级 ─────────────────────────

        /// <summary>读取页面尺寸（<c>p:sldSz</c>），缺失时按 16:9 的 1280×720 兜底。</summary>
        private static Size ReadPageSize(XElement root)
        {
            var size = root.Element(Ns.P + "sldSz");
            var cx = DoubleAttr(size, "cx", 0d);
            var cy = DoubleAttr(size, "cy", 0d);
            if (cx <= 0 || cy <= 0)
            {
                return new Size(1280, 720);
            }

            var width = PptxUnits.ToPixels(cx);
            var height = PptxUnits.ToPixels(cy);
            return width <= 1 || height <= 1 ? new Size(1280, 720) : new Size(width, height);
        }

        /// <summary>读取 <c>p:sldIdLst</c> 指向的页面部件。</summary>
        private List<string>? ReadSlideParts(XElement root)
        {
            var parts = new List<string>();
            var idList = root.Element(Ns.P + "sldIdLst");
            if (idList is not null)
            {
                foreach (var slideId in idList.Elements(Ns.P + "sldId"))
                {
                    var relationshipId = (string?)slideId.Attribute(Ns.R + "id");
                    if (string.IsNullOrEmpty(relationshipId))
                    {
                        continue;
                    }

                    var target = _package.ResolveTarget(OpcPackage.PresentationPart, relationshipId);
                    if (target is not null && _package.Exists(target))
                    {
                        parts.Add(target);
                    }
                }
            }

            if (parts.Count > 0)
            {
                return parts;
            }

            // 兜底：直接扫包内 ppt/slides/slideN.xml（部分导出工具不写 sldIdLst）。
            var scanned = _package.PartNames
                .Where(p => p.StartsWith("/ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                            && p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => ExtractTrailingNumber(p))
                .ToList();
            return scanned.Count > 0 ? scanned : null;
        }

        /// <summary>读取核心属性里的标题。</summary>
        private string? ReadCoreTitle()
        {
            try
            {
                var core = _package.LoadXml("/docProps/core.xml");
                return core?.Root?.Element(Ns.Dc + "title")?.Value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>文档级降级提示（去重）。</summary>
        private void NoteDocument(string message)
        {
            if (_documentNoteSet.Add(message))
            {
                _documentNotes.Add(message);
            }
        }

        // ───────────────────────── 页面级 ─────────────────────────

        /// <summary>解析一页幻灯片。</summary>
        private PptxSlide ParseSlide(string slidePart, int index)
        {
            var notes = new List<string>();
            var noteSet = new HashSet<string>(StringComparer.Ordinal);

            void Note(string message)
            {
                if (noteSet.Add(message))
                {
                    notes.Add(message);
                }
            }

            var nodes = new List<PptxNode>();
            var media = new List<PptxMediaFile>();
            var slide = new PptxSlide
            {
                Index = index,
                PartName = slidePart,
                PageSize = _pageSize,
            };

            XDocument? document;
            try
            {
                document = _package.LoadXml(slidePart);
            }
            catch (Exception ex)
            {
                Note($"页面部件解析失败，已跳过本页内容（{ex.Message}）");
                return slide with { UnsupportedFeatures = notes };
            }

            if (document?.Root is null)
            {
                Note("页面部件为空或损坏，已跳过本页内容");
                return slide with { UnsupportedFeatures = notes };
            }

            var root = document.Root;
            var inheritance = GetInheritance(slidePart);
            var context = new SlideContext(this, slidePart, inheritance, Note, media, index);

            var (background, backgroundFill) = ResolveBackground(root, inheritance, context);
            slide = slide with { Background = background, BackgroundFill = backgroundFill };

            var spTree = root.Element(Ns.P + "cSld")?.Element(Ns.P + "spTree");
            if (spTree is not null)
            {
                ParseShapeTree(spTree, context, nodes, Matrix.Identity);
            }
            else
            {
                Note("页面缺少形状树（p:spTree），已跳过本页内容");
            }

            var transition = ParseTransition(FindTransitionElement(root), Note);
            var timeline = ParseTiming(root.Element(Ns.P + "timing"), context, Note);
            var notesText = ReadNotesText(slidePart);

            return slide with
            {
                Nodes = nodes,
                Transition = transition,
                Animation = timeline,
                Media = media,
                UnsupportedFeatures = notes,
                Notes = notesText,
            };
        }

        /// <summary>页面背景：页面 → 版式 → 母版。</summary>
        private (Color Color, PptxFill? Fill) ResolveBackground(
            XElement slideRoot,
            SlideInheritance inheritance,
            SlideContext context)
        {
            var background = slideRoot.Element(Ns.P + "cSld")?.Element(Ns.P + "bg")
                             ?? inheritance.LayoutBackground
                             ?? inheritance.MasterBackground;

            if (background is null)
            {
                return (Colors.White, null);
            }

            var fillElement = background.Elements().FirstOrDefault(e => e.Name.Namespace == Ns.A);
            switch (fillElement?.Name.LocalName)
            {
                case "solidFill":
                    return (ResolveColor(fillElement, inheritance.Theme, Colors.White) ?? Colors.White, null);
                case "gradFill":
                    var gradient = ParseGradient(fillElement, inheritance.Theme);
                    return (gradient.Stops.Count > 0 ? gradient.Stops[^1].Color : Colors.White, gradient);
                case "blipFill":
                    context.Note("图片背景暂不支持，已按白色背景处理");
                    return (Colors.White, null);
                case "noFill":
                    return (Colors.White, null);
                default:
                    return (Colors.White, null);
            }
        }

        // ───────────────────────── 版式 / 母版继承 ─────────────────────────

        /// <summary>取得（并缓存）某页对应的版式 / 母版继承信息。</summary>
        private SlideInheritance GetInheritance(string slidePart)
        {
            if (_inheritanceCache.TryGetValue(slidePart, out var cached))
            {
                return cached;
            }

            var inheritance = new SlideInheritance();
            _inheritanceCache[slidePart] = inheritance;

            try
            {
                var layoutPart = _package.FindRelationshipTarget(slidePart, "slideLayout");
                if (layoutPart is not null && _package.Exists(layoutPart))
                {
                    inheritance.LayoutPart = layoutPart;
                    var layout = _package.LoadXml(layoutPart);
                    inheritance.LayoutSpTree = layout?.Root?.Element(Ns.P + "cSld")?.Element(Ns.P + "spTree");
                    inheritance.LayoutBackground = layout?.Root?.Element(Ns.P + "cSld")?.Element(Ns.P + "bg");
                    inheritance.LayoutPlaceholders = CollectPlaceholders(inheritance.LayoutSpTree);
                }

                var masterPart = layoutPart is null ? null : _package.FindRelationshipTarget(layoutPart, "slideMaster");
                if (masterPart is not null && _package.Exists(masterPart))
                {
                    inheritance.MasterPart = masterPart;
                    var master = _package.LoadXml(masterPart);
                    inheritance.MasterSpTree = master?.Root?.Element(Ns.P + "cSld")?.Element(Ns.P + "spTree");
                    inheritance.MasterBackground = master?.Root?.Element(Ns.P + "cSld")?.Element(Ns.P + "bg");
                    inheritance.MasterTxStyles = master?.Root?.Element(Ns.P + "txStyles");
                    inheritance.MasterColorMap = master?.Root?.Element(Ns.P + "clrMap");
                    inheritance.MasterPlaceholders = CollectPlaceholders(inheritance.MasterSpTree);
                }

                var themePart = masterPart is null ? null : _package.FindRelationshipTarget(masterPart, "theme")
                                ?? (layoutPart is null ? null : _package.FindRelationshipTarget(layoutPart, "theme"));
                themePart ??= _package.PartNames.FirstOrDefault(
                    p => p.StartsWith("/ppt/theme/theme", StringComparison.OrdinalIgnoreCase)
                         && p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

                if (themePart is not null)
                {
                    inheritance.Theme = ThemeData.Load(_package.LoadXml(themePart));
                }

                // 母版颜色映射（tx1 → dk1 之类）会影响主题色解析结果。
                inheritance.Theme.ApplyColorMap(inheritance.MasterColorMap);

                // 版式 / 母版里的非占位装饰形状本内核不渲染，只继承占位符的位置与文字样式。
                if (HasDecorativeShapes(inheritance.LayoutSpTree) || HasDecorativeShapes(inheritance.MasterSpTree))
                {
                    NoteDocument("版式 / 母版中的装饰形状暂不渲染（仅继承占位符位置与文字样式）");
                }
            }
            catch (Exception ex)
            {
                NoteDocument($"版式 / 母版继承信息读取失败，已按页面自身内容渲染（{ex.Message}）");
            }

            return inheritance;
        }

        /// <summary>收集形状树里的占位符（按 类型 + 索引 建索引）。</summary>
        private static Dictionary<string, XElement> CollectPlaceholders(XElement? spTree)
        {
            var map = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            if (spTree is null)
            {
                return map;
            }

            foreach (var sp in spTree.Elements(Ns.P + "sp"))
            {
                var placeholder = sp.Element(Ns.P + "nvSpPr")?
                    .Element(Ns.P + "nvPr")?
                    .Element(Ns.P + "ph");
                if (placeholder is null)
                {
                    continue;
                }

                var type = (string?)placeholder.Attribute("type") ?? "body";
                var idx = IntAttr(placeholder, "idx", 0);
                map[$"{type}#{idx}"] = sp;
            }

            return map;
        }

        /// <summary>版式 / 母版里是否存在非占位的装饰形状。</summary>
        private static bool HasDecorativeShapes(XElement? spTree)
            => spTree is not null && spTree.Elements().Any(e =>
                e.Name == Ns.P + "sp" && e.Element(Ns.P + "nvSpPr")?.Element(Ns.P + "nvPr")?.Element(Ns.P + "ph") is null);

        // ───────────────────────── 形状树 ─────────────────────────

        /// <summary>递归解析形状树；组合形状的子坐标会被合成为页面坐标。</summary>
        private void ParseShapeTree(XElement spTree, SlideContext context, List<PptxNode> output, Matrix transform)
        {
            foreach (var element in spTree.Elements())
            {
                try
                {
                    if (element.Name == Ns.P + "sp")
                    {
                        var node = ParseShape(element, context, transform);
                        if (node is not null)
                        {
                            output.Add(node);
                        }
                    }
                    else if (element.Name == Ns.P + "pic")
                    {
                        var node = ParsePicture(element, context, transform);
                        if (node is not null)
                        {
                            output.Add(node);
                        }
                    }
                    else if (element.Name == Ns.P + "graphicFrame")
                    {
                        var node = ParseGraphicFrame(element, context, transform);
                        if (node is not null)
                        {
                            output.Add(node);
                        }
                    }
                    else if (element.Name == Ns.P + "cxnSp")
                    {
                        var node = ParseConnector(element, context, transform);
                        if (node is not null)
                        {
                            output.Add(node);
                        }
                    }
                    else if (element.Name == Ns.P + "grpSp")
                    {
                        var group = ParseGroup(element, context, transform);
                        if (group is not null)
                        {
                            output.Add(group);
                        }
                    }
                    else if (element.Name == Ns.P + "contentPart")
                    {
                        context.Note("嵌入内容部件（ink / 3D 等）暂不支持，已跳过");
                    }
                }
                catch (Exception ex)
                {
                    context.Note($"形状解析失败，已跳过（{ex.GetType().Name}：{ex.Message}）");
                }
            }
        }

        /// <summary>解析组合形状：把子节点坐标从子坐标系合成为页面（未旋转）坐标。</summary>
        private PptxGroupNode? ParseGroup(XElement grpSp, SlideContext context, Matrix transform)
        {
            var cNvPr = grpSp.Element(Ns.P + "nvGrpSpPr")?.Element(Ns.P + "cNvPr");
            var spPr = grpSp.Element(Ns.P + "grpSpPr");
            var xfrm = spPr?.Element(Ns.A + "xfrm");

            var groupRect = ReadXfrmRect(xfrm, transform);
            if (groupRect.Width <= 0 && groupRect.Height <= 0)
            {
                // 没有自身变换的组合：直接用父变换解析子节点。
                var fallbackChildren = new List<PptxNode>();
                ParseShapeTree(grpSp, context, fallbackChildren, transform);
                return new PptxGroupNode
                {
                    Id = IntAttr(cNvPr, "id", context.NextSyntheticId()),
                    Name = (string?)cNvPr?.Attribute("name") ?? "组合",
                    Rect = fallbackChildren.Count > 0
                        ? fallbackChildren.Select(c => c.Rect).Aggregate((a, b) => a.Union(b))
                        : default,
                    Rotation = PptxUnits.RotationToDegrees(DoubleAttr(xfrm, "rot", 0d)),
                    Children = fallbackChildren,
                };
            }

            var off = xfrm?.Element(Ns.A + "off");
            var ext = xfrm?.Element(Ns.A + "ext");
            var chOff = xfrm?.Element(Ns.A + "chOff");
            var chExt = xfrm?.Element(Ns.A + "chExt");

            var offX = PptxUnits.ToPixels(DoubleAttr(off, "x", 0d));
            var offY = PptxUnits.ToPixels(DoubleAttr(off, "y", 0d));
            var extCx = PptxUnits.ToPixels(DoubleAttr(ext, "cx", 0d));
            var extCy = PptxUnits.ToPixels(DoubleAttr(ext, "cy", 0d));
            var chOffX = PptxUnits.ToPixels(DoubleAttr(chOff, "x", 0d));
            var chOffY = PptxUnits.ToPixels(DoubleAttr(chOff, "y", 0d));
            var chExtCx = PptxUnits.ToPixels(DoubleAttr(chExt, "cx", 0d));
            var chExtCy = PptxUnits.ToPixels(DoubleAttr(chExt, "cy", 0d));

            var scaleX = chExtCx > 0 ? extCx / chExtCx : 1d;
            var scaleY = chExtCy > 0 ? extCy / chExtCy : 1d;

            var flipH = IsTrue(xfrm?.Attribute("flipH"));
            var flipV = IsTrue(xfrm?.Attribute("flipV"));

            var m11 = flipH ? -scaleX : scaleX;
            var m22 = flipV ? -scaleY : scaleY;
            var m31 = flipH ? offX + extCx + chOffX * scaleX : offX - chOffX * scaleX;
            var m32 = flipV ? offY + extCy + chOffY * scaleY : offY - chOffY * scaleY;

            var local = new Matrix(m11, 0, 0, m22, m31, m32);
            var childTransform = local * transform;

            var children = new List<PptxNode>();
            ParseShapeTree(grpSp, context, children, childTransform);

            return new PptxGroupNode
            {
                Id = IntAttr(cNvPr, "id", context.NextSyntheticId()),
                Name = (string?)cNvPr?.Attribute("name") ?? "组合",
                Rect = groupRect,
                Rotation = PptxUnits.RotationToDegrees(DoubleAttr(xfrm, "rot", 0d)),
                Opacity = ReadAlphaMod(xfrm),
                Children = children,
            };
        }

        /// <summary>解析 <c>p:sp</c>（含文本框与自选图形）。</summary>
        private PptxNode? ParseShape(XElement sp, SlideContext context, Matrix transform)
        {
            var nvSpPr = sp.Element(Ns.P + "nvSpPr");
            var cNvPr = nvSpPr?.Element(Ns.P + "cNvPr");
            var placeholder = nvSpPr?.Element(Ns.P + "nvPr")?.Element(Ns.P + "ph");
            var spPr = sp.Element(Ns.P + "spPr");

            var id = IntAttr(cNvPr, "id", context.NextSyntheticId());
            var name = (string?)cNvPr?.Attribute("name") ?? string.Empty;
            var hidden = IsTrue(cNvPr?.Attribute("hidden")) || (string?)cNvPr?.Attribute("show") == "0";

            var placeholderType = placeholder is null ? null : (string?)placeholder.Attribute("type") ?? "body";
            var placeholderIndex = IntAttr(placeholder, "idx", 0);

            var xfrm = spPr?.Element(Ns.A + "xfrm")
                       ?? (placeholder is null
                           ? null
                           : context.Inheritance.FindPlaceholderXfrm(placeholderType, placeholderIndex));

            if (xfrm is null)
            {
                context.Note($"形状「{name}」缺少位置信息，已跳过");
                return null;
            }

            var rect = ReadXfrmRect(xfrm, transform);
            if (rect.Width <= 0 && rect.Height <= 0)
            {
                context.Note($"形状「{name}」尺寸为 0，已跳过");
                return null;
            }

            var rotation = PptxUnits.RotationToDegrees(DoubleAttr(xfrm, "rot", 0d));
            var flipH = IsTrue(xfrm.Attribute("flipH"));
            var flipV = IsTrue(xfrm.Attribute("flipV"));

            var geometryKind = PptxGeometryKind.Rectangle;
            var rawGeometryName = "rect";
            var adjustments = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string? customPath = null;
            var customPathSize = new Size(rect.Width, rect.Height);

            var prstGeom = spPr?.Element(Ns.A + "prstGeom");
            var custGeom = spPr?.Element(Ns.A + "custGeom");
            if (prstGeom is not null)
            {
                rawGeometryName = (string?)prstGeom.Attribute("prst") ?? "rect";
                geometryKind = MapGeometry(rawGeometryName);
                adjustments = ReadAdjustments(prstGeom.Element(Ns.A + "avLst"));
                if (geometryKind == PptxGeometryKind.Unsupported)
                {
                    context.Note($"形状预设「{rawGeometryName}」暂不支持，已按矩形绘制");
                }
            }
            else if (custGeom is not null)
            {
                geometryKind = PptxGeometryKind.Custom;
                rawGeometryName = "custGeom";
                (customPath, customPathSize) = ReadCustomGeometry(custGeom, rect, context);
            }

            var fill = ParseFill(spPr, context.Inheritance.Theme, context.Note);
            var stroke = ParseStroke(spPr?.Element(Ns.A + "ln"), context.Inheritance.Theme, flipH || flipV);

            // 直线式预置几何 → 线条节点（连接线的常见写法）。
            if (geometryKind == PptxGeometryKind.Line || IsLinePreset(rawGeometryName))
            {
                var (start, end) = ReadLineEndpoints(rect, flipH, flipV, rotation);
                return new PptxLineNode
                {
                    Id = id,
                    Name = name,
                    Rect = rect,
                    Rotation = rotation,
                    Hidden = hidden,
                    IsPlaceholder = placeholder is not null,
                    PlaceholderType = placeholderType,
                    PlaceholderIndex = placeholderIndex,
                    Start = start,
                    End = end,
                    Stroke = stroke ?? new PptxStroke(),
                    HeadEnd = ParseArrowHead(spPr?.Element(Ns.A + "ln")?.Element(Ns.A + "headEnd")),
                    TailEnd = ParseArrowHead(spPr?.Element(Ns.A + "ln")?.Element(Ns.A + "tailEnd")),
                    FlipHorizontal = flipH,
                    FlipVertical = flipV,
                };
            }

            var text = ParseTextBody(sp.Element(Ns.P + "txBody"), context, placeholderType, placeholderIndex);
            var hasFill = fill is not PptxNoFill;
            var hasStroke = stroke is { IsNone: false };

            if (text is not null && !hasFill && !hasStroke)
            {
                return new PptxTextBoxNode
                {
                    Id = id,
                    Name = name,
                    Rect = rect,
                    Rotation = rotation,
                    Hidden = hidden,
                    IsPlaceholder = placeholder is not null,
                    PlaceholderType = placeholderType,
                    PlaceholderIndex = placeholderIndex,
                    Text = text,
                };
            }

            return new PptxShapeNode
            {
                Id = id,
                Name = name,
                Rect = rect,
                Rotation = rotation,
                Hidden = hidden,
                IsPlaceholder = placeholder is not null,
                PlaceholderType = placeholderType,
                PlaceholderIndex = placeholderIndex,
                Geometry = geometryKind,
                RawGeometryName = rawGeometryName,
                Adjustments = adjustments,
                Fill = fill,
                Stroke = stroke,
                Text = text,
                CustomPathData = customPath,
                CustomPathSize = customPathSize,
            };
        }

        /// <summary>解析 <c>p:cxnSp</c> 连接线。</summary>
        private PptxNode? ParseConnector(XElement cxnSp, SlideContext context, Matrix transform)
        {
            var nvCxnSpPr = cxnSp.Element(Ns.P + "nvCxnSpPr");
            var cNvPr = nvCxnSpPr?.Element(Ns.P + "cNvPr");
            var placeholder = nvCxnSpPr?.Element(Ns.P + "nvPr")?.Element(Ns.P + "ph");
            var spPr = cxnSp.Element(Ns.P + "spPr");
            var id = IntAttr(cNvPr, "id", context.NextSyntheticId());
            var name = (string?)cNvPr?.Attribute("name") ?? "连接线";

            var placeholderType = placeholder is null ? null : (string?)placeholder.Attribute("type") ?? "body";
            var placeholderIndex = IntAttr(placeholder, "idx", 0);

            var xfrm = spPr?.Element(Ns.A + "xfrm")
                       ?? (placeholder is null
                           ? null
                           : context.Inheritance.FindPlaceholderXfrm(placeholderType, placeholderIndex));

            if (xfrm is null)
            {
                context.Note($"连接线「{name}」缺少位置信息，已跳过");
                return null;
            }

            var rect = ReadXfrmRect(xfrm, transform);
            var rotation = PptxUnits.RotationToDegrees(DoubleAttr(xfrm, "rot", 0d));
            var flipH = IsTrue(xfrm.Attribute("flipH"));
            var flipV = IsTrue(xfrm.Attribute("flipV"));
            var (start, end) = ReadLineEndpoints(rect, flipH, flipV, rotation);
            var line = spPr?.Element(Ns.A + "ln");
            var stroke = ParseStroke(line, context.Inheritance.Theme, flipH || flipV) ?? new PptxStroke();

            return new PptxLineNode
            {
                Id = id,
                Name = name,
                Rect = rect,
                Rotation = rotation,
                Hidden = IsTrue(cNvPr?.Attribute("hidden")),
                IsPlaceholder = placeholder is not null,
                PlaceholderType = placeholderType,
                PlaceholderIndex = placeholderIndex,
                Start = start,
                End = end,
                Stroke = stroke,
                HeadEnd = ParseArrowHead(line?.Element(Ns.A + "headEnd")),
                TailEnd = ParseArrowHead(line?.Element(Ns.A + "tailEnd")),
                FlipHorizontal = flipH,
                FlipVertical = flipV,
            };
        }

        /// <summary>解析 <c>p:pic</c>（图片 / 视频 / 音频）。</summary>
        private PptxNode? ParsePicture(XElement pic, SlideContext context, Matrix transform)
        {
            var nvPicPr = pic.Element(Ns.P + "nvPicPr");
            var cNvPr = nvPicPr?.Element(Ns.P + "cNvPr");
            var nvPr = nvPicPr?.Element(Ns.P + "nvPr");
            var placeholder = nvPr?.Element(Ns.P + "ph");
            var spPr = pic.Element(Ns.P + "spPr");

            var id = IntAttr(cNvPr, "id", context.NextSyntheticId());
            var name = (string?)cNvPr?.Attribute("name") ?? string.Empty;
            var hidden = IsTrue(cNvPr?.Attribute("hidden")) || (string?)cNvPr?.Attribute("show") == "0";
            var placeholderType = placeholder is null ? null : (string?)placeholder.Attribute("type") ?? "pic";
            var placeholderIndex = IntAttr(placeholder, "idx", 0);

            var xfrm = spPr?.Element(Ns.A + "xfrm")
                       ?? (placeholder is null
                           ? null
                           : context.Inheritance.FindPlaceholderXfrm(placeholderType, placeholderIndex));

            if (xfrm is null)
            {
                context.Note($"图片「{name}」缺少位置信息，已跳过");
                return null;
            }

            var rect = ReadXfrmRect(xfrm, transform);
            var rotation = PptxUnits.RotationToDegrees(DoubleAttr(xfrm, "rot", 0d));
            var blipFill = pic.Element(Ns.P + "blipFill");
            var blip = blipFill?.Element(Ns.A + "blip");

            // 视频：a:videoFile（r:link 外链 / r:embed 内嵌），或 p14:media 内嵌。
            var videoFile = nvPr?.Element(Ns.A + "videoFile");
            var audioFile = nvPr?.Element(Ns.A + "audioFile");
            var p14Media = nvPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "media" && e.Name.Namespace.NamespaceName.Contains("powerpoint"));

            if (videoFile is not null || (p14Media is not null && IsVideoPart(context, p14Media)))
            {
                var mediaFile = BuildMediaFile(PptxMediaKind.Video, videoFile ?? p14Media!, context, rect, id, name);
                var poster = ReadBlipBytes(blip, context, name, out var posterPart);
                return new PptxVideoNode
                {
                    Id = id,
                    Name = name,
                    Rect = rect,
                    Rotation = rotation,
                    Hidden = hidden,
                    IsPlaceholder = placeholder is not null,
                    PlaceholderType = placeholderType,
                    PlaceholderIndex = placeholderIndex,
                    Media = mediaFile,
                    PosterPartName = posterPart,
                    PosterData = poster,
                };
            }

            if (audioFile is not null || (p14Media is not null && !IsVideoPart(context, p14Media)))
            {
                var mediaFile = BuildMediaFile(PptxMediaKind.Audio, audioFile ?? p14Media!, context, rect, id, name);
                return new PptxAudioNode
                {
                    Id = id,
                    Name = name,
                    Rect = rect,
                    Rotation = rotation,
                    Hidden = hidden,
                    IsPlaceholder = placeholder is not null,
                    PlaceholderType = placeholderType,
                    PlaceholderIndex = placeholderIndex,
                    Media = mediaFile,
                    AutoPlay = IsTrue(nvPr?.Element(Ns.A + "audioFile")?.Attribute("autoPlay"))
                               || string.Equals((string?)p14Media?.Attribute("autoPlay"), "1", StringComparison.Ordinal),
                };
            }

            var data = ReadBlipBytes(blip, context, name, out var partName);
            if (data is null || partName is null)
            {
                context.Note($"图片「{name}」的媒体部件缺失，已跳过");
                return null;
            }

            var contentType = _package.GetContentType(partName);
            var isVectorFallback = false;
            string? vectorPartName = null;

            // PowerPoint 插入 SVG 的真实写法：<a:blip r:embed="PNG 回退图"> 内再挂
            // <a:extLst><a:ext uri="{96DAC541-…}"><asvg:svgBlip r:embed="SVG 部件"/></a:ext></a:extLst>。
            // 此时 blip 自身指向的位图就是官方回退图，SVG 部件只用于标注「已回退」。
            var svgBlipPart = ResolveSvgBlipPart(blip, context);
            if (svgBlipPart is not null)
            {
                vectorPartName = svgBlipPart;
                if (IsVectorPart(partName, contentType))
                {
                    // blip 自身也指向 SVG（部分生产者的写法）：走同目录位图回退。
                    var fallback = FindRasterFallback(svgBlipPart, context);
                    if (fallback is not null)
                    {
                        partName = fallback.Value.PartName;
                        data = fallback.Value.Data;
                        contentType = _package.GetContentType(partName);
                    }
                    else
                    {
                        context.Note("矢量图片缺少可用的位图回退，已跳过该图片");
                        return null;
                    }
                }

                isVectorFallback = true;
                context.Note(VectorFallbackNotice);
            }
            else if (IsVectorPart(partName, contentType))
            {
                vectorPartName = partName;
                var raster = FindRasterFallback(partName, context);
                if (raster is not null)
                {
                    partName = raster.Value.PartName;
                    data = raster.Value.Data;
                    contentType = _package.GetContentType(partName);
                    isVectorFallback = true;
                    context.Note(VectorFallbackNotice);
                }
                else
                {
                    context.Note("矢量图片缺少可用的位图回退，已跳过该图片");
                    return null;
                }
            }

            var crop = ReadSourceCrop(blipFill?.Element(Ns.A + "srcRect"));

            return new PptxPictureNode
            {
                Id = id,
                Name = name,
                Rect = rect,
                Rotation = rotation,
                Hidden = hidden,
                IsPlaceholder = placeholder is not null,
                PlaceholderType = placeholderType,
                PlaceholderIndex = placeholderIndex,
                PartName = partName,
                Data = data,
                ContentType = contentType,
                IsVectorFallback = isVectorFallback,
                VectorPartName = vectorPartName,
                Crop = crop,
            };
        }

        /// <summary>解析 <c>p:graphicFrame</c>（表格 / SmartArt / 图表 / OLE）。</summary>
        private PptxNode? ParseGraphicFrame(XElement frame, SlideContext context, Matrix transform)
        {
            var nvGraphicFramePr = frame.Element(Ns.P + "nvGraphicFramePr");
            var cNvPr = nvGraphicFramePr?.Element(Ns.P + "cNvPr");
            var id = IntAttr(cNvPr, "id", context.NextSyntheticId());
            var name = (string?)cNvPr?.Attribute("name") ?? string.Empty;
            var hidden = IsTrue(cNvPr?.Attribute("hidden")) || (string?)cNvPr?.Attribute("show") == "0";

            var xfrm = frame.Element(Ns.P + "xfrm");
            var graphicData = frame.Element(Ns.A + "graphic")?.Element(Ns.A + "graphicData");
            var uri = (string?)graphicData?.Attribute("uri") ?? string.Empty;

            if (graphicData?.Element(Ns.A + "tbl") is { } table)
            {
                var rect = ReadXfrmRect(xfrm, transform);
                return ParseTable(table, id, name, rect, hidden, context);
            }

            if (graphicData?.Elements().Any(e => e.Name.Namespace == Ns.Dgm) == true)
            {
                var rect = ReadXfrmRect(xfrm, transform);
                context.Note(SmartArtNotice);
                var relIds = graphicData.Elements().FirstOrDefault(e => e.Name.Namespace == Ns.Dgm);
                var dataPart = relIds is null ? null : ResolveFirstRelationshipTarget(frame, context, relIds);
                return new PptxSmartArtNode
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(name) ? "SmartArt" : name,
                    Rect = rect,
                    Hidden = hidden,
                    PlaceholderText = SmartArtNotice,
                    DataPartName = dataPart,
                };
            }

            var frameRect = ReadXfrmRect(xfrm, transform);
            if (uri.Contains("/chart", StringComparison.OrdinalIgnoreCase))
            {
                context.Note("图表暂不支持（仅显示占位）");
                return new PptxUnknownNode
                {
                    Id = id,
                    Name = name,
                    Rect = frameRect,
                    Hidden = hidden,
                    PlaceholderText = "图表暂不支持（仅显示占位）",
                };
            }

            context.Note("嵌入对象暂不支持（仅显示占位）");
            return new PptxUnknownNode
            {
                Id = id,
                Name = name,
                Rect = frameRect,
                Hidden = hidden,
                PlaceholderText = "嵌入对象暂不支持（仅显示占位）",
            };
        }

        /// <summary>解析表格（含 gridSpan / rowSpan / hMerge / vMerge）。</summary>
        private PptxTableNode ParseTable(
            XElement table,
            int id,
            string name,
            Rect rect,
            bool hidden,
            SlideContext context)
        {
            var theme = context.Inheritance.Theme;
            var columns = table.Element(Ns.A + "tblGrid")?.Elements(Ns.A + "gridCol")
                .Select(c => PptxUnits.ToPixels(DoubleAttr(c, "w", 0d)))
                .ToArray() ?? Array.Empty<double>();

            var tableProperties = table.Element(Ns.A + "tblPr");
            var firstRowHeader = string.Equals((string?)tableProperties?.Attribute("firstRow"), "1", StringComparison.Ordinal);
            if (tableProperties?.Element(Ns.A + "tableStyleId") is not null)
            {
                context.Note("表格样式（tableStyleId）暂不支持，仅按单元格显式格式渲染");
            }

            var rowElements = table.Elements(Ns.A + "tr").ToList();
            var rowHeights = rowElements
                .Select(tr => PptxUnits.ToPixels(DoubleAttr(tr, "h", 0d)))
                .ToArray();

            var rows = new List<PptxTableRow>(rowElements.Count);
            var y = rect.Y;

            for (var rowIndex = 0; rowIndex < rowElements.Count; rowIndex++)
            {
                var rowHeight = rowIndex < rowHeights.Length ? rowHeights[rowIndex] : 0d;
                var cells = new List<PptxTableCell>();
                var x = rect.X;
                var columnIndex = 0;

                foreach (var tc in rowElements[rowIndex].Elements(Ns.A + "tc"))
                {
                    var gridSpan = Math.Max(1, IntAttr(tc, "gridSpan", 1));
                    var rowSpan = Math.Max(1, IntAttr(tc, "rowSpan", 1));
                    var hMerge = IsTrue(tc.Attribute("hMerge"));
                    var vMerge = IsTrue(tc.Attribute("vMerge"));

                    var width = 0d;
                    for (var i = 0; i < gridSpan && columnIndex + i < columns.Length; i++)
                    {
                        width += columns[columnIndex + i];
                    }

                    if (width <= 0 && columns.Length > 0)
                    {
                        width = columns[Math.Min(columnIndex, columns.Length - 1)];
                    }

                    var height = 0d;
                    for (var i = 0; i < rowSpan && rowIndex + i < rowHeights.Length; i++)
                    {
                        height += rowHeights[rowIndex + i];
                    }

                    if (height <= 0)
                    {
                        height = rowHeight;
                    }

                    var cellRect = new Rect(x, y, width, height);
                    var cellProperties = tc.Element(Ns.A + "tcPr");
                    var cellText = ParseTextBody(tc.Element(Ns.A + "txBody"), context, "tbl", 0);

                    cells.Add(new PptxTableCell
                    {
                        Rect = cellRect,
                        GridSpan = gridSpan,
                        RowSpan = rowSpan,
                        IsMergeContinuation = hMerge || vMerge,
                        Fill = ParseFill(cellProperties, theme, context.Note),
                        BorderLeft = ParseStroke(cellProperties?.Element(Ns.A + "lnL")?.Element(Ns.A + "ln"), theme, false),
                        BorderTop = ParseStroke(cellProperties?.Element(Ns.A + "lnT")?.Element(Ns.A + "ln"), theme, false),
                        BorderRight = ParseStroke(cellProperties?.Element(Ns.A + "lnR")?.Element(Ns.A + "ln"), theme, false),
                        BorderBottom = ParseStroke(cellProperties?.Element(Ns.A + "lnB")?.Element(Ns.A + "ln"), theme, false),
                        Text = cellText,
                        Anchor = ParseAnchor((string?)cellProperties?.Attribute("anchor")),
                        MarginLeftPx = PptxUnits.ToPixels(DoubleAttr(cellProperties, "marL", 91440d)),
                        MarginTopPx = PptxUnits.ToPixels(DoubleAttr(cellProperties, "marT", 45720d)),
                        MarginRightPx = PptxUnits.ToPixels(DoubleAttr(cellProperties, "marR", 91440d)),
                        MarginBottomPx = PptxUnits.ToPixels(DoubleAttr(cellProperties, "marB", 45720d)),
                    });

                    x += width;
                    columnIndex += gridSpan;
                }

                rows.Add(new PptxTableRow { Height = rowHeight, Cells = cells });
                y += rowHeight;
            }

            return new PptxTableNode
            {
                Id = id,
                Name = name,
                Rect = rect,
                Hidden = hidden,
                ColumnWidths = columns,
                Rows = rows,
                FirstRowHeader = firstRowHeader,
            };
        }

        // ───────────────────────── 文本 ─────────────────────────

        /// <summary>解析 <c>p:txBody</c> 为文本框内容；没有可见文字时返回 <c>null</c>。</summary>
        private PptxTextBody? ParseTextBody(
            XElement? txBody,
            SlideContext context,
            string? placeholderType,
            int placeholderIndex)
        {
            if (txBody is null)
            {
                return null;
            }

            var bodyProperties = txBody.Element(Ns.A + "bodyPr");

            // 自动缩放比例先算出来，供 run 字号使用（a:normAutofit/@fontScale）。
            var autoFit = PptxAutoFit.None;
            var fontScale = 1d;
            if (bodyProperties?.Element(Ns.A + "normAutofit") is { } normalAutoFit)
            {
                autoFit = PptxAutoFit.Normal;
                fontScale = DoubleAttr(normalAutoFit, "fontScale", 100000d) / 100000d;
                if (fontScale is <= 0 or > 1)
                {
                    fontScale = 1d;
                }
            }
            else if (bodyProperties?.Element(Ns.A + "spAutoFit") is not null)
            {
                autoFit = PptxAutoFit.Shape;
            }

            var paragraphs = new List<PptxParagraph>();
            var hasText = false;
            var previousScale = context.CurrentFontScale;
            context.CurrentFontScale = fontScale;
            try
            {
                foreach (var paragraphElement in txBody.Elements(Ns.A + "p"))
                {
                    var paragraph = ParseParagraph(paragraphElement, txBody, context, placeholderType, placeholderIndex);
                    if (!string.IsNullOrEmpty(paragraph.Text))
                    {
                        hasText = true;
                    }

                    paragraphs.Add(paragraph);
                }
            }
            finally
            {
                context.CurrentFontScale = previousScale;
            }

            if (!hasText)
            {
                return null;
            }

            var wrap = !string.Equals((string?)bodyProperties?.Attribute("wrap"), "none", StringComparison.OrdinalIgnoreCase);
            var vertical = (string?)bodyProperties?.Attribute("vert");
            var isVertical = !string.IsNullOrEmpty(vertical) && vertical is not ("horz" or "horzOverflow");
            if (isVertical)
            {
                context.Note("竖排文本暂不支持，已按横排渲染");
            }

            var insets = ReadTextInsets(bodyProperties);

            return new PptxTextBody
            {
                Paragraphs = paragraphs,
                Anchor = ParseAnchor((string?)bodyProperties?.Attribute("anchor")),
                WordWrap = wrap,
                InsetLeftPx = insets.Left,
                InsetTopPx = insets.Top,
                InsetRightPx = insets.Right,
                InsetBottomPx = insets.Bottom,
                AutoFit = autoFit,
                FontScale = fontScale,
                IsVertical = isVertical,
            };
        }

        /// <summary>解析一个段落（<c>a:p</c>）。</summary>
        private PptxParagraph ParseParagraph(
            XElement paragraph,
            XElement txBody,
            SlideContext context,
            string? placeholderType,
            int placeholderIndex)
        {
            var theme = context.Inheritance.Theme;
            var pPr = paragraph.Element(Ns.A + "pPr");
            var level = Math.Clamp(IntAttr(pPr, "lvl", 0), 0, 8);
            var levelStyles = context.Inheritance.BuildLevelStyles(_defaultTextStyle, txBody, placeholderType, placeholderIndex, level);
            var defaults = CreateParagraphDefaults(placeholderType);

            var alignment = PptxTextAlignment.Left;
            var hasAlignment = false;
            double? lineSpacingFactor = null;
            double? lineSpacingPx = null;
            double? spaceBefore = null;
            double? spaceAfter = null;
            double? indent = null;
            double? hanging = null;
            PptxBullet? bullet = null;

            for (var styleIndex = levelStyles.Count - 1; styleIndex >= 0; styleIndex--)
            {
                var style = levelStyles[styleIndex];
                if (!hasAlignment && TryParseAlignment((string?)style.Attribute("algn"), out var parsedAlignment))
                {
                    alignment = parsedAlignment;
                    hasAlignment = true;
                }

                ApplyLineSpacing(style.Element(Ns.A + "lnSpc"), ref lineSpacingFactor, ref lineSpacingPx);
                spaceBefore ??= ParseSpacingPoints(style.Element(Ns.A + "spcBef"));
                spaceAfter ??= ParseSpacingPoints(style.Element(Ns.A + "spcAft"));
                indent ??= style.Attribute("marL") is { } marL ? PptxUnits.ToPixels((double)marL) : null;
                hanging ??= style.Attribute("indent") is { } ind ? PptxUnits.ToPixels((double)ind) : null;
                bullet ??= ParseBullet(style, theme);
            }

            var runs = new List<PptxTextRun>();
            foreach (var child in paragraph.Elements())
            {
                if (child.Name == Ns.A + "r")
                {
                    var text = child.Element(Ns.A + "t")?.Value ?? string.Empty;
                    runs.Add(ParseRun(text, child.Element(Ns.A + "rPr"), pPr, levelStyles, theme, defaults, context));
                }
                else if (child.Name == Ns.A + "br")
                {
                    runs.Add(ParseRun("\n", child.Element(Ns.A + "rPr"), pPr, levelStyles, theme, defaults, context));
                }
                else if (child.Name == Ns.A + "fld")
                {
                    var text = child.Element(Ns.A + "t")?.Value ?? string.Empty;
                    runs.Add(ParseRun(text, child.Element(Ns.A + "rPr"), pPr, levelStyles, theme, defaults, context));
                }
                else if (child.Name == Ns.A + "m")
                {
                    context.Note("公式内容暂不支持，已跳过");
                }
            }

            // 空段落：用 endParaRPr 的格式占位，保证空行高度正确。
            if (runs.Count == 0)
            {
                runs.Add(ParseRun(string.Empty, paragraph.Element(Ns.A + "endParaRPr"), pPr, levelStyles, theme, defaults, context));
            }

            return new PptxParagraph
            {
                Runs = runs,
                Alignment = alignment,
                Level = level,
                LineSpacingFactor = lineSpacingFactor ?? 1d,
                LineSpacingPx = lineSpacingPx,
                SpaceBeforePx = spaceBefore ?? 0d,
                SpaceAfterPx = spaceAfter ?? 0d,
                IndentPx = indent ?? defaults.IndentPx * level,
                HangingIndentPx = hanging ?? defaults.HangingIndentPx,
                Bullet = bullet ?? defaults.Bullet,
            };
        }

        /// <summary>
        /// 解析字体族记号：<c>+mj-lt</c> / <c>+mn-lt</c> 分别代表主题主字体与主题正文字体；
        /// 空字符串表示继承默认（返回 <c>null</c>，由渲染层走字体回退链）。
        /// </summary>
        private static string? ResolveTypefaceName(string? typeface, ThemeData theme)
        {
            if (string.IsNullOrWhiteSpace(typeface))
            {
                return null;
            }

            var name = typeface.Trim();
            return name switch
            {
                "+mj-lt" or "+mj-ea" or "+mj-cs" => theme.MajorFont,
                "+mn-lt" or "+mn-ea" or "+mn-cs" => theme.MinorFont,
                _ => name,
            };
        }

        /// <summary>解析一个 run 的最终格式（级别样式 → 段落 defRPr → run rPr）。</summary>
        private PptxTextRun ParseRun(
            string text,
            XElement? rPr,
            XElement? pPr,
            IReadOnlyList<XElement> levelStyles,
            ThemeData theme,
            ParagraphDefaults defaults,
            SlideContext context)
        {
            var style = new RunStyleAccumulator();

            foreach (var levelStyle in levelStyles)
            {
                style.Apply(levelStyle.Element(Ns.A + "defRPr"), theme);
            }

            style.Apply(pPr?.Element(Ns.A + "defRPr"), theme);
            style.Apply(rPr, theme);

            var fontFamily = ResolveTypefaceName(style.FontFamily, theme);
            var fontScale = context.CurrentFontScale;

            return new PptxTextRun
            {
                Text = text,
                FontFamily = fontFamily,
                FontSizePx = (style.FontSizePx ?? defaults.FontSizePx) * fontScale,
                Bold = style.Bold ?? defaults.Bold,
                Italic = style.Italic ?? false,
                Underline = style.Underline ?? false,
                Strikethrough = style.Strikethrough ?? false,
                Color = style.Color ?? defaults.Color,
                LetterSpacingPx = style.LetterSpacingPx ?? 0d,
            };
        }

        /// <summary>解析项目符号定义。</summary>
        private PptxBullet? ParseBullet(XElement style, ThemeData theme)
        {
            if (style.Element(Ns.A + "buNone") is not null)
            {
                return new PptxBullet { Kind = PptxBulletKind.None };
            }

            if (style.Element(Ns.A + "buChar") is { } character)
            {
                return new PptxBullet
                {
                    Kind = PptxBulletKind.Character,
                    Character = (string?)character.Attribute("char") ?? "•",
                    Color = ResolveColor(style.Element(Ns.A + "buClr"), theme, null),
                    SizePercent = DoubleAttr(style.Element(Ns.A + "buSzPct"), "val", 100000d) / 1000d,
                };
            }

            if (style.Element(Ns.A + "buAutoNum") is { } autoNumber)
            {
                return new PptxBullet
                {
                    Kind = PptxBulletKind.Number,
                    NumberType = (string?)autoNumber.Attribute("type") ?? "arabicPeriod",
                    StartAt = Math.Max(1, IntAttr(autoNumber, "startAt", 1)),
                    Color = ResolveColor(style.Element(Ns.A + "buClr"), theme, null),
                    SizePercent = DoubleAttr(style.Element(Ns.A + "buSzPct"), "val", 100000d) / 1000d,
                };
            }

            return null;
        }

        /// <summary>按占位符类型给出段落默认值。</summary>
        private static ParagraphDefaults CreateParagraphDefaults(string? placeholderType)
        {
            var isTitle = placeholderType is "title" or "ctrTitle";
            return isTitle
                ? new ParagraphDefaults(0d, 0d, new PptxBullet { Kind = PptxBulletKind.None }, 58.67d, true, Colors.Black)
                : new ParagraphDefaults(0d, -16d, new PptxBullet { Kind = PptxBulletKind.None }, 24d, false, Colors.Black);
        }

        /// <summary>段落默认值。</summary>
        private readonly record struct ParagraphDefaults(
            double IndentPx,
            double HangingIndentPx,
            PptxBullet Bullet,
            double FontSizePx,
            bool Bold,
            Color Color);

        // ───────────────────────── 颜色 / 填充 / 描边 ─────────────────────────

        /// <summary>解析填充（纯色 / 渐变 / 无）。</summary>
        private PptxFill ParseFill(XElement? properties, ThemeData theme, Action<string>? note)
        {
            if (properties is null)
            {
                return PptxFill.None;
            }

            foreach (var child in properties.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "solidFill":
                        return PptxFill.Solid(ResolveColor(child, theme, Colors.Black) ?? Colors.Black);
                    case "gradFill":
                        return ParseGradient(child, theme);
                    case "noFill":
                        return PptxFill.None;
                    case "blipFill":
                        note?.Invoke("图片填充暂不支持，已按透明处理");
                        return PptxFill.None;
                    case "pattFill":
                        note?.Invoke("图案填充暂不支持，已按前景色纯色处理");
                        return PptxFill.Solid(ResolveColor(child.Element(Ns.A + "fgClr"), theme, Colors.Black) ?? Colors.Black);
                    case "grpFill":
                        return PptxFill.None;
                }
            }

            return PptxFill.None;
        }

        /// <summary>解析渐变填充。</summary>
        private PptxGradientFill ParseGradient(XElement gradient, ThemeData theme)
        {
            var stops = new List<PptxGradientStop>();
            foreach (var stop in gradient.Element(Ns.A + "gsLst")?.Elements(Ns.A + "gs") ?? Enumerable.Empty<XElement>())
            {
                var offset = Math.Clamp(DoubleAttr(stop, "pos", 0d) / 100000d, 0d, 1d);
                var color = ResolveColor(stop, theme, Colors.Black) ?? Colors.Black;
                stops.Add(new PptxGradientStop { Offset = offset, Color = color });
            }

            stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            var linear = gradient.Element(Ns.A + "lin");
            var isPath = gradient.Element(Ns.A + "path") is not null;

            // OOXML 角度：0 = 从左到右，顺时针为正（单位 1/60000 度）。
            var angle = DoubleAttr(linear, "ang", 0d) / 60000d;

            return new PptxGradientFill
            {
                Stops = stops,
                AngleDegrees = angle,
                IsPathGradient = isPath,
            };
        }

        /// <summary>解析描边（<c>a:ln</c>）。</summary>
        private PptxStroke? ParseStroke(XElement? line, ThemeData theme, bool flip)
        {
            if (line is null)
            {
                return null;
            }

            if (line.Element(Ns.A + "noFill") is not null)
            {
                return PptxStroke.None;
            }

            var widthEmu = DoubleAttr(line, "w", 12700d);
            var width = Math.Max(0.5d, PptxUnits.ToPixels(widthEmu));
            var color = Colors.Black;

            foreach (var child in line.Elements())
            {
                if (child.Name.LocalName is "solidFill" or "gradFill")
                {
                    var fill = ParseFill(line, theme, null);
                    color = fill switch
                    {
                        PptxSolidFill solid => solid.Color,
                        PptxGradientFill gradient when gradient.Stops.Count > 0 => gradient.Stops[0].Color,
                        _ => Colors.Black,
                    };
                    break;
                }
            }

            var dash = PptxDashStyle.Solid;
            var dashElement = line.Element(Ns.A + "prstDash");
            if (dashElement is not null)
            {
                dash = MapDash((string?)dashElement.Attribute("val"));
            }
            else if (line.Element(Ns.A + "custDash") is not null)
            {
                dash = PptxDashStyle.Dash;
            }

            return new PptxStroke
            {
                Color = color,
                WidthPx = width,
                DashStyle = dash,
                IsNone = false,
            };
        }

        /// <summary>解析箭头端点。</summary>
        private static PptxArrowHead ParseArrowHead(XElement? endpoint)
        {
            var type = (string?)endpoint?.Attribute("type");
            return type switch
            {
                "triangle" => PptxArrowHead.Triangle,
                "arrow" => PptxArrowHead.Arrow,
                "stealth" => PptxArrowHead.Stealth,
                "diamond" => PptxArrowHead.Diamond,
                "oval" => PptxArrowHead.Oval,
                _ => PptxArrowHead.None,
            };
        }

        /// <summary>把 DrawingML 颜色（含主题色与明暗变换）解析成 <see cref="Color"/>。</summary>
        internal static Color? ResolveColor(XElement? colorParent, ThemeData theme, Color? fallback)
        {
            if (colorParent is null)
            {
                return fallback;
            }

            var colorElement = colorParent.Elements().FirstOrDefault(e =>
                e.Name.Namespace == Ns.A &&
                e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr" or "scrgbClr" or "hslClr");

            if (colorElement is null)
            {
                return fallback;
            }

            Color color;
            switch (colorElement.Name.LocalName)
            {
                case "srgbClr":
                    color = ParseHex((string?)colorElement.Attribute("val")) ?? fallback ?? Colors.Black;
                    break;
                case "schemeClr":
                    color = theme.ResolveSchemeColor((string?)colorElement.Attribute("val"), fallback ?? Colors.Black);
                    break;
                case "sysClr":
                    color = ParseHex((string?)colorElement.Attribute("lastClr"))
                            ?? theme.ResolveSchemeColor(MapSystemColor((string?)colorElement.Attribute("val")), fallback ?? Colors.Black);
                    break;
                case "prstClr":
                    color = PresetColors.TryGetValue((string?)colorElement.Attribute("val") ?? string.Empty, out var preset)
                        ? preset
                        : fallback ?? Colors.Black;
                    break;
                case "scrgbClr":
                    var r = Math.Clamp(DoubleAttr(colorElement, "r", 0d) / 100000d, 0d, 1d);
                    var g = Math.Clamp(DoubleAttr(colorElement, "g", 0d) / 100000d, 0d, 1d);
                    var b = Math.Clamp(DoubleAttr(colorElement, "b", 0d) / 100000d, 0d, 1d);
                    color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
                    break;
                case "hslClr":
                    var hue = DoubleAttr(colorElement, "hue", 0d) / 60000d;
                    var sat = Math.Clamp(DoubleAttr(colorElement, "sat", 0d) / 100000d, 0d, 1d);
                    var lum = Math.Clamp(DoubleAttr(colorElement, "lum", 0d) / 100000d, 0d, 1d);
                    color = ColorFromHsl(hue, sat, lum);
                    break;
                default:
                    return fallback;
            }

            var alpha = color.A / 255d;
            foreach (var transform in colorElement.Elements())
            {
                switch (transform.Name.LocalName)
                {
                    case "lumMod":
                        color = AdjustLuminance(color, DoubleAttr(transform, "val", 100000d) / 100000d, 0d);
                        break;
                    case "lumOff":
                        color = AdjustLuminance(color, 1d, DoubleAttr(transform, "val", 0d) / 100000d);
                        break;
                    case "tint":
                        color = ApplyTint(color, DoubleAttr(transform, "val", 100000d) / 100000d);
                        break;
                    case "shade":
                        color = ApplyShade(color, DoubleAttr(transform, "val", 100000d) / 100000d);
                        break;
                    case "satMod":
                        color = AdjustSaturation(color, DoubleAttr(transform, "val", 100000d) / 100000d);
                        break;
                    case "alpha":
                        alpha *= Math.Clamp(DoubleAttr(transform, "val", 100000d) / 100000d, 0d, 1d);
                        break;
                    case "alphaMod":
                        alpha *= Math.Clamp(DoubleAttr(transform, "val", 100000d) / 100000d, 0d, 1d);
                        break;
                    case "gray":
                        var gray = (byte)Math.Round(color.R * 0.299 + color.G * 0.587 + color.B * 0.114);
                        color = Color.FromRgb(gray, gray, gray);
                        break;
                    case "inv":
                        color = Color.FromRgb((byte)(255 - color.R), (byte)(255 - color.G), (byte)(255 - color.B));
                        break;
                }
            }

            return Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0d, 1d) * 255), color.R, color.G, color.B);
        }

        // ───────────────────────── 几何换算工具 ─────────────────────────

        /// <summary>读取 <c>a:xfrm</c> 的外接矩形（应用父级变换）。</summary>
        private static Rect ReadXfrmRect(XElement? xfrm, Matrix transform)
        {
            if (xfrm is null)
            {
                return default;
            }

            var off = xfrm.Element(Ns.A + "off");
            var ext = xfrm.Element(Ns.A + "ext");
            var x = PptxUnits.ToPixels(DoubleAttr(off, "x", 0d));
            var y = PptxUnits.ToPixels(DoubleAttr(off, "y", 0d));
            var cx = PptxUnits.ToPixels(DoubleAttr(ext, "cx", 0d));
            var cy = PptxUnits.ToPixels(DoubleAttr(ext, "cy", 0d));
            var rect = new Rect(x, y, Math.Max(0d, cx), Math.Max(0d, cy));

            return transform.IsIdentity ? rect : rect.TransformToAABB(transform);
        }

        /// <summary>直线的起止点（考虑 flipH / flipV）。</summary>
        private static (Point Start, Point End) ReadLineEndpoints(Rect rect, bool flipH, bool flipV, double rotation)
        {
            // 线条的局部几何固定是「左上 → 右下」，flipH / flipV 分别镜像 x / y 坐标。
            var x1 = rect.X;
            var x2 = rect.Right;
            var y1 = rect.Y;
            var y2 = rect.Bottom;

            if (flipH)
            {
                (x1, x2) = (x2, x1);
            }

            if (flipV)
            {
                (y1, y2) = (y2, y1);
            }

            return (new Point(x1, y1), new Point(x2, y2));
        }

        /// <summary>读取几何调整值（<c>a:avLst/a:gd</c>）。</summary>
        private static Dictionary<string, double> ReadAdjustments(XElement? avLst)
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (avLst is null)
            {
                return map;
            }

            foreach (var gd in avLst.Elements(Ns.A + "gd"))
            {
                var name = (string?)gd.Attribute("name");
                var formula = (string?)gd.Attribute("fmla");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(formula))
                {
                    continue;
                }

                if (formula.StartsWith("val", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = formula[3..].Trim();
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        map[name] = value / 100000d;
                    }
                }
            }

            return map;
        }

        /// <summary>读取自定义几何路径，转换为 SVG 路径语法（路径空间坐标）。</summary>
        private static (string? Path, Size Size) ReadCustomGeometry(XElement custGeom, Rect rect, SlideContext context)
        {
            var path = custGeom.Element(Ns.A + "pathLst")?.Elements(Ns.A + "path").FirstOrDefault();
            if (path is null)
            {
                context.Note("自定义几何缺少路径数据，已按矩形绘制");
                return (null, new Size(rect.Width, rect.Height));
            }

            var pathWidth = DoubleAttr(path, "w", 0d);
            var pathHeight = DoubleAttr(path, "h", 0d);
            if (pathWidth <= 0 || pathHeight <= 0)
            {
                context.Note("自定义几何路径空间无效，已按矩形绘制");
                return (null, new Size(rect.Width, rect.Height));
            }

            var scaleX = rect.Width / pathWidth;
            var scaleY = rect.Height / pathHeight;
            var builder = new StringBuilder();
            var supported = true;

            foreach (var command in path.Elements())
            {
                switch (command.Name.LocalName)
                {
                    case "moveTo":
                    case "lnTo":
                        var point = command.Element(Ns.A + "pt");
                        if (point is null)
                        {
                            continue;
                        }

                        var px = DoubleAttr(point, "x", 0d) * scaleX;
                        var py = DoubleAttr(point, "y", 0d) * scaleY;
                        builder.Append(command.Name.LocalName == "moveTo" ? 'M' : 'L')
                            .Append(Fmt(px)).Append(' ').Append(Fmt(py)).Append(' ');
                        break;
                    case "cubicBezTo":
                        var points = command.Elements(Ns.A + "pt").ToList();
                        if (points.Count < 3)
                        {
                            continue;
                        }

                        builder.Append('C');
                        foreach (var p in points)
                        {
                            builder.Append(Fmt(DoubleAttr(p, "x", 0d) * scaleX)).Append(' ')
                                .Append(Fmt(DoubleAttr(p, "y", 0d) * scaleY)).Append(' ');
                        }

                        break;
                    case "quadBezTo":
                        var quadPoints = command.Elements(Ns.A + "pt").ToList();
                        if (quadPoints.Count < 2)
                        {
                            continue;
                        }

                        builder.Append('Q');
                        foreach (var p in quadPoints)
                        {
                            builder.Append(Fmt(DoubleAttr(p, "x", 0d) * scaleX)).Append(' ')
                                .Append(Fmt(DoubleAttr(p, "y", 0d) * scaleY)).Append(' ');
                        }

                        break;
                    case "close":
                        builder.Append("Z ");
                        break;
                    case "arcTo":
                        // 圆弧转换为直线近似：只取终点，保证形状轮廓可辨认。
                        var arcPoint = command.Element(Ns.A + "pt");
                        if (arcPoint is null)
                        {
                            continue;
                        }

                        builder.Append('L')
                            .Append(Fmt(DoubleAttr(arcPoint, "x", 0d) * scaleX)).Append(' ')
                            .Append(Fmt(DoubleAttr(arcPoint, "y", 0d) * scaleY)).Append(' ');
                        break;
                    default:
                        supported = false;
                        break;
                }
            }

            if (!supported)
            {
                context.Note("自定义几何中的弧线已按直线近似");
            }

            return builder.Length == 0
                ? (null, new Size(rect.Width, rect.Height))
                : (builder.ToString(), new Size(rect.Width, rect.Height));
        }

        /// <summary>把预置几何名映射到内部枚举。</summary>
        private static PptxGeometryKind MapGeometry(string name) => name switch
        {
            "rect" => PptxGeometryKind.Rectangle,
            "roundRect" => PptxGeometryKind.RoundRectangle,
            "ellipse" or "circle" => PptxGeometryKind.Ellipse,
            "triangle" => PptxGeometryKind.Triangle,
            "rtTriangle" => PptxGeometryKind.RightTriangle,
            "diamond" => PptxGeometryKind.Diamond,
            "parallelogram" => PptxGeometryKind.Parallelogram,
            "trapezoid" => PptxGeometryKind.Trapezoid,
            "pentagon" => PptxGeometryKind.Pentagon,
            "hexagon" => PptxGeometryKind.Hexagon,
            "octagon" => PptxGeometryKind.Octagon,
            "star5" => PptxGeometryKind.Star5,
            "star4" => PptxGeometryKind.Star4,
            "star6" => PptxGeometryKind.Star6,
            "rightArrow" => PptxGeometryKind.ArrowRight,
            "leftArrow" => PptxGeometryKind.ArrowLeft,
            "upArrow" => PptxGeometryKind.ArrowUp,
            "downArrow" => PptxGeometryKind.ArrowDown,
            "chevron" => PptxGeometryKind.Chevron,
            "plus" or "mathPlus" => PptxGeometryKind.Cross,
            "flowChartProcess" => PptxGeometryKind.FlowChartProcess,
            "flowChartDecision" => PptxGeometryKind.FlowChartDecision,
            "flowChartData" or "flowChartInputOutput" => PptxGeometryKind.FlowChartData,
            "line" or "straightConnector1" => PptxGeometryKind.Line,
            _ => PptxGeometryKind.Unsupported,
        };

        /// <summary>是否为线条类预置几何（含折线连接线）。</summary>
        private static bool IsLinePreset(string name)
            => name is "line" or "straightConnector1" or "bentConnector2" or "bentConnector3" or "bentConnector4"
                or "bentConnector5" or "curvedConnector2" or "curvedConnector3" or "curvedConnector4" or "curvedConnector5";

        /// <summary>把 <c>a:prstDash/@val</c> 映射为内部虚线样式。</summary>
        private static PptxDashStyle MapDash(string? value) => value switch
        {
            "dash" => PptxDashStyle.Dash,
            "dot" => PptxDashStyle.Dot,
            "dashDot" => PptxDashStyle.DashDot,
            "lgDash" => PptxDashStyle.LongDash,
            "lgDashDot" or "lgDashDotDot" => PptxDashStyle.LongDashDot,
            "sysDash" => PptxDashStyle.SystemDash,
            "sysDot" => PptxDashStyle.SystemDot,
            _ => PptxDashStyle.Solid,
        };

        /// <summary>解析锚点。</summary>
        private static PptxTextAnchor ParseAnchor(string? value) => value switch
        {
            "ctr" => PptxTextAnchor.Middle,
            "b" => PptxTextAnchor.Bottom,
            _ => PptxTextAnchor.Top,
        };

        /// <summary>解析对齐。</summary>
        private static bool TryParseAlignment(string? value, out PptxTextAlignment alignment)
        {
            switch (value)
            {
                case "ctr":
                    alignment = PptxTextAlignment.Center;
                    return true;
                case "r":
                    alignment = PptxTextAlignment.Right;
                    return true;
                case "just" or "dist":
                    alignment = PptxTextAlignment.Justify;
                    return true;
                case "l":
                    alignment = PptxTextAlignment.Left;
                    return true;
                default:
                    alignment = PptxTextAlignment.Left;
                    return false;
            }
        }

        /// <summary>行距：百分比（倍数）或磅值（绝对像素），取第一个有定义的来源。</summary>
        private static void ApplyLineSpacing(XElement? lineSpacing, ref double? factor, ref double? pixels)
        {
            if (lineSpacing is null || factor is not null || pixels is not null)
            {
                return;
            }

            if (lineSpacing.Element(Ns.A + "spcPct") is { } percent)
            {
                var value = DoubleAttr(percent, "val", 100000d) / 100000d;
                if (value > 0)
                {
                    factor = value;
                }

                return;
            }

            if (lineSpacing.Element(Ns.A + "spcPts") is { } points)
            {
                var value = PptxUnits.HundredthPointsToPixels(DoubleAttr(points, "val", 0d));
                if (value > 0)
                {
                    pixels = value;
                }
            }
        }

        /// <summary>段前 / 段后距（磅）。</summary>
        private static double? ParseSpacingPoints(XElement? spacing)
        {
            if (spacing?.Element(Ns.A + "spcPts") is not { } points)
            {
                return null;
            }

            return PptxUnits.HundredthPointsToPixels(DoubleAttr(points, "val", 0d));
        }

        /// <summary>文本框内边距。</summary>
        private static Thickness ReadTextInsets(XElement? bodyProperties)
        {
            var left = DoubleAttr(bodyProperties, "lIns", 91440d);
            var top = DoubleAttr(bodyProperties, "tIns", 45720d);
            var right = DoubleAttr(bodyProperties, "rIns", 91440d);
            var bottom = DoubleAttr(bodyProperties, "bIns", 45720d);
            return new Thickness(
                PptxUnits.ToPixels(left),
                PptxUnits.ToPixels(top),
                PptxUnits.ToPixels(right),
                PptxUnits.ToPixels(bottom));
        }

        /// <summary>图片裁剪比例。</summary>
        private static Thickness ReadSourceCrop(XElement? sourceRect)
        {
            if (sourceRect is null)
            {
                return default;
            }

            return new Thickness(
                Math.Clamp(DoubleAttr(sourceRect, "l", 0d) / 100000d, 0d, 1d),
                Math.Clamp(DoubleAttr(sourceRect, "t", 0d) / 100000d, 0d, 1d),
                Math.Clamp(DoubleAttr(sourceRect, "r", 0d) / 100000d, 0d, 1d),
                Math.Clamp(DoubleAttr(sourceRect, "b", 0d) / 100000d, 0d, 1d));
        }

        /// <summary>读取 <c>a:blip</c> 对应的图片字节。</summary>
        private byte[]? ReadBlipBytes(XElement? blip, SlideContext context, string shapeName, out string? partName)
        {
            partName = null;
            if (blip is null)
            {
                return null;
            }

            var embed = (string?)blip.Attribute(Ns.R + "embed");
            if (!string.IsNullOrEmpty(embed))
            {
                var target = _package.ResolveTarget(context.SlidePart, embed);
                if (target is not null)
                {
                    partName = target;
                    return _package.LoadBytes(target);
                }
            }

            var link = (string?)blip.Attribute(Ns.R + "link");
            if (!string.IsNullOrEmpty(link))
            {
                var target = _package.ResolveTarget(context.SlidePart, link);
                if (target is not null && _package.Exists(target))
                {
                    partName = target;
                    return _package.LoadBytes(target);
                }

                context.Note($"图片「{shapeName}」为外链图片，暂不支持（仅显示占位）");
            }

            return null;
        }

        /// <summary>
        /// 从 <c>a:blip</c> 的扩展列表里取 <c>asvg:svgBlip</c>（SVG 矢量源）指向的部件名。
        /// 该扩展的 uri 固定为 <c>{96DAC541-7B7A-43D3-8B79-37D633B846F1}</c>，
        /// 命名空间为 <c>http://schemas.microsoft.com/office/drawing/2016/SVG/main</c>。
        /// </summary>
        private string? ResolveSvgBlipPart(XElement? blip, SlideContext context)
        {
            var svgBlip = blip?
                .Element(Ns.A + "extLst")?
                .Elements(Ns.A + "ext")
                .SelectMany(ext => ext.Elements())
                .FirstOrDefault(element => element.Name.LocalName == "svgBlip");

            var relationshipId = svgBlip is null ? null : (string?)svgBlip.Attribute(Ns.R + "embed");
            if (relationshipId is null)
            {
                return null;
            }

            var target = _package.ResolveTarget(context.SlidePart, relationshipId);
            return target is not null && _package.Exists(target) ? target : null;
        }

        /// <summary>矢量图片寻找同组位图回退。</summary>
        private (string PartName, byte[] Data)? FindRasterFallback(string vectorPart, SlideContext context)
        {
            var baseName = Path.GetFileNameWithoutExtension(vectorPart);
            var directory = Path.GetDirectoryName(vectorPart)?.Replace('\\', '/') ?? string.Empty;

            foreach (var candidate in _package.PartNames)
            {
                var candidateDirectory = Path.GetDirectoryName(candidate)?.Replace('\\', '/') ?? string.Empty;
                var candidateName = Path.GetFileNameWithoutExtension(candidate);
                var extension = Path.GetExtension(candidate).ToLowerInvariant();
                if (!candidateDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase)
                    || !candidateName.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                    || extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp"))
                {
                    continue;
                }

                var data = _package.LoadBytes(candidate);
                if (data is not null)
                {
                    return (candidate, data);
                }
            }

            // 再退一步：同一页里任意一张位图。
            foreach (var relationship in _package.GetRelationships(context.SlidePart).Values)
            {
                if (!relationship.Type.Contains("image", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = _package.ResolveTarget(context.SlidePart, relationship.Id);
                if (target is null)
                {
                    continue;
                }

                var extension = Path.GetExtension(target).ToLowerInvariant();
                if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")
                {
                    var data = _package.LoadBytes(target);
                    if (data is not null)
                    {
                        return (target, data);
                    }
                }
            }

            return null;
        }

        /// <summary>按内容类型判断是否矢量图。</summary>
        private static bool IsVectorPart(string partName, string contentType)
            => contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)
               || Path.GetExtension(partName).Equals(".svg", StringComparison.OrdinalIgnoreCase);

        /// <summary>构造媒体部件模型并导出到缓存目录。</summary>
        private PptxMediaFile BuildMediaFile(
            PptxMediaKind kind,
            XElement reference,
            SlideContext context,
            Rect rect,
            int shapeId,
            string shapeName)
        {
            var relationshipId = (string?)reference.Attribute(Ns.R + "embed")
                                 ?? (string?)reference.Attribute(Ns.R + "link");
            var partName = relationshipId is null ? null : _package.ResolveTarget(context.SlidePart, relationshipId);

            if (partName is null || !_package.Exists(partName))
            {
                context.Note($"{(kind == PptxMediaKind.Video ? "视频" : "音频")}「{shapeName}」的媒体部件缺失，已按占位显示");
                var missing = new PptxMediaFile { Kind = kind, Rect = rect, ShapeId = shapeId };
                context.Media.Add(missing);
                return missing;
            }

            var data = _package.LoadBytes(partName) ?? Array.Empty<byte>();
            var contentType = _package.GetContentType(partName);
            string? localPath = null;

            if (_options.ExportMedia && data.Length > 0)
            {
                localPath = MediaCacheExporter.Export(
                    _options.ResolveMediaCacheDirectory(),
                    _fileHash,
                    partName,
                    data);
            }

            var media = new PptxMediaFile
            {
                Kind = kind,
                PartName = partName,
                ContentType = contentType,
                LocalPath = localPath,
                Rect = rect,
                ShapeId = shapeId,
            };

            context.Media.Add(media);
            return media;
        }

        /// <summary>判断 <c>p14:media</c> 是否指向视频部件。</summary>
        private bool IsVideoPart(SlideContext context, XElement media)
        {
            var relationshipId = (string?)media.Attribute(Ns.R + "embed") ?? (string?)media.Attribute(Ns.R + "link");
            var partName = relationshipId is null ? null : _package.ResolveTarget(context.SlidePart, relationshipId);
            if (partName is null)
            {
                return false;
            }

            var contentType = _package.GetContentType(partName);
            return contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                   || Path.GetExtension(partName).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".mkv" or ".webm";
        }

        /// <summary>解析 <c>dgm:relIds</c> 上的第一个关系目标。</summary>
        private string? ResolveFirstRelationshipTarget(XElement owner, SlideContext context, XElement element)
        {
            var relationshipId = element.Attributes()
                .FirstOrDefault(a => a.Name.Namespace == Ns.R)?
                .Value;
            return relationshipId is null ? null : _package.ResolveTarget(context.SlidePart, relationshipId);
        }

        // ───────────────────────── 转场 ─────────────────────────

        /// <summary>找到 <c>p:transition</c>（含 <c>mc:AlternateContent</c> 里的分支）。</summary>
        private static XElement? FindTransitionElement(XElement root)
            => root.Element(Ns.P + "transition") ?? root.Descendants(Ns.P + "transition").FirstOrDefault();

        /// <summary>解析页面转场。</summary>
        private PptxTransition ParseTransition(XElement? transition, Action<string> note)
        {
            if (transition is null)
            {
                return PptxTransition.None;
            }

            var advanceOnClick = !string.Equals((string?)transition.Attribute("advClick"), "0", StringComparison.Ordinal);
            double? autoAdvance = null;
            var advanceAfterTime = (string?)transition.Attribute("advTm")
                                   ?? transition.Attributes().FirstOrDefault(a => a.Name.LocalName == "advTm")?.Value;
            if (advanceAfterTime is not null && double.TryParse(advanceAfterTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var autoMs))
            {
                autoAdvance = autoMs;
            }

            var duration = ParseTransitionDuration(transition);
            var effect = transition.Elements().FirstOrDefault(e => e.Name.LocalName is not ("sndAc" or "extLst"));

            if (effect is null)
            {
                return new PptxTransition
                {
                    Kind = PptxTransitionKind.None,
                    DurationMs = duration,
                    AdvanceOnClick = advanceOnClick,
                    AutoAdvanceAfterMs = autoAdvance,
                };
            }

            var name = effect.Name.LocalName;
            switch (name)
            {
                case "fade":
                    return new PptxTransition
                    {
                        Kind = PptxTransitionKind.Fade,
                        RawType = name,
                        DurationMs = duration,
                        AdvanceOnClick = advanceOnClick,
                        AutoAdvanceAfterMs = autoAdvance,
                    };
                case "push":
                    return new PptxTransition
                    {
                        Kind = PptxTransitionKind.Push,
                        RawType = name,
                        Direction = ParseDirection((string?)effect.Attribute("dir")),
                        DurationMs = duration,
                        AdvanceOnClick = advanceOnClick,
                        AutoAdvanceAfterMs = autoAdvance,
                    };
                case "cover":
                    return new PptxTransition
                    {
                        Kind = PptxTransitionKind.Cover,
                        RawType = name,
                        Direction = ParseDirection((string?)effect.Attribute("dir")),
                        DurationMs = duration,
                        AdvanceOnClick = advanceOnClick,
                        AutoAdvanceAfterMs = autoAdvance,
                    };
                case "wipe":
                    return new PptxTransition
                    {
                        Kind = PptxTransitionKind.Wipe,
                        RawType = name,
                        Direction = ParseDirection((string?)effect.Attribute("dir")),
                        DurationMs = duration,
                        AdvanceOnClick = advanceOnClick,
                        AutoAdvanceAfterMs = autoAdvance,
                    };
                case "split":
                    return new PptxTransition
                    {
                        Kind = PptxTransitionKind.Split,
                        RawType = name,
                        Direction = ParseDirection((string?)effect.Attribute("dir")),
                        SplitOrientation = (string?)effect.Attribute("orient") ?? "horz",
                        DurationMs = duration,
                        AdvanceOnClick = advanceOnClick,
                        AutoAdvanceAfterMs = autoAdvance,
                    };
                case "cut":
                    return new PptxTransition
                    {
                        Kind = PptxTransitionKind.None,
                        RawType = name,
                        DurationMs = 0d,
                        AdvanceOnClick = advanceOnClick,
                        AutoAdvanceAfterMs = autoAdvance,
                    };
                default:
                    note($"转场 {name} 不受支持，已降级为淡入淡出");
                    return new PptxTransition
                    {
                        Kind = PptxTransitionKind.Fade,
                        RawType = name,
                        DurationMs = duration,
                        IsDegraded = true,
                        AdvanceOnClick = advanceOnClick,
                        AutoAdvanceAfterMs = autoAdvance,
                    };
            }
        }

        /// <summary>转场时长：优先 p14:dur，其次按 spd 档位。</summary>
        private static double ParseTransitionDuration(XElement transition)
        {
            var explicitDuration = transition.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "dur")?.Value;
            if (explicitDuration is not null
                && double.TryParse(explicitDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return milliseconds;
            }

            return (string?)transition.Attribute("spd") switch
            {
                "slow" => 1000d,
                "fast" => 250d,
                _ => 500d,
            };
        }

        /// <summary>解析转场方向。</summary>
        private static PptxTransitionDirection ParseDirection(string? value) => value switch
        {
            "l" => PptxTransitionDirection.Left,
            "r" => PptxTransitionDirection.Right,
            "u" => PptxTransitionDirection.Up,
            "d" => PptxTransitionDirection.Down,
            _ => PptxTransitionDirection.None,
        };

        // ───────────────────────── 动画 ─────────────────────────

        /// <summary>解析 <c>p:timing</c> 得到动画时间轴。</summary>
        private PptxAnimationTimeline ParseTiming(XElement? timing, SlideContext context, Action<string> note)
        {
            if (timing is null)
            {
                return PptxAnimationTimeline.Empty;
            }

            try
            {
                var sequences = timing.Descendants(Ns.P + "seq").ToList();
                if (sequences.Count == 0)
                {
                    return PptxAnimationTimeline.Empty;
                }

                if (sequences.Count > 1)
                {
                    note("交互式触发器序列（多个 p:seq）暂不支持，仅播放主时间轴");
                }

                var mainSequence = sequences[0];
                var steps = new List<PptxAnimationStep>();
                var clickGroup = -1;
                var hasUnsupported = false;
                var previousTrigger = PptxAnimationTrigger.OnClick;
                double groupCursor = 0d;
                double previousStart = 0d;
                double timelineCursor = 0d;

                foreach (var effect in mainSequence.Descendants(Ns.P + "cTn"))
                {
                    if (effect.Attribute("presetClass") is null)
                    {
                        continue;
                    }

                    var step = ParseEffect(
                        effect,
                        context,
                        note,
                        clickGroup,
                        ref previousTrigger,
                        ref groupCursor,
                        ref previousStart,
                        ref timelineCursor);
                    if (step is null)
                    {
                        continue;
                    }

                    clickGroup = step.ClickGroupIndex;
                    if (step.Effect == PptxAnimationEffect.Unsupported)
                    {
                        hasUnsupported = true;
                    }

                    steps.Add(step);
                }

                if (steps.Count == 0)
                {
                    return PptxAnimationTimeline.Empty;
                }

                return new PptxAnimationTimeline
                {
                    Steps = steps,
                    ClickGroupCount = clickGroup + 1,
                    TotalDurationMs = timelineCursor,
                    HasUnsupportedEffects = hasUnsupported,
                };
            }
            catch (Exception ex)
            {
                note($"动画时间轴解析失败，本页按无动画播放（{ex.Message}）");
                return PptxAnimationTimeline.Empty;
            }
        }

        /// <summary>解析一个动画效果节点。</summary>
        private PptxAnimationStep? ParseEffect(
            XElement effect,
            SlideContext context,
            Action<string> note,
            int currentClickGroup,
            ref PptxAnimationTrigger previousTrigger,
            ref double groupCursor,
            ref double previousStart,
            ref double timelineCursor)
        {
            var presetClass = (string?)effect.Attribute("presetClass") ?? "entr";
            var presetId = IntAttr(effect, "presetID", 0);
            var subtype = IntAttr(effect, "presetSubtype", 0);
            var nodeType = (string?)effect.Attribute("nodeType");

            var trigger = nodeType switch
            {
                "clickEffect" => PptxAnimationTrigger.OnClick,
                "withEffect" => PptxAnimationTrigger.WithPrevious,
                "afterEffect" => PptxAnimationTrigger.AfterPrevious,
                _ => previousTrigger == PptxAnimationTrigger.OnClick ? PptxAnimationTrigger.AfterPrevious : previousTrigger,
            };

            var clickGroupIndex = currentClickGroup;
            if (trigger == PptxAnimationTrigger.OnClick)
            {
                clickGroupIndex = currentClickGroup + 1;
                groupCursor = 0d;
            }
            else if (clickGroupIndex < 0)
            {
                clickGroupIndex = 0;
            }

            var target = effect.Descendants(Ns.P + "spTgt").FirstOrDefault();
            var targetId = IntAttr(target, "spid", -1);
            if (targetId < 0)
            {
                // 没有目标（例如媒体播放指令）不参与节点变换。
                note("发现没有目标形状的动画指令，已忽略");
                previousTrigger = trigger;
                return null;
            }

            if (target?.Element(Ns.P + "txEl")?.Element(Ns.P + "pRg") is not null)
            {
                note("按段落 / 按字动画暂不支持，已按整形状播放");
            }

            var duration = ResolveEffectDuration(effect);
            var delay = ParseConditionDelay(effect);

            var isExit = string.Equals(presetClass, "exit", StringComparison.OrdinalIgnoreCase);
            var (kind, direction, degradedNotice) = DetectEffect(effect, presetClass, presetId, subtype);

            if (degradedNotice is not null)
            {
                note(degradedNotice);
            }

            // 同一组内：withEffect 与上一项同时开始，afterEffect 等上一项结束。
            var start = trigger switch
            {
                PptxAnimationTrigger.WithPrevious => previousStart + delay,
                _ => groupCursor + delay,
            };

            var end = start + duration;
            groupCursor = end;
            previousStart = start;
            timelineCursor = Math.Max(timelineCursor, end);
            previousTrigger = trigger;

            return new PptxAnimationStep
            {
                TargetId = targetId,
                TargetName = (string?)target?.Attribute("spid"),
                Effect = kind,
                Trigger = trigger,
                Direction = direction,
                DelayMs = delay,
                DurationMs = Math.Max(1d, duration),
                IsExit = isExit,
                RawEffectName = $"presetClass={presetClass}, presetID={presetId}, presetSubtype={subtype}",
                ClickGroupIndex = clickGroupIndex,
            };
        }

        /// <summary>识别动画效果类型与方向。</summary>
        private (PptxAnimationEffect Kind, PptxAnimationDirection Direction, string? Notice) DetectEffect(
            XElement effect,
            string presetClass,
            int presetId,
            int subtype)
        {
            var animationEffect = effect.Descendants(Ns.P + "animEffect").FirstOrDefault();
            var animations = effect.Descendants(Ns.P + "anim").ToList();
            var motionPaths = effect.Descendants(Ns.P + "animMotion").ToList();

            if (motionPaths.Count > 0 || string.Equals(presetClass, "path", StringComparison.OrdinalIgnoreCase))
            {
                return (PptxAnimationEffect.Unsupported, PptxAnimationDirection.None, MotionPathNotice);
            }

            if (string.Equals(presetClass, "emph", StringComparison.OrdinalIgnoreCase))
            {
                return (PptxAnimationEffect.Unsupported, PptxAnimationDirection.None, $"强调动画（presetID={presetId}）暂不支持");
            }

            var filter = (string?)animationEffect?.Attribute("filter") ?? string.Empty;

            // 注意顺序：飞入 / 缩放的效果节点里通常同时带一条 filter="fade" 的 animEffect，
            // 因此必须先按「位移 / 缩放」这类更具体的行为判定，再退回滤镜判定。
            var hasScale = animations.Any(a => AttrNames(a).Any(n => n is "ppt_w" or "ppt_h" or "ppt_c"));
            if (hasScale || presetId == 23)
            {
                return (PptxAnimationEffect.Zoom, PptxAnimationDirection.None, null);
            }

            var hasPosition = animations.Any(a => AttrNames(a).Any(n => n is "ppt_x" or "ppt_y"));
            if (hasPosition || presetId == 2)
            {
                var flyDirection = ParseFlyDirection(animations, subtype);
                if (flyDirection == PptxAnimationDirection.None)
                {
                    flyDirection = PptxAnimationDirection.FromLeft;
                }

                return (PptxAnimationEffect.Fly, flyDirection, null);
            }

            if (filter.StartsWith("wipe", StringComparison.OrdinalIgnoreCase) || presetId == 22)
            {
                return (PptxAnimationEffect.Wipe, ParseWipeDirection(filter), null);
            }

            if (filter.StartsWith("fade", StringComparison.OrdinalIgnoreCase)
                || filter.StartsWith("dissolve", StringComparison.OrdinalIgnoreCase)
                || presetId == 10)
            {
                return (PptxAnimationEffect.Fade, PptxAnimationDirection.None, null);
            }

            if (string.Equals(presetClass, "mediacall", StringComparison.OrdinalIgnoreCase))
            {
                return (PptxAnimationEffect.Unsupported, PptxAnimationDirection.None, "媒体播放指令动画暂不支持");
            }

            // 只有 p:set（把可见性 / 不透明度直接置位）的效果等价于「出现」。
            if (presetId is 0 or 1 || (animations.Count == 0 && effect.Descendants(Ns.P + "set").Any()))
            {
                return (PptxAnimationEffect.Appear, PptxAnimationDirection.None, null);
            }

            return (PptxAnimationEffect.Fade, PptxAnimationDirection.None,
                $"动画效果（presetID={presetId}）暂不支持，已降级为淡入淡出");
        }

        /// <summary>取行为节点上的属性名列表。</summary>
        private static IEnumerable<string> AttrNames(XElement behavior)
            => behavior.Descendants(Ns.P + "attrName").Select(e => e.Value.Trim());

        /// <summary>从 <c>p:animEffect/@filter</c> 解析擦除方向。</summary>
        private static PptxAnimationDirection ParseWipeDirection(string filter)
        {
            var open = filter.IndexOf('(');
            var close = filter.IndexOf(')');
            if (open < 0 || close <= open)
            {
                return PptxAnimationDirection.FromLeft;
            }

            var argument = filter[(open + 1)..close].Trim().ToLowerInvariant();
            return argument switch
            {
                "up" => PptxAnimationDirection.FromBottom,
                "down" => PptxAnimationDirection.FromTop,
                "left" => PptxAnimationDirection.FromRight,
                "right" => PptxAnimationDirection.FromLeft,
                _ => PptxAnimationDirection.FromLeft,
            };
        }

        /// <summary>
        /// 从 <c>p:anim</c> 的位置公式里解析飞入方向（最可靠：公式里带 ± 号）。
        /// 公式形如 <c>#ppt_x-(1+3*sin(4*atan(1)))</c> 表示从左飞入，<c>#ppt_x+(...)</c> 表示从右飞入。
        /// </summary>
        private static PptxAnimationDirection ParseFlyDirection(IReadOnlyList<XElement> animations, int subtype)
        {
            var horizontal = 0; // -1 = 左，1 = 右
            var vertical = 0;   // -1 = 上，1 = 下

            foreach (var animation in animations)
            {
                var names = AttrNames(animation).ToList();
                var value = animation.Descendants(Ns.P + "strVal").FirstOrDefault()?.Attribute("val")?.Value
                            ?? animation.Descendants(Ns.P + "val").FirstOrDefault()?.Value
                            ?? string.Empty;

                if (names.Contains("ppt_x"))
                {
                    var index = value.IndexOf("ppt_x", StringComparison.OrdinalIgnoreCase);
                    if (index >= 0 && index + 5 < value.Length)
                    {
                        var sign = value[index + 5];
                        if (sign == '-')
                        {
                            horizontal = -1;
                        }
                        else if (sign == '+')
                        {
                            horizontal = 1;
                        }
                    }
                }
                else if (names.Contains("ppt_y"))
                {
                    var index = value.IndexOf("ppt_y", StringComparison.OrdinalIgnoreCase);
                    if (index >= 0 && index + 5 < value.Length)
                    {
                        var sign = value[index + 5];
                        if (sign == '-')
                        {
                            vertical = -1;
                        }
                        else if (sign == '+')
                        {
                            vertical = 1;
                        }
                    }
                }
            }

            if (horizontal != 0 || vertical != 0)
            {
                return (horizontal, vertical) switch
                {
                    (-1, 0) => PptxAnimationDirection.FromLeft,
                    (1, 0) => PptxAnimationDirection.FromRight,
                    (0, -1) => PptxAnimationDirection.FromTop,
                    (0, 1) => PptxAnimationDirection.FromBottom,
                    (-1, -1) => PptxAnimationDirection.FromTopLeft,
                    (1, -1) => PptxAnimationDirection.FromTopRight,
                    (-1, 1) => PptxAnimationDirection.FromBottomLeft,
                    _ => PptxAnimationDirection.FromBottomRight,
                };
            }

            // 公式不可用时按 presetSubtype 近似（1 下 / 2 左 / 4 右 / 8 上，两两组合为对角）。
            return subtype switch
            {
                1 => PptxAnimationDirection.FromBottom,
                2 => PptxAnimationDirection.FromLeft,
                3 => PptxAnimationDirection.FromBottomLeft,
                4 => PptxAnimationDirection.FromRight,
                5 => PptxAnimationDirection.FromBottomRight,
                8 => PptxAnimationDirection.FromTop,
                10 => PptxAnimationDirection.FromTopLeft,
                12 => PptxAnimationDirection.FromTopRight,
                _ => PptxAnimationDirection.None,
            };
        }

        /// <summary>
        /// 取效果的时长：优先效果 <c>p:cTn</c> 自己的 <c>dur</c>；
        /// 没写时取内部行为（<c>p:animEffect</c> / <c>p:anim</c> / <c>p:set</c> 的 <c>p:cTn</c>）里最大的一个；
        /// 都没有才退回 500ms。
        /// </summary>
        private static double ResolveEffectDuration(XElement effect)
        {
            var own = ParseTimeValue((string?)effect.Attribute("dur"));
            if (own is > 0)
            {
                return own.Value;
            }

            var nested = effect
                .Descendants(Ns.P + "cTn")
                .Select(element => ParseTimeValue((string?)element.Attribute("dur")) ?? 0d)
                .DefaultIfEmpty(0d)
                .Max();

            return nested > 0 ? nested : 500d;
        }

        /// <summary>
        /// 读取效果的起始延迟：<b>沿祖先链累加</b>所有 <c>p:cTn/p:stCondLst/p:cond/@delay</c>。
        ///
        /// PowerPoint 常把「延迟 0.2 秒」写在包裹效果的 <c>p:par/p:cTn</c> 上而不是效果自身，
        /// 只读效果自身的 delay 会得到 0，导致动画提前播放、总时长偏短。
        /// <c>delay="indefinite"</c>（等待单击）按 0 计，遇到主序列 / 时间根节点即停止累加。
        /// </summary>
        private static double ParseConditionDelay(XElement effect)
        {
            var total = 0d;
            XElement? current = effect;

            while (current is not null)
            {
                if (current.Name == Ns.P + "seq")
                {
                    break;
                }

                if (current.Name == Ns.P + "cTn")
                {
                    var nodeType = (string?)current.Attribute("nodeType");
                    if (string.Equals(nodeType, "mainSeq", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(nodeType, "tmRoot", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    var condition = current.Element(Ns.P + "stCondLst")?.Elements(Ns.P + "cond").FirstOrDefault();
                    total += ParseTimeValue((string?)condition?.Attribute("delay")) ?? 0d;
                }

                current = current.Parent;
            }

            return total;
        }

        /// <summary>解析时间值（毫秒数字、<c>1.5s</c>、<c>500ms</c>、<c>indefinite</c>）。</summary>
        private static double? ParseTimeValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Trim();
            if (value.Equals("indefinite", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return milliseconds;
            }

            if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds * 1000d;
            }

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) ? raw : null;
        }

        // ───────────────────────── 备注 ─────────────────────────

        /// <summary>读取演讲者备注（若存在）。</summary>
        private string? ReadNotesText(string slidePart)
        {
            try
            {
                var notesPart = _package.FindRelationshipTarget(slidePart, "notesSlide");
                if (notesPart is null)
                {
                    return null;
                }

                var notes = _package.LoadXml(notesPart);
                var spTree = notes?.Root?.Element(Ns.P + "cSld")?.Element(Ns.P + "spTree");
                if (spTree is null)
                {
                    return null;
                }

                var builder = new StringBuilder();
                foreach (var sp in spTree.Elements(Ns.P + "sp"))
                {
                    var placeholderType = (string?)sp.Element(Ns.P + "nvSpPr")?
                        .Element(Ns.P + "nvPr")?
                        .Element(Ns.P + "ph")?
                        .Attribute("type");
                    if (placeholderType is "sldNum" or "sldImg")
                    {
                        continue;
                    }

                    var text = sp.Element(Ns.P + "txBody")?
                        .Elements(Ns.A + "p")
                        .Select(p => string.Concat(p.Elements(Ns.A + "r").Select(r => r.Element(Ns.A + "t")?.Value)))
                        .Where(t => !string.IsNullOrWhiteSpace(t));

                    foreach (var line in text ?? Enumerable.Empty<string>())
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(line);
                    }
                }

                return builder.Length == 0 ? null : builder.ToString();
            }
            catch
            {
                return null;
            }
        }

        // ───────────────────────── 小工具 ─────────────────────────

        /// <summary>读取数值属性。</summary>
        internal static double DoubleAttr(XElement? element, string name, double fallback)
        {
            var attribute = element?.Attribute(name);
            if (attribute is null)
            {
                return fallback;
            }

            return double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        /// <summary>读取整数属性。</summary>
        internal static int IntAttr(XElement? element, string name, int fallback)
        {
            var value = DoubleAttr(element, name, fallback);
            return (int)Math.Round(value);
        }

        /// <summary>读取 <c>"1"</c> / <c>"true"</c> 布尔属性。</summary>
        internal static bool IsTrue(XAttribute? attribute)
        {
            if (attribute is null)
            {
                return false;
            }

            return attribute.Value is "1" or "true" or "on" || attribute.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>解析 <c>RRGGBB</c> 颜色。</summary>
        private static Color? ParseHex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 6)
            {
                return null;
            }

            try
            {
                var hex = value.TrimStart('#');
                if (hex.Length == 8)
                {
                    // AARRGGBB
                    var a = Convert.ToByte(hex[..2], 16);
                    var r = Convert.ToByte(hex.Substring(2, 2), 16);
                    var g = Convert.ToByte(hex.Substring(4, 2), 16);
                    var b = Convert.ToByte(hex.Substring(6, 2), 16);
                    return Color.FromArgb(a, r, g, b);
                }

                if (hex.Length == 6)
                {
                    var r = Convert.ToByte(hex[..2], 16);
                    var g = Convert.ToByte(hex.Substring(2, 2), 16);
                    var b = Convert.ToByte(hex.Substring(4, 2), 16);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        /// <summary>系统颜色名 → 主题色名近似映射。</summary>
        private static string? MapSystemColor(string? name) => name switch
        {
            "windowText" => "tx1",
            "window" => "bg1",
            "highlight" => "accent1",
            "btnFace" => "bg2",
            _ => null,
        };

        /// <summary>HSL → RGB。</summary>
        private static Color ColorFromHsl(double hueDegrees, double saturation, double luminance)
        {
            var hue = ((hueDegrees % 360d) + 360d) % 360d / 360d;
            double r = luminance, g = luminance, b = luminance;
            if (saturation > 0)
            {
                var q = luminance < 0.5 ? luminance * (1 + saturation) : luminance + saturation - luminance * saturation;
                var p = 2 * luminance - q;
                r = HueToRgb(p, q, hue + 1d / 3d);
                g = HueToRgb(p, q, hue);
                b = HueToRgb(p, q, hue - 1d / 3d);
            }

            return Color.FromRgb(
                (byte)Math.Round(Math.Clamp(r, 0d, 1d) * 255),
                (byte)Math.Round(Math.Clamp(g, 0d, 1d) * 255),
                (byte)Math.Round(Math.Clamp(b, 0d, 1d) * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0)
            {
                t += 1;
            }

            if (t > 1)
            {
                t -= 1;
            }

            if (t < 1d / 6d)
            {
                return p + (q - p) * 6 * t;
            }

            if (t < 1d / 2d)
            {
                return q;
            }

            if (t < 2d / 3d)
            {
                return p + (q - p) * (2d / 3d - t) * 6;
            }

            return p;
        }

        /// <summary>RGB → HSL。</summary>
        private static (double Hue, double Saturation, double Luminance) RgbToHsl(Color color)
        {
            var r = color.R / 255d;
            var g = color.G / 255d;
            var b = color.B / 255d;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var luminance = (max + min) / 2d;
            var delta = max - min;

            if (delta < 1e-9)
            {
                return (0d, 0d, luminance);
            }

            var saturation = luminance > 0.5 ? delta / (2d - max - min) : delta / (max + min);
            double hue;
            if (Math.Abs(max - r) < 1e-9)
            {
                hue = (g - b) / delta + (g < b ? 6d : 0d);
            }
            else if (Math.Abs(max - g) < 1e-9)
            {
                hue = (b - r) / delta + 2d;
            }
            else
            {
                hue = (r - g) / delta + 4d;
            }

            return (hue * 60d, saturation, luminance);
        }

        /// <summary>明度变换（lumMod / lumOff 近似）。</summary>
        private static Color AdjustLuminance(Color color, double multiply, double offset)
        {
            var (hue, saturation, luminance) = RgbToHsl(color);
            var adjusted = Math.Clamp(luminance * multiply + offset, 0d, 1d);
            var result = ColorFromHsl(hue, saturation, adjusted);
            return Color.FromArgb(color.A, result.R, result.G, result.B);
        }

        /// <summary>饱和度变换（satMod 近似）。</summary>
        private static Color AdjustSaturation(Color color, double multiply)
        {
            var (hue, saturation, luminance) = RgbToHsl(color);
            var result = ColorFromHsl(hue, Math.Clamp(saturation * multiply, 0d, 1d), luminance);
            return Color.FromArgb(color.A, result.R, result.G, result.B);
        }

        /// <summary>tint 近似：向白色混合。</summary>
        private static Color ApplyTint(Color color, double amount)
        {
            var (hue, saturation, luminance) = RgbToHsl(color);
            var adjusted = Math.Clamp(luminance + (1d - luminance) * amount, 0d, 1d);
            var result = ColorFromHsl(hue, saturation, adjusted);
            return Color.FromArgb(color.A, result.R, result.G, result.B);
        }

        /// <summary>shade 近似：向黑色混合。</summary>
        private static Color ApplyShade(Color color, double amount)
        {
            var (hue, saturation, luminance) = RgbToHsl(color);
            var adjusted = Math.Clamp(luminance * amount, 0d, 1d);
            var result = ColorFromHsl(hue, saturation, adjusted);
            return Color.FromArgb(color.A, result.R, result.G, result.B);
        }

        /// <summary>透明度（<c>a:alphaModFix</c> / <c>a:xfrm</c> 上的透明度修饰）。</summary>
        private static double ReadAlphaMod(XElement? xfrm) => 1d;

        /// <summary>格式化路径坐标。</summary>
        private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>从部件名里取出结尾的序号（用于排序）。</summary>
        private static int ExtractTrailingNumber(string partName)
        {
            var name = Path.GetFileNameWithoutExtension(partName);
            var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
            return int.TryParse(digits, out var value) ? value : int.MaxValue;
        }

        /// <summary>预设颜色表（<c>a:prstClr</c>）。</summary>
        private static readonly Dictionary<string, Color> PresetColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = Colors.Black,
            ["white"] = Colors.White,
            ["red"] = Color.FromRgb(0xFF, 0x00, 0x00),
            ["green"] = Color.FromRgb(0x00, 0x80, 0x00),
            ["blue"] = Color.FromRgb(0x00, 0x00, 0xFF),
            ["yellow"] = Color.FromRgb(0xFF, 0xFF, 0x00),
            ["gray"] = Color.FromRgb(0x80, 0x80, 0x80),
            ["grey"] = Color.FromRgb(0x80, 0x80, 0x80),
            ["darkGray"] = Color.FromRgb(0xA9, 0xA9, 0xA9),
            ["lightGray"] = Color.FromRgb(0xD3, 0xD3, 0xD3),
            ["orange"] = Color.FromRgb(0xFF, 0xA5, 0x00),
            ["purple"] = Color.FromRgb(0x80, 0x00, 0x80),
            ["cyan"] = Color.FromRgb(0x00, 0xFF, 0xFF),
            ["magenta"] = Color.FromRgb(0xFF, 0x00, 0xFF),
            ["brown"] = Color.FromRgb(0xA5, 0x2A, 0x2A),
            ["pink"] = Color.FromRgb(0xFF, 0xC0, 0xCB),
            ["lime"] = Color.FromRgb(0x00, 0xFF, 0x00),
            ["navy"] = Color.FromRgb(0x00, 0x00, 0x80),
            ["teal"] = Color.FromRgb(0x00, 0x80, 0x80),
            ["olive"] = Color.FromRgb(0x80, 0x80, 0x00),
            ["silver"] = Color.FromRgb(0xC0, 0xC0, 0xC0),
            ["gold"] = Color.FromRgb(0xFF, 0xD7, 0x00),
        };
    }

    // ───────────────────────── 内部数据结构 ─────────────────────────

    /// <summary>DrawingML / PresentationML 命名空间。</summary>
    internal static class Ns
    {
        /// <summary>DrawingML 主命名空间。</summary>
        public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

        /// <summary>PresentationML 主命名空间。</summary>
        public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

        /// <summary>关系命名空间。</summary>
        public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>图示（SmartArt）命名空间。</summary>
        public static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

        /// <summary>核心属性命名空间。</summary>
        public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    }

    /// <summary>页面解析上下文。</summary>
    private sealed class SlideContext
    {
        private readonly ParseSession _session;

        public SlideContext(
            ParseSession session,
            string slidePart,
            SlideInheritance inheritance,
            Action<string> note,
            List<PptxMediaFile> media,
            int slideIndex)
        {
            _session = session;
            SlidePart = slidePart;
            Inheritance = inheritance;
            Note = note;
            Media = media;
            SlideIndex = slideIndex;
        }

        /// <summary>页面部件名。</summary>
        public string SlidePart { get; }

        /// <summary>页码（1 起）。</summary>
        public int SlideIndex { get; }

        /// <summary>版式 / 母版继承信息。</summary>
        public SlideInheritance Inheritance { get; }

        /// <summary>记录一条降级提示。</summary>
        public Action<string> Note { get; }

        /// <summary>本页媒体列表。</summary>
        public List<PptxMediaFile> Media { get; }

        /// <summary>当前文本框的自动缩放比例（解析文本时使用）。</summary>
        public double CurrentFontScale { get; set; } = 1d;

        /// <summary>生成合成形状 id。</summary>
        public int NextSyntheticId() => _session.NextId();
    }

    /// <summary>版式 / 母版继承信息。</summary>
    private sealed class SlideInheritance
    {
        /// <summary>版式部件名。</summary>
        public string? LayoutPart { get; set; }

        /// <summary>母版部件名。</summary>
        public string? MasterPart { get; set; }

        /// <summary>版式形状树。</summary>
        public XElement? LayoutSpTree { get; set; }

        /// <summary>母版形状树。</summary>
        public XElement? MasterSpTree { get; set; }

        /// <summary>版式背景。</summary>
        public XElement? LayoutBackground { get; set; }

        /// <summary>母版背景。</summary>
        public XElement? MasterBackground { get; set; }

        /// <summary>母版文字样式（<c>p:txStyles</c>）。</summary>
        public XElement? MasterTxStyles { get; set; }

        /// <summary>母版颜色映射（<c>p:clrMap</c>）。</summary>
        public XElement? MasterColorMap { get; set; }

        /// <summary>版式占位符（类型+索引 → <c>p:sp</c>）。</summary>
        public Dictionary<string, XElement> LayoutPlaceholders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>母版占位符。</summary>
        public Dictionary<string, XElement> MasterPlaceholders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>主题。</summary>
        public ThemeData Theme { get; set; } = ThemeData.CreateDefault();

        /// <summary>在版式 / 母版里查找占位符的位置。</summary>
        public XElement? FindPlaceholderXfrm(string? type, int index)
        {
            var layout = Find(LayoutPlaceholders, type, index);
            var layoutXfrm = layout?.Element(Ns.P + "spPr")?.Element(Ns.A + "xfrm");
            if (layoutXfrm is not null)
            {
                return layoutXfrm;
            }

            var master = Find(MasterPlaceholders, type, index);
            return master?.Element(Ns.P + "spPr")?.Element(Ns.A + "xfrm");
        }

        /// <summary>在版式 / 母版里查找占位符元素。</summary>
        public XElement? FindPlaceholder(string? type, int index)
            => Find(LayoutPlaceholders, type, index) ?? Find(MasterPlaceholders, type, index);

        private static XElement? Find(Dictionary<string, XElement> map, string? type, int index)
        {
            if (map.Count == 0)
            {
                return null;
            }

            if (type is not null && map.TryGetValue($"{type}#{index}", out var exact))
            {
                return exact;
            }

            if (type is not null)
            {
                var byType = map.FirstOrDefault(kv => kv.Key.StartsWith($"{type}#", StringComparison.OrdinalIgnoreCase));
                if (byType.Value is not null)
                {
                    return byType.Value;
                }
            }

            if (index > 0)
            {
                var suffix = $"#{index}";
                var byIndex = map.FirstOrDefault(kv => kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (byIndex.Value is not null)
                {
                    return byIndex.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// 组装某个大纲级别的样式链（优先级从低到高）：
        /// 演示文稿默认样式 → 母版文字样式 → 母版占位符列表样式 → 版式占位符列表样式 → 页面占位符列表样式。
        /// </summary>
        public List<XElement> BuildLevelStyles(
            XElement? defaultTextStyle,
            XElement? txBody,
            string? placeholderType,
            int placeholderIndex,
            int level)
        {
            var levelName = $"lvl{level + 1}pPr";
            var styles = new List<XElement>();

            var defaultLevel = defaultTextStyle?.Element(Ns.A + levelName);
            if (defaultLevel is not null)
            {
                styles.Add(defaultLevel);
            }

            var bucket = SelectStyleBucket(placeholderType, placeholderIndex);
            var masterLevel = MasterTxStyles?.Element(Ns.P + bucket)?.Element(Ns.A + levelName);
            if (masterLevel is not null)
            {
                styles.Add(masterLevel);
            }

            var masterPlaceholderLevel = FindInPlaceholder(MasterPlaceholders, placeholderType, placeholderIndex, levelName);
            if (masterPlaceholderLevel is not null)
            {
                styles.Add(masterPlaceholderLevel);
            }

            var layoutPlaceholderLevel = FindInPlaceholder(LayoutPlaceholders, placeholderType, placeholderIndex, levelName);
            if (layoutPlaceholderLevel is not null)
            {
                styles.Add(layoutPlaceholderLevel);
            }

            var ownLevel = txBody?.Element(Ns.A + "lstStyle")?.Element(Ns.A + levelName);
            if (ownLevel is not null)
            {
                styles.Add(ownLevel);
            }

            return styles;
        }

        private static XElement? FindInPlaceholder(
            Dictionary<string, XElement> map,
            string? type,
            int index,
            string levelName)
        {
            var placeholder = Find(map, type, index);
            return placeholder?
                .Element(Ns.P + "txBody")?
                .Element(Ns.A + "lstStyle")?
                .Element(Ns.A + levelName);
        }

        /// <summary>占位符 → 母版文字样式分档。</summary>
        private static string SelectStyleBucket(string? placeholderType, int index)
        {
            if (placeholderType is "title" or "ctrTitle")
            {
                return "titleStyle";
            }

            if (placeholderType is "body" or "subTitle" or "obj" or null && index == 0)
            {
                return "bodyStyle";
            }

            return placeholderType is "body" or "subTitle" or "obj" ? "bodyStyle" : "otherStyle";
        }
    }

    /// <summary>主题数据（颜色方案 + 字体方案 + 颜色映射）。</summary>
    private sealed class ThemeData
    {
        private readonly Dictionary<string, Color> _schemeColors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _colorMap = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>主标题字体。</summary>
        public string MajorFont { get; private set; } = "Calibri Light";

        /// <summary>正文字体。</summary>
        public string MinorFont { get; private set; } = "Calibri";

        /// <summary>缺省主题。</summary>
        public static ThemeData CreateDefault()
        {
            var theme = new ThemeData();
            theme._schemeColors["dk1"] = Color.FromRgb(0x00, 0x00, 0x00);
            theme._schemeColors["lt1"] = Color.FromRgb(0xFF, 0xFF, 0xFF);
            theme._schemeColors["dk2"] = Color.FromRgb(0x44, 0x54, 0x6A);
            theme._schemeColors["lt2"] = Color.FromRgb(0xEE, 0xEC, 0xE6);
            theme._schemeColors["accent1"] = Color.FromRgb(0x44, 0x72, 0xC4);
            theme._schemeColors["accent2"] = Color.FromRgb(0xED, 0x7D, 0x31);
            theme._schemeColors["accent3"] = Color.FromRgb(0xA5, 0xA5, 0xA5);
            theme._schemeColors["accent4"] = Color.FromRgb(0xFF, 0xC0, 0x00);
            theme._schemeColors["accent5"] = Color.FromRgb(0x5B, 0x9B, 0xD5);
            theme._schemeColors["accent6"] = Color.FromRgb(0x70, 0xAD, 0x47);
            theme._schemeColors["hlink"] = Color.FromRgb(0x05, 0x63, 0xC1);
            theme._schemeColors["folHlink"] = Color.FromRgb(0x95, 0x4F, 0x72);
            theme.ResetColorMap();
            return theme;
        }

        /// <summary>从 <c>a:theme</c> 文档加载。</summary>
        public static ThemeData Load(XDocument? themeDocument)
        {
            var theme = CreateDefault();
            var root = themeDocument?.Root;
            if (root is null)
            {
                return theme;
            }

            var scheme = root.Element(Ns.A + "themeElements")?.Element(Ns.A + "clrScheme");
            if (scheme is not null)
            {
                foreach (var entry in scheme.Elements())
                {
                    var name = entry.Name.LocalName;
                    var colorElement = entry.Elements().FirstOrDefault();
                    var value = (string?)colorElement?.Attribute("val")
                                ?? (string?)colorElement?.Attribute("lastClr");
                    if (value is null)
                    {
                        continue;
                    }

                    theme._schemeColors[name] = ParseThemeColor(value, theme._schemeColors.TryGetValue(name, out var existing) ? existing : Colors.Black);
                }
            }

            var fontScheme = root.Element(Ns.A + "themeElements")?.Element(Ns.A + "fontScheme");
            theme.MajorFont = (string?)fontScheme?.Element(Ns.A + "majorFont")?.Element(Ns.A + "latin")?.Attribute("typeface") ?? theme.MajorFont;
            theme.MinorFont = (string?)fontScheme?.Element(Ns.A + "minorFont")?.Element(Ns.A + "latin")?.Attribute("typeface") ?? theme.MinorFont;
            theme.ResetColorMap();
            return theme;
        }

        /// <summary>应用母版颜色映射（<c>p:clrMap</c>）。</summary>
        public void ApplyColorMap(XElement? colorMap)
        {
            ResetColorMap();
            if (colorMap is null)
            {
                return;
            }

            foreach (var attribute in colorMap.Attributes())
            {
                _colorMap[attribute.Name.LocalName] = attribute.Value;
            }
        }

        /// <summary>解析主题色名（含 tx1 / bg1 等映射名）。</summary>
        public Color ResolveSchemeColor(string? name, Color fallback)
        {
            if (string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            if (name is "phClr")
            {
                return fallback;
            }

            if (_colorMap.TryGetValue(name, out var mapped) && _schemeColors.TryGetValue(mapped, out var mappedColor))
            {
                return mappedColor;
            }

            return _schemeColors.TryGetValue(name, out var color) ? color : fallback;
        }

        private void ResetColorMap()
        {
            _colorMap.Clear();
            _colorMap["tx1"] = "dk1";
            _colorMap["bg1"] = "lt1";
            _colorMap["tx2"] = "dk2";
            _colorMap["bg2"] = "lt2";
            _colorMap["accent1"] = "accent1";
            _colorMap["accent2"] = "accent2";
            _colorMap["accent3"] = "accent3";
            _colorMap["accent4"] = "accent4";
            _colorMap["accent5"] = "accent5";
            _colorMap["accent6"] = "accent6";
            _colorMap["hlink"] = "hlink";
            _colorMap["folHlink"] = "folHlink";
        }

        private static Color ParseThemeColor(string value, Color fallback)
        {
            try
            {
                var hex = value.TrimStart('#');
                if (hex.Length is 6 or 8)
                {
                    var offset = hex.Length - 6;
                    var r = Convert.ToByte(hex.Substring(offset, 2), 16);
                    var g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
                    var b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch
            {
                return fallback;
            }

            return fallback;
        }
    }

    /// <summary>run 格式累积器（按优先级依次套用）。</summary>
    private sealed class RunStyleAccumulator
    {
        /// <summary>字号（像素）。</summary>
        public double? FontSizePx { get; private set; }

        /// <summary>是否加粗。</summary>
        public bool? Bold { get; private set; }

        /// <summary>是否倾斜。</summary>
        public bool? Italic { get; private set; }

        /// <summary>是否下划线。</summary>
        public bool? Underline { get; private set; }

        /// <summary>是否删除线。</summary>
        public bool? Strikethrough { get; private set; }

        /// <summary>前景色。</summary>
        public Color? Color { get; private set; }

        /// <summary>字体族名（含 <c>+mj-lt</c> / <c>+mn-lt</c> 记号）。</summary>
        public string? FontFamily { get; private set; }

        /// <summary>字符间距（像素）。</summary>
        public double? LetterSpacingPx { get; private set; }

        /// <summary>套用一层 <c>a:rPr</c> / <c>a:defRPr</c> / <c>a:endParaRPr</c>。</summary>
        public void Apply(XElement? properties, ThemeData theme)
        {
            if (properties is null)
            {
                return;
            }

            if (properties.Attribute("sz") is { } size)
            {
                FontSizePx = PptxUnits.HundredthPointsToPixels(ParseSession.DoubleAttr(properties, "sz", 1800d));
            }

            var bold = (string?)properties.Attribute("b");
            if (bold is not null)
            {
                Bold = bold is "1" or "true" or "on";
            }

            var italic = (string?)properties.Attribute("i");
            if (italic is not null)
            {
                Italic = italic is "1" or "true";
            }

            var underline = (string?)properties.Attribute("u");
            if (underline is not null)
            {
                Underline = underline is not ("none" or "0");
            }

            var strike = (string?)properties.Attribute("strike");
            if (strike is not null)
            {
                Strikethrough = strike is not "noStrike";
            }

            if (properties.Attribute("spc") is { } spacing)
            {
                LetterSpacingPx = PptxUnits.HundredthPointsToPixels(ParseSession.DoubleAttr(properties, "spc", 0d));
            }

            if (properties.Element(Ns.A + "solidFill") is not null)
            {
                var color = ParseSession.ResolveColor(properties.Element(Ns.A + "solidFill"), theme, null);
                if (color is not null)
                {
                    Color = color;
                }
            }

            var typeface = (string?)properties.Element(Ns.A + "latin")?.Attribute("typeface")
                           ?? (string?)properties.Element(Ns.A + "ea")?.Attribute("typeface")
                           ?? (string?)properties.Element(Ns.A + "cs")?.Attribute("typeface");
            if (!string.IsNullOrWhiteSpace(typeface))
            {
                FontFamily = typeface;
            }
        }
    }


    /// <summary>
    /// OPC（Open Packaging Conventions）包读取器：把 zip 包抽象成「部件名 → 内容」的只读视图。
    /// 所有读取都做了缓存与容错，读不到就返回 <c>null</c>，由上层决定降级策略。
    /// </summary>
    private sealed class OpcPackage
    {
        /// <summary>演示文稿主部件。</summary>
        public const string PresentationPart = "/ppt/presentation.xml";

        /// <summary>内容类型命名空间。</summary>
        private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

        /// <summary>关系命名空间。</summary>
        private static readonly XNamespace RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        private readonly ZipArchive _archive;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, XDocument?> _xmlCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]?> _bytesCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, OpcRelationship>> _relationshipCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _partContentTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _defaultContentTypes = new(StringComparer.OrdinalIgnoreCase);

        public OpcPackage(ZipArchive archive)
        {
            _archive = archive;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // 目录项
                }

                _entries[Normalize(entry.FullName)] = entry;
            }

            ReadContentTypes();
        }

        /// <summary>包内部件名（以 <c>/</c> 开头）。</summary>
        public IEnumerable<string> PartNames => _entries.Keys;

        /// <summary>部件是否存在。</summary>
        public bool Exists(string partName) => _entries.ContainsKey(Normalize(partName));

        /// <summary>读取 XML 部件；失败返回 <c>null</c>。</summary>
        public XDocument? LoadXml(string partName)
        {
            var key = Normalize(partName);
            if (_xmlCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            XDocument? document = null;
            try
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    using var stream = entry.Open();
                    document = XDocument.Load(stream, LoadOptions.None);
                }
            }
            catch
            {
                document = null;
            }

            _xmlCache[key] = document;
            return document;
        }

        /// <summary>读取二进制部件；失败返回 <c>null</c>。</summary>
        public byte[]? LoadBytes(string partName)
        {
            var key = Normalize(partName);
            if (_bytesCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            byte[]? bytes = null;
            try
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }
            }
            catch
            {
                bytes = null;
            }

            _bytesCache[key] = bytes;
            return bytes;
        }

        /// <summary>部件内容类型（MIME）。</summary>
        public string GetContentType(string partName)
        {
            var key = Normalize(partName);
            if (_partContentTypes.TryGetValue(key, out var contentType))
            {
                return contentType;
            }

            var extension = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
            return _defaultContentTypes.TryGetValue(extension, out var defaultType) ? defaultType : string.Empty;
        }

        /// <summary>取某部件的关系集合（按 r:id 索引）。</summary>
        public IReadOnlyDictionary<string, OpcRelationship> GetRelationships(string sourcePart)
        {
            var source = Normalize(sourcePart);
            if (_relationshipCache.TryGetValue(source, out var cached))
            {
                return cached;
            }

            var relationships = new Dictionary<string, OpcRelationship>(StringComparer.Ordinal);
            try
            {
                var relsPart = RelationshipPartName(source);
                if (_entries.ContainsKey(relsPart))
                {
                    var document = LoadXml(relsPart);
                    foreach (var element in document?.Root?.Elements(RelationshipsNs + "Relationship") ?? Enumerable.Empty<XElement>())
                    {
                        var id = (string?)element.Attribute("Id");
                        var type = (string?)element.Attribute("Type");
                        var target = (string?)element.Attribute("Target");
                        if (id is null || type is null || target is null)
                        {
                            continue;
                        }

                        relationships[id] = new OpcRelationship(
                            id,
                            type,
                            target,
                            (string?)element.Attribute("TargetMode") ?? "Internal");
                    }
                }
            }
            catch
            {
                // 关系文件损坏时按「没有关系」处理，上层会走占位降级。
            }

            _relationshipCache[source] = relationships;
            return relationships;
        }

        /// <summary>把关系目标解析为包内部件名（外部链接返回 <c>null</c>）。</summary>
        public string? ResolveTarget(string sourcePart, string relationshipId)
        {
            if (!GetRelationships(sourcePart).TryGetValue(relationshipId, out var relationship))
            {
                return null;
            }

            if (relationship.IsExternal)
            {
                return null;
            }

            var target = relationship.Target;

            // 去掉查询串 / 片段，避免把 # 之后的内容当成文件名。
            var hashIndex = target.IndexOf('#');
            if (hashIndex >= 0)
            {
                target = target[..hashIndex];
            }

            if (target.StartsWith('/'))
            {
                return Normalize(target);
            }

            var source = Normalize(sourcePart);
            var directory = source[..(source.LastIndexOf('/') + 1)];
            return Normalize(directory + target);
        }

        /// <summary>按关系类型关键字查找目标部件（例如 <c>slideLayout</c> / <c>theme</c>）。</summary>
        public string? FindRelationshipTarget(string sourcePart, string typeKeyword)
        {
            foreach (var relationship in GetRelationships(sourcePart).Values)
            {
                if (relationship.Type.EndsWith("/" + typeKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveTarget(sourcePart, relationship.Id);
                }
            }

            return null;
        }

        /// <summary>读取 <c>[Content_Types].xml</c>。</summary>
        private void ReadContentTypes()
        {
            try
            {
                var document = LoadXml("/[Content_Types].xml");
                var root = document?.Root;
                if (root is null)
                {
                    return;
                }

                foreach (var element in root.Elements(ContentTypesNs + "Default"))
                {
                    var extension = ((string?)element.Attribute("Extension") ?? string.Empty).ToLowerInvariant();
                    var contentType = (string?)element.Attribute("ContentType");
                    if (extension.Length > 0 && contentType is not null)
                    {
                        _defaultContentTypes[extension] = contentType;
                    }
                }

                foreach (var element in root.Elements(ContentTypesNs + "Override"))
                {
                    var partName = (string?)element.Attribute("PartName");
                    var contentType = (string?)element.Attribute("ContentType");
                    if (partName is not null && contentType is not null)
                    {
                        _partContentTypes[Normalize(partName)] = contentType;
                    }
                }
            }
            catch
            {
                // 内容类型缺失不影响主流程：拿不到 MIME 时按扩展名兜底。
            }
        }

        /// <summary>某部件对应的关系部件名。</summary>
        private static string RelationshipPartName(string sourcePart)
        {
            var source = Normalize(sourcePart);
            if (source == "/")
            {
                return "/_rels/.rels";
            }

            var lastSlash = source.LastIndexOf('/');
            var directory = source[..lastSlash];
            var fileName = source[(lastSlash + 1)..];
            return $"{directory}/_rels/{fileName}.rels";
        }

        /// <summary>
        /// 统一成「以 / 开头、使用 / 分隔、已折叠 . 与 ..」的部件名。
        /// 关系目标常写成 <c>../media/image1.png</c>，必须折叠成包内绝对部件名才能命中 zip 条目。
        /// </summary>
        private static string Normalize(string partName)
        {
            var segments = partName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var stack = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    continue;
                }

                stack.Add(segment);
            }

            return "/" + string.Join('/', stack);
        }
    }

    /// <summary>一条 OPC 关系。</summary>
    private sealed record OpcRelationship(string Id, string Type, string Target, string TargetMode)
    {
        /// <summary>是否指向包外资源。</summary>
        public bool IsExternal => TargetMode.Equals("External", StringComparison.OrdinalIgnoreCase);
    }
}
