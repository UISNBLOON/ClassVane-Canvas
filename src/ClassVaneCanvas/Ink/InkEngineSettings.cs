namespace ClassVaneCanvas.Ink;

/// <summary>
/// 自研笔迹引擎的可调参数（对应「书写设置」页，专门用于调希沃白板 5 的笔锋手感）。
/// 这些参数只影响**渲染与手感**，不改动已保存笔迹的原始采样点。
/// </summary>
public sealed class InkEngineSettings
{
    /// <summary>轨迹平滑强度，0~1。越大越顺滑（移动平均窗口越大）。</summary>
    public float SmoothingStrength { get; set; } = 0.55f;

    /// <summary>压感响应曲线指数。1 = 线性；&gt;1 更强调轻压细、重压粗。</summary>
    public float PressureGamma { get; set; } = 1.35f;

    /// <summary>压感死区：低于该值按最小压力处理，避免轻触时笔迹忽粗忽细。</summary>
    public float PressureDeadZone { get; set; } = 0.06f;

    /// <summary>压感低通强度，0~1。用于抑制压力抖动造成的笔触跳动。</summary>
    public float PressureSmoothing { get; set; } = 0.45f;

    /// <summary>压感曲线的宽度下限系数（相对基准笔宽）。轻压时的最细程度。</summary>
    public float MinWidthFactor { get; set; } = 0.16f;

    /// <summary>笔触粗细绝对下限（页面逻辑像素）。</summary>
    public float MinStrokeWidth { get; set; } = 0.9f;

    /// <summary>笔触粗细绝对上限（页面逻辑像素）。</summary>
    public float MaxStrokeWidth { get; set; } = 26f;

    /// <summary>荧光笔透明度，0~1（越高越不透明；默认高透明，不遮挡底层 PPT）。</summary>
    public float HighlighterOpacity { get; set; } = 0.3f;

    /// <summary>马克笔透明度，0~1。</summary>
    public float MarkerOpacity { get; set; } = 0.55f;

    /// <summary>马克笔斜锋扁平度（0~1，越小越扁）。笔尖倾斜越大越接近该值。</summary>
    public float MarkerFlatness { get; set; } = 0.34f;

    /// <summary>无倾斜信息时马克笔的默认笔锋角度（度）。</summary>
    public float MarkerDefaultNibAngle { get; set; } = 135f;

    /// <summary>是否允许手指书写（关闭时手指只做手势，笔始终书写）。</summary>
    public bool FingerDraws { get; set; }

    /// <summary>手写笔落笔期间是否忽略触摸（手掌抑制 / 防误触）。</summary>
    public bool IgnoreTouchWhilePenDown { get; set; } = true;

    /// <summary>单指滑动手势是否翻页。</summary>
    public bool SwipeTurnsPage { get; set; } = true;

    /// <summary>翻页滑动判定阈值（逻辑像素）。</summary>
    public float SwipeTurnThreshold { get; set; } = 72f;

    /// <summary>双指缩放手势灵敏度倍率。</summary>
    public float PinchSensitivity { get; set; } = 1f;

    /// <summary>手掌抑制：触摸点面积大于该值（相对值）时不参与书写。</summary>
    public float PalmRejectionThreshold { get; set; } = 1f;

    /// <summary>采样距离阈值（页面逻辑像素），过近的点会被合并以消除抖动。</summary>
    public float MinPointDistance { get; set; } = 0.7f;

    /// <summary>预判补点：插值步长（页面逻辑像素），越小越平滑、点越多。</summary>
    public float ResampleStep { get; set; } = 3.5f;

    /// <summary>跟手预判：实时绘制时沿速度方向外推的毫秒数（降低观感延迟）。</summary>
    public float PredictionLookaheadMs { get; set; } = 9f;

    /// <summary>参数版本号，每次修改后自增；几何缓存依赖它判断失效。</summary>
    public int Version { get; private set; }

    /// <summary>标记参数已变化（修改属性后调用）。</summary>
    public void MarkChanged() => Version++;

    /// <summary>拷贝一份（用于快照对比 / 还原默认值）。</summary>
    public InkEngineSettings Clone() => (InkEngineSettings)MemberwiseClone();
}
