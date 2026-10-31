using ClassVaneCanvas.Models.Annotations;

namespace ClassVaneCanvas.Annotations;

/// <summary>批注文件存放策略（老师在设置中自由选择）。</summary>
public enum AnnotationStorageMode
{
    /// <summary>方式 A：自动同目录模式，PPT 旁边生成同名 .cvcanno，拷贝 U 盘时一起带走。</summary>
    SidecarNextToFile,

    /// <summary>方式 B：统一批注库模式，所有批注集中在本机批注库目录（可配合 WebDAV 同步）。</summary>
    CentralLibrary,
}

/// <summary>某份课件当前使用的批注文件位置。</summary>
/// <param name="Mode">当前策略。</param>
/// <param name="Path">批注文件绝对路径。</param>
/// <param name="Exists">文件当前是否存在。</param>
public sealed record AnnotationLocation(AnnotationStorageMode Mode, string Path, bool Exists);

/// <summary>
/// 批注文件存储：负责 .cvcanno 的定位、加载、保存、导入导出与清除。
/// 实现必须保证不修改原始课件文件。
/// </summary>
public interface IAnnotationStore
{
    /// <summary>
    /// 当前存储策略。老师可以在设置页运行时切换（同目录模式 ↔ 统一批注库），
    /// 因此这里是可读写的；切换只影响后续的定位与保存，不改动已有文件。
    /// </summary>
    AnnotationStorageMode Mode { get; set; }

    /// <summary>计算某课件在当前策略下的批注文件位置。</summary>
    AnnotationLocation Resolve(string coursewarePath, string coursewareHash);

    /// <summary>批注库模式下的文件路径（<c>&lt;库目录&gt;/&lt;sha256&gt;.cvcanno</c>）。</summary>
    string ComputeLibraryPath(string coursewareHash);

    /// <summary>
    /// 加载批注。文件不存在时返回与课件绑定的空文档；
    /// 文件损坏或格式不符时抛出 <see cref="InvalidDataException"/>（由调用方提示老师）。
    /// </summary>
    AnnotationDocument Load(string coursewarePath, string coursewareHash);

    /// <summary>保存批注（自动创建目录；写入前更新 ModifiedUtc）。</summary>
    void Save(string coursewarePath, AnnotationDocument document);

    /// <summary>清除当前课件的批注文件，返回文件是否被删除。不影响播放进度库。</summary>
    bool Clear(string coursewarePath, string coursewareHash);

    /// <summary>导出 .cvcanno 到指定路径（另存为 / 分享给同事）。</summary>
    void Export(AnnotationDocument document, string targetFilePath);

    /// <summary>导入 .cvcanno（校验格式与哈希一致性后返回文档）。</summary>
    AnnotationDocument Import(string sourceFilePath);

    /// <summary>列出批注库中的全部批注文件路径（同步与数据管理用；只包含 .cvcanno）。</summary>
    IReadOnlyList<string> ListLibraryFiles();
}
