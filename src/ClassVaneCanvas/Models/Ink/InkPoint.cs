namespace ClassVaneCanvas.Models.Ink;

/// <summary>
/// 笔迹采样点（页面坐标系，单位为页面逻辑像素）。
/// 由输入设备直接采集：压感、笔尖倾斜角度，均为原始归一化值。
/// </summary>
/// <param name="X">横坐标（页面坐标系）。</param>
/// <param name="Y">纵坐标（页面坐标系）。</param>
/// <param name="Pressure">压感，0~1；不支持压感的设备为 0.5。</param>
/// <param name="TiltX">笔尖倾斜 X 分量，-1~1（左负右正）。</param>
/// <param name="TiltY">笔尖倾斜 Y 分量，-1~1（上负下正）。</param>
/// <param name="TimeMs">相对本笔起始时刻的毫秒数（用于平滑与预判补点）。</param>
public readonly record struct InkPoint(
    float X,
    float Y,
    float Pressure,
    float TiltX,
    float TiltY,
    int TimeMs)
{
    /// <summary>点与点的距离。</summary>
    public float DistanceTo(in InkPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
