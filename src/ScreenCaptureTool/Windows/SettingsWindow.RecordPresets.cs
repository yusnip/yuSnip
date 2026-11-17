using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Recording;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 设置 → 交互与行为 → 录屏 →「画质预设」卡（2026-09-29 第五版＝<b>行只读 ＋ 双击开编辑窗</b>，
/// 卡片与编辑窗都照「AI 模型」那套范式）。
/// <para>一行一档，四个量<b>直接摊在行上</b>：帧率／分辨率上限／码率／每分钟体积，各是一块"13.5 号 SemiBold 数字 ＋ 11 号字段名"
/// 的数字块（<c>recStatVal</c>／<c>recStatCap</c>），六档同列上下对齐、一眼可比。<b>改参数一律在双击弹出的
/// <see cref="RecordPresetEditWindow"/> 里做</b>。</para>
/// <para>🔴 前四版把编辑态长在列表行上（点行就地展开 → 主从双栏 → 双栏精修 → 卡片流＋就地展开）都被实报否掉，
/// 机制上的病根是同一条：单击一次就 <c>Rebuild</c> 整表（<c>Children.Clear()</c>），行既当读数又当表单，
/// 于是焦点每点一次丢一次、<c>DoubleTapped</c> 的第二下永远落在新建的控件实例上。本文件从此不在行上挂单击。</para>
/// <para>行上唯一的交互是「显示」勾选（只切可见性、<b>不重铺</b>），其余一律进编辑窗。
/// ⚠ 界面上<b>不写长解释</b>（用户 2026-09-29：「右边标注文字写得太多了，不要写那么细的，默认用户懂」）。</para>
/// <para>落盘仍走本页既有那一条链：编辑窗交回结果 → 写回 <c>_recPresets</c> → <see cref="SaveRecPresets"/>
/// → <see cref="RecordPresets.Save"/> 广播 <see cref="RecordPresets.Changed"/>（录制参数条据此重铺画质下拉）。
/// <b>编辑窗本身不碰磁盘</b>，与 <c>AiProviderEditWindow</c> 同构。</para>
/// <para>合法范围只在 Core 那一份（<see cref="RecordPresets"/> 的常量），本文件与编辑窗都只负责回显与夹取后的提示；
/// 行上的读数与徽标判据也共用 Core 那一份（<see cref="RecordPresets.IsFactoryDefault"/>）。</para>
/// <para>🔴 行底色/描边一律走样式类（<c>Border.recPresetItem</c> 一族，声明在 <c>SettingsWindow.axaml</c> 窗口私有样式里），
/// 代码只增删 <c>Classes</c>——初版用 <c>FindBrush</c> 在 C# 里取画刷，违反 <c>设计规范 §2</c>「C# 无画刷原则」，已收口。</para>
/// </summary>
public partial class SettingsWindow
{
    private List<RecordPreset> _recPresets = new();

    private void RebuildRecPresetList()
    {
        if (RecPresetListPanel == null) return;
        _recPresets = RecordPresets.Load();
        RecPresetListPanel.Children.Clear();

        foreach (var p in _recPresets)
            RecPresetListPanel.Children.Add(BuildRecPresetRow(p));

        if (BtnRecPresetAdd != null)
        {
            int userCount = _recPresets.Count(x => !x.IsBuiltIn);
            BtnRecPresetAdd.IsEnabled = userCount < RecordPresets.MaxUserPresets;
            ToolTip.SetTip(BtnRecPresetAdd, BtnRecPresetAdd.IsEnabled
                ? "以「标准」的参数为起点新建一档"
                : $"自定义预设最多 {RecordPresets.MaxUserPresets} 个");
        }
    }

    // —— 一行一档：勾选｜档名｜四块读数｜徽标（整行只读，双击开窗）——

    /// <summary>档名列宽：8 字满档名实测 121 DIP（雅黑 13.5），取 124。定宽是"六档读数同列上下对齐"的前提。</summary>
    private const double RowNameWidth = 124;

