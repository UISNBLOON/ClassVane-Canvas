using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Storage;

namespace ClassVaneCanvas.Annotations;

/// <summary>
/// 批注文件存储的默认实现：负责 <c>.cvcanno</c> 的定位、加载、保存、导入导出与清除。
///
/// 三条硬性隔离约定（与 <see cref="AppPaths"/> 的注释一致）：
/// · **绝不修改原始课件文件**：本类只读取课件路径用于定位与取名，从不打开课件写入；
/// · **绝不触碰播放进度库**：<see cref="Clear"/> 只删自己算出来的那一个批注文件；
/// · 批注文件与课件通过 <see cref="AnnotationDocument.CoursewareHash"/> 绑定，哈希不一致拒绝加载，
///   避免同名不同课件互相串批注。
/// </summary>
public sealed class AnnotationStore : IAnnotationStore
{
    private readonly AppPaths _paths;
    private readonly IAnnotationPackageCodec _codec;

    /// <param name="paths">本机数据目录布局。</param>
    /// <param name="mode">初始存放策略（可在运行时通过 <see cref="Mode"/> 切换）。</param>
    /// <param name="codec">批注包读写实现。</param>
    public AnnotationStore(AppPaths paths, AnnotationStorageMode mode, IAnnotationPackageCodec codec)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        Mode = mode;
    }

    /// <summary>
    /// 当前存放策略。接口只要求只读，这里做成可读写，方便老师在设置里随时切换
    /// （切换后 <see cref="Save"/> 会写到新位置，旧文件保留不动，避免误删老师已有的板书）。
    /// </summary>
    public AnnotationStorageMode Mode { get; set; }

    /// <inheritdoc />
    public AnnotationLocation Resolve(string coursewarePath, string coursewareHash)
    {
        var fullCoursewarePath = RequireCoursewarePath(coursewarePath);

        var path = Mode switch
        {
            // 方式 A：同目录同名，语文课件.pptx → 语文课件.cvcanno
            AnnotationStorageMode.SidecarNextToFile => Path.ChangeExtension(fullCoursewarePath, CvcannoFormat.Extension),

            // 方式 B：统一批注库，<库目录>/<sha256>.cvcanno
            AnnotationStorageMode.CentralLibrary => ComputeLibraryPath(coursewareHash),

            _ => throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "未知的批注存放策略。"),
        };

        return new AnnotationLocation(Mode, path, File.Exists(path));
    }

    /// <inheritdoc />
    public string ComputeLibraryPath(string coursewareHash)
    {
        if (!AnnotationHashRules.IsSha256Hex(coursewareHash))
        {
            throw new ArgumentException(
                "课件哈希必须是 64 位十六进制字符串（课件文件的 SHA256）。",
                nameof(coursewareHash));
        }

        return Path.Combine(
            _paths.AnnotationLibraryDirectory,
            AnnotationHashRules.Normalize(coursewareHash) + CvcannoFormat.Extension);
    }

    /// <inheritdoc />
    public AnnotationDocument Load(string coursewarePath, string coursewareHash)
    {
        var fullCoursewarePath = RequireCoursewarePath(coursewarePath);
        var location = Resolve(fullCoursewarePath, coursewareHash);
        var fileName = Path.GetFileName(fullCoursewarePath);

        // 还没有批注：返回与课件绑定的空文档，交给老师直接开始板书（不是错误，不抛异常）
        if (!location.Exists)
        {
            return AnnotationDocument.CreateFor(coursewareHash?.Trim() ?? string.Empty, fileName, 0, string.Empty);
        }

        var document = _codec.ReadFile(location.Path);

        // 哈希是唯一匹配依据：同名不同课件绝不能互相加载笔迹
        if (!string.Equals(document.CoursewareHash, coursewareHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"批注文件与当前课件不匹配：该批注绑定的是另一份课件（批注哈希 {Shorten(document.CoursewareHash)}，" +
                $"当前课件哈希 {Shorten(coursewareHash)}）。为避免串批注已停止加载，请确认课件文件是否被替换或改动过。");
        }

        return document;
    }

    /// <inheritdoc />
    public void Save(string coursewarePath, AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var fullCoursewarePath = RequireCoursewarePath(coursewarePath);
        var location = Resolve(fullCoursewarePath, document.CoursewareHash);

        // 防御：批注文件绝不允许落到课件文件本身上（例如误把 .cvcanno 当课件传入）
        if (string.Equals(location.Path, fullCoursewarePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"批注文件路径与课件文件路径相同（{fullCoursewarePath}），为避免覆盖课件已中止保存。");
        }

        document.ModifiedUtc = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(document.CoursewareFileName))
        {
            document.CoursewareFileName = Path.GetFileName(fullCoursewarePath);
        }

        // 只写批注文件；课件文件保持原样，绝不修改
        _codec.WriteFile(location.Path, document);
    }

    /// <inheritdoc />
    public bool Clear(string coursewarePath, string coursewareHash)
    {
        var location = Resolve(coursewarePath, coursewareHash);
        if (!File.Exists(location.Path))
        {
            return false;
        }

        // 只删这一个批注文件：播放进度库（本机 SQLite）与其它数据一律不动
        File.Delete(location.Path);
        return !File.Exists(location.Path);
    }

    /// <inheritdoc />
    public void Export(AnnotationDocument document, string targetFilePath)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            throw new ArgumentException("导出目标路径不能为空。", nameof(targetFilePath));
        }

        _codec.WriteFile(targetFilePath, document);
    }

    /// <inheritdoc />
    public AnnotationDocument Import(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new ArgumentException("导入文件路径不能为空。", nameof(sourceFilePath));
        }

        var fullPath = Path.GetFullPath(sourceFilePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到要导入的批注文件：{fullPath}", fullPath);
        }

        var document = _codec.ReadFile(fullPath);

        // 无论使用哪种 codec 实现，这里都再复核一次格式与版本，保证导入的确是本软件的批注包
        if (!string.Equals(document.Format, CvcannoFormat.Identifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"这不是 ClassVane Canvas 批注文件（格式标识为「{document.Format}」），无法导入。");
        }

        if (document.FormatVersion > CvcannoFormat.Version)
        {
            throw new InvalidDataException(
                $"批注文件版本（{document.FormatVersion}）高于当前软件支持的版本（{CvcannoFormat.Version}），" +
                "请升级 ClassVane Canvas 后再导入。");
        }

        return document;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListLibraryFiles()
    {
        var directory = _paths.AnnotationLibraryDirectory;

        // 目录还不存在（老师从未用过批注库模式）时返回空列表，不创建目录、不抛异常
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var files = Directory.GetFiles(directory, "*" + CvcannoFormat.Extension, SearchOption.TopDirectoryOnly);
        return files
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>校验并规整课件路径（只用于取名与定位，从不写入）。</summary>
    private static string RequireCoursewarePath(string coursewarePath)
    {
        if (string.IsNullOrWhiteSpace(coursewarePath))
        {
            throw new ArgumentException("课件文件路径不能为空。", nameof(coursewarePath));
        }

        return Path.GetFullPath(coursewarePath);
    }

    /// <summary>提示语里只显示哈希前 12 位，够老师核对又不至于刷屏。</summary>
    private static string Shorten(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return "空";
        }

        return hash.Length <= 12 ? hash : hash[..12] + "…";
    }
}
