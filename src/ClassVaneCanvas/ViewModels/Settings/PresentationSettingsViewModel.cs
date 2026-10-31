using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Services;
using ClassVaneCanvas.Storage;
using ClassVaneCanvas.Sync;
using ClassVaneCanvas.ViewModels.Settings.Options;

namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>放映设置：播放进度提示、批注文件策略、WebDAV 自持同步、冲突默认策略。</summary>
public sealed class PresentationSettingsViewModel : SettingsOptionsPageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IPlaybackProgressStore _progressStore;
    private readonly IAnnotationStore _annotationStore;
    private readonly SyncServiceFactory _syncFactory;

    private readonly InfoOptionViewModel _progressInfo;
    private readonly InfoOptionViewModel _storageInfo;
    private readonly InfoOptionViewModel _syncStatus;
    private readonly List<TextOptionViewModel> _webDavFields = new();

    public PresentationSettingsViewModel(
        ISettingsService settings,
        IPlaybackProgressStore progressStore,
        IAnnotationStore annotationStore,
        SyncServiceFactory syncFactory)
        : base("放映设置", "播放进度、批注文件策略与批注同步")
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _annotationStore = annotationStore ?? throw new ArgumentNullException(nameof(annotationStore));
        _syncFactory = syncFactory ?? throw new ArgumentNullException(nameof(syncFactory));

        var current = _settings.Current;

        void Commit() => _settings.NotifyChanged();

        // ---------------------------------------------------------- 播放进度
        Add(new SectionOptionViewModel("播放进度（仅本机）"));

        Add(new ToggleOptionViewModel(
            "打开文件时提示恢复本机上次播放位置",
            current.PromptResumePlayback,
            value =>
            {
                current.PromptResumePlayback = value;
                Commit();
            },
            "进度只保存在本机独立数据库，不跨设备同步、不写入批注文件；换电脑打开同一课件不会提示。"));
        _progressInfo = Add(new InfoOptionViewModel(
            "本机进度记录",
            DescribeProgressCount(),
            "清理批注不会删除进度；清理进度也不会删除批注。"));

        // ---------------------------------------------------------- 批注文件
        Add(new SectionOptionViewModel("批注文件策略"));

        Add(new ChoiceOptionViewModel(
            "批注存放方式",
            new[]
            {
                new ChoiceItem(
                    AnnotationStorageMode.SidecarNextToFile,
                    "方式 A：PPT 同目录自动生成同名 .cvcanno"),
                new ChoiceItem(
                    AnnotationStorageMode.CentralLibrary,
                    "方式 B：统一批注库（可配合同步）"),
            },
            current.AnnotationStorageMode,
            value =>
            {
                current.AnnotationStorageMode = (AnnotationStorageMode)value;
                _annotationStore.Mode = current.AnnotationStorageMode;
                Commit();
                RefreshStorageInfo();
            },
            "方式 A 最贴近本地批注体验：拷贝 U 盘时把 pptx 与配套 cvcanno 一起复制即可。" +
            "切换方式不会自动搬运已有批注：旧位置的 .cvcanno 仍与课件配套，可用「数据管理 → 导入 .cvcanno」搬进批注库。"));

        _storageInfo = Add(new InfoOptionViewModel(
            "批注存放位置",
            string.Empty,
            "批注通过 PPT 文件 SHA256 与课件绑定，哈希一致才会加载，避免同名不同课件串批注。"));

        // ---------------------------------------------------------- 同步
        Add(new SectionOptionViewModel("批注同步（WebDAV 自持）"));

        Add(new ToggleOptionViewModel(
            "启用批注同步",
            current.SyncEnabled,
            value =>
            {
                current.SyncEnabled = value;
                Commit();
                RefreshSyncFieldState();
            },
            "默认关闭。只支持老师自己的 WebDAV 服务器，不连接任何第三方公共服务器。"));

        _webDavFields.Add(Add(new TextOptionViewModel(
            "服务器地址",
            current.WebDavUrl,
            value =>
            {
                current.WebDavUrl = value;
                Commit();
            },
            "例如 https://nas.local/dav/classvane",
            "https://…")));

        _webDavFields.Add(Add(new TextOptionViewModel(
            "账号",
            current.WebDavUserName,
            value =>
            {
                current.WebDavUserName = value;
                Commit();
            },
            null,
            "username")));

        _webDavFields.Add(Add(new TextOptionViewModel(
            "密码",
            current.WebDavPassword,
            value =>
            {
                current.WebDavPassword = value;
                Commit();
            },
            "密码只保存在本机设置文件，不会写入批注文件。",
            "password",
            isPassword: true)));

        Add(new ChoiceOptionViewModel(
            "自动同步间隔",
            new[]
            {
                new ChoiceItem(5, "每 5 分钟"),
                new ChoiceItem(15, "每 15 分钟"),
                new ChoiceItem(30, "每 30 分钟"),
                new ChoiceItem(60, "每 60 分钟"),
                new ChoiceItem(0, "仅手动同步"),
            },
            current.SyncIntervalMinutes,
            value =>
            {
                current.SyncIntervalMinutes = (int)value;
                Commit();
            }));

        Add(new ChoiceOptionViewModel(
            "冲突默认处理",
            new[]
            {
                new ChoiceItem(AnnotationConflictResolution.Merge, "合并笔迹（推荐）"),
                new ChoiceItem(AnnotationConflictResolution.KeepLocal, "保留本地"),
                new ChoiceItem(AnnotationConflictResolution.KeepRemote, "保留远端"),
            },
            current.DefaultConflictResolution,
            value =>
            {
                current.DefaultConflictResolution = (AnnotationConflictResolution)value;
                Commit();
            },
            "同一课件在多台设备上都被修改时生效；也可以每次都弹窗手动选择。"));

        _syncStatus = Add(new InfoOptionViewModel("同步状态", "未同步", "同步只传输 .cvcanno 批注文件，永远不同步本机播放进度。"));

        Add(new ButtonOptionViewModel(
            "测试连接",
            async () =>
            {
                var service = _syncFactory.Create();
                if (service is null)
                {
                    _syncStatus.Value = "请先启用同步并填写服务器地址（地址必须是 http/https）。";
                    return;
                }

                _syncStatus.Value = "正在测试连接…";
                var report = await service.SyncAsync().ConfigureAwait(true);
                _syncStatus.Value = report.Succeeded
                    ? $"连接成功：上传 {report.Uploaded}、下载 {report.Downloaded}、冲突 {report.Conflicts}、跳过 {report.Skipped}。"
                    : $"连接失败：{report.Error}";
            },
            "会同时执行一次同步，便于确认账号权限。"));

        Add(new ButtonOptionViewModel(
            "立即同步批注",
            async () =>
            {
                var service = _syncFactory.Create();
                if (service is null)
                {
                    _syncStatus.Value = "请先启用同步并填写服务器地址。";
                    return;
                }

                _syncStatus.Value = "正在同步…";
                var report = await service.SyncAsync().ConfigureAwait(true);
                _syncStatus.Value = report.Succeeded
                    ? $"同步完成：上传 {report.Uploaded}、下载 {report.Downloaded}、冲突 {report.Conflicts}、跳过 {report.Skipped}。"
                    : $"同步失败：{report.Error}";
            },
            isPrimary: true));

        RefreshStorageInfo();
        RefreshSyncFieldState();
    }

    /// <summary>进度条数文本（数据管理页清理后重新进入会刷新）。</summary>
    public void RefreshProgressInfo() => _progressInfo.Value = DescribeProgressCount();

    private string DescribeProgressCount()
    {
        var count = _progressStore.Count;
        return count == 0 ? "暂无记录" : $"{count} 份课件";
    }

    private void RefreshStorageInfo()
        => _storageInfo.Value = _annotationStore.Mode == AnnotationStorageMode.SidecarNextToFile
            ? "与 PPT 同目录、同名（语文课件.pptx → 语文课件.cvcanno）"
            : $"统一批注库目录：{_annotationStore.ListLibraryFiles().Count} 个批注包";

    private void RefreshSyncFieldState()
    {
        var enabled = _settings.Current.SyncEnabled;
        foreach (var field in _webDavFields)
        {
            field.IsEnabled = enabled;
        }
    }
}
