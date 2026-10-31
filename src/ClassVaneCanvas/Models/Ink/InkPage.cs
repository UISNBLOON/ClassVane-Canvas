namespace ClassVaneCanvas.Models.Ink;

/// <summary>一页幻灯片上的全部笔迹（每页批注独立存储）。</summary>
public sealed class InkPage
{
    /// <summary>页码（1 起，与幻灯片序号一致）。</summary>
    public int PageIndex { get; set; }

    /// <summary>本页笔迹，按创建顺序排列（渲染顺序即此顺序）。</summary>
    public List<InkStroke> Strokes { get; set; } = new();

    /// <summary>本页是否有笔迹。</summary>
    public bool HasInk => Strokes.Count > 0;

    /// <summary>深拷贝。</summary>
    public InkPage Clone() => new()
    {
        PageIndex = PageIndex,
        Strokes = Strokes.Select(s => s.Clone()).ToList(),
    };
}
