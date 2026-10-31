namespace ClassVaneCanvas.Models;

/// <summary>放映时的画面显示模式（悬浮工具栏上的黑屏 / 白屏按钮）。</summary>
public enum SlideDisplayMode
{
    /// <summary>正常显示幻灯片。</summary>
    Normal,

    /// <summary>黑屏：遮住幻灯片内容，但保留板书笔迹。</summary>
    Black,

    /// <summary>白屏：遮住幻灯片内容，但保留板书笔迹。</summary>
    White,
}
