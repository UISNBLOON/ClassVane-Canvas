using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassVaneCanvas.ViewModels.Settings.Options;

/// <summary>
/// 设置项基类。设置页统一用「数据驱动」的选项列表渲染，
/// 这样新增设置项只需在对应页面的 ViewModel 里追加一个选项对象，不必写新的 XAML。
/// </summary>
public abstract class SettingsOptionViewModel : ViewModelBase
{
    protected SettingsOptionViewModel(string title, string? hint = null)
    {
        Title = title;
        Hint = hint;
    }

    /// <summary>设置项名称。</summary>
    public string Title { get; }

    /// <summary>补充说明（可为空）。</summary>
    public string? Hint { get; }

    /// <summary>是否有补充说明。</summary>
    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);
}

/// <summary>分组标题。</summary>
public sealed class SectionOptionViewModel : SettingsOptionViewModel
{
    public SectionOptionViewModel(string title, string? hint = null)
        : base(title, hint)
    {
    }
}

/// <summary>开关项。</summary>
public sealed partial class ToggleOptionViewModel : SettingsOptionViewModel
{
    private readonly Action<bool> _apply;

    [ObservableProperty]
    private bool _value;

    public ToggleOptionViewModel(string title, bool value, Action<bool> apply, string? hint = null)
        : base(title, hint)
    {
        _value = value;
        _apply = apply;
    }

    partial void OnValueChanged(bool value) => _apply(value);
}

/// <summary>滑杆项（笔锋手感调节都用它）。</summary>
public sealed partial class SliderOptionViewModel : SettingsOptionViewModel
{
    private readonly Action<double> _apply;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double _value;

    public SliderOptionViewModel(
        string title,
        double value,
        double minimum,
        double maximum,
        double step,
        Action<double> apply,
        string? hint = null,
        string format = "0.00")
        : base(title, hint)
    {
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Format = format;
        _value = value;
        _apply = apply;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    public string Format { get; }

    /// <summary>数值文本（滑杆右侧显示）。</summary>
    public string ValueText => Value.ToString(Format);

    partial void OnValueChanged(double value)
    {
        _apply(value);
        OnPropertyChanged(nameof(ValueText));
    }
}

/// <summary>下拉选项。</summary>
/// <param name="Value">选项值。</param>
/// <param name="Label">显示文本。</param>
public sealed record ChoiceItem(object Value, string Label);

/// <summary>单选项（下拉框）。</summary>
public sealed partial class ChoiceOptionViewModel : SettingsOptionViewModel
{
    private readonly Action<object> _apply;

    [ObservableProperty]
    private ChoiceItem _selected;

    public ChoiceOptionViewModel(
        string title,
        IReadOnlyList<ChoiceItem> items,
        object current,
        Action<object> apply,
        string? hint = null)
        : base(title, hint)
    {
        if (items is null || items.Count == 0)
        {
            throw new ArgumentException("选项列表不能为空", nameof(items));
        }

        Items = items;
        _selected = items.FirstOrDefault(i => Equals(i.Value, current)) ?? items[0];
        _apply = apply;
    }

    public IReadOnlyList<ChoiceItem> Items { get; }

    partial void OnSelectedChanged(ChoiceItem value) => _apply(value.Value);
}

/// <summary>文本输入项。</summary>
public sealed partial class TextOptionViewModel : SettingsOptionViewModel
{
    private readonly Action<string> _apply;

    [ObservableProperty]
    private string _value;

    public TextOptionViewModel(
        string title,
        string value,
        Action<string> apply,
        string? hint = null,
        string watermark = "",
        bool isPassword = false)
        : base(title, hint)
    {
        _value = value;
        _apply = apply;
        Watermark = watermark;
        IsPassword = isPassword;
    }

    /// <summary>输入框占位提示。</summary>
    public string Watermark { get; }

    /// <summary>是否密码框。</summary>
    public bool IsPassword { get; }

    /// <summary>密码掩码字符；普通输入框返回 '\0'（不掩码）。</summary>
    public char PasswordChar => IsPassword ? '\u25CF' : '\0';

    /// <summary>是否可用（例如未开启同步时 WebDAV 地址不可填）。</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    partial void OnValueChanged(string value) => _apply(value);
}

/// <summary>只读信息项。</summary>
public sealed partial class InfoOptionViewModel : SettingsOptionViewModel
{
    [ObservableProperty]
    private string _value;

    public InfoOptionViewModel(string title, string value, string? hint = null)
        : base(title, hint)
        => _value = value;
}

/// <summary>按钮项（清除数据、导入导出、测试连接等）。</summary>
public sealed partial class ButtonOptionViewModel : SettingsOptionViewModel
{
    public ButtonOptionViewModel(
        string title,
        Func<Task> action,
        string? hint = null,
        bool isPrimary = false,
        bool isDanger = false)
        : base(title, hint)
    {
        Command = new AsyncRelayCommand(action);
        IsPrimary = isPrimary;
        IsDanger = isDanger;
    }

    /// <summary>按钮命令。</summary>
    public IAsyncRelayCommand Command { get; }

    /// <summary>主按钮样式。</summary>
    public bool IsPrimary { get; }

    /// <summary>危险操作样式（清除数据）。</summary>
    public bool IsDanger { get; }
}
