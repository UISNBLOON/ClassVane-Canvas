using Avalonia;
using Avalonia.Media;

namespace ClassVaneCanvas.Pptx.Model;

/// <summary>
/// PPTX 长度单位换算。
///
/// OOXML 里的几何长度一律以 EMU（English Metric Unit）存储：
/// 1 英寸 = 914400 EMU，1 英寸 = 96 逻辑像素（本内核统一使用 96 DPI 页面坐标系），
/// 因此 <b>EMU → 逻辑像素 = EMU / 9525</b>。
/// 字号在 OOXML 中以 1/100 磅存储（<c>sz="1800"</c> 表示 18 磅），
/// 磅 → 像素 = 磅 × 96 / 72。
/// </summary>
public static class PptxUnits
{
    /// <summary>1 英寸对应的 EMU 数。</summary>
    public const double EmuPerInch = 914_400d;

    /// <summary>页面坐标系 DPI（与 Avalonia 逻辑像素一致）。</summary>
    public const double Dpi = 96d;

    /// <summary>1 逻辑像素对应的 EMU 数（914400 / 96 = 9525）。</summary>
    public const double EmuPerPixel = EmuPerInch / Dpi;

    /// <summary>EMU → 逻辑像素。</summary>
    public static double ToPixels(double emu) => emu / EmuPerPixel;

    /// <summary>1/100 磅 → 逻辑像素。</summary>
    public static double HundredthPointsToPixels(double hundredthPoints)
        => hundredthPoints / 100d * Dpi / 72d;

    /// <summary>磅 → 逻辑像素。</summary>
    public static double PointsToPixels(double points) => points * Dpi / 72d;

    /// <summary>角度制旋转（OOXML <c>rot</c>，单位 1/60000 度）→ 度。</summary>
    public static double RotationToDegrees(double sixtyThousandthsOfDegree)
        => sixtyThousandthsOfDegree / 60_000d;
}

/// <summary>内部页面节点类型。</summary>
public enum PptxNodeKind
{
    /// <summary>自选图形（预置或自定义几何 + 填充 + 描边，可带文字）。</summary>
    Shape,

    /// <summary>纯文本框（无填充无描边的文字容器）。</summary>
    TextBox,

    /// <summary>位图 / 矢量图（矢量图用同组位图回退）。</summary>
    Picture,

    /// <summary>表格。</summary>
    Table,

    /// <summary>直线 / 连接线。</summary>
    Line,

    /// <summary>组合形状（子节点坐标已合成为页面坐标）。</summary>
    Group,

    /// <summary>SmartArt 等图示的占位节点（只画提示框）。</summary>
    SmartArt,

    /// <summary>视频部件占位框。</summary>
    Video,

    /// <summary>音频部件提示图标。</summary>
    Audio,

    /// <summary>无法识别的内容占位（例如未知的 graphicFrame）。</summary>
    Unknown,
}

/// <summary>预置几何形状（<c>a:prstGeom/@prst</c> 的归一化结果）。</summary>
public enum PptxGeometryKind
{
    /// <summary>矩形。</summary>
    Rectangle,

    /// <summary>圆角矩形。</summary>
    RoundRectangle,

    /// <summary>椭圆 / 圆。</summary>
    Ellipse,

    /// <summary>等腰三角形。</summary>
    Triangle,

    /// <summary>直角三角形。</summary>
    RightTriangle,

    /// <summary>菱形。</summary>
    Diamond,

    /// <summary>平行四边形。</summary>
    Parallelogram,

    /// <summary>梯形。</summary>
    Trapezoid,

    /// <summary>正五边形。</summary>
    Pentagon,

    /// <summary>六边形。</summary>
    Hexagon,

    /// <summary>八边形。</summary>
    Octagon,

    /// <summary>五角星。</summary>
    Star5,

    /// <summary>四角星。</summary>
    Star4,

    /// <summary>六角星。</summary>
    Star6,

    /// <summary>右箭头。</summary>
    ArrowRight,

    /// <summary>左箭头。</summary>
    ArrowLeft,

    /// <summary>上箭头。</summary>
    ArrowUp,

    /// <summary>下箭头。</summary>
    ArrowDown,

    /// <summary>V 形箭头（chevron）。</summary>
    Chevron,

    /// <summary>十字。</summary>
    Cross,

    /// <summary>流程图「处理」框。</summary>
    FlowChartProcess,

    /// <summary>流程图「判定」框。</summary>
    FlowChartDecision,

    /// <summary>流程图「数据」框。</summary>
    FlowChartData,

    /// <summary>直线（仅用于连接线形状）。</summary>
    Line,

    /// <summary>自定义几何（<c>a:custGeom</c>），按外接矩形绘制。</summary>
    Custom,

