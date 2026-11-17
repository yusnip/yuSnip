using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 水印参数条（版式＝设计稿《水印标注-参数面板-方案对比》的丙′ ＋ D1 八档文字菜单：条上只有内容、位置
/// 与一枚参数按钮，不透明度／大小／间距三行滑杆全在那枚按钮下挂的面板里）。截图遮罩独占；量程与默认值的
/// 唯一出处是 <see cref="WatermarkConfig"/> 的那组常量，本类只把常量铺到滑杆上，不再各写一份数字。
///
/// <para>位置下拉走 §6.12 菜单唯一配方：条目按 <see cref="WatermarkPosition"/> 枚举现铺，档名取自
/// <see cref="WatermarkEffect.DisplayName"/>，当前档挂 <c>MenuItem.current</c> 染品牌紫（不拼 ✓），
/// 宽度与对齐走全局的窄条 + 居中两条变体（纯文字短档名，挂类统一由
/// <see cref="MenuFlyoutPresenterStyle"/> 负责，与字号菜单同源）。</para>
/// </summary>
public partial class WatermarkOptionsControl : UserControl
{
    /// <summary>框尾省略号位占掉的宽度，与 XAML 里 <c>OverflowHint</c> 的 <c>Width</c> 同值。</summary>
    private const double EllipsisSlotWidth = 12;

    private readonly MenuFlyout _positionMenu;
    private readonly MenuItem[] _positionItems;
    private WatermarkPosition _position = WatermarkPosition.Tile;

    /// <summary>用户在打字（内容框持焦）。宿主**必须**把它并进"正在打字"总闸，理由与序号起始值那格完全相同：
    /// 遮罩窗口的 KeyDown 走 Tunnel 相位，会抢在持焦输入框之前看到按键。</summary>
    private bool _editingContent;

    /// <summary>面板打开期间挂在窗口根上的"点外面即收"处理器（与笔迹两枚菜单同一配方）。</summary>
    private InputElement? _dismissHost;

    /// <summary>铺记忆值期间压制事件，免得"恢复上次参数"被当成一次用户改动。</summary>
    private bool _suppress;

    /// <summary>任一参数被用户改动。宿主（截图会话）当场把值回灌进 AppSettings 并重画遮罩预览。</summary>
    public event Action? Changed;

    public WatermarkOptionsControl()
    {
        InitializeComponent();

        OpacitySlider.Minimum = WatermarkConfig.MinOpacity;
        OpacitySlider.Maximum = WatermarkConfig.MaxOpacity;
        SizeSlider.Minimum = WatermarkConfig.MinSize;
        SizeSlider.Maximum = WatermarkConfig.MaxSize;
        SpacingSlider.Minimum = WatermarkConfig.MinSpacing;
        SpacingSlider.Maximum = WatermarkConfig.MaxSpacing;

        OpacitySlider.Value = WatermarkConfig.DefaultOpacity;
        SizeSlider.Value = WatermarkConfig.DefaultSize;
        SpacingSlider.Value = WatermarkConfig.DefaultSpacing;

        OpacitySlider.ValueChanged += (_, _) => { OpacityText.Text = ((int)OpacitySlider.Value).ToString(); RaiseChanged(); };
        SizeSlider.ValueChanged += (_, _) => { SizeText.Text = ((int)SizeSlider.Value).ToString(); RaiseChanged(); };
        SpacingSlider.ValueChanged += (_, _) => { SpacingText.Text = ((int)SpacingSlider.Value).ToString(); RaiseChanged(); };
        ContentBox.TextChanged += (_, _) => { SyncOverflowHint(); RaiseChanged(); };
        // 首帧之前 Bounds.Width 还是 0，量不出是否溢出；框宽钉死不再变，故只在尺寸变化时补算这一次
        ContentBox.SizeChanged += (_, _) => SyncOverflowHint();
        ContentBox.GotFocus += (_, _) => _editingContent = true;
        ContentBox.LostFocus += (_, _) => _editingContent = false;

        // 位置菜单：条目表＝枚举表，加一档只改 WatermarkPosition 与它的 DisplayName
        _positionMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _positionItems = new MenuItem[Enum.GetValues<WatermarkPosition>().Length];
        foreach (WatermarkPosition value in Enum.GetValues<WatermarkPosition>())
        {
            WatermarkPosition captured = value;
            var item = new MenuItem
            {
                Header = new TextBlock { Text = WatermarkEffect.DisplayName(captured), VerticalAlignment = VerticalAlignment.Center },
            };
            item.Click += (_, _) => PickPosition(captured);
            _positionItems[(int)value] = item;
            _positionMenu.Items.Add(item);
        }
        MenuFlyoutPresenterStyle.Attach(_positionItems[0],
            MenuFlyoutPresenterStyle.Compact, MenuFlyoutPresenterStyle.Centered);
        BtnPosition.Flyout = _positionMenu;

        // 面板开关期间挂/摘窗口根的"点外面即收"钩子（本面板刻意不用 Popup 自带的轻点关闭，理由见 XAML 那段）
        AdvancedPopup.PropertyChanged += (_, args) =>
        {
            if (args.Property != Popup.IsOpenProperty) return;
            if (AdvancedPopup.IsOpen) AttachDismissHook();
            else DetachDismissHook();
        };
        DetachedFromVisualTree += (_, _) => DetachDismissHook();

        SyncPosition();
    }

