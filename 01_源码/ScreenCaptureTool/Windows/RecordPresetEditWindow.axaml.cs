using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScreenCaptureTool.Core.Recording;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 录屏画质预设的编辑窗（2026-09-29）：设置页那一排卡片行只读，双击任意一档（或点「新建预设」）开这一扇。
/// <para>骨架照搬 <c>AiProviderEditWindow</c>——自绘 48 高标题栏 ＋「左＝名称＋说明 / 右＝控件」的字段行 ＋
/// 底栏取消→保存，连 Esc＝取消、Ctrl+Enter＝保存那条 Tunnel 约定也同一条。</para>
/// <para><b>本窗不碰磁盘</b>：只把改好的 <see cref="ResultPreset"/> 与三个 flag 交回设置页，由那边写回整表并走
/// <see cref="RecordPresets.Save"/>（它广播 <see cref="RecordPresets.Changed"/>，录制参数条据此重铺画质下拉）。</para>
/// <para>底栏左侧那一枚按档位分两种：内置档＝「还原默认」（把出厂值填回并当作一次保存；本来就等于出厂时置灰），
/// 自建档＝「删除预设」（只置 <see cref="IsDeleteRequested"/>，真正的删除仍由设置页执行）；
/// 新建途中既没有可还原的默认也没有可删的档，整枚不出现。</para>
/// <para>合法范围只在 <see cref="RecordPresets"/> 那一份：本窗只做"读不到就当没改"的兜底，
/// 夹取过则置 <see cref="WasClamped"/>，由设置页提示一句，界面上不写长解释。</para>
/// </summary>
public partial class RecordPresetEditWindow : Window
{
    private readonly RecordPreset _working;
    private readonly bool _isNew;

    /// <summary>交回设置页的那一份（＝进窗时那份 working copy，保存时把四个量写进它）。</summary>
    public RecordPreset ResultPreset => _working;

    /// <summary>用户是否按了「保存」或「还原默认」。</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>用户是否按了「删除预设」（只有自建档会出现这枚，与 <see cref="IsConfirmed"/> 互斥）。</summary>
    public bool IsDeleteRequested { get; private set; }

    /// <summary>保存时是否被 <see cref="RecordPresets.Normalize"/> 夹取过（设置页据此提示"已调整到最接近的合法值"）。</summary>
    public bool WasClamped { get; private set; }

    public RecordPresetEditWindow(RecordPreset preset, bool isNew)
    {
        InitializeComponent();

        _isNew = isNew;
        _working = preset.Clone();

        TxtWindowTitle.Text = isNew ? "新建画质预设" : "编辑画质预设 · " + _working.Name;
        // 量程一律现取，不把数字抄进 XAML（Core 那份常量是唯一出处）。
        // ⚠ 说明只写"一个数或四个字以内"（用户 2026-09-29：「不要用这么晦涩的，你就写几个字就行」）：
        //    机制解释（"峰值自动 ×1.25""原生＝按选区像素、等比缩小不放大"）一律不上界面。
        TxtNameHint.Text = $"最多 {RecordPresets.MaxNameLength} 个字";
        TxtFpsHint.Text = $"{RecordPresets.MinFps}~{RecordPresets.MaxFps}";
        TxtBitrateHint.Text = $"{RecordPresets.MinBitrateKbps / 1000.0:0.#}~{RecordPresets.MaxBitrateKbps / 1000}";

        LoadFields();
        SetupActionButton();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>把 working copy 摊到四枚控件上。「还原默认」也重走这一条，不写第二份回显。</summary>
    private void LoadFields()
    {
        TxtName.MaxLength = RecordPresets.MaxNameLength;
        TxtName.Text = _working.Name;
        TxtFps.Text = _working.Fps.ToString(CultureInfo.InvariantCulture);
        TxtBitrate.Text = (_working.BitrateKbps / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

        // 分辨率上限只给六档（0＝原生），不给自由输入：非常规高度对体积/清晰度没有额外意义
        CmbHeight.Items.Clear();
        ComboBoxItem? toSelect = null;
        foreach (int h in RecordPresets.HeightOptions)
        {
            var item = new ComboBoxItem { Content = h == 0 ? "原生" : h + "P", Tag = h };
            CmbHeight.Items.Add(item);
            if (h == _working.MaxHeight) toSelect = item;
        }
        CmbHeight.SelectedItem = toSelect;   // 先定选中再交给用户，避免初始化那一下被当成改动
    }

    private void SetupActionButton()
    {
        if (_isNew) return;   // 新建途中：没有出厂值可还原，也没有已落表的档可删
        if (_working.IsBuiltIn)
        {
            BtnAction.Classes.Add("secondary");
            BtnAction.Content = "还原默认";
            BtnAction.IsEnabled = !RecordPresets.IsFactoryDefault(_working);
            ToolTip.SetTip(BtnAction, "把这一档填回出厂值");
        }
        else
        {
            BtnAction.Classes.Add("danger");
            BtnAction.Classes.Add("ghost");
            BtnAction.Content = "删除预设";
        }
        BtnAction.IsVisible = true;
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (_working.IsBuiltIn)
        {
            var f = RecordPresets.FactoryDefault(_working.Id);
            if (f == null) return;
            _working.Name = f.Name;
            _working.Fps = f.Fps;
            _working.MaxHeight = f.MaxHeight;
            _working.BitrateKbps = f.BitrateKbps;
            LoadFields();
            IsConfirmed = true;
            Close();
            return;
        }
        IsDeleteRequested = true;
        Close();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var before = _working.Clone();

        // 读不到的那一项就保持原值（用户输了 "abc" 不该把帧率清成 0）
        string name = (TxtName.Text ?? "").Trim();
        if (name.Length > 0) _working.Name = name;   // 留空＝不改名：Normalize 给自建档另起的是「自定义」，不是他原来的名字
        if (int.TryParse(TxtFps.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps)) _working.Fps = fps;
        if (CmbHeight.SelectedItem is ComboBoxItem { Tag: int h }) _working.MaxHeight = h;
        if (double.TryParse(TxtBitrate.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mbps))
            _working.BitrateKbps = (int)Math.Round(mbps * 1000);

        var n = RecordPresets.Normalize(_working);
        _working.Name = n.Name;
        _working.Fps = n.Fps;
        _working.MaxHeight = n.MaxHeight;
        _working.BitrateKbps = n.BitrateKbps;
        // 比的是"进窗时的那一档"：用户真越界才提示，没动过的合规值不会被莫名警告一次
        WasClamped = n.Fps != before.Fps || n.BitrateKbps != before.BitrateKbps || n.MaxHeight != before.MaxHeight;

        IsConfirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        Close();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            try { BeginMoveDrag(e); } catch { }
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelClick(sender, e);
        }
        else if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            e.Handled = true;
            OnSaveClick(sender, e);
        }
    }
}