    /// <summary>未识别的预置几何，渲染时按矩形降级。</summary>
    Unsupported,
}

/// <summary>描边虚线样式。</summary>
public enum PptxDashStyle
{
    /// <summary>实线。</summary>
    Solid,

    /// <summary>虚线。</summary>
    Dash,

    /// <summary>点线。</summary>
    Dot,

    /// <summary>点划线。</summary>
    DashDot,

    /// <summary>长划线。</summary>
    LongDash,

    /// <summary>长点划线。</summary>
    LongDashDot,

    /// <summary>圆点线。</summary>
    RoundDot,

    /// <summary>系统虚线（与虚线等价）。</summary>
    SystemDash,

    /// <summary>系统点线（与点线等价）。</summary>
    SystemDot,
}

/// <summary>线段端点箭头类型。</summary>
public enum PptxArrowHead
{
    /// <summary>无箭头。</summary>
    None,

    /// <summary>三角形箭头。</summary>
    Triangle,

    /// <summary>开口箭头。</summary>
    Arrow,

    /// <summary>燕尾箭头。</summary>
    Stealth,

    /// <summary>菱形端点。</summary>
    Diamond,

    /// <summary>圆形端点。</summary>
    Oval,
}

/// <summary>段落水平对齐。</summary>
public enum PptxTextAlignment
{
    /// <summary>左对齐。</summary>
    Left,

    /// <summary>居中。</summary>
    Center,

    /// <summary>右对齐。</summary>
    Right,

    /// <summary>两端对齐。</summary>
    Justify,
}

/// <summary>文本框垂直锚点（<c>a:bodyPr/@anchor</c>）。</summary>
public enum PptxTextAnchor
{
    /// <summary>顶端对齐。</summary>
    Top,

    /// <summary>垂直居中。</summary>
    Middle,

    /// <summary>底端对齐。</summary>
    Bottom,
}

/// <summary>文本自动缩放策略（<c>a:bodyPr</c> 子元素）。</summary>
public enum PptxAutoFit
{
    /// <summary>不缩放。</summary>
    None,

    /// <summary>按形状调整（模型里按不缩放处理）。</summary>
    Shape,

    /// <summary>缩小字号以适应（<c>a:normAutofit/@fontScale</c>）。</summary>
    Normal,
}

/// <summary>项目符号类型。</summary>
public enum PptxBulletKind
{
    /// <summary>无项目符号。</summary>
    None,

    /// <summary>字符项目符号（•、-、§ 等）。</summary>
    Character,

    /// <summary>自动编号项目符号。</summary>
    Number,
}

/// <summary>进入 / 退出动画效果类型。</summary>
public enum PptxAnimationEffect
{
    /// <summary>出现（瞬时显示）。</summary>
    Appear,

    /// <summary>淡入 / 淡出。</summary>
    Fade,

    /// <summary>飞入 / 飞出。</summary>
    Fly,

    /// <summary>缩放。</summary>
    Zoom,

    /// <summary>擦除。</summary>
    Wipe,

    /// <summary>不受支持的效果（路径动画、复杂强调等），按恒等变换处理。</summary>
    Unsupported,
}

/// <summary>动画触发器。</summary>
public enum PptxAnimationTrigger
{
    /// <summary>单击时（<c>nodeType="clickEffect"</c>）。</summary>
    OnClick,

    /// <summary>与上一项同时（<c>nodeType="withEffect"</c>）。</summary>
    WithPrevious,

    /// <summary>上一项之后（<c>nodeType="afterEffect"</c>）。</summary>
    AfterPrevious,
}

/// <summary>动画方向（飞入 / 飞出的来向，擦除的推进方向）。</summary>
public enum PptxAnimationDirection
{
    /// <summary>无方向（淡入淡出、出现等）。</summary>
    None,

    /// <summary>自左。</summary>
    FromLeft,

    /// <summary>自右。</summary>
    FromRight,

    /// <summary>自上。</summary>
    FromTop,

    /// <summary>自下。</summary>
    FromBottom,

    /// <summary>自左上。</summary>
    FromTopLeft,

    /// <summary>自右上。</summary>
    FromTopRight,

    /// <summary>自左下。</summary>
    FromBottomLeft,

    /// <summary>自右下。</summary>
    FromBottomRight,
}

/// <summary>页面转场类型（归一化后）。</summary>
public enum PptxTransitionKind
{
    /// <summary>无转场（直接切换）。</summary>
    None,

    /// <summary>淡入淡出。</summary>
    Fade,

    /// <summary>推进（新页推着旧页离开）。</summary>
    Push,

    /// <summary>覆盖（新页盖在旧页之上滑入）。</summary>
    Cover,

