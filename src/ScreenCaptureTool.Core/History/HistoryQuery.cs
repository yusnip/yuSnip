using System;

using System.Globalization;

using System.Text;



namespace ScreenCaptureTool.Core.History

{

    /// <summary>历史搜索框的判定分支。</summary>

    public enum HistoryQueryMode

    {

        /// <summary>空输入：不过滤。</summary>

        Empty,

        /// <summary>时间表达式：整天 / 整月 / 整年 / 相对区间 / 时刻前缀。</summary>

        Time,

        /// <summary>类型词（「类型:贴图 / 类型:截图 / 类型:长截图」）：等价于工具栏左段的来源筛选；
        /// 2026-09-10 起裸词不再走此分支，一律按关键词搜识别文字。</summary>

        Source,

        /// <summary>关键词：识别文字包含（忽略大小写）。</summary>

        Keyword,

    }



    /// <summary>

    /// 截图历史「单框智能判定」的解析结果：一个输入框吃时间与文字两类查询，不拆框、不加模式开关。

    ///

    /// 判定顺序是硬规则 —— <b>日期形态优先于关键词</b>：否则搜 <c>14:03</c> 会因为某条识别文字里

    /// 含这串数字而误命中一堆段落。时间基准是记录创建时间 <see cref="CaptureHistoryItem.CapturedAt"/>

    /// （本地时区），与卡片上显示的时间同源。

    ///

    /// 刻意不做（设计稿 §03「刻意不做的」）：文件名搜索、布尔表达式、"最近"以外的自然语言、正则。

    /// 纯手写解析（NativeAOT 下正则要额外配 RuntimeAttribute，且这里的形态有限，字符串切分更直白）。

    /// </summary>

    public sealed class HistoryQuery

    {

        /// <summary>空查询（不过滤任何记录）。</summary>

        public static readonly HistoryQuery Empty = new HistoryQuery(HistoryQueryMode.Empty);



        private HistoryQuery(HistoryQueryMode mode)

        {

            Mode = mode;

        }



        /// <summary>判定分支。</summary>

        public HistoryQueryMode Mode { get; private set; }



        /// <summary>时间分支区间下界（含）。只给时刻前缀时为 <see cref="DateTime.MinValue"/>。</summary>

        public DateTime Start { get; private set; } = DateTime.MinValue;



        /// <summary>时间分支区间上界（<b>不含</b>）。只给时刻前缀时为 <see cref="DateTime.MaxValue"/>。</summary>

        public DateTime End { get; private set; } = DateTime.MaxValue;



        /// <summary>时刻前缀，形如 <c>14</c> / <c>14:0</c> / <c>14:03</c>（小时已补零到两位）；null = 不按时刻筛。</summary>

        public string? TimePrefix { get; private set; }



        /// <summary>类型词命中的来源集合（仅 <see cref="HistoryQueryMode.Source"/> 分支非空）。</summary>

        public CaptureHistorySource[]? Sources { get; private set; }



        /// <summary>关键词原词 / 类型词原词。</summary>

        public string Term { get; private set; } = "";



        /// <summary>输入为空（含只有空白）时为 true，UI 侧据此决定要不要显示"命中 N 条"。</summary>

        public bool IsEmpty => Mode == HistoryQueryMode.Empty;



        /// <summary>是否被判成时间表达式 —— 只有这一种要给一次浮层确认（关键词每敲一个字都弹就是噪音）。</summary>

        public bool IsTime => Mode == HistoryQueryMode.Time;



        /// <summary>

        /// 需要在文字里高亮的词。只有关键词分支高亮：时间分支命中的是记录本身，不是某段文字，

        /// 没有可标的片段。

        /// </summary>

        public string? HighlightTerm => !IsEmpty && Term.Length > 0 ? Term : null;



        /// <summary>该记录是否命中当前查询。空查询恒命中（不参与过滤）。</summary>

        public bool Matches(CaptureHistoryItem? item)

