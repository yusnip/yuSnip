using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 滑杆"点哪儿指哪儿"：由 <c>Themes/Controls.axaml</c> 的 <c>Slider.compactSlider</c> 样式统一挂上，
/// 参数条与下挂面板里的 22 枚标注滑杆一处生效，各窗口不需要逐个接线（与
/// <see cref="SegmentedDividerBehavior"/> 同一副配方）。
///
/// <para>为什么只能在代码里补：Avalonia 的 <c>Slider</c> 没有 WPF 那套 <c>IsMoveToPointEnabled</c>
/// （11.2.8 程序集元数据里查无此属性），轨道左右两块是 <c>RepeatButton</c>，点上去只按 <c>LargeChange</c>
/// 挪一格，而本项目的滑杆从没设过 <c>LargeChange</c>（默认 0 ⇒ 点了等于没反应）。于是能精确落点的目标
/// 只剩那枚 16 宽的滑块头——这就是"能点的位置范围特别小、手感死板"的根因。</para>
///
/// <para>本行为把整条 28 高的轨道变成可点可拖的落点：按在滑块头以外＝当场跳到该点并跟手拖动；
/// 按在滑块头上＝原样交还它自己的拖动手势（免得压一下边缘就先跳掉半格）。</para>
/// </summary>
public class SliderTrackTapBehavior
{
    /// <summary>只用静态成员；类本身仅作为附加属性的宿主类型出现（RegisterAttached 的 TOwner 不能是静态类）。</summary>
    private SliderTrackTapBehavior() { }

    /// <summary>打开本行为。样式里设成 True 即可，不需要窗口侧调用。</summary>
    public static readonly AttachedProperty<bool> MoveToPointProperty =
        AvaloniaProperty.RegisterAttached<SliderTrackTapBehavior, Slider, bool>("MoveToPoint");

    /// <summary>已经接好处理器的标记：样式可能重复应用，防止重复订阅。</summary>
    private static readonly AttachedProperty<bool> WiredProperty =
        AvaloniaProperty.RegisterAttached<SliderTrackTapBehavior, Slider, bool>("Wired");

    /// <summary>本滑杆正被"点轨道"这一趟拖着的标记。</summary>
    private static readonly AttachedProperty<bool> DraggingProperty =
        AvaloniaProperty.RegisterAttached<SliderTrackTapBehavior, Slider, bool>("Dragging");

    static SliderTrackTapBehavior()
    {
        MoveToPointProperty.Changed.AddClassHandler<Slider>((slider, e) =>
        {
            if (e.NewValue is true) Wire(slider);
        });
    }

    public static void SetMoveToPoint(Slider owner, bool value) => owner.SetValue(MoveToPointProperty, value);

    public static bool GetMoveToPoint(Slider owner) => owner.GetValue(MoveToPointProperty);

    private static void Wire(Slider slider)
    {
        if (slider.GetValue(WiredProperty)) return;
        slider.SetValue(WiredProperty, true);

        // Tunnel 相位＝Slider 是这一路的第一站（轨道内容全在它自己的模板里），先判"按在哪儿"再决定插不插手；
        // handledEventsToo 是保险：万一有模板部件先吃掉按下事件，这里照样收得到。
        slider.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, true);
        slider.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, true);
        slider.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, true);
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;
        if (TrackOf(slider) is not { } track) return;

        Point position = e.GetPosition(track);
        // 按在滑块头上：原样交还它自己的拖动手势，本行为不插手
        if (track.Thumb is { } thumb && thumb.Bounds.Contains(position)) return;

        slider.SetValue(DraggingProperty, true);
        e.Pointer.Capture(slider);
        Apply(slider, track, position);
        e.Handled = true;
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Slider slider || !slider.GetValue(DraggingProperty)) return;
        // 左键已在别处松开（捕获被抢走等）：就地收工，不必等 Released
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) { End(slider, e.Pointer); return; }
        if (TrackOf(slider) is not { } track) return;

        Apply(slider, track, e.GetPosition(track));
        e.Handled = true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Slider slider || !slider.GetValue(DraggingProperty)) return;

        End(slider, e.Pointer);
        e.Handled = true;
    }

    private static void End(Slider slider, IPointer pointer)
    {
        slider.SetValue(DraggingProperty, false);
        if (ReferenceEquals(pointer.Captured, slider)) pointer.Capture(null);
    }

    private static Track? TrackOf(Slider slider) =>
        slider.GetVisualDescendants().OfType<Track>().FirstOrDefault();

    /// <summary>
    /// 落点换算：滑块中心的可行程＝轨道长减去滑块宽（Avalonia 的 <c>Track</c> 就是这么排的，两端各让出
    /// 半个滑块），故取点先扣掉这半个、再按比例摊回量程。
    /// </summary>
    private static void Apply(Slider slider, Track track, Point positionInTrack)
    {
        bool vertical = slider.Orientation == Avalonia.Layout.Orientation.Vertical;
        double span = vertical ? track.Bounds.Height : track.Bounds.Width;
        double head = vertical ? track.Thumb?.Bounds.Height ?? 0 : track.Thumb?.Bounds.Width ?? 0;
        double travel = span - head;
        if (travel <= 0) return;

        double at = vertical ? positionInTrack.Y : positionInTrack.X;
        double ratio = Math.Clamp((at - head / 2.0) / travel, 0.0, 1.0);
        slider.Value = Snap(slider, slider.Minimum + ratio * (slider.Maximum - slider.Minimum));
    }

    /// <summary>自己先按刻度取整：Slider 的刻度吸附挂在它内部拖拽那条路上，外部赋 Value 时不保证再过一遍。</summary>
    private static double Snap(Slider slider, double value)
    {
        if (!slider.IsSnapToTickEnabled || slider.TickFrequency <= 0) return value;

        double snapped = slider.Minimum
            + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
        return Math.Clamp(snapped, slider.Minimum, slider.Maximum);
    }
}
