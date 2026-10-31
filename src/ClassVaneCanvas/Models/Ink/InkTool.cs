namespace ClassVaneCanvas.Models.Ink;

/// <summary>笔触类型。笔迹引擎自研，不复用任何第三方 ink 组件。</summary>
public enum InkTool
{
    /// <summary>硬笔（板书主笔）：压感粗细变化，边缘柔和抗锯齿。</summary>
    HardPen,

    /// <summary>马克笔：笔尖倾斜触发扁平斜锋、半透明笔触。</summary>
    Marker,

    /// <summary>荧光笔：高透明度叠加，不遮挡底层 PPT 画面。</summary>
    Highlighter,

    /// <summary>橡皮擦：点擦（只擦笔迹，不擦 PPT 内容）。</summary>
    Eraser,
}
