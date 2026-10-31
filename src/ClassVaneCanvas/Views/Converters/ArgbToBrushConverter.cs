using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ClassVaneCanvas.Views.Converters;

/// <summary>把 ARGB（uint）转成画刷，用于调色板按钮。</summary>
public sealed class ArgbToBrushConverter : IValueConverter
{
    /// <summary>共享实例（XAML 里用 {x:Static} 引用）。</summary>
    public static readonly ArgbToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is uint argb ? new SolidColorBrush(Color.FromUInt32(argb)) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