    /// <summary>擦除。</summary>
    Wipe,

    /// <summary>拆分（从中间向两侧 / 两侧向中间）。</summary>
    Split,
}

/// <summary>转场方向（<c>dir</c> 属性：l/r/u/d）。</summary>
public enum PptxTransitionDirection
{
    /// <summary>未指定方向（默认向左推进）。</summary>
    None,

    /// <summary>向左（l）。</summary>
    Left,

    /// <summary>向右（r）。</summary>
    Right,

    /// <summary>向上（u）。</summary>
    Up,

    /// <summary>向下（d）。</summary>
    Down,
}

/// <summary>页面转场剪辑模式（供渲染层决定如何裁两页）。</summary>
public enum PptxTransitionClipMode
{
    /// <summary>不做剪辑（淡入淡出 / 推进 / 覆盖）。</summary>
    None,

    /// <summary>单边擦除：按方向从一侧推进露出。</summary>
    Edge,

    /// <summary>拆分：从中心向两侧展开。</summary>
    SplitOut,

    /// <summary>拆分：从两侧向中心收拢。</summary>
    SplitIn,
}

/// <summary>媒体部件类型。</summary>
public enum PptxMediaKind
{
    /// <summary>视频。</summary>
    Video,

    /// <summary>音频。</summary>
    Audio,
}

/// <summary>页面内的一个媒体部件（视频 / 音频），以及它在缓存目录中的绝对路径。</summary>
public sealed record PptxMediaFile
{
    /// <summary>媒体类型。</summary>
    public PptxMediaKind Kind { get; init; }

    /// <summary>包内部件名（例如 <c>ppt/media/media1.mp4</c>）。</summary>
    public string PartName { get; init; } = string.Empty;

    /// <summary>内容类型（MIME）。</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>导出到缓存目录后的绝对路径；导出失败时为 <c>null</c>。</summary>
    public string? LocalPath { get; init; }

    /// <summary>媒体在页面坐标系中占据的矩形。</summary>
    public Rect Rect { get; init; }

    /// <summary>关联的形状 id（用于动画定位）。</summary>
    public int ShapeId { get; init; }

    /// <summary>是否已成功导出到本机缓存。</summary>
    public bool IsExported => !string.IsNullOrEmpty(LocalPath) && File.Exists(LocalPath);
}

/// <summary>统一的填充描述（纯色 / 渐变 / 无填充）。</summary>
public abstract record PptxFill
{
    /// <summary>无填充（透明）。</summary>
    public static readonly PptxFill None = new PptxNoFill();

    /// <summary>按纯色创建填充。</summary>
    public static PptxFill Solid(Color color) => new PptxSolidFill { Color = color };
}

/// <summary>无填充。</summary>
public sealed record PptxNoFill : PptxFill;

/// <summary>纯色填充。</summary>
public sealed record PptxSolidFill : PptxFill
{
    /// <summary>填充色（含 Alpha 通道）。</summary>
    public Color Color { get; init; } = Colors.Black;
}

/// <summary>渐变色标。</summary>
public sealed record PptxGradientStop
{
    /// <summary>位置（0..1）。</summary>
    public double Offset { get; init; }

    /// <summary>颜色。</summary>
    public Color Color { get; init; } = Colors.Black;
}

/// <summary>渐变填充（线性；路径渐变按线性近似）。</summary>
public sealed record PptxGradientFill : PptxFill
{
    /// <summary>色标列表（按位置升序）。</summary>
    public IReadOnlyList<PptxGradientStop> Stops { get; init; } = Array.Empty<PptxGradientStop>();

    /// <summary>线性渐变角度（度，0 = 从左到右，顺时针）。</summary>
    public double AngleDegrees { get; init; }

    /// <summary>是否为路径渐变（渲染时按线性近似）。</summary>
    public bool IsPathGradient { get; init; }
}

/// <summary>描边（线条）描述。</summary>
public sealed record PptxStroke
{
    /// <summary>颜色（含 Alpha）。</summary>
    public Color Color { get; init; } = Colors.Black;

    /// <summary>线宽（逻辑像素）。</summary>
    public double WidthPx { get; init; } = 1d;

    /// <summary>虚线样式。</summary>
    public PptxDashStyle DashStyle { get; init; } = PptxDashStyle.Solid;

    /// <summary>是否为无描边。</summary>
    public bool IsNone { get; init; }

    /// <summary>无描边单例。</summary>
    public static readonly PptxStroke None = new() { IsNone = true };
}

