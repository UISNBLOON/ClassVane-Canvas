using ClassVaneCanvas.Ink;
using ClassVaneCanvas.Services;
using ClassVaneCanvas.ViewModels.Settings.Options;

namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>
/// 书写设置：专门用于调希沃白板 5 的笔锋手感（平滑强度、压感曲线、粗细上下限、荧光笔透明度、输入源区分）。
/// 调节即时生效并写入本机设置。
/// </summary>
public sealed class WritingSettingsViewModel : SettingsOptionsPageViewModel
{
    public WritingSettingsViewModel(ISettingsService settings, InkEngineSettings engine)
        : base("书写设置", "笔迹引擎与笔锋手感（对齐希沃白板 5）")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(engine);

        var current = settings.Current;

        void Commit()
        {
            current.ApplyTo(engine);
            settings.NotifyChanged();
        }

        Add(new SectionOptionViewModel("笔锋手感", "自研矢量笔迹管线，参数即时生效"));

        Add(new SliderOptionViewModel(
            "笔迹平滑强度",
            current.InkSmoothing,
            0,
            1,
            0.05,
            value =>
            {
                current.InkSmoothing = (float)value;
                Commit();
            },
            "移动平均 + 预判补点：越大越顺滑，快速书写时越不容易出现锯齿和拖尾。"));

        Add(new SliderOptionViewModel(
            "压感响应曲线",
            current.PressureGamma,
            0.5,
            3,
            0.05,
            value =>
            {
                current.PressureGamma = (float)value;
                Commit();
            },
            "轻压极细、压力上升平缓加粗；数值越大，轻压段越细。"));

        Add(new SliderOptionViewModel(
            "压感防抖强度",
            current.PressureSmoothing,
            0,
            1,
            0.05,
            value =>
            {
                current.PressureSmoothing = (float)value;
                Commit();
            },
            "抑制微小压力抖动造成的笔触跳动（不跟手时调小）。"));

        Add(new SectionOptionViewModel("笔触粗细"));

        Add(new SliderOptionViewModel(
            "粗细下限",
            current.MinStrokeWidth,
            0.4,
            6,
            0.1,
            value =>
            {
                current.MinStrokeWidth = (float)value;
                Commit();
            },
            "轻压时的最细笔触（页面逻辑像素）。",
            "0.0"));

        Add(new SliderOptionViewModel(
            "粗细上限",
            current.MaxStrokeWidth,
            4,
            60,
            0.5,
            value =>
            {
                current.MaxStrokeWidth = (float)value;
                Commit();
            },
            "重压时的最粗笔触（页面逻辑像素）。",
            "0.0"));

        Add(new SectionOptionViewModel("笔触样式"));

        Add(new SliderOptionViewModel(
            "荧光笔透明度",
            current.HighlighterOpacity,
            0.05,
            0.9,
            0.01,
            value =>
            {
                current.HighlighterOpacity = (float)value;
                Commit();
            },
            "数值越小越透明，避免遮挡底层 PPT 画面。"));

        Add(new SliderOptionViewModel(
            "马克笔透明度",
            current.MarkerOpacity,
            0.1,
            1,
            0.05,
            value =>
            {
                current.MarkerOpacity = (float)value;
                Commit();
            },
            "马克笔笔尖倾斜时呈现扁平斜锋。"));

        Add(new SectionOptionViewModel("输入源区分"));

        Add(new ToggleOptionViewModel(
            "允许手指书写",
            current.FingerDraws,
            value =>
            {
                current.FingerDraws = value;
                Commit();
            },
            "关闭时：手写笔书写、手指只做手势（推荐，符合课堂使用习惯）。"));

        Add(new ToggleOptionViewModel(
            "手写笔落笔时忽略触摸",
            current.IgnoreTouchWhilePenDown,
            value =>
            {
                current.IgnoreTouchWhilePenDown = value;
                Commit();
            },
            "手掌抑制 / 防误触：书写过程中手掌压到屏幕不会触发翻页或缩放。"));
    }
}
