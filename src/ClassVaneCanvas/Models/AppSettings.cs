using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Sync;

namespace ClassVaneCanvas.Models;

/// <summary>
/// 全局设置（持久化到本机 <c>settings.json</c>）。
///
/// 注意：WebDAV 账号密码只保存在本机设置文件里，
/// **不会**被写入批注文件，也不参与批注同步。
/// </summary>
public sealed class AppSettings
{
    // ---------------------------------------------------------- 放映设置

    /// <summary>打开文件时提示恢复本机上次播放位置（总开关）。</summary>
    public bool PromptResumePlayback { get; set; } = true;

    /// <summary>批注文件策略：同目录自动生成 / 统一批注库。</summary>
    public AnnotationStorageMode AnnotationStorageMode { get; set; } = AnnotationStorageMode.SidecarNextToFile;

    /// <summary>批注同步开关（默认关闭，只支持 WebDAV 自持服务器）。</summary>
    public bool SyncEnabled { get; set; }

    /// <summary>WebDAV 服务器地址。</summary>
    public string WebDavUrl { get; set; } = string.Empty;

    /// <summary>WebDAV 账号。</summary>
    public string WebDavUserName { get; set; } = string.Empty;

    /// <summary>WebDAV 密码（仅本机保存）。</summary>
    public string WebDavPassword { get; set; } = string.Empty;

    /// <summary>自动同步间隔（分钟）。</summary>
    public int SyncIntervalMinutes { get; set; } = 30;

    /// <summary>批注冲突默认处理策略。</summary>
    public AnnotationConflictResolution DefaultConflictResolution { get; set; } = AnnotationConflictResolution.Merge;

    /// <summary>
    /// 已选择「不再提示此文件」的课件（存课件 SHA256 小写十六进制）。
    /// 只影响「动画与字体兼容性提示」，不影响放映与板书。
    /// </summary>
    public HashSet<string> SuppressedCompatibilityWarnings { get; set; } = new(StringComparer.Ordinal);

    // ---------------------------------------------------------- 书写设置

    /// <summary>笔迹平滑强度。</summary>
    public float InkSmoothing { get; set; } = 0.55f;

    /// <summary>压感响应曲线。</summary>
    public float PressureGamma { get; set; } = 1.35f;

    /// <summary>压感低通强度（抑制压力抖动）。</summary>
    public float PressureSmoothing { get; set; } = 0.45f;

    /// <summary>笔触粗细下限（页面逻辑像素）。</summary>
    public float MinStrokeWidth { get; set; } = 0.9f;

    /// <summary>笔触粗细上限（页面逻辑像素）。</summary>
    public float MaxStrokeWidth { get; set; } = 26f;

    /// <summary>荧光笔透明度。</summary>
    public float HighlighterOpacity { get; set; } = 0.3f;

    /// <summary>马克笔透明度。</summary>
    public float MarkerOpacity { get; set; } = 0.55f;

    /// <summary>是否允许手指书写（关闭时手指只做手势）。</summary>
    public bool FingerDraws { get; set; }

    // ---------------------------------------------------------- 手势设置

    /// <summary>单指滑动是否翻页。</summary>
    public bool SwipeTurnsPage { get; set; } = true;

    /// <summary>翻页滑动判定阈值（逻辑像素）。</summary>
    public float SwipeTurnThreshold { get; set; } = 72f;

    /// <summary>双指缩放灵敏度倍率。</summary>
    public float PinchSensitivity { get; set; } = 1f;

    /// <summary>手写笔落笔期间是否忽略触摸（手掌抑制 / 防误触）。</summary>
    public bool IgnoreTouchWhilePenDown { get; set; } = true;

    /// <summary>本机设备标识（首次运行时生成，写入笔迹用于冲突排查）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>缺少设备标识时补齐。</summary>
    public void EnsureDeviceId()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
        {
            DeviceId = Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>把设置同步进笔迹引擎参数（老师调完手感立即生效）。</summary>
    public void ApplyTo(InkEngineSettings ink)
    {
        ArgumentNullException.ThrowIfNull(ink);

        ink.SmoothingStrength = InkSmoothing;
        ink.PressureGamma = PressureGamma;
        ink.PressureSmoothing = PressureSmoothing;
        ink.MinStrokeWidth = MinStrokeWidth;
        ink.MaxStrokeWidth = MaxStrokeWidth;
        ink.HighlighterOpacity = HighlighterOpacity;
        ink.MarkerOpacity = MarkerOpacity;
        ink.FingerDraws = FingerDraws;
        ink.SwipeTurnsPage = SwipeTurnsPage;
        ink.SwipeTurnThreshold = SwipeTurnThreshold;
        ink.PinchSensitivity = PinchSensitivity;
        ink.IgnoreTouchWhilePenDown = IgnoreTouchWhilePenDown;
        ink.MarkChanged();
    }

    /// <summary>从笔迹引擎参数回写设置（用于「恢复默认」后同步）。</summary>
    public void CopyFrom(InkEngineSettings ink)
    {
        ArgumentNullException.ThrowIfNull(ink);

        InkSmoothing = ink.SmoothingStrength;
        PressureGamma = ink.PressureGamma;
        PressureSmoothing = ink.PressureSmoothing;
        MinStrokeWidth = ink.MinStrokeWidth;
        MaxStrokeWidth = ink.MaxStrokeWidth;
        HighlighterOpacity = ink.HighlighterOpacity;
        MarkerOpacity = ink.MarkerOpacity;
        FingerDraws = ink.FingerDraws;
        SwipeTurnsPage = ink.SwipeTurnsPage;
        SwipeTurnThreshold = ink.SwipeTurnThreshold;
        PinchSensitivity = ink.PinchSensitivity;
        IgnoreTouchWhilePenDown = ink.IgnoreTouchWhilePenDown;
    }
}
