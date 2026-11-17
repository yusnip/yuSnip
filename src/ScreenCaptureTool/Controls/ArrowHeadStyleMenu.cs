using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 箭头头型下拉：<b>箭头标注工具</b>（截图遮罩与贴图浮窗两处参数条）与<b>序号引线箭头</b>共用这一份条目表，
/// 故"箭头一共有哪几档"在界面上只有一处定义，加一档只改 <see cref="ArrowAnnotationStyle"/> 与本表。
///
/// <para>条目<b>只有一格小样、不带档名</b>（2026-09-20 按用户要求去掉——形本身就是这一档的全部内容，文字是
/// 重复信息）；档名只留在 <see cref="Entries"/> 表里给宿主按钮的 ToolTip 用。当前档由那一格小样自己染品牌紫
/// （<see cref="ArrowHeadSwatch.Highlight"/>），不拼 ✓。小样放进 Header 而不是 Icon 槽——Fluent 把图标槽锁死
/// 成 16×16，箭头在那里面看不清。</para>
///
/// <para>皮走 §6.12 的菜单唯一配方（<see cref="MenuFlyout"/>）。</para>
/// </summary>
public sealed class ArrowHeadStyleMenu
{
    /// <summary>档名表，顺序＝<see cref="ArrowAnnotationStyle"/> 的声明顺序（枚举只追加不重排）。</summary>
    private static readonly (ArrowAnnotationStyle Style, string Name)[] Entries =
    {
        (ArrowAnnotationStyle.Designed, "设计箭头"),
        (ArrowAnnotationStyle.Sharp, "尖锐箭头"),
        (ArrowAnnotationStyle.Notch, "凹口箭头"),
        (ArrowAnnotationStyle.Curve, "弧边燕尾"),
        (ArrowAnnotationStyle.Taper, "笔锋箭头"),
        (ArrowAnnotationStyle.Chevron, "开口 V"),
        (ArrowAnnotationStyle.OpenTriangle, "空心三角"),
        (ArrowAnnotationStyle.Diamond, "实心菱形"),
        (ArrowAnnotationStyle.OpenDiamond, "空心菱形"),
        (ArrowAnnotationStyle.Dot, "实心圆点"),
        (ArrowAnnotationStyle.Ring, "空心圆"),
    };

    private readonly MenuItem[] _items;
    private readonly ArrowHeadSwatch[] _swatches;

    /// <summary>直接挂到宿主按钮的 <c>Button.Flyout</c> 上：点击由框架弹菜单并自动 light-dismiss。</summary>
    public MenuFlyout Flyout { get; }

    /// <summary>用户点选了某一档（宿主记住值 + 刷按钮上的小样，不必自己染色——本类已经同步过）。</summary>
    public event Action<ArrowAnnotationStyle>? Picked;

    /// <param name="legacyPath">Designed / Sharp 两档是否按箭头标注工具的模板路径出样：箭头工具那边是，
    /// 序号引线那边不是（引线从不套模板，见 <see cref="ArrowHeadSwatch.LegacyPath"/>）。</param>
    /// <param name="doubleHead">V 形家族那四档是否按"两端各挂一个头"出样：箭头工具那边是（那四档在工具上
    /// 就是双头，见 <see cref="ArrowHeadGeometry.IsDoubleHeadStyle"/>），序号引线那边不是（引线恒单头）。</param>
    public ArrowHeadStyleMenu(bool legacyPath, bool doubleHead)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _items = new MenuItem[Entries.Length];
        _swatches = new ArrowHeadSwatch[Entries.Length];
        for (int i = 0; i < Entries.Length; i++)
        {
            ArrowAnnotationStyle style = Entries[i].Style;
            var swatch = BuildSwatch(style, legacyPath, doubleHead);
            var item = new MenuItem { Header = swatch };
            item.Click += (_, _) =>
            {
                Sync(style);
                Picked?.Invoke(style);
            };
            _items[i] = item;
            _swatches[i] = swatch;
            flyout.Items.Add(item);
        }
        // 条目只剩等宽小样（档名已按用户要求去掉），逐行居中不再错位，故与字号／水印／序号那三枚同挂窄条 + 居中。
        MenuFlyoutPresenterStyle.Attach(_items[0],
            MenuFlyoutPresenterStyle.Compact, MenuFlyoutPresenterStyle.Centered);
        Flyout = flyout;
    }

    /// <summary>
    /// 宿主收起浮层（切工具、摊开工具组）时调用。刻意不直接 <c>Flyout.Hide()</c>：那条作用在<b>从未弹出过</b>
    /// 的 flyout 上不保证是空操作，而这两处时机在程序运行早期就会走到。
    /// </summary>
    public void HideIfOpen()
    {
        if (Flyout.IsOpen) Flyout.Hide();
    }

    /// <summary>
    /// 把「当前档」那一格小样染成品牌紫、其余退回常规图标色（§6.12：颜色是唯一的状态表达）。
    /// 档名已按要求去掉，故这里不再打 <c>MenuItem.current</c> 类——那组规则只染文字，对本菜单已无物可染。
    /// </summary>
    public void Sync(ArrowAnnotationStyle current)
    {
        for (int i = 0; i < _swatches.Length; i++)
            _swatches[i].Highlight = Entries[i].Style == current;
    }

    /// <summary>该档的中文名（宿主按钮 ToolTip 用，档名不再进下拉）；表外取值退回成员名。</summary>
    public static string DisplayName(ArrowAnnotationStyle style)
    {
        foreach (var entry in Entries)
            if (entry.Style == style) return entry.Name;
        return style.ToString();
    }

    static ArrowHeadSwatch BuildSwatch(ArrowAnnotationStyle style, bool legacyPath, bool doubleHead)
        => new()
        {
            Width = 46,
            Height = 18,
            // 行高下限 30 > 小样 18：不给这一句会顶在行上沿（档名在时由那枚 TextBlock 的居中带着走）
            VerticalAlignment = VerticalAlignment.Center,
            Style = style,
            LegacyPath = legacyPath,
            DoubleHead = doubleHead,
        };
}
