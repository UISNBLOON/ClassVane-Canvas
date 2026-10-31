using System.Globalization;
using ClassVaneCanvas.Models;
using Microsoft.Data.Sqlite;

namespace ClassVaneCanvas.Storage;

/// <summary>
/// <see cref="IPlaybackProgressStore"/> 的 SQLite 实现。
///
/// 硬性边界（不得越界）：
/// 1. 只写 <see cref="AppPaths.PlaybackProgressDatabase"/> 指向的独立数据库文件，绝不与批注数据
///    （<c>*.cvcanno</c> / 批注库目录）共用文件，因此 <see cref="ClearAll"/> 不可能影响批注；
/// 2. 不含任何网络 / 同步代码，进度只留在本机；
/// 3. 连接一律短生命周期（每次操作 <c>using</c> 打开并关闭），避免长连接在休眠 / 换机后失效。
///
/// 线程安全：所有公开成员都在同一把锁内执行，多线程可安全调用（数据量极小，串行化代价可忽略）。
/// </summary>
public sealed class SqlitePlaybackProgressStore : IPlaybackProgressStore
{
    /// <summary>当前实现的表结构版本，写入 <c>PRAGMA user_version</c>。</summary>
    private const int SchemaVersion = 1;

    /// <summary>进度表建表语句（字段与需求约定一致，逐字对应）。</summary>
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS playback_progress (
            courseware_hash   TEXT PRIMARY KEY,
            courseware_path   TEXT NOT NULL,
            courseware_file_name TEXT NOT NULL,
            slide_count       INTEGER NOT NULL DEFAULT 0,
            last_slide_index  INTEGER NOT NULL DEFAULT 1,
            last_played_utc   TEXT NOT NULL,
            prompt_suppressed INTEGER NOT NULL DEFAULT 0
        );
        """;

    private readonly string _databasePath;
    private readonly string _connectionString;

    /// <summary>写操作与「首次建表」的互斥锁；所有公开成员共用，保证多线程下状态可预期。</summary>
    private readonly object _gate = new();

    private bool _initialized;

    /// <summary>以数据库文件绝对路径创建实例。</summary>
    /// <param name="databasePath">SQLite 数据库文件路径（应为 <see cref="AppPaths.PlaybackProgressDatabase"/>）。</param>
    /// <exception cref="ArgumentException">路径为空或空白。</exception>
    public SqlitePlaybackProgressStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("播放进度数据库路径不能为空。", nameof(databasePath));
        }

        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        }.ToString();
    }

    /// <summary>以本机数据目录布局创建实例（使用 <see cref="AppPaths.PlaybackProgressDatabase"/>）。</summary>
    /// <param name="paths">数据目录布局；为 <c>null</c> 抛 <see cref="ArgumentNullException"/>。</param>
    public SqlitePlaybackProgressStore(AppPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).PlaybackProgressDatabase)
    {
    }

    /// <summary>数据库文件路径（只读，便于日志与数据管理页展示）。</summary>
    public string DatabasePath => _databasePath;

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM playback_progress;";

                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <inheritdoc />
    public PlaybackProgress? Find(string coursewareHash)
    {
        ThrowIfInvalidHash(coursewareHash, nameof(coursewareHash));

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE courseware_hash = $hash;";
            command.Parameters.AddWithValue("$hash", coursewareHash);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadProgress(reader) : null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 按 <see cref="PlaybackProgress.CoursewareHash"/> 做 upsert；<see cref="PlaybackProgress.PromptSuppressed"/>
    /// 按传入值整体覆盖（调用方负责携带当前值），这样「已忽略」状态可以被显式撤销。
    /// </remarks>
    public void Save(PlaybackProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ThrowIfInvalidHash(progress.CoursewareHash, nameof(progress));

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            // ON CONFLICT：同 hash 覆盖为最新一次放映状态，不产生第二条记录。
            command.CommandText = """
                INSERT INTO playback_progress (
                    courseware_hash, courseware_path, courseware_file_name,
                    slide_count, last_slide_index, last_played_utc, prompt_suppressed)
                VALUES ($hash, $path, $fileName, $slideCount, $slideIndex, $playedUtc, $suppressed)
                ON CONFLICT(courseware_hash) DO UPDATE SET
                    courseware_path = excluded.courseware_path,
                    courseware_file_name = excluded.courseware_file_name,
                    slide_count = excluded.slide_count,
                    last_slide_index = excluded.last_slide_index,
                    last_played_utc = excluded.last_played_utc,
                    prompt_suppressed = excluded.prompt_suppressed;
                """;

            command.Parameters.AddWithValue("$hash", progress.CoursewareHash);
            command.Parameters.AddWithValue("$path", progress.CoursewarePath ?? string.Empty);
            command.Parameters.AddWithValue("$fileName", progress.CoursewareFileName ?? string.Empty);
            command.Parameters.AddWithValue("$slideCount", progress.SlideCount);
            command.Parameters.AddWithValue("$slideIndex", progress.LastSlideIndex);
            command.Parameters.AddWithValue("$playedUtc", ToStorageTimestamp(progress.LastPlayedUtc));
            command.Parameters.AddWithValue("$suppressed", progress.PromptSuppressed ? 1 : 0);

            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只执行 <c>UPDATE</c>：既不删除记录，也不改变页码与时间；若该课件尚无进度记录，则不做任何事
    /// （不会凭空插入一条空记录）。
    /// </remarks>
    public void SuppressPrompt(string coursewareHash)
    {
        ThrowIfInvalidHash(coursewareHash, nameof(coursewareHash));

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE playback_progress SET prompt_suppressed = 1 WHERE courseware_hash = $hash;";
            command.Parameters.AddWithValue("$hash", coursewareHash);
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public bool Remove(string coursewareHash)
    {
        ThrowIfInvalidHash(coursewareHash, nameof(coursewareHash));

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM playback_progress WHERE courseware_hash = $hash;";
            command.Parameters.AddWithValue("$hash", coursewareHash);

            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只清空本库的 <c>playback_progress</c> 表：批注文件（<c>*.cvcanno</c> / 批注库目录）位于另一个
    /// 位置，本方法不会读取、写入或删除任何批注文件。
    /// </remarks>
    public int ClearAll()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM playback_progress;";

            return command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    /// <remarks>按存储的 UTC 时间字符串倒序；ISO-8601「o」格式定长，字典序即时间序。</remarks>
    public IReadOnlyList<PlaybackProgress> GetAll()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = SelectColumns + " ORDER BY last_played_utc DESC;";

            var results = new List<PlaybackProgress>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadProgress(reader));
            }

            return results;
        }
    }

    private const string SelectColumns =
        "SELECT courseware_hash, courseware_path, courseware_file_name, slide_count, " +
        "last_slide_index, last_played_utc, prompt_suppressed FROM playback_progress";

    /// <summary>打开短连接，并应用必要的连接级 PRAGMA（WAL / 外键 / 忙等待）。</summary>
    private SqliteConnection OpenConnection()
    {
        EnsureInitialized();

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            using var pragma = connection.CreateCommand();
            // WAL：读写不互相阻塞，适合「放映中频繁写进度」的场景。
            // foreign_keys=ON：本表暂无外键，但保持与全局存储约定一致。
            // busy_timeout：多线程/多进程短连接偶发争用时等待而不是立刻抛 SQLITE_BUSY。
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>首次使用时创建目录与表结构，并用 <c>PRAGMA user_version</c> 管理 schema 版本。</summary>
    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        // 数据根目录可能尚未创建（例如测试里指定了临时目录）。
        var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        var version = ReadUserVersion(connection);

        if (version > SchemaVersion)
        {
            // 数据库由更新版本的应用写入：宁可报错，也不要破坏性地“降级”用户数据。
            throw new InvalidOperationException(
                $"播放进度数据库版本（{version}）高于当前应用支持的版本（{SchemaVersion}），请升级应用后再试。");
        }

        if (version < SchemaVersion)
        {
            using var create = connection.CreateCommand();
            create.CommandText = CreateTableSql;
            create.ExecuteNonQuery();

            // 迁移入口：后续新增列时在此按 version 逐级 ALTER TABLE，并同步提升 SchemaVersion。
            WriteUserVersion(connection, SchemaVersion);
        }

        _initialized = true;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void WriteUserVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        // PRAGMA 不支持参数占位符；version 是编译期常量，不存在注入风险。
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static PlaybackProgress ReadProgress(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
        reader.IsDBNull(4) ? 1 : reader.GetInt32(4),
        ParseStorageTimestamp(reader.IsDBNull(5) ? null : reader.GetString(5)),
        !reader.IsDBNull(6) && reader.GetInt32(6) != 0);

    /// <summary>写入 ISO-8601 往返格式（「o」）的 UTC 字符串，定长可比较。</summary>
    private static string ToStorageTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>按 UTC 解析回 <see cref="DateTimeOffset"/>；损坏/空值退化为 <see cref="DateTimeOffset.MinValue"/>。</summary>
    private static DateTimeOffset ParseStorageTimestamp(string? text)
        => DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>课件哈希是唯一键，必须非空（空白视为非法参数）。</summary>
    private static void ThrowIfInvalidHash(string? coursewareHash, string paramName)
    {
        if (string.IsNullOrWhiteSpace(coursewareHash))
        {
            throw new ArgumentException("课件哈希不能为空。", paramName);
        }
    }
}
