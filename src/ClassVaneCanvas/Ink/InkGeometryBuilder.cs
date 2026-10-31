using Avalonia;
using Avalonia.Media;
using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Ink;

/// <summary>
/// 笔迹几何生成（自研矢量笔迹管线）。
///
/// · 硬笔：压感驱动的变宽闭合轮廓（含圆形笔帽），边缘由抗锯齿填充保证柔和；
/// · 马克笔：笔尖倾斜驱动的**椭圆笔锋**，用椭圆的支撑函数求法向偏移，形成扁平斜锋；
/// · 荧光笔：等宽折线，交由描边笔刷渲染，透明度处处一致（叠加后不会出现深斑）；
/// · 橡皮擦：不产生几何。
///
/// 所有几何都构建在**页面坐标系**，渲染时由画布变换矩阵统一缩放平移，
/// 因此缩放漫游后笔迹位置不变、形状不变形，且任意倍率下都保持矢量清晰。
/// </summary>
public static class InkGeometryBuilder
{
    /// <summary>圆弧笔帽的采样段数。</summary>
    private const int CapSegments = 10;

    /// <summary>构建一条笔迹的几何。</summary>
    public static Geometry? Build(InkStroke stroke, InkEngineSettings settings)
        => Build(stroke.Points, stroke.Tool, stroke.BaseWidth, settings);

    /// <summary>构建几何；点不足或工具为橡皮擦时返回 <c>null</c>。</summary>
    public static Geometry? Build(
        IReadOnlyList<InkPoint> rawPoints,
        InkTool tool,
        float baseWidth,
        InkEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rawPoints);
        ArgumentNullException.ThrowIfNull(settings);

        if (tool == InkTool.Eraser || rawPoints.Count == 0)
        {
            return null;
        }

        var points = StrokeSmoother.Smooth(rawPoints, settings);
        if (points.Count == 0)
        {
            return null;
        }

        // 荧光笔：等宽折线 + 描边渲染（透明度均匀，不遮挡底层画面）
        if (tool == InkTool.Highlighter)
        {
            return BuildPolyline(points);
        }

        var width = Math.Clamp(
            baseWidth * PressureCurve.Evaluate(points[0].Pressure, settings),
            settings.MinStrokeWidth,
            settings.MaxStrokeWidth);

        // 单点（点一下）：画一个圆点
        if (points.Count == 1)
        {
            return BuildDot(points[0], width);
        }

