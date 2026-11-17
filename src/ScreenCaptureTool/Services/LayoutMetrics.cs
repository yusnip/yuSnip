using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 「同一拍里改内容 → 量尺寸 → 按尺寸定位」这类浮层定位的共用度量口径。
///
/// 存在理由（两个都是实测出来的，不是猜的）：
/// ① 元素处于 <c>IsVisible=false</c> 时显式 <c>Measure()</c> 得到 <c>0×0</c> —— 于是"先量宽再置可见"的
///    写法每一帧都按零宽定位，浮层整条偏出自身一个尺寸（截图遮罩的拖动信息胶囊踩过）。
/// ② <see cref="Layoutable.InvalidateMeasure"/> <b>只作用于元素自身</b>那一层，子孙的 measure 缓存照旧，
///    中间那层面板会直接把刚改过的文字宽度挡在缓存里 —— 紧随的 <c>Measure()</c> 返回<b>上一拍</b>的宽度。
///    真控件探针实测：只失效自身时每帧仍偏 3~19 DIP、内容变窄那帧反向偏 62 DIP；递归失效后逐帧归零。
///
/// 消费方：截图遮罩 <c>CaptureWindow</c> 的六处同帧量宽定位（拖动信息胶囊 / 信息条 / 取色预览气泡 /
/// 工具参数下拉菜单 / 快捷键小抄的行网格与胶囊本体）与 <c>AnnotationToolbar.ReflowBars</c>（贴图标注浮窗）。
/// 用途边界：只在"内容刚刚改完、本帧就要拿宽度去定位"时调用；内容不随帧变的固定条（如截图侧主工具栏
/// <c>ActionBar</c> 与整叠摆位 <c>CaptureWindow.LayoutOverlayStack</c> 里的一级那一条）不必调——
/// 那里读缓存本来就是对的，而整棵子树重测是每帧实打实的开销（<c>ActionBar</c> 约 120 节点实测 249µs）。
/// </summary>
internal static class LayoutMetrics
{
    /// <summary>整棵子树递归失效 measure。见类型注释 ②。</summary>
    internal static void InvalidateMeasureDeep(Visual node)
    {
        if (node is Layoutable layoutable) layoutable.InvalidateMeasure();
        foreach (var child in node.GetVisualChildren()) InvalidateMeasureDeep(child);
    }

    /// <summary>
    /// 量出<b>本帧</b>的实际尺寸：递归失效 + <c>Measure(Infinity)</c>，返回 DesiredSize。
    /// ⚠ 调用前目标必须已经 <c>IsVisible=true</c>，否则量回来是 0×0（见类型注释 ①）。
    /// </summary>
    internal static Size MeasureNow(Control target)
    {
        InvalidateMeasureDeep(target);
        target.Measure(Size.Infinity);
        return target.DesiredSize;
    }
}
