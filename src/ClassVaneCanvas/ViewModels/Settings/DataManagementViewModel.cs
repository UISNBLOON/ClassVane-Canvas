using ClassVaneCanvas.Annotations;
using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Services;
using ClassVaneCanvas.Storage;
using ClassVaneCanvas.ViewModels.Settings.Options;

namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>
/// 数据管理：本机播放进度与批注数据分别清理、批注包导入导出。
/// 两条数据链在本机物理隔离，任何一侧的清理都不会影响另一侧。
/// </summary>
public sealed class DataManagementViewModel : SettingsOptionsPageViewModel
{
    private readonly AppPaths _paths;
    private readonly IPlaybackProgressStore _progressStore;
    private readonly IAnnotationStore _annotationStore;
    private readonly IAnnotationPackageCodec _codec;
    private readonly IFileDialogService _fileDialogs;
    private readonly IConfirmService _confirm;

    private readonly InfoOptionViewModel _progressInfo;
    private readonly InfoOptionViewModel _annotationInfo;
    private readonly InfoOptionViewModel _status;

    public DataManagementViewModel(
        AppPaths paths,
        IPlaybackProgressStore progressStore,
        IAnnotationStore annotationStore,
        IAnnotationPackageCodec codec,
        IFileDialogService fileDialogs,
        IConfirmService confirm)
        : base("数据管理", "本机播放进度与批注数据分开清理")
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _annotationStore = annotationStore ?? throw new ArgumentNullException(nameof(annotationStore));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));

        // 信息项先建好（按钮回调里会更新它们），再按视觉顺序加入页面
        _progressInfo = new InfoOptionViewModel("当前记录", DescribeProgress());
        _annotationInfo = new InfoOptionViewModel("批注库", DescribeLibrary());
        _status = new InfoOptionViewModel("最近操作", "—");

        // ---------------------------------------------------------- 播放进度
        Add(new SectionOptionViewModel("本机播放进度记录", "只存在本机，绝不参与任何同步"));

        Options.Add(_progressInfo);

        Add(new ButtonOptionViewModel(
            "清除本机全部播放进度记录",
            async () =>
            {
                if (!await _confirm.ConfirmAsync(
                        "清除播放进度",
                        "将删除本机记录的全部课件播放进度，此操作不影响任何批注文件。是否继续？",
                        "清除进度",
                        "取消"))
                {
                    return;
                }

                var removed = _progressStore.ClearAll();
                _progressInfo.Value = DescribeProgress();
                _status.Value = $"已清除 {removed} 条本机播放进度记录，批注文件未受影响。";
            },
            "只删除 playback-progress.db 中的记录，不碰 .cvcanno 批注文件。",
            isDanger: true));

        // ---------------------------------------------------------- 批注
        Add(new SectionOptionViewModel("批注数据", "全部存放在独立的 .cvcanno 批注包里"));

        Options.Add(_annotationInfo);

        Add(new ButtonOptionViewModel(
            "清除本地批注库缓存",
            async () =>
            {
                if (!await _confirm.ConfirmAsync(
                        "清除本地批注库",
                        "将删除本机批注库目录中的全部 .cvcanno 文件（同目录模式的批注不受影响），此操作不影响播放进度记录。是否继续？",
                        "清除批注库",
                        "取消"))
                {
                    return;
                }

                var removed = 0;
                foreach (var file in _annotationStore.ListLibraryFiles())
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException)
                    {
                        // 单个文件被占用时跳过，继续清理其余文件
                    }
                }

                _annotationInfo.Value = DescribeLibrary();
                _status.Value = $"已清除 {removed} 个批注包，播放进度记录未受影响。";
            },
            "只清理批注库目录，不碰播放进度数据库。",
            isDanger: true));

        Add(new ButtonOptionViewModel(
            "导入 .cvcanno 批注包",
            async () =>
            {
                var source = await _fileDialogs.PickOpenFileAsync("选择批注包", CvcannoFormat.Extension);
                if (string.IsNullOrWhiteSpace(source))
                {
                    return;
                }

                try
                {
                    var document = _codec.ReadFile(source);

                    // 导入统一进批注库（按课件哈希命名），避免覆盖同目录批注
                    var libraryStore = new AnnotationStore(_paths, AnnotationStorageMode.CentralLibrary, _codec);
                    libraryStore.Save(document.CoursewareFileName, document);

                    _annotationInfo.Value = DescribeLibrary();
                    _status.Value = $"已导入 {Path.GetFileName(source)}（课件：{document.CoursewareFileName}，共 {document.TotalStrokeCount} 条笔迹）。";
                }
                catch (Exception ex)
                {
                    _status.Value = $"导入失败：{ex.Message}";
                }
            },
            "导入后按课件哈希存入批注库，打开对应课件时自动加载。"));

        Add(new ButtonOptionViewModel(
            "导出批注库中的 .cvcanno",
            async () =>
            {
                var libraryFiles = _annotationStore.ListLibraryFiles();
                if (libraryFiles.Count == 0)
                {
                    _status.Value = "批注库中还没有可导出的批注包。";
                    return;
                }

                var source = libraryFiles[0];
                if (libraryFiles.Count > 1)
                {
                    var picked = await _fileDialogs.PickOpenFileAsync(
                        "选择要导出的批注包（默认取第一个）",
                        CvcannoFormat.Extension);
                    if (!string.IsNullOrWhiteSpace(picked))
                    {
                        source = picked;
                    }
                }

                var target = await _fileDialogs.PickSaveFileAsync(
                    "导出批注包",
                    Path.GetFileName(source),
                    CvcannoFormat.Extension);

                if (string.IsNullOrWhiteSpace(target))
                {
                    return;
                }

                try
                {
                    var document = _codec.ReadFile(source);
                    _codec.WriteFile(target, document);
                    _status.Value = $"已导出到 {target}。";
                }
                catch (Exception ex)
                {
                    _status.Value = $"导出失败：{ex.Message}";
                }
            },
            "导出后可以拷贝到其它电脑，或作为课件批注的备份。"));

        Options.Add(_status);

        Add(new SectionOptionViewModel("数据位置", "便于备份与排查"));

        Add(new InfoOptionViewModel("数据根目录", _paths.Root));

        Add(new InfoOptionViewModel("播放进度数据库", Path.GetFileName(_paths.PlaybackProgressDatabase)));

        Add(new InfoOptionViewModel("批注库目录", _paths.AnnotationLibraryDirectory));
    }

    private string DescribeProgress()
    {
        var count = _progressStore.Count;
        return count == 0 ? "暂无记录" : $"{count} 份课件";
    }

    private string DescribeLibrary()
    {
        var files = _annotationStore.ListLibraryFiles();
        return files.Count == 0 ? "空" : $"{files.Count} 个批注包";
    }
}
