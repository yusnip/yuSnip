using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 截图选区历史：最近若干<b>截图成功</b>那一刻的矩形，供截图遮罩里按「恢复上一次截图框」（默认裸 R）逐格往回退。
    ///
    /// 口径（2026-09-29 定稿）：
    /// - <b>只有真出了结果的那一次才算一格</b>。取消／Esc／半途放弃一律不留痕，所以这里躺着的全是"截下过东西"的框。
    ///   同一会话里先框 A 再框 B、只把 B 截出去 ⇒ 只多 B 这一格，A 不回来。
    /// - 微调（移动／拖手柄／滚轮／方向键）不单开一格：入栈的是完成那一刻的最终矩形。
    /// - 恢复出来的框不再入栈（否则按两下 R 再截图，历史会自复制）。
    ///
    /// 坐标：屏幕<b>物理像素</b>，与 <see cref="SelectionState"/> / <see cref="CapturedFrame.VirtualScreenRect"/> 同一坐标系。
    /// 持久化只借 <see cref="AppSettings.CaptureRectHistory"/> 这一个字符串载体，几十字节，不写任何文件；
    /// 与截图历史（图片副本 <c>CaptureHistoryStore</c>）是两回事，不参与它的条数与容量双闸。
    ///
    /// 线程模型：只在截图会话的 UI 线程上读写，无锁。全部方法不抛异常（失败只记日志）——
    /// 历史是旁路，绝不允许影响截图主流程。
    /// </summary>
    public static class SelectionHistory
    {
        /// <summary>保留几格（超出的从最旧那头挤掉）。</summary>
        public const int MaxEntries = 10;

        /// <summary>与栈顶四条边都差不过这么多像素视作同一个框，不重复堆一格。</summary>
        private const int DedupEpsilonPx = 4;

        /// <summary>条目间分隔符与单条内部分隔符（值里不会出现别的字符）。</summary>
        private const char EntrySeparator = ';';
        private const char FieldSeparator = ',';

        private static readonly List<Rectangle> _entries = new List<Rectangle>();

        /// <summary>下一次 <see cref="TryNext"/> 要给的格下标；每次截图会话开头由 <see cref="ResetCursor"/> 归零。</summary>
        private static int _cursor;

        private static bool _loaded;

        /// <summary>当前格数（已加载）。</summary>
        public static int Count
        {
            get { EnsureLoaded(); return _entries.Count; }
        }

        /// <summary>
        /// 记一格"刚刚截图成功"的矩形，并当场落盘。空矩形、与栈顶同框的直接忽略。
        /// </summary>
        public static void Push(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            EnsureLoaded();

            if (_entries.Count > 0 && SameAsTop(rect, _entries[0])) return;

            _entries.Insert(0, rect);
            while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
            Save();
        }

        /// <summary>每次截图会话开头调一次：让第一下 R 一定是"上一次截图的那个框"。</summary>
        public static void ResetCursor() => _cursor = 0;

        /// <summary>
        /// 往后走一格。落在当前虚拟屏之外的旧框会被跳过（拔显示器／改分辨率之后），
        /// 找不到可用的一格就返回 false、游标不动（画面纹丝不动，只记一行日志）。
        /// </summary>
        public static bool TryNext(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            EnsureLoaded();
            if (_entries.Count == 0) return false;

            Rectangle virtualScreen = ScreenCaptureTool.Platform.MonitorHelper.GetVirtualScreenRect();
            for (int i = _cursor; i < _entries.Count; i++)
            {
                Rectangle candidate = _entries[i];
                if (!virtualScreen.Contains(candidate)) continue;
                _cursor = i + 1;
                rect = candidate;
                return true;
            }

            AppLogger.Info("SelectionHistory: 已退到栈底或全部落在屏外（游标=" + _cursor
                + "，格数=" + _entries.Count + "，虚屏=" + virtualScreen + "）");
            return false;
        }

        /// <summary>清空历史并落盘（"恢复默认设置"链路用）。</summary>
        public static void Clear()
        {
            _entries.Clear();
            _cursor = 0;
            _loaded = true;
            Save();
        }

        // ---------- 持久化 ----------

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Parse(AppSettings.Current == null ? "" : AppSettings.Current.CaptureRectHistory);
        }

        private static void Save()
        {
            try
            {
                AppSettings.Current.CaptureRectHistory = Encode();
                AppSettings.Current.Save();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SelectionHistory.Save 失败：" + ex.Message);
            }
        }

        private static string Encode()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _entries.Count; i++)
            {
                Rectangle r = _entries[i];
                if (i > 0) sb.Append(EntrySeparator);
                sb.Append(r.X.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                  .Append(r.Y.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                  .Append(r.Width.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                  .Append(r.Height.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>逐条容错解析：某条格式不对就整条丢掉，不牵连其余。</summary>
        private static void Parse(string raw)
        {
            _entries.Clear();
            _cursor = 0;
            if (string.IsNullOrWhiteSpace(raw)) return;

            string[] parts = raw.Split(EntrySeparator);
            for (int i = 0; i < parts.Length && _entries.Count < MaxEntries; i++)
            {
                string[] fields = parts[i].Split(FieldSeparator);
                if (fields.Length != 4) continue;
                int x, y, w, h;
                if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) continue;
                if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) continue;
                if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) continue;
                if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) continue;
                if (w <= 0 || h <= 0) continue;
                _entries.Add(new Rectangle(x, y, w, h));
            }
        }

        private static bool SameAsTop(Rectangle a, Rectangle b)
        {
            return Math.Abs(a.Left - b.Left) <= DedupEpsilonPx
                && Math.Abs(a.Top - b.Top) <= DedupEpsilonPx
                && Math.Abs(a.Right - b.Right) <= DedupEpsilonPx
                && Math.Abs(a.Bottom - b.Bottom) <= DedupEpsilonPx;
        }
    }
}
