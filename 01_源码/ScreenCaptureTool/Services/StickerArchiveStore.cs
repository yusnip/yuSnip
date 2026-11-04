using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Windows;
using Annotations = ScreenCaptureTool.Core.Capture.Annotations;
using GdiColor = System.Drawing.Color;
using AvaColor = Avalonia.Media.Color;

namespace ScreenCaptureTool.Services;

/// <summary>贴图窗口摆放现场（物理像素 + 无单位倍率），恢复时逐项回灌。</summary>
public sealed class StickerArchiveState
{
    /// <summary>窗口左上角（屏幕物理像素）。</summary>
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>窗口外框尺寸（物理像素，含阴影/透明边距）。</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>用户缩放倍率（与底图物理像素相乘得到显示尺寸）。</summary>
    public double Scale { get; set; } = 1.0;
    public bool Locked { get; set; }
    public bool Penetrate { get; set; }
    public bool Shadow { get; set; } = true;
    /// <summary>用户是否用 Shift+Y 手动切过本贴图的阴影（2026-10-01，issue #50）。
    /// true ⇒ 恢复后只认 <see cref="Shadow"/>，阴影档位（Off/On/Auto）不再参与；
    /// false ⇒ 恢复后重新走档位判据。缺了这一位，用户的手动选择关窗即丢。</summary>
    public bool ShadowManual { get; set; }
    /// <summary>这张图截图时「阴影」有没有被勾选（2026-10-01，issue #50）。
    /// `null`＝无从得知（剪贴板/历史/外来图/脚本/文本贴图），Auto 档下回落为画阴影。
    /// ⚠ 与 <see cref="StickerArchiveCard.BakedShadow"/> 是两件事：那个是"像素里真有一圈阴影带"
    /// 的物理事实、会因原地换图失效，这个是用户意图、换图后必须留住。</summary>
    public bool? CaptureShadowIntent { get; set; }
    public bool Thumbnail { get; set; }
    /// <summary>缩略态取景框视口偏移（DIP，恒 ≤0）。窗口外框尺寸已在 Width/Height 里，
    /// 缺这两个数就只能回到"以鼠标为中心"的默认取景，用户框好的那一片会跑。</summary>
    public double ThumbViewportX { get; set; }
    public double ThumbViewportY { get; set; }
}

/// <summary>底图外缘的烘焙效果参数（位图物理像素）。恢复后窗体阴影的基准线要靠它对齐。</summary>
public sealed class StickerArchiveCard
{
    public double ContentRadiusPx { get; set; }
    public double StrokeWidthPx { get; set; }
    /// <summary>底图外圈自带一圈烘焙阴影带 ⇒ 恢复时阴影带下垫棋盘格，带宽由像素探针回读（窗体阴影不让位，两种阴影并存）。</summary>
    public bool BakedShadow { get; set; }
}

/// <summary>
/// 一张贴图关闭时的完整现场。
///
/// 底图**不另存**：历史里 <c>history/贴图/{HistoryId}.png</c> 那张就是干净底图，
/// 标注是独立图元层、坐标锚在底图物理像素，恢复＝重放图元。
/// </summary>
public sealed class StickerArchive
{
    /// <summary>与历史条目同 Id，也是存档文件名。</summary>
    public string HistoryId { get; set; } = "";
    /// <summary>image / text / markdown / color。文本类只影响"能不能再当文本编辑"，底图一律走历史 PNG。</summary>
    public string Kind { get; set; } = "image";
    /// <summary>文本 / Markdown / 颜色贴图的源文本，图片贴图为空。</summary>
    public string? Text { get; set; }
    public DateTime ClosedAt { get; set; }
    public StickerArchiveState State { get; set; } = new();
    public StickerArchiveCard Card { get; set; } = new();
    public List<Annotations.AnnotationShape> Shapes { get; set; } = new();
    public List<TextLayerSnapshot> Texts { get; set; } = new();
}

/// <summary>
/// 贴图存档读写（<c>%APPDATA%/yuSnip/history/贴图存档/{历史Id}.json</c>，一张贴图一个文件）。
///
/// 目录名归 <see cref="CaptureHistoryStore"/>（历史条目删除时要把存档一起带走），
/// 格式归本类。NativeAOT 下没有反射式序列化，写用 <see cref="Utf8JsonWriter"/>、
/// 读用 <see cref="JsonDocument"/> + <c>TryGetProperty</c>，与项目既有 JSON 口径一致。
///
/// 全部方法不抛异常：存档是旁路功能，绝不允许影响贴图开关窗。
/// </summary>
public static class StickerArchiveStore
{
    /// <summary>存档格式版本。读到不认识的版本按"无法恢复"处理，不做跨版本猜测。</summary>
    private const int FormatVersion = 1;