/// <summary>文本 run（同一格式的一段连续文字）。</summary>
public sealed record PptxTextRun
{
    /// <summary>文字内容（保留原始空格与换行）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>请求的字体族名（<c>latin</c>/<c>ea</c>/<c>cs</c> 中取到的第一个）；<c>null</c> 表示继承默认。</summary>
    public string? FontFamily { get; init; }

    /// <summary>字号（逻辑像素）。</summary>
    public double FontSizePx { get; init; } = 24d;

    /// <summary>是否加粗。</summary>
    public bool Bold { get; init; }

    /// <summary>是否倾斜。</summary>
    public bool Italic { get; init; }

    /// <summary>是否下划线。</summary>
    public bool Underline { get; init; }

    /// <summary>是否删除线。</summary>
    public bool Strikethrough { get; init; }

    /// <summary>前景色。</summary>
    public Color Color { get; init; } = Colors.Black;

    /// <summary>字符间距（逻辑像素，<c>rPr/@spc</c> 的 1/100 磅换算）。</summary>
    public double LetterSpacingPx { get; init; }
}

/// <summary>项目符号描述。</summary>
public sealed record PptxBullet
{
    /// <summary>符号类型。</summary>
    public PptxBulletKind Kind { get; init; } = PptxBulletKind.None;

    /// <summary>字符符号（<see cref="PptxBulletKind.Character"/> 时有意义）。</summary>
    public string Character { get; init; } = "•";

    /// <summary>编号起始值（<see cref="PptxBulletKind.Number"/> 时有意义）。</summary>
    public int StartAt { get; init; } = 1;

    /// <summary>编号类型（arabicPeriod / romanUcPeriod …）。</summary>
    public string NumberType { get; init; } = "arabicPeriod";

    /// <summary>符号颜色（默认继承文本色）。</summary>
    public Color? Color { get; init; }

    /// <summary>符号缩放（相对于字号，<c>buSzPct</c>）。</summary>
    public double SizePercent { get; init; } = 100d;
}

/// <summary>段落（一个 <c>a:p</c>）。</summary>
public sealed record PptxParagraph
{
    /// <summary>段落内的 run 列表。</summary>
    public IReadOnlyList<PptxTextRun> Runs { get; init; } = Array.Empty<PptxTextRun>();

    /// <summary>水平对齐。</summary>
    public PptxTextAlignment Alignment { get; init; } = PptxTextAlignment.Left;

    /// <summary>大纲级别（0 起）。</summary>
    public int Level { get; init; }

    /// <summary>行距倍数（1 = 单倍；<c>a:lnSpc/a:spcPct</c>）。</summary>
    public double LineSpacingFactor { get; init; } = 1d;

    /// <summary>绝对行距（逻辑像素，<c>a:lnSpc/a:spcPts</c>）；<c>null</c> 表示按倍数计算。</summary>
    public double? LineSpacingPx { get; init; }

    /// <summary>段前距（逻辑像素）。</summary>
    public double SpaceBeforePx { get; init; }

    /// <summary>段后距（逻辑像素）。</summary>
    public double SpaceAfterPx { get; init; }

    /// <summary>文本左缩进（逻辑像素，<c>marL</c>）。</summary>
    public double IndentPx { get; init; }

    /// <summary>首行悬挂（逻辑像素，<c>indent</c>，负值表示悬挂缩进）。</summary>
    public double HangingIndentPx { get; init; }

    /// <summary>项目符号。</summary>
    public PptxBullet Bullet { get; init; } = new();

    /// <summary>段落内所有 run 的连接文本（供渲染与断言使用）。</summary>
    public string Text
    {
        get
        {
            if (Runs.Count == 0)
            {
                return string.Empty;
            }

            if (Runs.Count == 1)
            {
                return Runs[0].Text;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var run in Runs)
            {
                builder.Append(run.Text);
            }

            return builder.ToString();
        }
    }
}

/// <summary>文本框内容（<c>p:txBody</c> / <c>a:txBody</c>）。</summary>
public sealed record PptxTextBody
{
    /// <summary>段落列表。</summary>
    public IReadOnlyList<PptxParagraph> Paragraphs { get; init; } = Array.Empty<PptxParagraph>();

    /// <summary>垂直锚点。</summary>
    public PptxTextAnchor Anchor { get; init; } = PptxTextAnchor.Top;

    /// <summary>是否自动换行。</summary>
    public bool WordWrap { get; init; } = true;

    /// <summary>左内边距（逻辑像素）。</summary>
    public double InsetLeftPx { get; init; } = 9.6d;

    /// <summary>上内边距（逻辑像素）。</summary>
    public double InsetTopPx { get; init; } = 4.8d;

    /// <summary>右内边距（逻辑像素）。</summary>
    public double InsetRightPx { get; init; } = 9.6d;

