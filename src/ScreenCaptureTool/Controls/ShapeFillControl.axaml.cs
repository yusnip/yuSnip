using System;
using Avalonia.Controls;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 形状工具的「填充」参数（一枚勾选 + 一条浓淡滑杆），截图遮罩与贴图标注浮窗共用这一份。
/// 对外只有一个读数 <see cref="FillOpacity"/>：未勾选恒为 0＝不填充，勾选后＝滑杆值 ÷ 100。
/// 这里刻意没有"填充色"这一格——填充色恒等于线条颜色（与 <c>RectangleShape.FillOpacity</c> 同一口径），
/// 多存一份配色就会多一处会漂移的色值。
/// </summary>
public partial class ShapeFillControl : UserControl
{
    /// <summary>程序性写值（恢复记忆 / 回显选中图元）时的抑制位：摆控件不该再反手发一次变更事件。</summary>
    private bool _suppress;

    public ShapeFillControl()
    {
        InitializeComponent();
        FillCheck.IsCheckedChanged += (_, _) =>
        {
            FillRow.IsEnabled = Filled;
            if (!_suppress) FillChanged?.Invoke();
        };
        FillSlider.ValueChanged += (_, _) =>
        {
            FillText.Text = Math.Round(FillSlider.Value).ToString();
            if (!_suppress) FillChanged?.Invoke();
        };
    }

    /// <summary>用户改动勾选或浓淡。宿主据此回灌选中图元并重绘预览。</summary>
    public event Action? FillChanged;

    public bool Filled => FillCheck.IsChecked == true;

    /// <summary>当前填充不透明度 0～1；未勾选＝0＝不填充。</summary>
    public float FillOpacity => Filled ? (float)(FillSlider.Value / 100.0) : 0f;

    /// <summary>
    /// 写口（读上次会话的记忆值、或选中图元时回显它自己的填充）：只摆控件，
    /// <b>不</b>触发 <see cref="FillChanged"/>——否则"选中一个已填充的形状"会反过来把工具栏值又灌回图元，
    /// 与 <c>CaptureWindow.SyncShapeCornerRadius</c> 同一条"只写字段、不触发回灌"的规矩。
    /// </summary>
    public void SetState(bool filled, double opacityPercent)
    {
        _suppress = true;
        try
        {
            FillCheck.IsChecked = filled;
            FillSlider.Value = Math.Clamp(Math.Round(opacityPercent), FillSlider.Minimum, FillSlider.Maximum);
            FillText.Text = Math.Round(FillSlider.Value).ToString();
        }
        finally
        {
            _suppress = false;
        }
        FillRow.IsEnabled = Filled;
    }
}
