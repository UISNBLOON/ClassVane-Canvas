using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassVaneCanvas.Storage;

namespace ClassVaneCanvas.Sync;

/// <summary>
/// 单个批注文件的同步状态：上次同步时本机内容的哈希与远端版本标识。
/// 这是**冲突检测的唯一依据**，不包含任何播放进度信息。
/// </summary>
public sealed class SyncStateEntry
{
    /// <summary>上次同步时本机 .cvcanno 内容的 SHA256（小写十六进制）。</summary>
    public string LastSyncedHash { get; set; } = string.Empty;

    /// <summary>上次同步时远端的 ETag（远端原样值，可能为 <c>null</c>）。</summary>
    public string? RemoteETag { get; set; }

    /// <summary>上次同步完成时间（UTC）。</summary>
    public DateTimeOffset LastSyncedUtc { get; set; }
}

/// <summary>
/// 同步状态持久化：记录每个 <c>*.cvcanno</c> 的上次同步哈希 / 远端 ETag / 时间。
///
/// 设计约束：
/// · 落盘位置固定为 <see cref="AppPaths.SyncStateFile"/>，属于本机私有数据，<b>绝不参与同步</b>；
/// · JSON 文本、camelCase、缩进可读，方便老师出问题时直接查看；
/// · 读取完全容错——文件损坏 / 被手工改坏 / 无权限时一律当作「空状态」，
///   下一次同步会重新比对内容并自愈，绝不因此崩溃或丢批注。
/// </summary>
public sealed class SyncStateStore
{
    /// <summary>状态文件结构版本号（预留给后续升级迁移）。</summary>
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.Ordinal);
    private bool _dirty;

    /// <param name="stateFilePath">状态文件完整路径（通常为 <see cref="AppPaths.SyncStateFile"/>）。</param>
    public SyncStateStore(string stateFilePath)
    {
        if (string.IsNullOrWhiteSpace(stateFilePath))
        {
            throw new ArgumentException("同步状态文件路径不能为空。", nameof(stateFilePath));
        }

        FilePath = Path.GetFullPath(stateFilePath);
        Load();
    }

    /// <param name="paths">应用目录布局；使用其中的同步状态文件路径。</param>
    public SyncStateStore(AppPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).SyncStateFile)
    {
    }

    /// <summary>状态文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>加载状态文件时遇到的问题；正常时为 <c>null</c>（此时状态按空处理）。</summary>
    public string? LoadError { get; private set; }

    /// <summary>当前全部状态（只读快照，键为批注文件名）。</summary>
    public IReadOnlyDictionary<string, SyncStateEntry> All
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, SyncStateEntry>(_entries, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>状态条目数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>取某个批注文件的同步状态；不存在返回 <c>null</c>。</summary>
    public SyncStateEntry? Get(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var key = NormalizeKey(relativePath);
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    /// <summary>记录一次成功同步：本机内容哈希 + 远端 ETag（时间取当前 UTC）。</summary>
    public void Set(string relativePath, string lastSyncedHash, string? remoteETag)
    {
        var key = NormalizeKey(relativePath);
        var entry = new SyncStateEntry
        {
            LastSyncedHash = lastSyncedHash ?? string.Empty,
            RemoteETag = string.IsNullOrWhiteSpace(remoteETag) ? null : remoteETag,
            LastSyncedUtc = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            _entries[key] = entry;
            _dirty = true;
        }
    }

    /// <summary>移除某个批注文件的同步状态（例如老师清除了该课件的批注）。</summary>
    public void Remove(string relativePath)
    {
        var key = NormalizeKey(relativePath);
        lock (_gate)
        {
            if (_entries.Remove(key))
            {
                _dirty = true;
            }
        }
    }

    /// <summary>清空全部状态（切换同步目标或彻底重来时可调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            _dirty = true;
        }
    }

    /// <summary>
    /// 把当前状态写入磁盘（先写临时文件再原子替换，避免写坏已有状态）。
    /// 没有变化时不产生任何磁盘写入。
    /// </summary>
    public void Save()
    {
        StateFileModel model;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            model = new StateFileModel
            {
                Version = CurrentFormatVersion,
                Entries = new Dictionary<string, SyncStateEntry>(_entries, StringComparer.Ordinal),
            };
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(model, WriteOptions);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        lock (_gate)
        {
            _dirty = false;
        }
    }

    /// <summary>重新从磁盘加载（外部修改状态文件后使用）。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _entries.Clear();
            _dirty = false;
        }

        Load();
    }

    /// <summary>
    /// 读取状态文件。任何异常（文件不存在、JSON 损坏、无权限）都退化为空状态，
    /// 并把原因记录在 <see cref="LoadError"/> 中，绝不抛出。
    /// </summary>
    private void Load()
    {
        LoadError = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var entries = ReadEntries(json);
            if (entries is null)
            {
                LoadError = "同步状态文件内容无法识别，已按空状态处理。";
                return;
            }

            lock (_gate)
            {
                foreach (var pair in entries)
                {
                    if (pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.LastSyncedHash))
                    {
                        continue;
                    }

                    var key = NormalizeKey(pair.Key);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    _entries[key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            // 容错：损坏 / 权限不足一律当空状态，下次同步会自动重建。
            LoadError = $"同步状态文件读取失败（已按空状态处理）：{ex.Message}";
            lock (_gate)
            {
                _entries.Clear();
            }
        }
    }

    private static Dictionary<string, SyncStateEntry>? ReadEntries(string json)
    {
        // 兼容两种写法：标准的 { version, entries } 包装，以及直接以文件名为键的扁平对象。
        var model = JsonSerializer.Deserialize<StateFileModel>(json, ReadOptions);
        if (model?.Entries is { Count: > 0 })
        {
            return model.Entries;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SyncStateEntry>>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return model is null ? null : new Dictionary<string, SyncStateEntry>(StringComparer.Ordinal);
        }
    }

    private static string NormalizeKey(string relativePath)
        => relativePath.Trim().Replace('\\', '/').Trim('/');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响主流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>状态文件的 JSON 结构（camelCase、缩进可读）。</summary>
    private sealed class StateFileModel
    {
        /// <summary>结构版本。</summary>
        public int Version { get; set; } = CurrentFormatVersion;

        /// <summary>批注文件名 → 同步状态。</summary>
        public Dictionary<string, SyncStateEntry>? Entries { get; set; }
    }
}