    /// <summary>下内边距（逻辑像素）。</summary>
    public double InsetBottomPx { get; init; } = 4.8d;

    /// <summary>自动缩放策略。</summary>
    public PptxAutoFit AutoFit { get; init; } = PptxAutoFit.None;

    /// <summary>自动缩小字号的比例（1 = 不缩小）。</summary>
    public double FontScale { get; init; } = 1d;

    /// <summary>是否旋转 90 度（竖排）；本内核不实现，已记录降级。</summary>
    public bool IsVertical { get; init; }

    /// <summary>全部段落的纯文本行（含段落换行）。</summary>
    public string PlainText
        => string.Join("\n", Paragraphs.Select(p => p.Text));
}

/// <summary>页面节点基类（不可变）。</summary>
public abstract record PptxNode
{
    /// <summary>OOXML 形状 id（动画 <c>spTgt/@spid</c> 的定位依据）。</summary>
    public int Id { get; init; }

    /// <summary>形状名（<c>p:cNvPr/@name</c>）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>页面坐标系中的外接矩形（逻辑像素）。</summary>
    public Rect Rect { get; init; }

    /// <summary>绕矩形中心旋转角度（度，顺时针）。</summary>
    public double Rotation { get; init; }

    /// <summary>静态透明度（0..1）。</summary>
    public double Opacity { get; init; } = 1d;

    /// <summary>是否来自占位符。</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>占位符类型（title / body / pic / tbl …）。</summary>
    public string? PlaceholderType { get; init; }

    /// <summary>占位符索引（<c>ph/@idx</c>，无 idx 时按类型推算）。</summary>
    public int PlaceholderIndex { get; init; }

    /// <summary>是否被标记为隐藏（<c>hidden="1"</c> / <c>show="0"</c>）。</summary>
    public bool Hidden { get; init; }

    /// <summary>节点类型。</summary>
    public abstract PptxNodeKind Kind { get; }
}

/// <summary>自选图形（可带文字）。</summary>
public sealed record PptxShapeNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Shape;

    /// <summary>预置几何种类。</summary>
    public PptxGeometryKind Geometry { get; init; } = PptxGeometryKind.Rectangle;

    /// <summary>原始预置几何名（<c>prst</c>），便于诊断与降级提示。</summary>
    public string RawGeometryName { get; init; } = "rect";

    /// <summary>几何调整值（<c>a:avLst/a:gd</c> 的归一化取值，按名称索引）。</summary>
    public IReadOnlyDictionary<string, double> Adjustments { get; init; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>填充。</summary>
    public PptxFill Fill { get; init; } = PptxFill.None;

    /// <summary>描边；<c>null</c> 表示无描边。</summary>
    public PptxStroke? Stroke { get; init; }

    /// <summary>文字内容；<c>null</c> 表示没有文本。</summary>
    public PptxTextBody? Text { get; init; }

    /// <summary>
    /// 自定义几何的路径数据（SVG 路径语法）。坐标位于 <see cref="CustomPathSize"/> 定义的路径空间，
    /// 渲染时按外接矩形缩放；非 <c>a:custGeom</c> 时为 <c>null</c>。
    /// </summary>
    public string? CustomPathData { get; init; }

    /// <summary>自定义几何的路径空间尺寸（<c>a:path/@w/@h</c> 换算后的逻辑像素）。</summary>
    public Size CustomPathSize { get; init; }
}

/// <summary>纯文本框（无填充、无描边的文字容器）。</summary>
public sealed record PptxTextBoxNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.TextBox;

    /// <summary>文字内容（必定非空）。</summary>
    public PptxTextBody Text { get; init; } = new();

    /// <summary>可选背景填充（少数文本框带底色）。</summary>
    public PptxFill Fill { get; init; } = PptxFill.None;

    /// <summary>可选描边。</summary>
    public PptxStroke? Stroke { get; init; }
}

/// <summary>图片节点。</summary>
public sealed record PptxPictureNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Picture;

    /// <summary>包内部件名（也是位图缓存键）。</summary>
    public string PartName { get; init; } = string.Empty;

    /// <summary>图片字节（已从包里读出，渲染时解码）。</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>内容类型。</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>是否由矢量图（SVG）降级为位图。</summary>
    public bool IsVectorFallback { get; init; }

    /// <summary>源裁剪（左、上、右、下，单位 0..1 的比例）。</summary>
    public Thickness Crop { get; init; }

    /// <summary>原始矢量部件名（降级时记录，便于 UI 提示）。</summary>
    public string? VectorPartName { get; init; }
}

/// <summary>表格单元格。</summary>
public sealed record PptxTableCell
{
    /// <summary>单元格矩形（已按合并跨度扩展，逻辑像素）。</summary>
    public Rect Rect { get; init; }

