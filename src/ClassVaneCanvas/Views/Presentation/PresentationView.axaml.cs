using Avalonia.Controls;

namespace ClassVaneCanvas.Views.Presentation;

/// <summary>
/// 放映视图：工具栏 + 板书批注画布 + 缩略图导航 + 状态栏。
/// 笔迹渲染、手势与输入源区分都在 <see cref="Controls.InkCanvas"/> 内实现。
/// </summary>
public partial class PresentationView : UserControl
{
    public PresentationView() => InitializeComponent();
}
