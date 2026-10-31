namespace ClassVaneCanvas.Ink;

/// <summary>
/// 压感响应曲线（对齐希沃白板 5 的手感特征）：
///
/// · 轻压极细：压力低于死区时按最小宽度渲染，但不会细到看不见；
/// · 压力上升平缓加粗：先用 smoothstep 做 S 形过渡，再套 <see cref="InkEngineSettings.PressureGamma"/>，
///   中段加粗明显、两端平缓，避免「一按就粗」；
/// · 抑制微小抖动：由 <see cref="SmoothPressure"/> 做低通 + 死区，曲线本身保持单调，便于推理与测试。
/// </summary>
public static class PressureCurve
{
    /// <summary>无压感设备（鼠标、部分触摸屏）的默认压力。</summary>
    public const float DefaultPressure = 0.5f;

    /// <summary>压力死区下限，低于此值都不再变细。</summary>
    private const float MinPressure = 0.01f;

    /// <summary>
    /// 把归一化压力映射为宽度系数，返回值范围 [MinWidthFactor, 1]。
    /// </summary>
    public static float Evaluate(float pressure, InkEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var deadZone = Math.Clamp(settings.PressureDeadZone, 0f, 0.9f);
        var p = Normalize(pressure);

        // 死区：轻压统一按最小有效压力处理（轻压极细，且不会抖动）
        if (p < deadZone)
        {
            p = deadZone;
        }

        // 归一化到 (0,1]
        var t = (p - deadZone) / (1f - deadZone);
        t = Math.Clamp(t, 0f, 1f);

        // smoothstep：两端平缓、中段明显
        var s = t * t * (3f - (2f * t));

        // 曲线指数：>1 时轻压更细、重压更粗
        var gamma = Math.Max(0.1f, settings.PressureGamma);
        var shaped = gamma == 1f ? s : MathF.Pow(s, gamma);

        var floor = Math.Clamp(settings.MinWidthFactor, 0.02f, 1f);
        return floor + ((1f - floor) * shaped);
    }

    /// <summary>
    /// 压感低通 + 死区：抑制微小压力抖动造成的笔触跳动。
    /// </summary>
    /// <param name="previous">上一个已平滑压力（首点传 <see cref="float.NaN"/> 表示无历史）。</param>
    /// <param name="current">本次原始压力。</param>
    public static float SmoothPressure(float previous, float current, InkEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var value = Normalize(current);
        if (float.IsNaN(previous))
        {
            return value;
        }

        // 死区：变化太小就认为没有变化，避免笔触边缘毛刺
        if (MathF.Abs(value - previous) < 0.008f)
        {
            return previous;
        }

        // 一阶低通：保留 15% 以上的跟随性，保证跟手不迟钝
        var strength = Math.Clamp(settings.PressureSmoothing, 0f, 1f);
        var alpha = 1f - (strength * 0.85f);
        return previous + ((value - previous) * alpha);
    }

    /// <summary>把设备压力归一化到 [0,1]；0 或非法值按默认压力处理。</summary>
    public static float Normalize(float pressure)
    {
        if (float.IsNaN(pressure) || pressure <= MinPressure)
        {
            return DefaultPressure;
        }

        return Math.Clamp(pressure, 0f, 1f);
    }

    /// <summary>
    /// 计算「笔触粗细上下限」约束下的最终线宽（页面逻辑像素）。
    /// </summary>
    public static float ResolveWidth(float baseWidth, float pressure, InkEngineSettings settings)
    {
        var factor = Evaluate(pressure, settings);
        var width = baseWidth * factor;
        return Math.Clamp(width, settings.MinStrokeWidth, settings.MaxStrokeWidth);
    }
}