    /// <summary>横向合并列数（<c>gridSpan</c>，1 表示不合并）。</summary>
    public int GridSpan { get; init; } = 1;

    /// <summary>纵向合并行数（<c>rowSpan</c>，1 表示不合并）。</summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>是否为被合并覆盖的从属单元格（渲染时跳过，避免重复画）。</summary>
    public bool IsMergeContinuation { get; init; }

    /// <summary>单元格底色。</summary>
    public PptxFill Fill { get; init; } = PptxFill.None;

    /// <summary>左边框。</summary>
    public PptxStroke? BorderLeft { get; init; }

    /// <summary>上边框。</summary>
    public PptxStroke? BorderTop { get; init; }

    /// <summary>右边框。</summary>
    public PptxStroke? BorderRight { get; init; }

    /// <summary>下边框。</summary>
    public PptxStroke? BorderBottom { get; init; }

    /// <summary>单元格文字。</summary>
    public PptxTextBody? Text { get; init; }

    /// <summary>垂直锚点。</summary>
    public PptxTextAnchor Anchor { get; init; } = PptxTextAnchor.Middle;

    /// <summary>左内边距（逻辑像素）。</summary>
    public double MarginLeftPx { get; init; } = 9.6d;

    /// <summary>上内边距（逻辑像素）。</summary>
    public double MarginTopPx { get; init; } = 4.8d;

    /// <summary>右内边距（逻辑像素）。</summary>
    public double MarginRightPx { get; init; } = 9.6d;

    /// <summary>下内边距（逻辑像素）。</summary>
    public double MarginBottomPx { get; init; } = 4.8d;

    /// <summary>单元格文本（便于断言）。</summary>
    public string PlainText => Text?.PlainText ?? string.Empty;
}

/// <summary>表格行。</summary>
public sealed record PptxTableRow
{
    /// <summary>行高（逻辑像素）。</summary>
    public double Height { get; init; }

    /// <summary>该行的单元格（含被合并覆盖的从属单元格）。</summary>
    public IReadOnlyList<PptxTableCell> Cells { get; init; } = Array.Empty<PptxTableCell>();
}

/// <summary>表格节点。</summary>
public sealed record PptxTableNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Table;

    /// <summary>列宽（逻辑像素）。</summary>
    public IReadOnlyList<double> ColumnWidths { get; init; } = Array.Empty<double>();

    /// <summary>行列表。</summary>
    public IReadOnlyList<PptxTableRow> Rows { get; init; } = Array.Empty<PptxTableRow>();

    /// <summary>是否带首行强调（<c>firstRow="1"</c>）。</summary>
    public bool FirstRowHeader { get; init; }
}

/// <summary>直线 / 连接线节点。</summary>
public sealed record PptxLineNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Line;

    /// <summary>起点（页面坐标）。</summary>
    public Point Start { get; init; }

    /// <summary>终点（页面坐标）。</summary>
    public Point End { get; init; }

    /// <summary>描边。</summary>
    public PptxStroke Stroke { get; init; } = new();

    /// <summary>起点箭头（<c>a:headEnd</c>）。</summary>
    public PptxArrowHead HeadEnd { get; init; } = PptxArrowHead.None;

    /// <summary>终点箭头（<c>a:tailEnd</c>）。</summary>
    public PptxArrowHead TailEnd { get; init; } = PptxArrowHead.None;

    /// <summary>是否水平翻转（<c>flipH</c>）。</summary>
    public bool FlipHorizontal { get; init; }

    /// <summary>是否垂直翻转（<c>flipV</c>）。</summary>
    public bool FlipVertical { get; init; }
}

/// <summary>组合形状节点（子节点坐标已合成为页面坐标）。</summary>
public sealed record PptxGroupNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Group;

    /// <summary>子节点（页面坐标）。</summary>
    public IReadOnlyList<PptxNode> Children { get; init; } = Array.Empty<PptxNode>();
}

/// <summary>SmartArt / 图示占位节点。</summary>
public sealed record PptxSmartArtNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.SmartArt;

    /// <summary>提示文字（渲染在占位框内）。</summary>
    public string PlaceholderText { get; init; } = "SmartArt 图形暂不支持（仅显示占位）";

    /// <summary>图示数据部件名（如果有）。</summary>
    public string? DataPartName { get; init; }
}

/// <summary>视频占位节点。</summary>
public sealed record PptxVideoNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Video;

    /// <summary>媒体部件。</summary>
    public PptxMediaFile Media { get; init; } = new();

    /// <summary>封面图部件名（<c>p14:media</c> 旁的 poster，如果有）。</summary>
    public string? PosterPartName { get; init; }

    /// <summary>封面图字节。</summary>
    public byte[]? PosterData { get; init; }
}

