namespace ClassVaneCanvas.Storage;

/// <summary>
/// 本机数据目录布局。三条硬性隔离约定（不要在别处另建目录）：
///
/// 1. 播放进度只存在于 <see cref="PlaybackProgressDatabase"/>（本机 SQLite），
///    绝不写入批注文件，也绝不参与任何同步；
/// 2. 批注只存在于 &lt;课件同目录&gt;*.cvcanno 或 <see cref="AnnotationLibraryDirectory"/>，
///    与播放进度库物理隔离，互相清理不受影响；
/// 3. 幻灯片渲染缓存可以随时删除重建，不承载任何用户数据。
/// </summary>
public sealed class AppPaths
{
    /// <summary>应用数据目录名（位于系统 ApplicationData 之下）。</summary>
    public const string ApplicationDirectoryName = "ClassVaneCanvas";

    /// <summary>本机播放进度数据库文件名。</summary>
    public const string PlaybackProgressDatabaseName = "playback-progress.db";

    /// <summary>批注库目录名（批注库模式使用）。</summary>
    public const string AnnotationLibraryDirectoryName = "annotations";

    /// <summary>同步状态文件名（记录上次同步的哈希 / ETag，供冲突检测）。</summary>
    public const string SyncStateFileName = "sync-state.json";

    /// <summary>全局设置文件名。</summary>
    public const string SettingsFileName = "settings.json";

    /// <param name="rootDirectory">数据根目录；传 <c>null</c> 使用系统默认位置（测试时可指定临时目录）。</param>
    public AppPaths(string? rootDirectory = null)
        => Root = string.IsNullOrWhiteSpace(rootDirectory) ? GetDefaultRoot() : Path.GetFullPath(rootDirectory);

    /// <summary>数据根目录。</summary>
    public string Root { get; }

    /// <summary>全局设置文件。</summary>
    public string SettingsFile => Path.Combine(Root, SettingsFileName);

    /// <summary>本机播放进度数据库（独立 SQLite，仅本机）。</summary>
    public string PlaybackProgressDatabase => Path.Combine(Root, PlaybackProgressDatabaseName);

    /// <summary>统一批注库目录（批注库模式）。</summary>
    public string AnnotationLibraryDirectory => Path.Combine(Root, AnnotationLibraryDirectoryName);

    /// <summary>同步状态文件。</summary>
    public string SyncStateFile => Path.Combine(Root, SyncStateFileName);

    /// <summary>幻灯片渲染缓存目录（可随时清理）。</summary>
    public string SlideCacheDirectory => Path.Combine(Root, "slide-cache");

    /// <summary>创建所有需要的目录。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AnnotationLibraryDirectory);
        Directory.CreateDirectory(SlideCacheDirectory);
    }

    /// <summary>系统默认数据根目录。</summary>
    public static string GetDefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ApplicationDirectoryName);
}