        {

            if (item == null) return false;

            // 若正文文本直接包含所搜词，优先命中（防搜图内含“今天”、“贴图”等文字时被条件完全拦截）

            if (Mode != HistoryQueryMode.Keyword && Mode != HistoryQueryMode.Empty && MatchesKeyword(item)) return true;

            switch (Mode)

            {

                case HistoryQueryMode.Empty:

                    return true;



                case HistoryQueryMode.Time:

                    DateTime t = item.CapturedAt;

                    if (t < Start || t >= End) return false;

                    if (TimePrefix != null)

                    {

                        string clock = t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                        return clock.StartsWith(TimePrefix, StringComparison.Ordinal);

                    }

                    return true;



                case HistoryQueryMode.Source:

                    if (Sources == null) return false;

                    for (int i = 0; i < Sources.Length; i++)

                        if (Sources[i] == item.Source) return true;

                    return false;



                default:

                    return MatchesKeyword(item);

            }

        }



        /// <summary>

        /// 关键词判定：<b>空格分词后逐词 AND</b>。OCR 出来的两段文字之间是换行而不是空格，整串包含

        /// 让「报错 弹窗」这种两词查询永远匹配不上（issue #9 的主症状）；拆开逐词命中即可跨行、跨段。

        /// 比对对象是 <see cref="CaptureHistoryItem.SearchText"/>——它与 <see cref="Term"/> 走同一份

        /// <see cref="NormalizeForSearch"/>，所以全角折半角、换行压空格两侧同步生效。

        /// </summary>

        private bool MatchesKeyword(CaptureHistoryItem item)

        {

            string haystack = item.SearchText;

            if (haystack.Length == 0) return false;



            int from = 0;

            string? compactHaystack = null;

            while (from < Term.Length)

            {

                int space = Term.IndexOf(' ', from);

                int end = space < 0 ? Term.Length : space;

                ReadOnlySpan<char> sub = Term.AsSpan(from, end - from);

                if (haystack.IndexOf(sub, StringComparison.OrdinalIgnoreCase) < 0)

                {

                    // 容错：常规包含未命中时，忽略文本内残留的多余空格/换行进行紧凑比对（彻底解决存量换行断词）

                    compactHaystack ??= item.SearchText.Replace(" ", "");

                    string compactSub = sub.ToString().Replace(" ", "");

                    if (compactSub.Length == 0 || compactHaystack.IndexOf(compactSub, StringComparison.OrdinalIgnoreCase) < 0)

                        return false;

                }

                from = end + 1;

            }

            return true;

        }



        // -------------------- 解析 --------------------



        /// <summary>

        /// 解析搜索框输入。<paramref name="now"/> 只用于"今天 / 本周 / 30分钟前"这类相对表达，

        /// 测试可传固定时刻。

        /// </summary>

        public static HistoryQuery Parse(string? raw, DateTime? now = null)

        {

            string s = NormalizeForSearch(raw);

            if (s.Length == 0) return Empty;



            DateTime anchor = now ?? DateTime.Now;



            var q = new HistoryQuery(HistoryQueryMode.Keyword) { Term = s };



            if (TryRelativeWords(s, anchor, out DateTime r0, out DateTime r1))

                return TimeResult(s, r0, r1, null);



            if (TryRelativeDuration(s, anchor, out r0, out r1))

                return TimeResult(s, r0, r1, null);



            if (TryDateAndClock(s, anchor.Year, out r0, out r1, out string? prefix))

                return TimeResult(s, r0, r1, prefix);



            if (TrySourceWord(s, out CaptureHistorySource[] sources))

            {

                var src = new HistoryQuery(HistoryQueryMode.Source) { Sources = sources, Term = s };

                return src;

            }



            return q;

        }



        private static HistoryQuery TimeResult(string raw, DateTime start, DateTime end, string? prefix)

        {

            return new HistoryQuery(HistoryQueryMode.Time)

            {

                Start = start,

                End = end,

                TimePrefix = prefix,

                Term = raw,

            };

        }



        /// <summary>

        /// 全角数字与全角字母折成半角、<c>：／－．</c> 四个全角标点折成半角、连续空白压成一个空格

        /// （全角空格 U+3000 属 <see cref="char.IsWhiteSpace(char)"/>，顺带一起压掉）。

        /// 中文输入法下打出「１４:０」「9／6」很常见，不折叠就判不成时间，用户只会以为搜索坏了。

        ///

        /// ⚠ 这份归一**查询串与库内文本两侧共用**（<see cref="CaptureHistoryItem.SearchText"/> 就是它的产物）：

        /// 只改一边就会回到 issue #9 报的那个症状——截图里明明有这几个字，搜就是搜不到。

