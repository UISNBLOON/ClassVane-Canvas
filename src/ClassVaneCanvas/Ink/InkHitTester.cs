using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Ink;

/// <summary>橡皮擦命中判定：只擦笔迹，不擦 PPT 内容。</summary>
public static class InkHitTester
{
    /// <summary>命中容差（页面逻辑像素），让触摸/手写笔更容易擦到细笔迹。</summary>
    public const float Tolerance = 2.5f;

    /// <summary>
    /// 返回与给定页面坐标（含半径）相交的笔迹 Id，**按渲染顺序倒序**（最上层优先）。
    /// </summary>
    public static IReadOnlyList<string> HitTest(
        IEnumerable<InkStroke> strokes,
        double x,
        double y,
        double radius,
        InkEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(settings);

        var hits = new List<string>();

        foreach (var stroke in strokes.Reverse())
        {
            if (Intersects(stroke, x, y, radius, settings))
            {
                hits.Add(stroke.Id);
            }
        }

        return hits;
    }

    /// <summary>判断单条笔迹是否与擦除点相交。</summary>
    public static bool Intersects(
        InkStroke stroke,
        double x,
        double y,
        double radius,
        InkEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(settings);

        if (stroke.Points.Count == 0)
        {
            return false;
        }

        var halfWidth = Math.Max(stroke.BaseWidth, settings.MinStrokeWidth) / 2f;
        var threshold = radius + halfWidth + Tolerance;

        // 单点笔迹：按圆点判定
        if (stroke.Points.Count == 1)
        {
            var only = stroke.Points[0];
            return Distance(only.X, only.Y, x, y) <= threshold;
        }

        // 粗筛：先看包围盒
        if (!BoundingBoxIntersects(stroke, x, y, threshold))
        {
            return false;
        }

        for (var i = 1; i < stroke.Points.Count; i++)
        {
            var a = stroke.Points[i - 1];
            var b = stroke.Points[i];
            if (DistanceToSegment(x, y, a.X, a.Y, b.X, b.Y) <= threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static bool BoundingBoxIntersects(InkStroke stroke, double x, double y, double margin)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var point in stroke.Points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return x >= minX - margin && x <= maxX + margin && y >= minY - margin && y <= maxY + margin;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared < 1e-6)
        {
            return Distance(px, py, ax, ay);
        }

        var t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0.0, 1.0);
        return Distance(px, py, ax + (t * dx), ay + (t * dy));
    }
}
