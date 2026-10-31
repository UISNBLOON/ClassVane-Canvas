using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Annotations;

/// <summary>
/// 批注合并的默认实现：把两台设备（例如办公室电脑与教室电脑）各自记的板书取并集。
///
/// 合并规则（顺序稳定，可重复执行）：
/// · 两份文档必须绑定同一份课件（<see cref="AnnotationDocument.CoursewareHash"/> 相同），否则拒绝合并；
/// · 页取并集、页码升序；
/// · 每页笔迹按 <see cref="InkStroke.Id"/> 去重：同一 Id 只保留 <see cref="InkStroke.CreatedUtcTicks"/>
///   较晚的一条，完全相同则保留 <paramref name="local"/>（本机）的；
/// · 每页输出顺序：先按创建时间升序，再按 Id 字典序升序，保证任何设备上合并结果一致；
/// · **不修改入参文档**：返回全新对象，笔迹一律 <see cref="InkStroke.Clone"/>，避免撤销栈与渲染共享可变集合。
/// </summary>
public sealed class AnnotationMerger : IAnnotationMerger
{
    /// <inheritdoc />
    public AnnotationDocument Merge(AnnotationDocument local, AnnotationDocument remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (!string.Equals(local.CoursewareHash, remote.CoursewareHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"两份批注绑定的课件不同（本机 {Shorten(local.CoursewareHash)}，" +
                $"对方 {Shorten(remote.CoursewareHash)}），不能合并笔迹。",
                nameof(remote));
        }

        var localPages = IndexPages(local);
        var remotePages = IndexPages(remote);

        var pageIndexes = new SortedSet<int>(localPages.Keys);
        pageIndexes.UnionWith(remotePages.Keys);

        var mergedPages = new List<InkPage>(pageIndexes.Count);
        foreach (var pageIndex in pageIndexes)
        {
            localPages.TryGetValue(pageIndex, out var localPage);
            remotePages.TryGetValue(pageIndex, out var remotePage);

            mergedPages.Add(new InkPage
            {
                PageIndex = pageIndex,
                Strokes = MergePageStrokes(localPage, remotePage),
            });
        }

        return new AnnotationDocument
        {
            Format = string.IsNullOrEmpty(local.Format) ? CvcannoFormat.Identifier : local.Format,
            FormatVersion = local.FormatVersion,
            CoursewareHash = local.CoursewareHash,
            CoursewareFileName = FirstNonEmpty(local.CoursewareFileName, remote.CoursewareFileName),
            SlideCount = Math.Max(local.SlideCount, remote.SlideCount),
            ModifiedUtc = local.ModifiedUtc >= remote.ModifiedUtc ? local.ModifiedUtc : remote.ModifiedUtc,
            OriginDeviceId = local.OriginDeviceId,
            Pages = mergedPages,
        };
    }

    /// <summary>单页笔迹合并：先本机后对方，同 Id 保留创建时间较晚的一条（相同则保留本机）。</summary>
    private static List<InkStroke> MergePageStrokes(InkPage? localPage, InkPage? remotePage)
    {
        var byId = new Dictionary<string, InkStroke>(StringComparer.Ordinal);

        Collect(byId, localPage);
        Collect(byId, remotePage);

        return byId.Values
            .OrderBy(stroke => stroke.CreatedUtcTicks)
            .ThenBy(stroke => stroke.Id, StringComparer.Ordinal)
            .Select(stroke => stroke.Clone())
            .ToList();
    }

    /// <summary>把一页的笔迹并入字典；同一 Id 只保留创建时间较晚的那条。</summary>
    private static void Collect(Dictionary<string, InkStroke> target, InkPage? page)
    {
        if (page?.Strokes is null)
        {
            return;
        }

        foreach (var stroke in page.Strokes)
        {
            if (stroke is null)
            {
                continue;
            }

            var id = stroke.Id ?? string.Empty;
            if (!target.TryGetValue(id, out var existing) || stroke.CreatedUtcTicks > existing.CreatedUtcTicks)
            {
                // 先处理本机：时间戳完全相同时不会覆盖，天然满足「相同则保留 local」
                target[id] = stroke;
            }
        }
    }

    /// <summary>同页重复出现时取第一条，保证合并不确定性最小。</summary>
    private static Dictionary<int, InkPage> IndexPages(AnnotationDocument document)
    {
        var pages = new Dictionary<int, InkPage>();
        if (document.Pages is null)
        {
            return pages;
        }

        foreach (var page in document.Pages)
        {
            if (page is null)
            {
                continue;
            }

            pages.TryAdd(page.PageIndex, page);
        }

        return pages;
    }

    private static string FirstNonEmpty(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred! : fallback ?? string.Empty;

    /// <summary>提示语里只显示哈希前 12 位。</summary>
    private static string Shorten(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return "空";
        }

        return hash.Length <= 12 ? hash : hash[..12] + "…";
    }
}