        /// 存储侧保持原样，归一只发生在检索用的那一份副本上，所以详情面板的多行文本不受影响。

        /// </summary>

        public static string NormalizeForSearch(string? raw)

        {

            if (string.IsNullOrWhiteSpace(raw)) return "";

            var sb = new StringBuilder(raw.Length);

            bool pendingSpace = false;

            foreach (char ch in raw)

            {

                char c = ch;

                if (c >= '０' && c <= '９') c = (char)('0' + (c - '０'));

                else if (c >= 'Ａ' && c <= 'Ｚ') c = (char)('A' + (c - 'Ａ'));

                else if (c >= 'ａ' && c <= 'ｚ') c = (char)('a' + (c - 'ａ'));

                else if (c == '：') c = ':';

                else if (c == '／') c = '/';

                else if (c == '－' || c == '—') c = '-';

                else if (c == '．' || c == '。') c = '.';

                else if (c == '，' || c == '、') c = ',';

                else if (c == '；') c = ';';

                else if (c == '！') c = '!';

                else if (c == '？') c = '?';

                else if (c == '（' || c == '【' || c == '〔') c = '(';

                else if (c == '）' || c == '】' || c == '〕') c = ')';

                else if (c == '“' || c == '”' || c == '「' || c == '」') c = '"';

                else if (c == '‘' || c == '’' || c == '『' || c == '』') c = (char)39;

                else if (c == '～') c = '~';



                if (char.IsWhiteSpace(c))

                {

                    if (sb.Length > 0) pendingSpace = true;

                    continue;

                }

                if (pendingSpace)

                {

                    sb.Append(' ');

                    pendingSpace = false;

                }

                sb.Append(c);

            }

            return sb.ToString();

        }



        /// <summary>中文相对日 / 相对区间：今天、昨天、前天、本周、上周、本月、上个月。</summary>

        private static bool TryRelativeWords(string s, DateTime now, out DateTime start, out DateTime end)

        {

            start = default;

            end = default;

            DateTime today = now.Date;



            switch (s)

            {

                case "今天":

                    start = today; end = today.AddDays(1); return true;

                case "昨天":

                    start = today.AddDays(-1); end = today; return true;

                case "前天":

                    start = today.AddDays(-2); end = today.AddDays(-1); return true;

                case "本周":

                    return TryWeek(now, 0, out start, out end);

                case "上周":

                    return TryWeek(now, -1, out start, out end);

                case "本月":

                    return TryMonth(now, 0, out start, out end);

                case "上个月":

                    return TryMonth(now, -1, out start, out end);

                default:

                    return false;

            }

        }



        /// <summary>周区间，周一为一周起点（本地）。</summary>

        private static bool TryWeek(DateTime now, int back, out DateTime start, out DateTime end)

        {

            int dow = (int)now.DayOfWeek;           // Sunday = 0

            int toMonday = dow == 0 ? -6 : 1 - dow;

            start = now.Date.AddDays(toMonday + back * 7);

            end = start.AddDays(7);

            return true;

        }



        private static bool TryMonth(DateTime now, int back, out DateTime start, out DateTime end)

        {

            start = new DateTime(now.Year, now.Month, 1);

            if (back != 0) start = start.AddMonths(back);

            end = start.AddMonths(1);

            return true;

        }



        /// <summary>

        /// 相对时长：「30分钟前」「2小时前」「3天内」「1周内」—— 下界 = now − 时长，上界 = now。

        /// 数字一律只认阿拉伯数字，「一周内」「两天前」这类中文数字按设计稿 §03 属刻意不做的自然语言解析。

        /// </summary>

        private static bool TryRelativeDuration(string s, DateTime now, out DateTime start, out DateTime end)

