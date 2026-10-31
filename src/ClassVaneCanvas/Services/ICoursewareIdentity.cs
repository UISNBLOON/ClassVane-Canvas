namespace ClassVaneCanvas.Services;

/// <summary>
/// 课件文件指纹。批注文件以 SHA256 作为唯一匹配依据：
/// 只有哈希一致才加载笔迹，避免同名不同课件互相串批注。
/// </summary>
public interface ICoursewareIdentity
{
    /// <summary>异步计算文件的 SHA256（小写十六进制），内部按 路径+大小+修改时间 缓存结果。</summary>
    Task<string> ComputeHashAsync(string coursewarePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅查询缓存，不读取文件内容；缓存未命中返回 <c>null</c>。
    /// 用于打开课件时快速判断是否已有批注/进度。
    /// </summary>
    string? TryGetCachedHash(string coursewarePath);

    /// <summary>把外部已知的哈希写入缓存（例如从批注文件头部读到的哈希）。</summary>
    void Remember(string coursewarePath, string coursewareHash);

    /// <summary>校验文件当前哈希是否与期望值一致。</summary>
    Task<bool> VerifyAsync(string coursewarePath, string expectedHash, CancellationToken cancellationToken = default);
}