/// <summary>音频提示节点。</summary>
public sealed record PptxAudioNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Audio;

    /// <summary>媒体部件。</summary>
    public PptxMediaFile Media { get; init; } = new();

    /// <summary>是否自动播放。</summary>
    public bool AutoPlay { get; init; }
}

/// <summary>无法识别内容的占位节点。</summary>
public sealed record PptxUnknownNode : PptxNode
{
    /// <inheritdoc />
    public override PptxNodeKind Kind => PptxNodeKind.Unknown;

    /// <summary>提示文字。</summary>
    public string PlaceholderText { get; init; } = "该内容暂不支持显示";
}

/// <summary>一次动画求值得到的节点视觉状态（未参与动画的节点为 <see cref="Identity"/>）。</summary>
/// <param name="Opacity">不透明度（0..1）。</param>
/// <param name="TranslateX">水平位移（逻辑像素，正值向右）。</param>
/// <param name="TranslateY">垂直位移（逻辑像素，正值向下）。</param>
/// <param name="Scale">绕中心缩放倍数（1 = 原尺寸）。</param>
/// <param name="ClipProgress">擦除进度（0..1，1 表示不裁剪）。</param>
/// <param name="ClipFrom">擦除的推进方向（<see cref="PptxAnimationDirection.None"/> 表示不裁剪）。</param>
public readonly record struct PptxNodeVisualState(
    double Opacity,
    double TranslateX,
    double TranslateY,
    double Scale,
    double ClipProgress,
    PptxAnimationDirection ClipFrom = PptxAnimationDirection.None)
{
    /// <summary>恒等状态：完全可见、无位移、无缩放、无裁剪。</summary>
    public static readonly PptxNodeVisualState Identity =
        new(1d, 0d, 0d, 1d, 1d, PptxAnimationDirection.None);

    /// <summary>是否与恒等状态等价（渲染层可跳过变换）。</summary>
    public bool IsIdentity
        => Math.Abs(Opacity - 1d) < 1e-9
           && Math.Abs(TranslateX) < 1e-9
           && Math.Abs(TranslateY) < 1e-9
           && Math.Abs(Scale - 1d) < 1e-9
           && Math.Abs(ClipProgress - 1d) < 1e-9;

    /// <summary>是否完全不可见（渲染层可直接跳过）。</summary>
    public bool IsInvisible => Opacity <= 1e-6 || ClipProgress <= 1e-6;
}

/// <summary>单个动画步骤（一个形状上的一次进入 / 退出效果）。</summary>
public sealed record PptxAnimationStep
{
    /// <summary>目标形状 id（<c>spTgt/@spid</c>）。</summary>
    public int TargetId { get; init; }

    /// <summary>目标形状名（诊断用）。</summary>
    public string? TargetName { get; init; }

    /// <summary>效果类型。</summary>
    public PptxAnimationEffect Effect { get; init; } = PptxAnimationEffect.Fade;

    /// <summary>触发器。</summary>
    public PptxAnimationTrigger Trigger { get; init; } = PptxAnimationTrigger.OnClick;

    /// <summary>方向。</summary>
    public PptxAnimationDirection Direction { get; init; } = PptxAnimationDirection.None;

    /// <summary>延迟（毫秒，相对触发时刻）。</summary>
    public double DelayMs { get; init; }

    /// <summary>时长（毫秒）。</summary>
    public double DurationMs { get; init; } = 500d;

    /// <summary>是否为退出动画。</summary>
    public bool IsExit { get; init; }

    /// <summary>原始效果名（诊断用：<c>presetID</c>/<c>presetClass</c> 组合）。</summary>
    public string RawEffectName { get; init; } = string.Empty;

    /// <summary>所属单击组序号（0 起；同一组由一次单击触发）。</summary>
    public int ClickGroupIndex { get; init; }
}

/// <summary>页面动画时间轴（不可变）。</summary>
public sealed record PptxAnimationTimeline
{
    /// <summary>空的（无动画）时间轴。</summary>
    public static readonly PptxAnimationTimeline Empty = new();

    /// <summary>动画步骤（按 XML 顺序）。</summary>
    public IReadOnlyList<PptxAnimationStep> Steps { get; init; } = Array.Empty<PptxAnimationStep>();

    /// <summary>单击组数量（0 表示没有需要单击触发的动画）。</summary>
    public int ClickGroupCount { get; init; }

    /// <summary>全部动画结束所需时间（毫秒，不含等待单击的时间）。</summary>
    public double TotalDurationMs { get; init; }

