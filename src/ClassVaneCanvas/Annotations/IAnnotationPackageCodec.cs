using ClassVaneCanvas.Models.Annotations;

namespace ClassVaneCanvas.Annotations;

/// <summary>.cvcanno 批注包的读写（序列化格式实现）。</summary>
public interface IAnnotationPackageCodec
{
    /// <summary>从流中读取批注文档。</summary>
    AnnotationDocument Read(Stream stream);

    /// <summary>把批注文档写入流。</summary>
    void Write(Stream stream, AnnotationDocument document);

    /// <summary>读取文件。</summary>
    AnnotationDocument ReadFile(string path);

    /// <summary>写入文件（使用临时文件 + 原子替换，避免写坏已有批注）。</summary>
    void WriteFile(string path, AnnotationDocument document);
}

/// <summary>批注合并（冲突处理中的「合并笔迹」）。</summary>
public interface IAnnotationMerger
{
    /// <summary>
    /// 按笔迹 Id 取并集合并两份文档（要求 CoursewareHash 相同）。
    /// 结果不修改入参；输出页与笔迹顺序稳定（页升序，笔迹按 CreatedUtcTicks、Id 排序）。
    /// </summary>
    AnnotationDocument Merge(AnnotationDocument local, AnnotationDocument remote);
}
