using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Ink;

/// <summary>
/// 轨迹处理管线（自研）：去抖 → 压感低通 → 端点锚定移动平均 → 预判补点。
///
/// 设计目标（对齐希沃白板 5 的书写观感）：
/// · 消除锯齿与拖尾：移动平均窗口随「笔迹平滑强度」变化，首尾点锚定，避免起笔/收笔缩水；
/// · 跟手低延迟：实时绘制时用 <see cref="SmoothForPreview"/> 沿速度方向外推一小段（预判补点）；
/// · 采样点保持原始值入库：平滑只作用于渲染，保存到 .cvcanno 的仍是原始输入点。
/// </summary>
public static class StrokeSmoother
{
    /// <summary>单段最多插入的补点数，避免异常跳点导致点数爆炸。</summary>
    private const int MaxInsertedPerSegment = 24;

    /// <summary>
    /// 完整平滑管线，返回用于渲染的点序列。
    /// </summary>
    public static List<InkPoint> Smooth(IReadOnlyList<InkPoint> rawPoints, InkEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rawPoints);
        ArgumentNullException.ThrowIfNull(settings);

        if (rawPoints.Count == 0)
        {
            return new List<InkPoint>();
        }

        var filtered = Filter(rawPoints, settings);
        var pressured = ApplyPressureFilter(filtered, settings);
        var averaged = MovingAverage(pressured, settings);
        return Resample(averaged, settings);
    }

    /// <summary>
    /// 实时（未落笔完成）绘制用的点序列：在完整管线结果末尾追加一个预判点，降低观感延迟。
    /// 预判点只用于显示，不会写入笔迹数据。
    /// </summary>
    public static List<InkPoint> SmoothForPreview(IReadOnlyList<InkPoint> rawPoints, InkEngineSettings settings)
    {
        var smoothed = Smooth(rawPoints, settings);
        if (smoothed.Count < 2)
        {
            return smoothed;
        }

        var lookahead = settings.PredictionLookaheadMs;
        if (lookahead <= 0f)
        {
            return smoothed;
        }

        var last = smoothed[^1];
        var previous = smoothed[^2];
        var dt = last.TimeMs - previous.TimeMs;
        if (dt <= 0)
        {
            return smoothed;
        }

        // 速度（页面像素/毫秒）→ 按预判时长外推
        var vx = (last.X - previous.X) / dt;
        var vy = (last.Y - previous.Y) / dt;
        var predicted = last with
        {
            X = last.X + (vx * lookahead),
            Y = last.Y + (vy * lookahead),
        };

        smoothed.Add(predicted);
        return smoothed;
    }

    /// <summary>距离阈值去抖：过近的点合并，但保留最后一个点。</summary>
    private static List<InkPoint> Filter(IReadOnlyList<InkPoint> rawPoints, InkEngineSettings settings)
    {
        var minDistance = Math.Max(0f, settings.MinPointDistance);
        var result = new List<InkPoint>(rawPoints.Count) { rawPoints[0] };

        for (var i = 1; i < rawPoints.Count; i++)
        {
            var candidate = rawPoints[i];
            if (candidate.DistanceTo(result[^1]) >= minDistance)
            {
                result.Add(candidate);
            }
        }

        // 保证收笔点一定保留：末点与最后一个入选点不同值时补上
        var lastRaw = rawPoints[^1];
        if (!result[^1].Equals(lastRaw))
        {
            result.Add(lastRaw);
        }

        return result;
    }

    /// <summary>压感低通 + 死区（抑制压力抖动引起的笔触跳动）。</summary>
    private static List<InkPoint> ApplyPressureFilter(List<InkPoint> points, InkEngineSettings settings)
    {
        var result = new List<InkPoint>(points.Count);
        var previous = float.NaN;

        foreach (var point in points)
        {
            var pressure = PressureCurve.SmoothPressure(previous, point.Pressure, settings);
            previous = pressure;
            result.Add(point with { Pressure = pressure });
        }

        return result;
    }

    /// <summary>首尾锚定的移动平均（窗口大小由平滑强度决定）。</summary>
    private static List<InkPoint> MovingAverage(List<InkPoint> points, InkEngineSettings settings)
    {
        if (points.Count < 3)
        {
            return points;
        }

        var strength = Math.Clamp(settings.SmoothingStrength, 0f, 1f);
        var half = (int)MathF.Round(strength * 3f);
        if (half <= 0)
        {
            return points;
        }

        var result = new List<InkPoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            // 端点锚定：首尾保持原样，避免起笔/收笔被“拉短”
            if (i == 0 || i == points.Count - 1)
            {
                result.Add(points[i]);
                continue;
            }

            var from = Math.Max(1, i - half);
            var to = Math.Min(points.Count - 2, i + half);

            float sumX = 0f, sumY = 0f;
            var count = 0;
            for (var j = from; j <= to; j++)
            {
                sumX += points[j].X;
                sumY += points[j].Y;
                count++;
            }

            result.Add(points[i] with { X = sumX / count, Y = sumY / count });
        }

        return result;
    }

    /// <summary>预判补点：按步长在段内做 Catmull-Rom 插值，快写时不再出现折线感。</summary>
    private static List<InkPoint> Resample(List<InkPoint> points, InkEngineSettings settings)
    {
        var step = settings.ResampleStep;
        if (points.Count < 2 || step <= 0.1f)
        {
            return points;
        }

        var result = new List<InkPoint>(points.Count * 2) { points[0] };

        for (var i = 1; i < points.Count; i++)
        {
            var p0 = points[Math.Max(0, i - 2)];
            var p1 = points[i - 1];
            var p2 = points[i];
            var p3 = points[Math.Min(points.Count - 1, i + 1)];

            var length = p1.DistanceTo(p2);
            var insertCount = Math.Min((int)(length / step), MaxInsertedPerSegment);

            for (var k = 1; k <= insertCount; k++)
            {
                var t = (float)k / (insertCount + 1);
                result.Add(Interpolate(p0, p1, p2, p3, t));
            }

            result.Add(p2);
        }

        return result;
    }

    /// <summary>Catmull-Rom 插值（坐标走曲线，压力/倾斜/时间线性过渡）。</summary>
    private static InkPoint Interpolate(in InkPoint p0, in InkPoint p1, in InkPoint p2, in InkPoint p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;

        var x = 0.5f * ((2f * p1.X) + ((-p0.X + p2.X) * t) +
                        ((2f * p0.X - 5f * p1.X + 4f * p2.X - p3.X) * t2) +
                        ((-p0.X + 3f * p1.X - 3f * p2.X + p3.X) * t3));

        var y = 0.5f * ((2f * p1.Y) + ((-p0.Y + p2.Y) * t) +
                        ((2f * p0.Y - 5f * p1.Y + 4f * p2.Y - p3.Y) * t2) +
                        ((-p0.Y + 3f * p1.Y - 3f * p2.Y + p3.Y) * t3));

        return new InkPoint(
            x,
            y,
            Lerp(p1.Pressure, p2.Pressure, t),
            Lerp(p1.TiltX, p2.TiltX, t),
            Lerp(p1.TiltY, p2.TiltY, t),
            (int)MathF.Round(Lerp(p1.TimeMs, p2.TimeMs, t)));
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
