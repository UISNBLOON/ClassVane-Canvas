using ClassVaneCanvas.Pptx;

namespace ClassVaneCanvas.Models;

/// <summary>兼容性提示的分组（动画 / 转场 / 字体 / 其它）。</summary>
/// <param name="Title">分组标题（例如「动画」）。</param>
/// <param name="Items">最多 5 条明细。</param>
/// <param name="TotalCount">该分组的条目总数（含未列出的）。</param>
public sealed record CompatibilityNoticeGroup(string Title, IReadOnlyList<string> Items, int TotalCount)
{
    /// <summary>该分组被折叠的条数（0 表示全部列出）。</summary>
    public int HiddenCount => Math.Max(0, TotalCount - Items.Count);

    /// <summary>「等 N 项」提示；没有折叠项时为空字符串。</summary>
    public string HiddenText => HiddenCount > 0 ? $"等 {HiddenCount} 项" : string.Empty;
}

/// <summary>
/// 打开课件后的「动画与字体兼容性提示」内容：把内核报告的降级 / 不支持项按类别分组，
/// 每组最多列 <see cref="MaxItemsPerGroup"/> 条，其余折叠成「等 N 项」。
///
/// 只描述「放映时会与 PowerPoint 有哪些差异」，不参与解析、不影响放映本身。
/// </summary>
public sealed record CompatibilityNotice(IReadOnlyList<CompatibilityNoticeGroup> Groups)
{
    /// <summary>每组最多列出的条数。</summary>
    public const int MaxItemsPerGroup = 5;

    /// <summary>动画组标题。</summary>
    public const string AnimationGroupTitle = "动画";

    /// <summary>转场组标题。</summary>
    public const string TransitionGroupTitle = "转场";

    /// <summary>字体组标题。</summary>
    public const string FontGroupTitle = "字体";

    /// <summary>其它降级项组标题。</summary>
    public const string OtherGroupTitle = "其它";

    /// <summary>没有任何需要提示的内容。</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>弹窗标题。</summary>
    public const string Title = "动画与字体兼容性提示";

    /// <summary>明细总条数。</summary>
    public int TotalCount => Groups.Sum(g => g.TotalCount);

    /// <summary>主按钮文案（知道了）。</summary>
    public const string AcknowledgeText = "知道了";

    /// <summary>次按钮文案（不再提示此文件）。</summary>
    public const string SuppressText = "不再提示此文件";

    /// <summary>
    /// 由内核后端汇总的降级提示列表构建通知内容；没有可提示内容时返回 <c>null</c>。
    /// </summary>
    /// <param name="messages">降级 / 不支持项（去重后）。</param>
    public static CompatibilityNotice? Create(IEnumerable<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [AnimationGroupTitle] = new(),
            [TransitionGroupTitle] = new(),
            [FontGroupTitle] = new(),
            [OtherGroupTitle] = new(),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in messages)
        {
            var message = raw?.Trim();
            if (string.IsNullOrEmpty(message) || !seen.Add(message))
            {
                continue;
            }

            buckets[Classify(message)].Add(message);
        }

        var groups = new List<CompatibilityNoticeGroup>(4);
        foreach (var title in new[] { AnimationGroupTitle, TransitionGroupTitle, FontGroupTitle, OtherGroupTitle })
        {
            var items = buckets[title];
            if (items.Count == 0)
            {
                continue;
            }

            groups.Add(new CompatibilityNoticeGroup(
                title,
                items.Take(MaxItemsPerGroup).ToArray(),
                items.Count));
        }

        return groups.Count == 0 ? null : new CompatibilityNotice(groups);
    }

    /// <summary>由内核后端构建通知内容（含文档级、页级与字体替换记录）。</summary>
    /// <param name="source">已初始化的自研 PPTX 后端。</param>
    public static CompatibilityNotice? FromSlideSource(PptxSlideSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Create(source.UnsupportedFeatures);
    }

    /// <summary>格式化为弹窗正文（分组 + 明细 + 「等 N 项」）。</summary>
    public string ToMessage()
    {
        var builder = new System.Text.StringBuilder();

        builder.Append("本课件的部分动画 / 转场 / 字体会以兼容方式放映，")
            .Append("放映与板书功能不受影响：");

        foreach (var group in Groups)
        {
            builder.Append('\n').Append('\n').Append('【').Append(group.Title).Append('】');

            foreach (var item in group.Items)
            {
                builder.Append('\n').Append("· ").Append(item);
            }

            if (group.HiddenCount > 0)
            {
                builder.Append('\n').Append("· ").Append(group.HiddenText);
            }
        }

        return builder.ToString();
    }

    /// <summary>按关键字把一条提示分到动画 / 转场 / 字体 / 其它。</summary>
    private static string Classify(string message)
    {
        if (Contains(message, "字体") || Contains(message, "font"))
        {
            return FontGroupTitle;
        }

        if (Contains(message, "转场") || Contains(message, "切换") || Contains(message, "morph"))
        {
            return TransitionGroupTitle;
        }

        if (Contains(message, "动画") || Contains(message, "animation"))
        {
            return AnimationGroupTitle;
        }

        return OtherGroupTitle;
    }

    private static bool Contains(string text, string token)
        => text.Contains(token, StringComparison.OrdinalIgnoreCase);
}
