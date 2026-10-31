using System.Text.Json;
using System.Threading;
using System.Text.Json.Serialization;
using ClassVaneCanvas.Models;
using ClassVaneCanvas.Storage;

namespace ClassVaneCanvas.Services;

/// <summary>
/// 设置持久化：本机 <c>settings.json</c>。
/// 写入采用「临时文件 + 原子替换」，避免掉电 / 崩溃写坏配置文件。
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>滑杆拖动会高频触发变更，这里做去抖后再落盘。</summary>
    private const int SaveDebounceMilliseconds = 600;

    private readonly AppPaths _paths;
    private readonly object _saveGate = new();
    private Timer? _saveTimer;

    public JsonSettingsService(AppPaths paths)
        => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        _paths.EnsureCreated();

        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                var json = File.ReadAllText(_paths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // 配置损坏不应阻止启动：回退到默认设置（原文件保留，便于老师排查）
            Current = new AppSettings();
        }

        Current.EnsureDeviceId();
        Save();
    }

    public void Save()
    {
        try
        {
            _paths.EnsureCreated();

            var json = JsonSerializer.Serialize(Current, SerializerOptions);
            var temporary = _paths.SettingsFile + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _paths.SettingsFile, overwrite: true);
        }
        catch (Exception)
        {
            // 设置写入失败不影响上课：内存中的设置仍然生效
        }
    }

    public void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);

        lock (_saveGate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => Save(), null, SaveDebounceMilliseconds, Timeout.Infinite);
        }
    }
}
