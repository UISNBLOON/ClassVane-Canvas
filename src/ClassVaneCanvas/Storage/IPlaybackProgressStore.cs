using ClassVaneCanvas.Models;

namespace ClassVaneCanvas.Storage;

/// <summary>
/// 本机播放进度存储。实现必须使用独立 SQLite 数据库，
/// 不得与批注数据共用文件，也不得实现任何网络同步能力。
/// </summary>
public interface IPlaybackProgressStore
{
    /// <summary>记录条数。</summary>
    int Count { get; }

    /// <summary>按课件哈希查询进度；不存在返回 <c>null</c>。</summary>
    PlaybackProgress? Find(string coursewareHash);

    /// <summary>写入 / 覆盖一条进度记录（按 <see cref="PlaybackProgress.CoursewareHash"/> upsert）。</summary>
    void Save(PlaybackProgress progress);

    /// <summary>把某个课件标记为「不再提示」（不删除已记录的进度）。</summary>
    void SuppressPrompt(string coursewareHash);

    /// <summary>删除某个课件的进度记录。</summary>
    bool Remove(string coursewareHash);

    /// <summary>清除本机全部播放进度记录，返回删除条数。<b>只影响进度库，绝不触碰批注文件。</b></summary>
    int ClearAll();

    /// <summary>按最近放映时间倒序列出全部记录（数据管理页展示用）。</summary>
    IReadOnlyList<PlaybackProgress> GetAll();
}
