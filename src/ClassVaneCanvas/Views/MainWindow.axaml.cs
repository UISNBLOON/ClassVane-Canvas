using Avalonia.Controls;
using Avalonia.Media.Imaging;
using ClassVaneCanvas.Services;

namespace ClassVaneCanvas.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 窗口 / 任务栏图标使用 logo-1；资源缺失时保持系统默认图标。
        if (AppAssets.TryLoadAppIcon() is Bitmap logo)
        {
            Icon = new WindowIcon(logo);
        }
    }
}
