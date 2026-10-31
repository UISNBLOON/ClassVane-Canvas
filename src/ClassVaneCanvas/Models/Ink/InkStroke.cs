namespace ClassVaneCanvas.Models.Ink;

/// <summary>
/// 一条矢量笔迹。坐标全部位于**页面坐标系**，
/// 渲染时由画布变换矩阵统一缩放平移，因此在任意缩放 / 漫游状态下位置与形状都不变形。
/// </summary>
public sealed class InkStroke
{
    /// <summary>笔迹唯一 Id（GUID 字符串）。跨设备冲突合并时按 Id 去重，务必稳定。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>笔触类型。</summary>
    public InkTool Tool { get; set; } = InkTool.HardPen;

    /// <summary>颜色（ARGB，例如 0xFFE53935）。</summary>
    public uint ColorArgb { get; set; } = 0xFF1B1D21;

    /// <summary>基准笔宽（页面坐标系；实际粗细再乘压感系数）。</summary>
    public float BaseWidth { get; set; } = 3f;

    /// <summary>创建时间（UTC Ticks），用于合并后的稳定排序。</summary>
    public long CreatedUtcTicks { get; set; }

    /// <summary>产生这条笔迹的设备标识，冲突排查时区分来源。</summary>
    public string OriginDeviceId { get; set; } = string.Empty;

    /// <summary>采样点序列（原始输入点，平滑在渲染阶段进行）。</summary>
    public List<InkPoint> Points { get; set; } = new();

    /// <summary>新建一条笔迹并补齐 Id / 时间戳 / 设备标识。</summary>
    public static InkStroke Create(
        InkTool tool,
        uint colorArgb,
        float baseWidth,
        IEnumerable<InkPoint> points,
        string deviceId)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Tool = tool,
            ColorArgb = colorArgb,
            BaseWidth = baseWidth,
            Points = points.ToList(),
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            OriginDeviceId = deviceId,
        };

    /// <summary>深拷贝（撤销栈与合并时使用，避免共享可变集合）。</summary>
    public InkStroke Clone() => new()
    {
        Id = Id,
        Tool = Tool,
        ColorArgb = ColorArgb,
        BaseWidth = BaseWidth,
        Points = new List<InkPoint>(Points),
        CreatedUtcTicks = CreatedUtcTicks,
        OriginDeviceId = OriginDeviceId,
    };
}