    /// <summary>内容框此刻是否持焦（宿主并入打字总闸用）。</summary>
    public bool IsEditingContent => _editingContent;

    /// <summary>有内容才盖章（留空＝不盖也不提示，但按钮照样能点亮、参数照样能调）。</summary>
    public bool HasContent => !string.IsNullOrEmpty(ContentBox.Text);

    /// <summary>收起下挂面板与位置菜单（宿主切工具、收起参数条时调用）。</summary>
    public void ClosePopups()
    {
        if (AdvancedPopup.IsOpen) AdvancedPopup.IsOpen = false;
        if (_positionMenu.IsOpen) _positionMenu.Hide();
    }

    /// <summary>把记忆值铺上控件，不发 <see cref="Changed"/>。</summary>
    public void ApplyDefaults(AppSettings? s)
    {
        if (s == null) return;

        _suppress = true;
        try
        {
            ContentBox.Text = s.WatermarkText ?? string.Empty;
            _position = WatermarkEffect.TryParsePosition(s.WatermarkPosition, out WatermarkPosition p) ? p : WatermarkPosition.Tile;
            OpacitySlider.Value = Math.Clamp(s.WatermarkOpacity, WatermarkConfig.MinOpacity, WatermarkConfig.MaxOpacity);
            SizeSlider.Value = Math.Clamp(s.WatermarkSize, WatermarkConfig.MinSize, WatermarkConfig.MaxSize);
            SpacingSlider.Value = Math.Clamp(s.WatermarkSpacing, WatermarkConfig.MinSpacing, WatermarkConfig.MaxSpacing);
            OpacityText.Text = ((int)OpacitySlider.Value).ToString();
            SizeText.Text = ((int)SizeSlider.Value).ToString();
            SpacingText.Text = ((int)SpacingSlider.Value).ToString();
            SyncPosition();
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>把当前各项写回 AppSettings（宿主既在改动当场调、也会在会话收尾的 <c>CaptureAnnotationDefaults</c> 里再调一次）。
    /// 开关刻意不在这里——<c>WatermarkEnabled</c> 不落盘。</summary>
    public void CaptureDefaults(AppSettings? s)
    {
        if (s == null) return;

        s.WatermarkText = ContentBox.Text ?? string.Empty;
        s.WatermarkPosition = _position.ToString();
        s.WatermarkOpacity = (int)Math.Round(OpacitySlider.Value);
        s.WatermarkSize = (int)Math.Round(SizeSlider.Value);
        s.WatermarkSpacing = (int)Math.Round(SpacingSlider.Value);
    }

    /// <summary>
    /// 当前各项的快照，供遮罩预览与三路成品出口现取现用。字色不在本控件里——水印复用全局标注色，
    /// 故由宿主把它手上的那枚颜色传进来（整条链路上只有这一处需要知道颜色从哪来）。
    /// </summary>
    public WatermarkConfig BuildConfig(int colorArgb) => new WatermarkConfig
    {
        Text = ContentBox.Text ?? string.Empty,
        Position = _position,
        OpacityPercent = (int)Math.Round(OpacitySlider.Value),
        SizePx = (int)Math.Round(SizeSlider.Value),
        SpacingPercent = (int)Math.Round(SpacingSlider.Value),
        ColorArgb = colorArgb,
    };

    void OnAdvancedClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        AdvancedPopup.IsOpen = !AdvancedPopup.IsOpen;
    }

    private void AttachDismissHook()
    {
        if (_dismissHost != null || TopLevel.GetTopLevel(this) is not { } host) return;
        _dismissHost = host;
        host.AddHandler(InputElement.PointerPressedEvent, OnDismissPressed, RoutingStrategies.Tunnel);
    }

    private void DetachDismissHook()
    {
        _dismissHost?.RemoveHandler(InputElement.PointerPressedEvent, OnDismissPressed);
        _dismissHost = null;
    }

    /// <summary>
    /// 窗口根 Tunnel 按下（面板开着才挂）：两件事都只在这一路第一站做，判据按**几何**不按命中——
    /// 置灰行整棵子树都不吃命中，按上去"命中谁"说不准，而它的屏幕矩形是确定的。
    /// ① 按在置灰的「间距」那一行那一带＝把这一下原地吃掉：面板不关、画布也收不到（全屏遮罩上漏下去
    ///    就是挪选区／另起新框，2026-09-20 用户报的"间距点不到、点了穿透到下面"正是这条）；
    /// ② 按在面板与开关钮之外＝收起面板，但**不吞事件**（与 <c>CaptureWindow.OnBrushMenusDismissPressed</c>
    ///    同口径：吞掉会改动画布起笔语义）。
    /// ⚠ 开关钮算"自己人"：否则按下这一下先收面板，紧接着的 <see cref="OnAdvancedClick"/> 读到
    /// <c>IsOpen==false</c> 又把它弹回来，表现为"那枚按钮点两下也关不掉面板"。
    /// </summary>
    private void OnDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        // 该行自身坐标系里的矩形（Bounds 的 X/Y 是相对父面板的偏移，与下面这个相对本行的点位不同源）
        if (!SpacingRow.IsEnabled && new Rect(SpacingRow.Bounds.Size).Contains(e.GetPosition(SpacingRow)))
        {
            e.Handled = true;
            return;
        }

        if (e.Source is Visual source
            && (ReferenceEquals(source, BtnAdvanced)
                || source.GetVisualAncestors().Any(a => ReferenceEquals(a, AdvancedPanel)))) return;

        AdvancedPopup.IsOpen = false;
    }

