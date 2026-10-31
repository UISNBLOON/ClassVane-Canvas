using ClassVaneCanvas.Services;
using ClassVaneCanvas.ViewModels.Settings.Options;

namespace ClassVaneCanvas.ViewModels.Settings;

/// <summary>手势设置：触摸与手写笔的手势映射。</summary>
public sealed class GestureSettingsViewModel : SettingsOptionsPageViewModel
{
    public GestureSettingsViewModel(ISettingsService settings)
        : base("手势设置", "触摸手势与手写笔的分工")
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = settings.Current;

        Add(new SectionOptionViewModel("单指手势"));

        Add(new ToggleOptionViewModel(
            "单指滑动翻页",
            current.SwipeTurnsPage,
            value =>
            {
                current.SwipeTurnsPage = value;
                settings.NotifyChanged();
            },
            "左滑 / 上滑下一页，右滑 / 下滑上一页。"));

        Add(new SliderOptionViewModel(
            "翻页滑动阈值",
            current.SwipeTurnThreshold,
            30,
            200,
            5,
            value =>
            {
                current.SwipeTurnThreshold = (float)value;
                settings.NotifyChanged();
            },
            "滑动多少距离才算翻页（逻辑像素），防止误触。",
            "0"));

        Add(new SectionOptionViewModel("双指手势"));

        Add(new SliderOptionViewModel(
            "缩放灵敏度",
            current.PinchSensitivity,
            0.3,
            2.5,
            0.1,
            value =>
            {
                current.PinchSensitivity = (float)value;
                settings.NotifyChanged();
            },
            "双指捏合缩放画布，双指拖拽漫游；笔迹随画布变换，位置与形状不变。",
            "0.0"));

        Add(new SectionOptionViewModel("笔与触摸的分工"));

        Add(new InfoOptionViewModel(
            "手写笔",
            "落笔即书写",
            "手写笔永远优先书写，不会误触发页面移动或缩放。"));

        Add(new ToggleOptionViewModel(
            "手指书写",
            current.FingerDraws,
            value =>
            {
                current.FingerDraws = value;
                settings.NotifyChanged();
            },
            "与「书写设置 → 输入源区分」同一开关；默认关闭（手指只做手势）。"));

        Add(new ToggleOptionViewModel(
            "手掌抑制",
            current.IgnoreTouchWhilePenDown,
            value =>
            {
                current.IgnoreTouchWhilePenDown = value;
                settings.NotifyChanged();
            },
            "书写过程中忽略触摸点，避免手掌误触发翻页。"));
    }
}
