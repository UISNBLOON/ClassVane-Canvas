using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Storage;
using ClassVaneCanvas.Sync;

namespace ClassVaneCanvas.Services;

/// <summary>
/// 按当前设置创建批注同步服务（WebDAV 自持服务器，默认关闭）。
/// 同步只涉及 <c>*.cvcanno</c>，播放进度数据库不在同步范围内。
/// </summary>
public sealed class SyncServiceFactory
{
    private readonly AppPaths _paths;
    private readonly IAnnotationPackageCodec _codec;
    private readonly IAnnotationMerger _merger;
    private readonly ISettingsService _settings;

    public SyncServiceFactory(
        AppPaths paths,
        IAnnotationPackageCodec codec,
        IAnnotationMerger merger,
        ISettingsService settings)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>是否已经配置好 WebDAV 地址。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.WebDavUrl);

    /// <summary>
    /// 创建同步服务；未配置地址或未开启同步时返回 <c>null</c>。
    /// 冲突默认策略为「合并」时自动合并，否则交给老师裁决。
    /// </summary>
    public IAnnotationSyncService? Create()
    {
        var current = _settings.Current;
        if (!current.SyncEnabled || string.IsNullOrWhiteSpace(current.WebDavUrl))
        {
            return null;
        }

        IAnnotationSyncTarget target;
        try
        {
            target = new WebDavSyncTarget(current.WebDavUrl, current.WebDavUserName, current.WebDavPassword);
        }
        catch (ArgumentException)
        {
            // 地址非法：交给设置页提示，不影响其它功能
            return null;
        }

        return new AnnotationSyncService(
            _paths,
            _codec,
            _merger,
            target,
            new SyncStateStore(_paths),
            enabled: true,
            defaultResolution: current.DefaultConflictResolution);
    }
}