    private void PickPosition(WatermarkPosition position)
    {
        _position = position;
        SyncPosition();
        RaiseChanged();
    }

    /// <summary>摆好按钮上的档名、菜单里当前档的染色，以及「间距」那一行的可用态（非平铺整行禁用）。</summary>
    private void SyncPosition()
    {
        TxtPosition.Text = WatermarkEffect.DisplayName(_position);
        for (int i = 0; i < _positionItems.Length; i++)
            _positionItems[i].Classes.Set("current", i == (int)_position);

        bool tiled = _position == WatermarkPosition.Tile;
        SpacingRow.IsEnabled = tiled;
        ToolTip.SetTip(BtnAdvanced, tiled ? "不透明度、大小与间距" : "不透明度与大小（间距只作用在平铺档）");
    }

    /// <summary>
    /// 内容太长装不下时亮出框尾那枚省略号位。Avalonia 的 <c>TextBox</c> 没有 <c>TextTrimming</c>
    /// （11.2.8 元数据里只有 <c>TextBlock</c>/<c>ContentPresenter</c> 有这一项），溢出时字要么整条滚、
    /// 要么裁断在半笔上，故量一次实宽给个明确的"后面还有字"。整枚水印本来就实时画在遮罩上，全串看得见。
    /// </summary>
    private void SyncOverflowHint()
    {
        string text = ContentBox.Text ?? string.Empty;
        double budget = ContentBox.Bounds.Width
            - ContentBox.BorderThickness.Left - ContentBox.BorderThickness.Right
            - ContentBox.Padding.Left - ContentBox.Padding.Right
            - EllipsisSlotWidth;
        bool overflow = false;
        if (text.Length > 0 && budget > 0)
        {
            // 前景刷只是 FormattedText 的必填项，这里只取它的度量
            var typeface = new Typeface(ContentBox.FontFamily, ContentBox.FontStyle, ContentBox.FontWeight);
            var measured = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, ContentBox.FontSize, Brushes.White);
            overflow = measured.Width > budget;
        }
        OverflowHint.IsVisible = overflow;
    }

    private void RaiseChanged()
    {
        if (_suppress) return;
        Changed?.Invoke();
    }
}
