using Avalonia.Media;
using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Ink;

/// <summary>
/// 笔迹几何缓存：按笔迹 Id 缓存已构建的几何，避免每帧重建。
/// 参数指纹变化（老师调手感滑杆）或调用 <see cref="Clear"/> 时整体失效。
/// </summary>
public sealed class InkGeometryCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _fingerprint = int.MinValue;

    /// <summary>缓存条目数。</summary>
    public int Count => _entries.Count;

    /// <summary>取几何；未命中则构建并缓存。返回 null 表示该笔迹不产生几何（橡皮擦/空点）。</summary>
    public (Geometry Geometry, bool Stroked)? Get(InkStroke stroke, InkEngineSettings settings)
    {
        var fingerprint = ComputeFingerprint(settings);
        if (fingerprint != _fingerprint)
        {
            _entries.Clear();
            _fingerprint = fingerprint;
        }

        if (_entries.TryGetValue(stroke.Id, out var entry))
        {
            return entry.Geometry is null ? null : (entry.Geometry, entry.Stroked);
        }

        var geometry = InkGeometryBuilder.Build(stroke, settings);
        var stroked = stroke.Tool == InkTool.Highlighter;
        _entries[stroke.Id] = new Entry(geometry, stroked);

        return geometry is null ? null : (geometry, stroked);
    }

    /// <summary>让某条笔迹的缓存失效（例如笔迹被修改）。</summary>
    public void Invalidate(string strokeId) => _entries.Remove(strokeId);

    /// <summary>清空缓存。</summary>
    public void Clear()
    {
        _entries.Clear();
        _fingerprint = int.MinValue;
    }

    /// <summary>
    /// 影响几何形态的参数指纹。用指纹而不是自增版本号，
    /// 即使调用方忘记标记变更也不会渲染出错。
    /// </summary>
    private static int ComputeFingerprint(InkEngineSettings settings)
    {
        var hash = new HashCode();
        hash.Add(settings.SmoothingStrength);
        hash.Add(settings.PressureGamma);
        hash.Add(settings.PressureDeadZone);
        hash.Add(settings.PressureSmoothing);
        hash.Add(settings.MinWidthFactor);
        hash.Add(settings.MinStrokeWidth);
        hash.Add(settings.MaxStrokeWidth);
        hash.Add(settings.MarkerFlatness);
        hash.Add(settings.MarkerDefaultNibAngle);
        hash.Add(settings.ResampleStep);
        hash.Add(settings.MinPointDistance);
        hash.Add(settings.PredictionLookaheadMs);
        return hash.ToHashCode();
    }

    private readonly record struct Entry(Geometry? Geometry, bool Stroked);
}