        return tool == InkTool.Marker
            ? BuildNibOutline(points, baseWidth, settings)
            : BuildPressureOutline(points, baseWidth, settings);
    }

    /// <summary>等宽折线（荧光笔用，渲染时以圆帽 Pen 描边）。</summary>
    private static Geometry BuildPolyline(IReadOnlyList<InkPoint> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, points[0].Y), false);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(new Point(points[i].X, points[i].Y));
            }

            context.EndFigure(false);
        }

        return geometry;
    }

    /// <summary>点状笔迹：一个填充圆。</summary>
    private static Geometry BuildDot(in InkPoint point, float width)
    {
        var radius = Math.Max(width, 0.5f) / 2f;
        return new EllipseGeometry(new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2));
    }

    /// <summary>硬笔：压感变宽闭合轮廓（左右偏移 + 两端圆形笔帽，单条闭合图形保证透明度均匀）。</summary>
    private static Geometry BuildPressureOutline(
        IReadOnlyList<InkPoint> points,
        float baseWidth,
        InkEngineSettings settings)
    {
        var count = points.Count;
        var halfWidths = new float[count];

        // 1) 逐点半宽（压感 → 宽度），并做窗口平滑，避免边缘毛刺
        var raw = new float[count];
        for (var i = 0; i < count; i++)
        {
            var width = PressureCurve.ResolveWidth(baseWidth, points[i].Pressure, settings);
            raw[i] = width / 2f;
        }

        for (var i = 0; i < count; i++)
        {
            float sum = 0f;
            var used = 0;
            for (var j = Math.Max(0, i - 2); j <= Math.Min(count - 1, i + 2); j++)
            {
                sum += raw[j];
                used++;
            }

            halfWidths[i] = sum / used;
        }

        // 2) 逐点法向
        var normals = ComputeNormals(points);

        // 3) 生成闭合轮廓
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var start = Offset(points[0], normals[0], halfWidths[0]);
            context.BeginFigure(start, true);

            // 左侧（沿法向正向）
            for (var i = 1; i < count; i++)
            {
                context.LineTo(Offset(points[i], normals[i], halfWidths[i]));
            }

            // 末端笔帽：绕末点从左侧转到右侧
            AppendCap(context, points[count - 1], normals[count - 1], halfWidths[count - 1], forward: true);

            // 右侧（回程）
            for (var i = count - 1; i >= 0; i--)
            {
                context.LineTo(Offset(points[i], normals[i], -halfWidths[i]));
            }

            // 起始笔帽：绕起点从右侧转回左侧
            AppendCap(context, points[0], normals[0], halfWidths[0], forward: false);

            context.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>
    /// 马克笔：笔尖倾斜形成扁平斜锋。
    /// 用椭圆（半长轴 = 压感宽度，半短轴 = 宽度 × 扁平度）的支撑函数求每个法向上的偏移长度，
    /// 于是横向运笔粗、纵向运笔细，形成典型马克笔斜锋。
    /// </summary>
    private static Geometry BuildNibOutline(
        IReadOnlyList<InkPoint> points,
        float baseWidth,
        InkEngineSettings settings)
    {
        var count = points.Count;
        var normals = ComputeNormals(points);
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            var (firstSemiMajor, firstSemiMinor, firstDirX, firstDirY) = ResolveNib(points[0], baseWidth, settings);

            context.BeginFigure(
                OffsetNib(points[0], normals[0], firstSemiMajor, firstSemiMinor, firstDirX, firstDirY, 1f),
                true);

            for (var i = 1; i < count; i++)
            {
                var (semiMajor, semiMinor, dirX, dirY) = ResolveNib(points[i], baseWidth, settings);
                context.LineTo(OffsetNib(points[i], normals[i], semiMajor, semiMinor, dirX, dirY, 1f));
            }

            // 末端笔帽
            var (endMajor, endMinor, endDirX, endDirY) = ResolveNib(points[count - 1], baseWidth, settings);
            AppendNibCap(context, points[count - 1], normals[count - 1], endMajor, endMinor, endDirX, endDirY, true);

            for (var i = count - 1; i >= 0; i--)
            {
                var (semiMajor, semiMinor, dirX, dirY) = ResolveNib(points[i], baseWidth, settings);
                context.LineTo(OffsetNib(points[i], normals[i], semiMajor, semiMinor, dirX, dirY, -1f));
            }

            var (startMajor, startMinor, startDirX, startDirY) = ResolveNib(points[0], baseWidth, settings);
            AppendNibCap(context, points[0], normals[0], startMajor, startMinor, startDirX, startDirY, false);

            context.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>由压感与倾斜算出椭圆笔锋（半长轴、半短轴、笔锋方向）。</summary>
    private static (float SemiMajor, float SemiMinor, float DirX, float DirY) ResolveNib(
        in InkPoint point,
        float baseWidth,
        InkEngineSettings settings)
    {
        var width = PressureCurve.ResolveWidth(baseWidth, point.Pressure, settings);
        var semiMajor = width / 2f;

        // 倾斜越大越扁：无倾斜信息时接近圆头
        var tiltMagnitude = MathF.Sqrt((point.TiltX * point.TiltX) + (point.TiltY * point.TiltY));
        var flatten = Math.Clamp(tiltMagnitude * 2.5f, 0f, 1f);
        var flatness = Lerp(0.85f, settings.MarkerFlatness, flatten);
        var semiMinor = semiMajor * Math.Clamp(flatness, 0.05f, 1f);

        // 笔锋方向固定在页面坐标系（不随运笔方向旋转），才有书法斜锋效果
        float dirX, dirY;
        if (tiltMagnitude > 0.05f)
        {
            dirX = point.TiltX / tiltMagnitude;
            dirY = point.TiltY / tiltMagnitude;
        }
        else
        {
            var angle = settings.MarkerDefaultNibAngle * MathF.PI / 180f;
            dirX = MathF.Cos(angle);
            dirY = MathF.Sin(angle);
        }

        return (semiMajor, semiMinor, dirX, dirY);
    }

    /// <summary>椭圆支撑函数：方向 n 上的偏移长度。</summary>
    private static Point OffsetNib(
        in InkPoint point,
        in (float X, float Y) normal,
        float semiMajor,
        float semiMinor,
        float dirX,
        float dirY,
        float sign)
    {
        var along = (normal.X * dirX) + (normal.Y * dirY);
        var across = (normal.X * -dirY) + (normal.Y * dirX);
        var extent = MathF.Sqrt(
            (semiMajor * semiMajor * along * along) +
            (semiMinor * semiMinor * across * across));

        return new Point(point.X + (normal.X * extent * sign), point.Y + (normal.Y * extent * sign));
    }

    /// <summary>变宽轮廓的圆形笔帽。</summary>
    private static void AppendCap(
        StreamGeometryContext context,
        in InkPoint point,
        in (float X, float Y) normal,
        float halfWidth,
        bool forward)
    {
        var (tangentX, tangentY) = Tangent(normal);
        var sign = forward ? 1f : -1f;

        for (var i = 1; i < CapSegments; i++)
        {
            var angle = MathF.PI * i / CapSegments;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);

            var dirX = (normal.X * cos * sign) + (tangentX * sin * sign);
            var dirY = (normal.Y * cos * sign) + (tangentY * sin * sign);

            context.LineTo(new Point(point.X + (dirX * halfWidth), point.Y + (dirY * halfWidth)));
        }
    }

    /// <summary>马克笔笔帽（按椭圆轮廓收口）。</summary>
    private static void AppendNibCap(
        StreamGeometryContext context,
        in InkPoint point,
        in (float X, float Y) normal,
        float semiMajor,
        float semiMinor,
        float dirX,
        float dirY,
        bool forward)
    {
        var (tangentX, tangentY) = Tangent(normal);
        var sign = forward ? 1f : -1f;

        for (var i = 1; i < CapSegments; i++)
        {
            var angle = MathF.PI * i / CapSegments;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);

            var dirNormX = (normal.X * cos) + (tangentX * sin);
            var dirNormY = (normal.Y * cos) + (tangentY * sin);

            var offset = OffsetNib(point, (dirNormX, dirNormY), semiMajor, semiMinor, dirX, dirY, 1f);
            context.LineTo(new Point(
                point.X + ((offset.X - point.X) * sign),
                point.Y + ((offset.Y - point.Y) * sign)));
        }
    }

    /// <summary>逐点法向（由相邻点差分求切线后旋转 90°）。</summary>
    private static (float X, float Y)[] ComputeNormals(IReadOnlyList<InkPoint> points)
    {
        var count = points.Count;
        var normals = new (float X, float Y)[count];

        for (var i = 0; i < count; i++)
        {
            var prev = points[Math.Max(0, i - 1)];
            var next = points[Math.Min(count - 1, i + 1)];

            var dx = next.X - prev.X;
            var dy = next.Y - prev.Y;
            var length = MathF.Sqrt((dx * dx) + (dy * dy));

            if (length < 1e-4f)
            {
                // 退化情况：沿用上一个法向，保证轮廓封闭不断裂
                normals[i] = i > 0 ? normals[i - 1] : (0f, -1f);
                continue;
            }

            normals[i] = (-dy / length, dx / length);
        }

        return normals;
    }

    private static (float X, float Y) Tangent(in (float X, float Y) normal)
        => (-normal.Y, normal.X);

    private static Point Offset(in InkPoint point, in (float X, float Y) normal, float distance)
        => new(point.X + (normal.X * distance), point.Y + (normal.Y * distance));

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
