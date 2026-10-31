using ClassVaneCanvas.Models;

namespace ClassVaneCanvas.Services;

/// <summary>全局设置的读写（本机 settings.json）。</summary>
public interface ISettingsService
{
    /// <summary>当前设置对象（就地修改后调用 <see cref="Save"/>）。</summary>
    AppSettings Current { get; }

    /// <summary>设置发生变化（用于让界面即时响应）。</summary>
    event EventHandler? Changed;

    /// <summary>从磁盘加载（不存在则使用默认值并写出）。</summary>
    void Load();

    /// <summary>写回磁盘。</summary>
    void Save();

    /// <summary>通知设置已变化（触发 <see cref="Changed"/> 与保存）。</summary>
    void NotifyChanged();
}
