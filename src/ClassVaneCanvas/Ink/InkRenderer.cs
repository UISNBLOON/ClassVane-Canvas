using Avalonia;
using Avalonia.Media;
using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Ink;

/// <summary>
/// 笔迹渲染：把矢量笔迹画到 Avalonia 绘制上下文。
/// 渲染只使用页面坐标系，画布变换由调用方通过 PushTransform 提供。
/// </summary>
public static class InkRenderer
{
    /// <summary>绘制一条已完成的笔迹。</summary>
    public static void Draw(
        DrawingContext context,
        InkStroke stroke,
        InkEngineSettings settings,
        InkGeometryCache? cache = null)
    {
        if (stroke.Tool == InkTool.Eraser || stroke.Points.Count == 0)
        {
            return;
        }

        var color = ResolveColor(stroke.ColorArgb, stroke.Tool, settings);
        var geometry = cache?.Get(stroke, settings);
        if (geometry is null)
        {
            var built = InkGeometryBuilder.Build(stroke, settings);
            if (built is null)
            {
                return;
            }

            DrawGeometry(context, built, stroke.Tool, color, stroke.BaseWidth, settings);
            return;
        }

        DrawGeometry(context, geometry.Value.Geometry, stroke.Tool, color, stroke.BaseWidth, settings);
    }

    /// <summary>绘制正在书写的实时笔迹（含预判补点）。</summary>
    public static void DrawLive(
        DrawingContext context,
        IReadOnlyList<InkPoint> rawPoints,
        InkTool tool,
        uint colorArgb,
        float baseWidth,
        InkEngineSettings settings)
    {
        if (tool == InkTool.Eraser || rawPoints.Count == 0)
        {
            return;
        }

        var geometry = InkGeometryBuilder.Build(
            StrokeSmoother.SmoothForPreview(rawPoints, settings),
            tool,
            baseWidth,
            settings);

        if (geometry is null)
        {
            return;
        }

        DrawGeometry(context, geometry, tool, ResolveColor(colorArgb, tool, settings), baseWidth, settings);
    }

    /// <summary>按工具类型选择填充或描边。</summary>
    private static void DrawGeometry(
        DrawingContext context,
        Geometry geometry,
        InkTool tool,
        Color color,
        float baseWidth,
        InkEngineSettings settings)
    {
        if (tool == InkTool.Highlighter)
        {
            // 荧光笔：等宽描边 + 圆帽圆角，透明度处处一致（叠加不出现深斑）
            var thickness = Math.Clamp(baseWidth, settings.MinStrokeWidth, settings.MaxStrokeWidth);
            var pen = new Pen(new SolidColorBrush(color), thickness)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };

            context.DrawGeometry(null, pen, geometry);
            return;
        }

        context.DrawGeometry(new SolidColorBrush(color), null, geometry);
    }

    /// <summary>把笔迹颜色 + 工具透明度解析为最终颜色。</summary>
    public static Color ResolveColor(uint colorArgb, InkTool tool, InkEngineSettings settings)
    {
        var color = Color.FromUInt32(colorArgb);
        var opacity = tool switch
        {
            InkTool.Highlighter => Math.Clamp(settings.HighlighterOpacity, 0.02f, 1f),
            InkTool.Marker => Math.Clamp(settings.MarkerOpacity, 0.02f, 1f),
            _ => 1f,
        };

        var alpha = (byte)Math.Clamp(MathF.Round(color.A * opacity), 0f, 255f);

        // 荧光笔/马克笔叠加在 PPT 之上：不做任何混合技巧，仅靠 alpha 保证不遮挡底层内容
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