    /// <summary>当前活跃贴图会话持久化路径（记录软件退出/重启前桌面打开的所有贴图现场）。</summary>
    public static string ActiveSessionFilePath => Path.Combine(CaptureHistoryStore.HistoryDirectory, "active_stickers_session.json");

    private static DispatcherTimer? _sessionSyncTimer;

    /// <summary>
    /// 请求防抖同步活跃会话（在贴图开启、移动、缩放、变更标注后调用）。
    /// 500ms 防抖，将当前屏幕活跃贴图现场原子保存至 active_stickers_session.json。
    /// </summary>
    public static void RequestSessionSync()
    {
        if (App.IsShuttingDown) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (App.IsShuttingDown) return;
            if (_sessionSyncTimer == null)
            {
                _sessionSyncTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _sessionSyncTimer.Tick += (s, e) =>
                {
                    _sessionSyncTimer.Stop();
                    if (!App.IsShuttingDown)
                    {
                        SaveActiveSession();
                    }
                };
            }
            _sessionSyncTimer.Stop();
            _sessionSyncTimer.Start();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 同步保存当前屏幕所有活跃贴图的现场（软件重启或退出时调用）。
    /// </summary>
    public static void SaveActiveSession()
    {
        try
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var openStickers = desktop?.Windows.OfType<StickerWindow>()
                .Where(w => !w.IsClosed)
                .ToList();

            string sessionFile = ActiveSessionFilePath;
            if (openStickers == null || openStickers.Count == 0)
            {
                if (File.Exists(sessionFile))
                {
                    try { File.Delete(sessionFile); } catch { }
                }
                return;
            }

            var list = new List<StickerArchive>();
            foreach (var win in openStickers)
            {
                // ⚠ 这里**只能**取快照，绝不能调 win.SaveArchiveSnapshot()（2026-09-22 修自激循环）：
                // 那个方法末尾会 RequestSessionSync() 把 500ms 防抖定时器重新踢起来，而本方法正是
                // 定时器 Tick 调进来的 → SaveActiveSession → SaveArchiveSnapshot → RequestSessionSync
                // → 500ms 后又 SaveActiveSession，形成**与用户操作无关的永久循环**。
                // 实证（log/app.log pid=16840，20:47:31–20:53:22）：497 次 SaveActiveSession
                // 间隔**全部**精确落在 500ms 档、持续 5 分 51 秒，期间零用户操作日志；
                // 每轮都在 UI 线程上遍历标注文档 + 建文字层快照 + 序列化 + 两次磁盘写
                // （单张 json ~390B 加本方法的 session 全量重写），贴图开着就一直烧。
                // 单张 json 也不必在此重写：本方法自己就把全部现场写进 active_stickers_session.json，
                // 而单张存档由真实用户操作（移动/缩放/标注/开关窗）各自的 SaveArchiveSnapshot 负责，
                // 与本方法的职责不重叠。防抖定时器由此只被真实操作触发，回到注释声明的设计意图。
                // ⚠ 但补录历史这一步必须留下：BuildArchiveSnapshot 在 HistoryId 为空时直接返回 null，
                // 而历史外键原由 SaveArchiveSnapshot 开头的 EnsureHistoryRecorded 兜底补录
                // （刚贴出、排版未完成的贴图可能还没外键）。少了它，未补录的贴图会被静默漏出会话、
                // 重启后恢复不出来。EnsureHistoryRecorded 幂等且首行就是 `HistoryId != null` 即返回，
                // 故不构成回边：它只在**首次**补录成功那一次内部调 SaveArchiveSnapshot（会踢一次防抖），
                // 之后同一张贴图再进来一律短路 —— 每张贴图一次性，不是每 500ms 一次的循环。
                win.EnsureHistoryRecorded();
                var a = win.BuildArchiveSnapshot();
                if (a != null && !string.IsNullOrEmpty(a.HistoryId))
                {
                    list.Add(a);
                }
            }

            if (list.Count == 0)
            {
                if (File.Exists(sessionFile))
                {
                    try { File.Delete(sessionFile); } catch { }
                }
                return;
            }

            Directory.CreateDirectory(CaptureHistoryStore.HistoryDirectory);
            string tmpFile = sessionFile + ".tmp";
            var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartArray();
                foreach (var a in list)
                {
                    WriteArchive(w, a);
                }
                w.WriteEndArray();
            }
            File.WriteAllBytes(tmpFile, stream.ToArray());
            File.Move(tmpFile, sessionFile, overwrite: true);
            AppLogger.Info($"StickerArchiveStore.SaveActiveSession: 已保存 {list.Count} 张活跃贴图会话");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerArchiveStore.SaveActiveSession 异常: " + ex.Message);
        }
    }

    /// <summary>读取当前活跃贴图会话列表。</summary>
    public static List<StickerArchive> LoadActiveSession()
    {
        var list = new List<StickerArchive>();
        string sessionFile = ActiveSessionFilePath;
        if (!File.Exists(sessionFile)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sessionFile));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    var a = ReadArchive(elem, elem.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "");
                    if (a != null && !string.IsNullOrEmpty(a.HistoryId))
                    {
                        list.Add(a);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerArchiveStore.LoadActiveSession 异常: " + ex.Message);
        }
        return list;
    }

    /// <summary>清除活跃贴图会话文件（恢复完成后调用，避免反复恢复）。</summary>
    public static void ClearActiveSession()
    {
        try
        {
            string sessionFile = ActiveSessionFilePath;
            if (File.Exists(sessionFile))
            {
                File.Delete(sessionFile);
            }
        }
        catch { }
    }

    private static string PathOf(string id)
        => Path.Combine(CaptureHistoryStore.StickerArchiveDirectory, (id ?? "") + ".json");

    /// <summary>写一张贴图的存档（覆盖同 Id 的旧档，即"同一张贴图关掉两次以最后一次为准"）。</summary>
    public static void TryWrite(StickerArchive? archive)
    {
        try
        {
            if (archive == null || string.IsNullOrEmpty(archive.HistoryId)) return;
            string path = PathOf(archive.HistoryId);
            Directory.CreateDirectory(CaptureHistoryStore.StickerArchiveDirectory);

            var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                WriteArchive(w, archive);
            }
            File.WriteAllBytes(path, stream.ToArray());
            AppLogger.Info("StickerArchive: 已存档贴图 " + archive.HistoryId
                           + " kind=" + archive.Kind
                           + " 图元=" + archive.Shapes.Count + " 文字层=" + archive.Texts.Count
                           + " 窗口=" + archive.State.Width + "x" + archive.State.Height
                           + " scale=" + archive.State.Scale.ToString("F3", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerArchive: 写存档失败（不影响贴图关闭）。" + ex.Message);
        }
    }

    /// <summary>读一张贴图的存档；文件缺失、格式不认识或解析失败都返回 null。</summary>
    public static StickerArchive? TryRead(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            string path = PathOf(id!);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ReadArchive(doc.RootElement, id!);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerArchive: 读存档失败 " + id + " — " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 列出全部存档。恢复侧按「关闭时间」或「创建时间」两种模式自己排序，这里不替它决定。
    /// 单个文件坏掉只跳过它，不影响其余。
    /// </summary>
    public static List<StickerArchive> LoadAll()
    {
        var list = new List<StickerArchive>();
        try
        {
            if (!Directory.Exists(CaptureHistoryStore.StickerArchiveDirectory)) return list;
            foreach (string file in Directory.GetFiles(CaptureHistoryStore.StickerArchiveDirectory, "*.json"))
            {
                StickerArchive? a = TryRead(Path.GetFileNameWithoutExtension(file));
                if (a != null) list.Add(a);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerArchive: 枚举存档目录失败。" + ex.Message);
        }
        return list;
    }

    /// <summary>恢复成功后删除存档，使"下一张"能连着恢复；同 Id 的贴图再次关闭时会重新写档。</summary>
    public static void TryDelete(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            string path = PathOf(id!);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerArchive: 删除存档失败 " + id + " — " + ex.Message);
        }
    }

    // -------------------- 写 --------------------

    private static void WriteArchive(Utf8JsonWriter w, StickerArchive a)
    {
        w.WriteStartObject();
        w.WriteNumber("v", FormatVersion);
        w.WriteString("id", a.HistoryId);
        w.WriteString("kind", a.Kind);
        if (!string.IsNullOrEmpty(a.Text)) w.WriteString("text", a.Text);
        w.WriteNumber("closedAt", new DateTimeOffset(a.ClosedAt).ToUnixTimeMilliseconds());

        var s = a.State;
        w.WriteStartObject("state");
        w.WriteNumber("x", s.X);
        w.WriteNumber("y", s.Y);
        w.WriteNumber("w", s.Width);
        w.WriteNumber("h", s.Height);
        w.WriteNumber("scale", s.Scale);
        w.WriteBoolean("locked", s.Locked);
        w.WriteBoolean("penetrate", s.Penetrate);
        w.WriteBoolean("shadow", s.Shadow);
        w.WriteBoolean("shadowManual", s.ShadowManual);
        // 三态：没有这一项＝无从得知（老存档与非截图来源的贴图都落在这里）
        if (s.CaptureShadowIntent.HasValue) w.WriteBoolean("captureShadowIntent", s.CaptureShadowIntent.Value);
        w.WriteBoolean("thumbnail", s.Thumbnail);
        w.WriteNumber("thumbVpX", s.ThumbViewportX);
        w.WriteNumber("thumbVpY", s.ThumbViewportY);
        w.WriteEndObject();

        w.WriteStartObject("card");
        w.WriteNumber("contentRadiusPx", a.Card.ContentRadiusPx);
        w.WriteNumber("strokeWidthPx", a.Card.StrokeWidthPx);
        w.WriteBoolean("bakedShadow", a.Card.BakedShadow);
        w.WriteEndObject();

        w.WriteStartArray("shapes");
        foreach (Annotations.AnnotationShape shape in a.Shapes) WriteShape(w, shape);
        w.WriteEndArray();

        w.WriteStartArray("texts");
        foreach (TextLayerSnapshot t in a.Texts) WriteText(w, t);
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private static void WriteShape(Utf8JsonWriter w, Annotations.AnnotationShape shape)
    {
        switch (shape)
        {
            case Annotations.ErasureShape erase:
                w.WriteStartObject();
                w.WriteString("kind", "erasure");
                WriteGuid(w, erase.Id);
                w.WriteStartArray("targetShapes");
                foreach (Guid id in erase.TargetShapes) w.WriteStringValue(id.ToString());
                w.WriteEndArray();
                w.WriteStartArray("targetTexts");
                foreach (Guid id in erase.TargetTexts) w.WriteStringValue(id.ToString());
                w.WriteEndArray();
                w.WriteStartArray("marks");
                foreach (Annotations.ErasureMark mark in erase.Marks)
                {
                    w.WriteStartObject();
                    WriteRect(w, "bounds", mark.Bounds);
                    w.WriteBoolean("circle", mark.IsCircle);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case Annotations.RectangleShape r:
                w.WriteStartObject();
                w.WriteString("kind", "rect");
                WriteGuid(w, r.Id);
                WriteRect(w, "rect", r.Rect);
                WriteGdiColor(w, "strokeColor", r.StrokeColor);
                w.WriteNumber("strokeThickness", r.StrokeThickness);
                w.WriteString("shapeKind", r.ShapeKind.ToString());
                w.WriteString("lineStyle", r.LineStyle.ToString());
                w.WriteNumber("cornerRadius", r.CornerRadius);
                w.WriteNumber("fillOpacity", r.FillOpacity);
                w.WriteEndObject();
                break;

            case Annotations.ArrowShape ar:
                w.WriteStartObject();
                w.WriteString("kind", "arrow");
                WriteGuid(w, ar.Id);
                WritePoint(w, "start", ar.Start);
                WritePoint(w, "end", ar.End);
                WritePoint(w, "control", ar.ControlPoint);
                WriteGdiColor(w, "color", ar.Color);
                w.WriteNumber("thickness", ar.Thickness);
                w.WriteNumber("scale", ar.Scale);
                w.WriteString("style", ar.Style.ToString());
                w.WriteEndObject();
                break;

            case Annotations.StrokeShape st:
                w.WriteStartObject();
                w.WriteString("kind", "stroke");
                WriteGuid(w, st.Id);
                WriteGdiColor(w, "color", st.Color);
                w.WriteNumber("thickness", st.Thickness);
                w.WriteString("lineStyle", st.LineStyle.ToString());
                w.WriteStartObject("profile");
                w.WriteNumber("opacity", st.Profile.Opacity);
                w.WriteNumber("taperStrength", st.Profile.TaperStrength);
                w.WriteNumber("smoothing", st.Profile.Smoothing);
                w.WriteNumber("minWidthRatio", st.Profile.MinWidthRatio);
                w.WriteEndObject();
                // 笔迹存原始采样点（x,y,时刻）而不是烘焙后的像素：重放时按当前 DPI 重新栅格化
                w.WriteStartArray("samples");
                foreach (Annotations.StrokePoint p in st.Samples)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(p.X);
                    w.WriteNumberValue(p.Y);
                    w.WriteNumberValue(p.TimestampMs);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;

            case Annotations.CounterShape c:
                w.WriteStartObject();
                w.WriteString("kind", "counter");
                WriteGuid(w, c.Id);
                WritePoint(w, "center", c.Center);
                w.WriteNumber("number", c.Number);
                w.WriteNumber("radius", c.Radius);
                WriteGdiColor(w, "fillColor", c.FillColor);
                WriteGdiColor(w, "textColor", c.TextColor);
                // 2026-09-18 的样式档、说明文字与引线：逐字段写，老存档缺项时由读侧取默认值
                //（与下面 rect 的 cornerRadius/fillOpacity 同一套路），旧存档照样能开。
                // 2026-09-19 样式减法后只剩这几类：描边宽、字体、引线粗细、说明字号已固定成常量，
                // 界面无处可设，故不再进存档（老文件里残留的这几个字段读侧不解析＝按新默认重画）。
                // 同日补的 capsuleCorner（胶囊圆角）同属图元自带样式，不写的话重建贴图时圆角回到全圆。
                // tailHead（引线箭头头型）是同日稍晚随"引线箭头做成下拉"重新进档的：老存档缺项＝Sharp，
                // 正是这一档出现之前引线一直画的那颗实心三角，历史贴图零变形。
                w.WriteString("serial", c.Serial.ToString());
                w.WriteString("glyph", c.Glyph.ToString());
                w.WriteString("fill", c.Fill.ToString());
                w.WriteString("tailHead", c.TailHead.ToString());
                w.WriteNumber("capsuleCorner", c.CapsuleCorner);
                w.WriteString("note", c.Note);
                if (c.Tail is System.Drawing.PointF tail) WritePoint(w, "tail", tail);
                // tailShown（2026-09-19 的浮条引线开关）只在"藏起来"这一态才写：默认显示时留空，
                // 老存档缺项读侧按显示处理＝历史贴图零变形。
                if (!c.TailShown) w.WriteBoolean("tailShown", false);
                // tailControl（2026-09-19 的引线可拖弯折点）只在拖弯过才写：没弯过＝null，
                // 老存档缺项读侧按直线重建＝历史贴图零变形。
                if (c.TailControl is System.Drawing.PointF tailControl) WritePoint(w, "tailControl", tailControl);
                w.WriteEndObject();
                break;

            case Annotations.SpotlightShape sp:
                w.WriteStartObject();
                w.WriteString("kind", "spotlight");
                WriteGuid(w, sp.Id);
                WriteRect(w, "rect", sp.Rect);
                w.WriteNumber("darkness", sp.Darkness);
                w.WriteString("shapeKind", sp.ShapeKind.ToString());
                WriteGdiColor(w, "strokeColor", sp.StrokeColor);
                w.WriteNumber("strokeThickness", sp.StrokeThickness);
                w.WriteNumber("cornerRadius", sp.CornerRadius);
                w.WriteEndObject();
                break;

            case Annotations.BlurShape b:
                w.WriteStartObject();
                w.WriteString("kind", "blur");
                WriteGuid(w, b.Id);
                WriteRect(w, "rect", b.Rect);
                w.WriteString("mode", b.Mode.ToString());
                // Intensity / MosaicBlockSize 都是 UiLevel 的派生值，只存这一个真值，避免自相矛盾
                w.WriteNumber("uiLevel", b.UiLevel);
                w.WriteEndObject();
                break;

            default:
                AppLogger.Warn("StickerArchive: 未登记的标注类型，跳过存档。" + shape.GetType().Name);
                break;
        }
    }

    private static void WriteText(Utf8JsonWriter w, TextLayerSnapshot t)
    {
        w.WriteStartObject();
        WriteGuid(w, t.Id);
        w.WriteString("text", t.Text);
        w.WriteNumber("left", t.Left);
        w.WriteNumber("top", t.Top);
        w.WriteNumber("maxWidth", t.MaxWidth);
        w.WriteNumber("fontSize", t.FontSize);
        w.WriteBoolean("isBold", t.IsBold);
        w.WriteBoolean("isItalic", t.IsItalic);
        w.WriteBoolean("hasStroke", t.HasStroke);
        w.WriteBoolean("hasBackground", t.HasBackground);
        w.WriteString("fontFamily", t.FontFamily);
        WriteAvaColor(w, "color", t.Color);
        WriteAvaColor(w, "fillColor", t.FillColor);
        WriteAvaColor(w, "strokeColor", t.StrokeColor);
        w.WriteNumber("strokeThickness", t.StrokeThickness);
        w.WriteNumber("fillOpacity", t.FillOpacity);
        w.WriteNumber("fillCornerRadius", t.FillCornerRadius);
        w.WriteNumber("fillPadding", t.FillPadding);
        w.WriteNumber("rotationAngle", t.RotationAngle);
        w.WriteNumber("measuredWidth", t.MeasuredWidth);
        w.WriteNumber("measuredHeight", t.MeasuredHeight);
        w.WriteNumber("lineHeight", t.LineHeight);
        w.WriteEndObject();
    }

    private static void WriteGuid(Utf8JsonWriter w, Guid id) => w.WriteString("id", id.ToString());

    private static void WriteRect(Utf8JsonWriter w, string name, System.Drawing.RectangleF r)
    {
        w.WriteStartObject(name);
        w.WriteNumber("x", r.X);
        w.WriteNumber("y", r.Y);
        w.WriteNumber("w", r.Width);
        w.WriteNumber("h", r.Height);
        w.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter w, string name, System.Drawing.PointF p)
    {
        w.WriteStartObject(name);
        w.WriteNumber("x", p.X);
        w.WriteNumber("y", p.Y);
        w.WriteEndObject();
    }

    private static void WriteGdiColor(Utf8JsonWriter w, string name, GdiColor c)
        => w.WriteString(name, $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");

    private static void WriteAvaColor(Utf8JsonWriter w, string name, AvaColor c)
        => w.WriteString(name, $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");

    // -------------------- 读 --------------------

    private static StickerArchive? ReadArchive(JsonElement root, string fallbackId)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (GetInt(root, "v", 0) != FormatVersion)
        {
            AppLogger.Warn("StickerArchive: 存档版本不认识，跳过。id=" + fallbackId);
            return null;
        }

        var a = new StickerArchive
        {
            HistoryId = GetString(root, "id", fallbackId),
            Kind = GetString(root, "kind", "image"),
            Text = GetString(root, "text", ""),
        };
        if (string.IsNullOrEmpty(a.HistoryId)) a.HistoryId = fallbackId;
        long closedMs = GetLong(root, "closedAt", 0);
        if (closedMs > 0) a.ClosedAt = DateTimeOffset.FromUnixTimeMilliseconds(closedMs).ToLocalTime().DateTime;

        if (root.TryGetProperty("state", out JsonElement st) && st.ValueKind == JsonValueKind.Object)
        {
            a.State.X = GetInt(st, "x", 0);
            a.State.Y = GetInt(st, "y", 0);
            a.State.Width = GetInt(st, "w", 0);
            a.State.Height = GetInt(st, "h", 0);
            a.State.Scale = GetDouble(st, "scale", 1.0);
            a.State.Locked = GetBool(st, "locked", false);
            a.State.Penetrate = GetBool(st, "penetrate", false);
            a.State.Shadow = GetBool(st, "shadow", true);
            a.State.ShadowManual = GetBool(st, "shadowManual", false);
            // 缺键＝null（无从得知）；⚠ 不能用 GetBool 的默认值代替，那会把"不知道"和"没勾选"混为一谈
            a.State.CaptureShadowIntent =
                st.TryGetProperty("captureShadowIntent", out JsonElement csi)
                && (csi.ValueKind == JsonValueKind.True || csi.ValueKind == JsonValueKind.False)
                    ? csi.GetBoolean() : null;
            a.State.Thumbnail = GetBool(st, "thumbnail", false);
            a.State.ThumbViewportX = GetDouble(st, "thumbVpX", 0);
            a.State.ThumbViewportY = GetDouble(st, "thumbVpY", 0);
        }
        if (root.TryGetProperty("card", out JsonElement cd) && cd.ValueKind == JsonValueKind.Object)
        {
            a.Card.ContentRadiusPx = GetDouble(cd, "contentRadiusPx", 0);
            a.Card.StrokeWidthPx = GetDouble(cd, "strokeWidthPx", 0);
            a.Card.BakedShadow = GetBool(cd, "bakedShadow", false);
        }
        if (root.TryGetProperty("shapes", out JsonElement shapes) && shapes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in shapes.EnumerateArray())
            {
                Annotations.AnnotationShape? shape = ReadShape(e);
                if (shape != null) a.Shapes.Add(shape);
            }
        }
        if (root.TryGetProperty("texts", out JsonElement texts) && texts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in texts.EnumerateArray())
            {
                TextLayerSnapshot? t = ReadText(e);
                if (t != null) a.Texts.Add(t);
            }
        }
        return a;
    }

    private static Annotations.AnnotationShape? ReadShape(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string kind = GetString(e, "kind", "");
        Guid id = TryParseGuid(GetString(e, "id", ""));

        switch (kind)
        {
            case "erasure":
            {
                static Guid[] Ids(JsonElement owner, string name)
                    => owner.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
                        ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => TryParseGuid(item.GetString() ?? "")).Where(value => value != Guid.Empty).ToArray()
                        : Array.Empty<Guid>();
                var marks = new List<Annotations.ErasureMark>();
                if (e.TryGetProperty("marks", out JsonElement values) && values.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement mark in values.EnumerateArray())
                        if (mark.ValueKind == JsonValueKind.Object)
                            marks.Add(new Annotations.ErasureMark(ReadRect(mark, "bounds"), GetBool(mark, "circle", false)));
                return new Annotations.ErasureShape(Ids(e, "targetShapes"), Ids(e, "targetTexts"), marks, id);
            }
            case "rect":
                // cornerRadius / fillOpacity 都是后加的字段：老存档没有这两项时取 0（直角、不填充），与旧版行为一致
                return new Annotations.RectangleShape(
                    ReadRect(e, "rect"), ReadGdiColor(e, "strokeColor", GdiColor.Black),
                    (float)GetDouble(e, "strokeThickness", 3),
                    EnumOr(e, "shapeKind", Annotations.ShapeAnnotationKind.Rectangle),
                    EnumOr(e, "lineStyle", Annotations.AnnotationLineStyle.Solid),
                    (float)GetDouble(e, "cornerRadius", 0),
                    (float)GetDouble(e, "fillOpacity", 0), id);

            case "arrow":
                return new Annotations.ArrowShape(
                    ReadPoint(e, "start"), ReadPoint(e, "end"),
                    ReadGdiColor(e, "color", GdiColor.Red), (float)GetDouble(e, "thickness", 3),
                    EnumOr(e, "style", Annotations.ArrowAnnotationStyle.Designed),
                    (float)GetDouble(e, "scale", 1.0), id, ReadPoint(e, "control"));

            case "stroke":
            {
                var samples = new List<Annotations.StrokePoint>();
                if (e.TryGetProperty("samples", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in arr.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Array) continue;
                        int i = 0;
                        float px = 0, py = 0; double ts = 0;
                        foreach (JsonElement n in p.EnumerateArray())
                        {
                            double v = n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
                            if (i == 0) px = (float)v;
                            else if (i == 1) py = (float)v;
                            else ts = v;
                            i++;
                        }
                        samples.Add(new Annotations.StrokePoint(px, py, ts));
                    }
                }
                var profile = new Annotations.BrushProfile(1f, 0f, 0f, 1f);
                if (e.TryGetProperty("profile", out JsonElement pr) && pr.ValueKind == JsonValueKind.Object)
                {
                    profile = new Annotations.BrushProfile(
                        (float)GetDouble(pr, "opacity", 1.0),
                        (float)GetDouble(pr, "taperStrength", 0.0),
                        (float)GetDouble(pr, "smoothing", 0.0),
                        (float)GetDouble(pr, "minWidthRatio", 1.0));
                }
                return new Annotations.StrokeShape(
                    samples, ReadGdiColor(e, "color", GdiColor.Red),
                    (float)GetDouble(e, "thickness", 3), profile,
                    EnumOr(e, "lineStyle", Annotations.AnnotationLineStyle.Solid), id);
            }

            case "counter":
            {
                // 这批字段都是 2026-09-18 才加的：老存档没有 → 逐项取默认值，而默认值恰好就是旧版那个样子
                //（圆点、实心、白字、无说明、无引线），故历史贴图不必迁移照样能开（同 rect 的 cornerRadius）。
                // 2026-09-19 样式减法后只读这几类：老存档里残留的描边宽/字体/引线粗细/说明字号
                // 一律不解析，CounterShape 的构造默认值（＝那几枚固定常量）接管，外观按新默认重画。
                // capsuleCorner（同日补）缺项时取默认 100＝两端全圆，正是这项能力之前画出来的样子；
                // tailHead（同日稍晚补）缺项时取 Sharp＝实心三角，与引线箭头这一档出现之前的画法一致；
                // tailShown（同日再晚，浮条引线开关）缺项时取 true＝照常画，老存档一张图也不会少条引线；
                // tailControl（同日引线可拖弯折点）缺项时为 null＝引线按直线重建，历史贴图一根都不会莫名弯掉。
                System.Drawing.PointF? tail =
                    e.TryGetProperty("tail", out JsonElement tailEl) && tailEl.ValueKind == JsonValueKind.Object
                        ? ReadPoint(e, "tail") : null;
                System.Drawing.PointF? tailControl =
                    e.TryGetProperty("tailControl", out JsonElement tcEl) && tcEl.ValueKind == JsonValueKind.Object
                        ? ReadPoint(e, "tailControl") : null;
                return new Annotations.CounterShape(
                    ReadPoint(e, "center"), GetInt(e, "number", 1), (float)GetDouble(e, "radius", 16),
                    ReadGdiColor(e, "fillColor", GdiColor.Red), ReadGdiColor(e, "textColor", GdiColor.White), id,
                    EnumOr(e, "serial", Annotations.CounterSerialKind.Number),
                    EnumOr(e, "glyph", Annotations.CounterGlyphShape.Circle),
                    EnumOr(e, "fill", Annotations.CounterFillMode.Solid),
                    note: GetString(e, "note", ""),
                    tail: tail,
                    tailHead: EnumOr(e, "tailHead", Annotations.ArrowAnnotationStyle.Sharp),
                    tailShown: GetBool(e, "tailShown", true),
                    capsuleCorner: (float)GetDouble(e, "capsuleCorner",
                        Annotations.AnnotationRenderOptions.CounterCapsuleCornerDefault),
                    tailControl: tailControl);
            }

            case "spotlight":
                return new Annotations.SpotlightShape(
                    ReadRect(e, "rect"), (float)GetDouble(e, "darkness", 0.5),
                    EnumOr(e, "shapeKind", Annotations.SpotlightShapeKind.Rectangle),
                    ReadGdiColor(e, "strokeColor", GdiColor.FromArgb(255, 214, 102)),
                    (float)GetDouble(e, "strokeThickness", 3), (float)GetDouble(e, "cornerRadius", 0), id);

            case "blur":
                return new Annotations.BlurShape(
                    ReadRect(e, "rect"), EnumOr(e, "mode", Annotations.BlurMode.Blur),
                    GetInt(e, "uiLevel", 5), id);

            default:
                AppLogger.Warn("StickerArchive: 存档里有未登记的图元类型，跳过。kind=" + kind);
                return null;
        }
    }

    private static TextLayerSnapshot? ReadText(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        return new TextLayerSnapshot
        {
            Id = TryParseGuid(GetString(e, "id", "")) is Guid textId && textId != Guid.Empty
                ? textId : Guid.NewGuid(),
            Text = GetString(e, "text", ""),
            Left = GetDouble(e, "left", 0),
            Top = GetDouble(e, "top", 0),
            MaxWidth = GetDouble(e, "maxWidth", 0),
            FontSize = GetDouble(e, "fontSize", 18),
            IsBold = GetBool(e, "isBold", false),
            IsItalic = GetBool(e, "isItalic", false),
            HasStroke = GetBool(e, "hasStroke", false),
            HasBackground = GetBool(e, "hasBackground", false),
            FontFamily = GetString(e, "fontFamily", "Microsoft YaHei UI"),
            Color = ReadAvaColor(e, "color", AvaColor.Parse("#FF000000")),
            FillColor = ReadAvaColor(e, "fillColor", AvaColor.Parse("#80808080")),
            StrokeColor = ReadAvaColor(e, "strokeColor", AvaColor.Parse("#FFFFFFFF")),
            StrokeThickness = GetDouble(e, "strokeThickness", 0),
            FillOpacity = GetDouble(e, "fillOpacity", 1),
            FillCornerRadius = GetDouble(e, "fillCornerRadius", 0),
            FillPadding = GetDouble(e, "fillPadding", 0),
            RotationAngle = GetDouble(e, "rotationAngle", 0),
            MeasuredWidth = GetDouble(e, "measuredWidth", 0),
            MeasuredHeight = GetDouble(e, "measuredHeight", 0),
            LineHeight = GetDouble(e, "lineHeight", 0),
        };
    }

    // -------------------- 取值兜底 --------------------

    private static string GetString(JsonElement e, string name, string fallback)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback) : fallback;

    private static int GetInt(JsonElement e, string name, int fallback)
    {
        if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out int n)) return n;
        return fallback;
    }

    private static long GetLong(JsonElement e, string name, long fallback)
    {
        if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out long n)) return n;
        return fallback;
    }

    private static double GetDouble(JsonElement e, string name, double fallback)
    {
        if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out double n)) return n;
        return fallback;
    }

    private static bool GetBool(JsonElement e, string name, bool fallback)
    {
        if (e.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }

    private static T EnumOr<T>(JsonElement e, string name, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(GetString(e, name, ""), ignoreCase: false, out T v) ? v : fallback;

    private static Guid TryParseGuid(string s)
        => Guid.TryParse(s, out Guid g) ? g : Guid.Empty;

    private static System.Drawing.RectangleF ReadRect(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement r) || r.ValueKind != JsonValueKind.Object)
            return default;
        return new System.Drawing.RectangleF(
            (float)GetDouble(r, "x", 0), (float)GetDouble(r, "y", 0),
            (float)GetDouble(r, "w", 0), (float)GetDouble(r, "h", 0));
    }

    private static System.Drawing.PointF ReadPoint(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement p) || p.ValueKind != JsonValueKind.Object)
            return default;
        return new System.Drawing.PointF((float)GetDouble(p, "x", 0), (float)GetDouble(p, "y", 0));
    }

    private static GdiColor ReadGdiColor(JsonElement e, string name, GdiColor fallback)
    {
        string hex = GetString(e, name, "");
        if (hex.Length == 9 && hex[0] == '#'
            && uint.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
        {
            return GdiColor.FromArgb((int)((v >> 24) & 0xFF), (int)((v >> 16) & 0xFF),
                                     (int)((v >> 8) & 0xFF), (int)(v & 0xFF));
        }
        return fallback;
    }

    private static AvaColor ReadAvaColor(JsonElement e, string name, AvaColor fallback)
    {
        string hex = GetString(e, name, "");
        try
        {
            if (hex.Length == 9 && hex[0] == '#') return AvaColor.Parse(hex);
        }
        catch { }
        return fallback;
    }
}