    /// <summary>是否含不受支持的效果（路径动画等），这些步骤按恒等变换处理。</summary>
    public bool HasUnsupportedEffects { get; init; }

    /// <summary>是否没有任何动画。</summary>
    public bool IsEmpty => Steps.Count == 0;
}

/// <summary>页面转场描述（已归一化）。</summary>
public sealed record PptxTransition
{
    /// <summary>无转场。</summary>
    public static readonly PptxTransition None = new();

    /// <summary>转场类型。</summary>
    public PptxTransitionKind Kind { get; init; } = PptxTransitionKind.None;

    /// <summary>方向。</summary>
    public PptxTransitionDirection Direction { get; init; } = PptxTransitionDirection.None;

    /// <summary>时长（毫秒）。</summary>
    public double DurationMs { get; init; } = 500d;

    /// <summary>原始转场元素名（诊断用，例如 <c>morph</c>）。</summary>
    public string RawType { get; init; } = string.Empty;

    /// <summary>是否由不受支持的转场降级而来。</summary>
    public bool IsDegraded { get; init; }

    /// <summary>是否单击换页（<c>advClick</c>）。</summary>
    public bool AdvanceOnClick { get; init; } = true;

    /// <summary>自动换页时间（毫秒）；<c>null</c> 表示不自动换页。</summary>
    public double? AutoAdvanceAfterMs { get; init; }

    /// <summary>拆分的朝向（<c>split/@orient</c>：horz / vert）。</summary>
    public string SplitOrientation { get; init; } = "horz";
}

/// <summary>一页幻灯片（不可变）。</summary>
public sealed record PptxSlide
{
    /// <summary>页码（1 起）。</summary>
    public int Index { get; init; } = 1;

    /// <summary>包内部件名（例如 <c>ppt/slides/slide1.xml</c>）。</summary>
    public string PartName { get; init; } = string.Empty;

    /// <summary>页面尺寸（逻辑像素）。</summary>
    public Size PageSize { get; init; } = new(1280, 720);

    /// <summary>页面背景色。</summary>
    public Color Background { get; init; } = Colors.White;

    /// <summary>页面背景填充（渐变背景时使用；纯色时为 <c>null</c>）。</summary>
    public PptxFill? BackgroundFill { get; init; }

    /// <summary>节点列表（按 XML 出现顺序，后画的在上）。</summary>
    public IReadOnlyList<PptxNode> Nodes { get; init; } = Array.Empty<PptxNode>();

    /// <summary>页面切换到此页时播放的转场。</summary>
    public PptxTransition Transition { get; init; } = PptxTransition.None;

    /// <summary>页面动画时间轴。</summary>
    public PptxAnimationTimeline Animation { get; init; } = PptxAnimationTimeline.Empty;

    /// <summary>页面内媒体（视频 / 音频）。</summary>
    public IReadOnlyList<PptxMediaFile> Media { get; init; } = Array.Empty<PptxMediaFile>();

    /// <summary>本页遇到的降级 / 不支持特性（中文提示）。</summary>
    public IReadOnlyList<string> UnsupportedFeatures { get; init; } = Array.Empty<string>();

    /// <summary>演讲者备注文本。</summary>
    public string? Notes { get; init; }

    /// <summary>递归展开本页全部节点（含组合内的子节点）。</summary>
    public IEnumerable<PptxNode> Flatten()
    {
        var stack = new Stack<PptxNode>(Nodes.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node is PptxGroupNode group)
            {
                for (var i = group.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(group.Children[i]);
                }
            }
        }
    }

    /// <summary>按形状 id 查找节点（含组合子节点）；找不到返回 <c>null</c>。</summary>
    public PptxNode? FindNode(int shapeId)
        => Flatten().FirstOrDefault(n => n.Id == shapeId);
}

/// <summary>整份演示文稿（不可变）。</summary>
public sealed record PptxDocument
{
    /// <summary>课件绝对路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>课件 SHA256（小写十六进制），用作媒体缓存目录名。</summary>
    public string FileHash { get; init; } = string.Empty;

    /// <summary>页面尺寸（逻辑像素）。</summary>
    public Size PageSize { get; init; } = new(1280, 720);

    /// <summary>页面列表。</summary>
    public IReadOnlyList<PptxSlide> Slides { get; init; } = Array.Empty<PptxSlide>();

    /// <summary>文档级降级提示（与页面级合并展示）。</summary>
    public IReadOnlyList<string> UnsupportedFeatures { get; init; } = Array.Empty<string>();

    /// <summary>字体替换记录（请求字体 → 实际使用的字体）。</summary>
    public IReadOnlyDictionary<string, string> FontSubstitutions { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>文档标题（核心属性）。</summary>
    public string? Title { get; init; }
}