        {

            start = default;

            end = default;



            int i = 0;

            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;

            if (i == 0 || i > 4) return false;

            if (!int.TryParse(s.Substring(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0)

                return false;



            string rest = s.Substring(i);

            string unit;

            bool trailing = false;

            if (rest.EndsWith("前") || rest.EndsWith("内"))

            {

                unit = rest.Substring(0, rest.Length - 1);

                trailing = true;

            }

            else

            {

                unit = rest;

            }

            if (!trailing || unit.Length == 0) return false;



            switch (unit)

            {

                case "分钟":

                case "分":

                    start = now.AddMinutes(-n); break;

                case "小时":

                    start = now.AddHours(-n); break;

                case "天":

                case "日":

                    start = now.AddDays(-n); break;

                case "周":

                    start = now.AddDays(-7 * n); break;

                case "个月":

                case "月":

                    start = now.AddMonths(-n); break;

                case "年":

                    start = now.AddYears(-n); break;

                default:

                    return false;

            }

            end = now;

            return true;

        }



        /// <summary>

        /// 日期形态：可选日期 ＋ 可选时刻。<paramref name="prefix"/> 非 null 时按"时分秒前缀"再收窄一层。

        ///

        /// 认：<c>2026-09-06</c>、<c>09-06</c>、<c>9/6</c>、<c>9月6日</c>、<c>20260906</c>、<c>2026-09</c>、

        /// <c>9月</c>、<c>2026</c>、<c>2026年</c>，以及它们与 <c>14</c> / <c>14:03</c> 的两段组合；

        /// 单独一段 <c>14:03</c> 也认（不带日期＝任意一天的这个时刻）。

        /// 不认：单独一个裸数字（<c>9</c>、<c>14</c>）——那是关键词，不是日期。

        /// </summary>

        private static bool TryDateAndClock(string s, int currentYear,

            out DateTime start, out DateTime end, out string? prefix)

        {

            start = DateTime.MinValue;

            end = DateTime.MaxValue;

            prefix = null;



            string head = s;

            string tail = "";

            int sp = s.IndexOf(' ');

            if (sp >= 0)

            {

                head = s.Substring(0, sp);

                tail = s.Substring(sp + 1);

                if (tail.Length == 0) return false;

            }



            bool hasDate = head.Length > 0 && TryDateSpan(head, currentYear, out start, out end);

            if (!hasDate)

            {

                start = DateTime.MinValue;

                end = DateTime.MaxValue;

            }



            if (tail.Length > 0)

            {

                // 有第二段：第一段必须是日期，第二段必须是时刻，否则整体不算时间表达式

                if (!hasDate) return false;

                if (!TryClockPrefix(tail, out prefix)) return false;

                return true;

            }



            if (hasDate) return true;



            // 没有日期段：只有形如 14:03 的带冒号时刻才算时间形态；裸数字留给关键词分支

            if (!TryClockPrefix(head, out prefix)) return false;

            return prefix.IndexOf(':') >= 0;

        }



        /// <summary>把一段中文/分隔符混写的日期折成 [start, end) 区间。</summary>

        private static bool TryDateSpan(string head, int currentYear, out DateTime start, out DateTime end)

        {

            start = default;

            end = default;



            // 「9月」「2026年」这类只到月/年的写法先单独认掉，否则折成纯数字后与裸数字无法区分

            if (head.Length >= 2 && head[head.Length - 1] == '月' && IsDigits(head.Substring(0, head.Length - 1), 1, 2))

                return TryMonthSpan(ParseInt(head.Substring(0, head.Length - 1)), currentYear, out start, out end);

            if (head.Length >= 2 && head[head.Length - 1] == '年' && IsDigits(head.Substring(0, head.Length - 1), 4, 4))

                return TryYearSpan(ParseInt(head.Substring(0, head.Length - 1)), out start, out end);



            string t = head.Replace('年', '-').Replace('月', '-').Replace('日', ' ')

                           .Replace('/', '-').Replace('.', '-').Trim();

            while (t.EndsWith("-", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);

            if (t.Length == 0) return false;



            string[] parts = t.Split('-', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || parts.Length > 3) return false;

            // 单段可以到 8 位（20260906 这种紧凑写法），多段仍限 4 位；越界的年月日由下游区间校验挡掉

            int maxPartLen = parts.Length == 1 ? 8 : 4;

            foreach (string p in parts) if (!IsDigits(p, 1, maxPartLen)) return false;



            if (parts.Length == 1)

            {

                string p = parts[0];

                if (p.Length == 8)

                {

                    int y = ParseInt(p.Substring(0, 4));

                    int m = ParseInt(p.Substring(4, 2));

                    int d = ParseInt(p.Substring(6, 2));

                    return TryDaySpan(y, m, d, out start, out end);

                }

                if (p.Length == 6) return TryMonthSpan(ParseInt(p.Substring(4, 2)), ParseInt(p.Substring(0, 4)), out start, out end);

                // 裸4位数字（如 8080、3000）不作为单段年份，避免程序员常见端口号搜空；年份带「年」如 2026年 照常生效

                // if (p.Length == 4) return TryYearSpan(ParseInt(p), out start, out end);

                return false;

            }



            if (parts.Length == 2)

            {

                if (parts[0].Length >= 3) return TryMonthSpan(ParseInt(parts[1]), ParseInt(parts[0]), out start, out end);

                return TryDaySpan(currentYear, ParseInt(parts[0]), ParseInt(parts[1]), out start, out end);

            }



            int year = ParseInt(parts[0]);

            if (parts[0].Length <= 2) year += 2000;

            return TryDaySpan(year, ParseInt(parts[1]), ParseInt(parts[2]), out start, out end);

        }



        private static bool TryYearSpan(int year, out DateTime start, out DateTime end)

        {

            start = default;

            end = default;

            if (year < 1900 || year > 9999) return false;

            start = new DateTime(year, 1, 1);

            end = start.AddYears(1);

            return true;

        }



        private static bool TryMonthSpan(int month, int year, out DateTime start, out DateTime end)

        {

            start = default;

            end = default;

            if (month < 1 || month > 12 || year < 1900 || year > 9999) return false;

            start = new DateTime(year, month, 1);

            end = start.AddMonths(1);

            return true;

        }



        private static bool TryDaySpan(int year, int month, int day, out DateTime start, out DateTime end)

        {

            start = default;

            end = default;

            if (month < 1 || month > 12 || year < 1900 || year > 9999) return false;

            if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;

            start = new DateTime(year, month, day);

            end = start.AddDays(1);

            return true;

        }



        /// <summary>

        /// 时刻前缀：<c>14</c> / <c>14:0</c> / <c>14:03</c> / <c>14:03:20</c>。

        ///

        /// 只把小时补零到两位、分秒按用户敲进的位数留着做前缀 —— 这样 <c>14:0</c> 能同时命中

        /// 14:00、14:03、14:07（设计稿 §03 第一条的行为），而 <c>4:30</c> 也不会因为少一位而落空。

        /// </summary>

        private static bool TryClockPrefix(string tail, out string prefix)

        {

            prefix = "";

            int colon = tail.IndexOf(':');

            string hourText = colon < 0 ? tail : tail.Substring(0, colon);

            if (!IsDigits(hourText, 1, 2)) return false;



            int hour = ParseInt(hourText);

            if (hour > 23) return false;



            if (colon >= 0)

            {

                string rest = tail.Substring(colon);

                if (rest.Length > 6) return false;

                foreach (char c in rest)

                    if (c != ':' && (c < '0' || c > '9')) return false;

                if (rest[rest.Length - 1] == ':') return false;

                prefix = hour.ToString("00", CultureInfo.InvariantCulture) + rest;

                return true;

            }



            prefix = hour.ToString("00", CultureInfo.InvariantCulture);

            return true;

        }



        /// <summary>类型词：只认「类型:贴图 / 类型:截图 / 类型:长截图」这种带前缀的显式写法。
        /// 2026-09-10 起裸词「截图」等不再劫持为来源筛选——用户搜「截图」多半是想搜图内文字，
        /// 裸词改按普通关键词处理；要按类型筛请打「类型:截图」或点工具栏左段。</summary>

        private static bool TrySourceWord(string s, out CaptureHistorySource[] sources)

        {

            sources = Array.Empty<CaptureHistorySource>();

            if (!s.StartsWith("类型:", StringComparison.Ordinal)) return false;

            string word = s.Substring(3);

            if (word.Length == 0) return false;



            // 「长截图」必须先判：它是「截图」的超串，顺序颠倒会被「截图」抢走

            if (word == "长截图") { sources = new[] { CaptureHistorySource.LongScroll }; return true; }

            if (word == "贴图") { sources = new[] { CaptureHistorySource.Sticker }; return true; }

            // 「截图」档 = 保存 + 剪贴板两类直出截图，与左段筛选同口径

            if (word == "截图") { sources = new[] { CaptureHistorySource.Save, CaptureHistorySource.Clipboard }; return true; }

            return false;

        }



        private static bool IsDigits(string s, int min, int max)

        {

            if (s.Length < min || s.Length > max) return false;

            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;

            return true;

        }



        private static int ParseInt(string s)

            => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    }

}

