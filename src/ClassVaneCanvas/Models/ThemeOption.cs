namespace ClassVaneCanvas.Models;

/// <summary>主题设置项（用于设置页的外观主题下拉框）。</summary>
/// <param name="Value">对应的主题枚举值。</param>
/// <param name="Label">界面上显示的名称。</param>
public sealed record ThemeOption(AppTheme Value, string Label);