    private static Control Stat(string value, string caption, double width) => new StackPanel
    {
        Spacing = 1,
        Width = width,
        Children =
        {
            new TextBlock { Text = value, Classes = { "recStatVal" } },
            new TextBlock { Text = caption, Classes = { "recStatCap" } },
        }
    };

    /// <summary>每分钟成品体积（MB）：码率 × 7.5，与 <see cref="RecordPresets"/> 那套峰值/缓冲比例同源，写在这句话里当读数用。</summary>
    private static double VolumePerMinute(int bitrateKbps) => bitrateKbps / 1000.0 * 7.5;

    private Control BuildRecPresetRow(RecordPreset p)
    {
        var chk = new CheckBox
        {
            IsChecked = p.Visible,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(chk, "在录制参数条的画质下拉里显示");
        chk.IsCheckedChanged += (_, _) => SetRecPresetVisible(p.Id, chk.IsChecked == true);

        var name = new TextBlock
        {
            Text = p.Name,
            Classes = { "fieldLabel" },
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Width = RowNameWidth,
            Margin = new Thickness(8, 0, 0, 0),
        };
        // 四个量摊在行上（数字 13.5 号 SemiBold＋字段名 11 号）。每块定宽 ⇒ 上下档同列可比；
        // 体积不是设置项而是现算读数，与编辑窗里那句说明同源。
        // ⚠ 下面四块定宽 52/84/56/56 是数值还是 16 号字时实测的，改成 13.5 后读数变窄，
        //    暂按原值保留（列位与徽标位置不变）；若实测四列之间偏松，收这几处宽度即可，不要再动字号。
        var stats = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0),
            Children =
            {
                Stat(p.Fps.ToString(CultureInfo.InvariantCulture), "FPS", 52),
                Stat(p.MaxHeight == 0 ? "原生" : p.MaxHeight + "P", "分辨率上限", 84),
                Stat((p.BitrateKbps / 1000.0).ToString("0.##", CultureInfo.InvariantCulture), "Mbps", 56),
                Stat(VolumePerMinute(p.BitrateKbps).ToString("0.#", CultureInfo.InvariantCulture), "MB/分钟", 56),
            },
        };
        var tag = new Border
        {
            Classes = { "badge" },
            VerticalAlignment = VerticalAlignment.Center,
            // 徽标文字挂全局类 badgeText（11 + SctBadgeTextBrush），不在代码里写 FontSize（设计稿 P2）
            Child = new TextBlock { Text = p.IsBuiltIn ? (RecordPresets.IsFactoryDefault(p) ? "内置" : "内置 · 已修改") : "自定义", Classes = { "badgeText" } },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        grid.Children.Add(chk);
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        Grid.SetColumn(stats, 2); grid.Children.Add(stats);
        Grid.SetColumn(tag, 3); grid.Children.Add(tag);

        var row = new Border
        {
            Classes = { "recPresetItem" },
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        // 🔴 只挂 DoubleTapped，不挂 PointerPressed：从前那句单击就 Rebuild，第二下落在重建出来的新 Border 上，
        //    DoubleTapped 一次都不会触发。行也不再挂 .selected——没有"就地展开的那一行"了，就没有选中态。
        row.DoubleTapped += (_, e) =>
        {
            // 双击勾选框那一下归勾选框（切显示），不开窗
            if (e.Source is Visual v && (ReferenceEquals(v, chk) || IsDescendantOf(v, chk))) return;
            e.Handled = true;
            _ = OpenRecPresetEditorAsync(p, isNew: false);
        };
        return row;
    }

    private static bool IsDescendantOf(Visual v, Visual ancestor)
    {
        for (Visual? cur = v; cur != null; cur = cur.GetVisualParent())
            if (ReferenceEquals(cur, ancestor)) return true;
        return false;
    }

    /// <summary>开编辑窗（新建与编辑共用这一条）：<b>窗不写盘</b>，交回结果后才由本文件落盘。
    /// 取消＝什么都不发生（连重铺都不做，行上的焦点不回跳）。</summary>
    private async Task OpenRecPresetEditorAsync(RecordPreset working, bool isNew)
    {
        var win = new RecordPresetEditWindow(working, isNew);
        await win.ShowDialog(this);

        if (win.IsDeleteRequested)
        {
            _recPresets.RemoveAll(x => x.Id == working.Id);
            SaveRecPresets();
            ShowRecPresetHint("已删除「" + working.Name + "」");
            return;
        }
        if (!win.IsConfirmed) return;

        var result = win.ResultPreset;
        var live = isNew ? null : _recPresets.FirstOrDefault(x => x.Id == working.Id);
        if (live == null)
        {
            // 新建：按钮那一步已经挡过上限量，这里只兜"窗开着的时候又从别处多了一档"
            if (_recPresets.Count(x => !x.IsBuiltIn) >= RecordPresets.MaxUserPresets)
            {
                ShowRecPresetHint($"自定义预设最多 {RecordPresets.MaxUserPresets} 个，这一档没存进去");
                RebuildRecPresetList();
                return;
            }
            _recPresets.Add(result);
        }
        else
        {
            // 只回写四个量：Visible 归行上那枚勾选框，一处一个写入方
            live.Name = result.Name;
            live.Fps = result.Fps;
            live.MaxHeight = result.MaxHeight;
            live.BitrateKbps = result.BitrateKbps;
        }
        SaveRecPresets();
        if (win.WasClamped) ShowRecPresetHint("超出允许范围，已调整到最接近的合法值");
    }

    // —— 行上那枚勾选（唯一在行上直接生效、且不重铺的操作）——

    private void SetRecPresetVisible(string id, bool visible)
    {
        var p = _recPresets.FirstOrDefault(x => x.Id == id);
        if (p == null || p.Visible == visible) return;
        p.Visible = visible;
        // 最后一档也不许藏：参数条下拉不能是空的
        if (!_recPresets.Any(x => x.Visible))
        {
            p.Visible = true;
            ShowRecPresetHint("至少保留一个在参数条上显示的预设");
            RebuildRecPresetList();
            return;
        }
        SaveRecPresets(rebuild: false);
    }

    private void OnRecPresetAddClick(object? sender, RoutedEventArgs e)
    {
        if (_recPresets.Count(x => !x.IsBuiltIn) >= RecordPresets.MaxUserPresets) return;
        var std = _recPresets.FirstOrDefault(x => x.Id == nameof(RecordQuality.Standard)) ?? RecordPresets.FactoryDefault(nameof(RecordQuality.Standard))!;
        int n = 1;
        while (_recPresets.Any(x => x.Name == "自定义" + n)) n++;
        var p = new RecordPreset
        {
            Id = RecordPresets.NewUserId(),
            Name = "自定义" + n,
            Fps = std.Fps,
            MaxHeight = std.MaxHeight,
            BitrateKbps = std.BitrateKbps,
            Visible = true,
        };
        // 新建也走同一扇窗：保存才落表，取消什么都不留（从前是"直接插一行＋切展开"，取消会留下一档白档）
        _ = OpenRecPresetEditorAsync(p, isNew: true);
    }

    private void SaveRecPresets(bool rebuild = true)
    {
        RecordPresets.Save(_recPresets);
        AppLogger.Info("设置：录屏画质预设已保存 " + string.Join("；",
            _recPresets.Select(x => $"{x.Id}={x.Name}/{x.Fps}fps/{x.MaxHeight}p/{x.BitrateKbps}k/{(x.Visible ? "显" : "隐")}")));
        if (rebuild) RebuildRecPresetList();
    }

    private void ShowRecPresetHint(string text)
    {
        try { StickerToastWindow.Show(text, this); } catch { }
    }
}
