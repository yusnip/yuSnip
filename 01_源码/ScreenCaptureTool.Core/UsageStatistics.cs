#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 单日使用计数（趋势图的一个数据点）。纯数据，不含任何画刷或界面状态。
    /// </summary>
    public sealed class UsageDayBucket
    {
        /// <summary>该桶对应的本地日期（时间部分恒为 00:00）。</summary>
        public DateTime Date;
        public long Screenshot;
        public long AiChat;
        public long Ocr;
        public long Translate;
        public long Sticker;

        /// <summary>当日五项合计。</summary>
        public long Total { get { return Screenshot + AiChat + Ocr + Translate + Sticker; } }
    }

    /// <summary>
    /// 本机使用次数统计（截图 / AI 对话 / OCR / 翻译 / 贴图）。
    ///
    /// 刻意独立于 <see cref="AppSettings"/> 存放在 <c>%APPDATA%/yuSnip/stats.ini</c>：
    /// 统计是"本机行为痕迹"而非配置，不参与云同步、不参与本地导出/导入、不被「恢复默认设置」清零，
    /// 混进 settings.ini 会被那三条链路一并带走或覆盖。
    ///
    /// 计数口径：**成功完成才 +1**（取消 / 失败 / 无结果都不计），原四类保持原口径，贴图独立累计；截图转贴图可同时计入两项
    /// （例如翻译内部会跑一次 OCR，那次只算翻译，不计入 OCR 次数）。
    ///
    /// 落盘策略：内存计数为唯一真相，自增后异步节流写盘（<see cref="FlushDelayMs"/>），
    /// 避免高频功能（连续截图）每次都同步写文件拖慢主流程。
    ///
    /// 存储格式 v3（兼容旧版四列日桶）：五项累计数**继续保留**为"全时段总数"，另按天分桶
    /// （<c>Day_yyyyMMdd=截图,AI,OCR,翻译,贴图</c>）保留最近 <see cref="DayBucketRetentionDays"/> 天，
    /// 供「使用统计」页趋势图使用。v1 旧文件可直接读入（累计数不丢），但趋势数据自升级之日起才有。
    /// </summary>
    public static class UsageStatistics
    {
        /// <summary>自增后延迟落盘的节流窗口（毫秒）。窗口内的多次自增合并为一次写盘。</summary>
        private const int FlushDelayMs = 1500;

        /// <summary>日桶保留天数。超期的桶在落盘时裁掉，文件规模因此恒定在百行以内。</summary>
        private const int DayBucketRetentionDays = 90;

        /// <summary>五项计数在日桶数组里的下标。</summary>
        private const int IdxScreenshot = 0;
        private const int IdxAiChat = 1;
        private const int IdxOcr = 2;
        private const int IdxTranslate = 3;
        private const int IdxSticker = 4;
        private const int SeriesCount = 5;

        private static readonly object _lock = new object();

        /// <summary>按天分桶：键为 <c>yyyyMMdd</c>（本地日期），值为 5 项计数。</summary>
        private static readonly Dictionary<string, long[]> _dayBuckets = new Dictionary<string, long[]>();

        /// <summary>是否已从磁盘加载过（懒加载，首次访问时读取）。</summary>
        private static bool _loaded;

        /// <summary>待落盘标记：有未写盘的自增时为 true。</summary>
        private static bool _dirty;

        /// <summary>节流落盘计时器（首次需要时创建）。</summary>
        private static Timer _flushTimer;

        private static long _screenshotCount;
        private static long _aiChatCount;
        private static long _ocrCount;
        private static long _translateCount;
        private static long _stickerCount;

        /// <summary>统计文件路径（与 settings.ini 同目录，独立文件）。</summary>
        public static string StatsPath
        {
            get { return Path.Combine(AppSettings.SettingsDirectory, "stats.ini"); }
        }

        /// <summary>截图次数（保存到文件 / 复制到剪贴板 / 贴图 / 长截图成功各计一次）。</summary>
        public static long ScreenshotCount { get { EnsureLoaded(); lock (_lock) return _screenshotCount; } }

        /// <summary>AI 对话次数（识图与追问每收到一次完整回答计一次）。</summary>
        public static long AiChatCount { get { EnsureLoaded(); lock (_lock) return _aiChatCount; } }

        /// <summary>OCR 次数（文字识别成功出结果计一次，翻译内部的 OCR 不计入）。</summary>
        public static long OcrCount { get { EnsureLoaded(); lock (_lock) return _ocrCount; } }

        /// <summary>翻译次数（译文成功返回计一次）。</summary>
        public static long TranslateCount { get { EnsureLoaded(); lock (_lock) return _translateCount; } }

        /// <summary>贴图首次成功显示次数，不含重复显隐、自动恢复及临时预览。</summary>
        public static long StickerCount { get { EnsureLoaded(); lock (_lock) return _stickerCount; } }

        /// <summary>五项累计总次数（功能调用合计，非去重操作数）。</summary>
        public static long TotalCount
        {
            get
            {
                EnsureLoaded();
                lock (_lock)
                {
                    return _screenshotCount + _aiChatCount + _ocrCount + _translateCount + _stickerCount;
                }
            }
        }

        // -------------------- 自增入口 --------------------

        /// <summary>记录一次成功的截图输出。</summary>
        public static void RecordScreenshot()
        {
            EnsureLoaded();
            lock (_lock) { _screenshotCount++; BumpTodayNoLock(IdxScreenshot); }
            MarkDirty();
        }

        /// <summary>记录一次成功的 AI 对话（收到完整回答）。</summary>
        public static void RecordAiChat()
        {
            EnsureLoaded();
            lock (_lock) { _aiChatCount++; BumpTodayNoLock(IdxAiChat); }
            MarkDirty();
        }

        /// <summary>记录一次成功的 OCR 识别（识别出文字）。</summary>
        public static void RecordOcr()
        {
            EnsureLoaded();
            lock (_lock) { _ocrCount++; BumpTodayNoLock(IdxOcr); }
            MarkDirty();
        }

        /// <summary>记录一次成功的翻译（译文已返回）。</summary>
        public static void RecordTranslate()
        {
            EnsureLoaded();
            lock (_lock) { _translateCount++; BumpTodayNoLock(IdxTranslate); }
            MarkDirty();
        }

        /// <summary>记录一次成功创建的贴图；调用方保证每个窗口只记一次。</summary>
        public static void RecordSticker()
        {
            EnsureLoaded();
            lock (_lock) { _stickerCount++; BumpTodayNoLock(IdxSticker); }
            MarkDirty();
        }

        /// <summary>今日桶对应项 +1（须在 <see cref="_lock"/> 内调用）。</summary>
        private static void BumpTodayNoLock(int index)
        {
            string key = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (!_dayBuckets.TryGetValue(key, out long[] bucket))
            {
                bucket = new long[SeriesCount];
                _dayBuckets[key] = bucket;
            }
            bucket[index]++;
        }

        // -------------------- 分日查询 --------------------

        /// <summary>
        /// 找到分桶记录中最早有实际使用数据（五项之和 > 0）的日期。若全无记录则返回 null。
        /// 须在持有锁时调用。
        /// </summary>
        private static DateTime? GetEarliestDataDateNoLock()
        {
            DateTime? earliest = null;
            foreach (var kvp in _dayBuckets)
            {
                long[] b = kvp.Value;
                if (b == null) continue;
                if (b[IdxScreenshot] + b[IdxAiChat] + b[IdxOcr] + b[IdxTranslate] + b[IdxSticker] <= 0) continue;

                if (DateTime.TryParseExact(kvp.Key, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                {
                    if (!earliest.HasValue || d < earliest.Value)
                    {
                        earliest = d;
                    }
                }
            }
            return earliest;
        }

        /// <summary>
        /// 统计用分日计数（按日期**升序**，固定返回 <paramref name="days"/> 个桶）。
        /// 规则：
        /// 1. 若本机历史跨度已满 N 天，正常显示「近 N 天」（截止到今天）；
        /// 2. 若前面尚无历史数据（起步不满 N 天），从最早有数据的一天起往后展示 N 天，
        ///    使左侧立柱以有内容的日期为起点饱满展开；
        /// 3. 若全部无记录，则返回默认近 N 天区间（供空状态提示渲染）。
        /// </summary>
        /// <param name="days">天数，夹取到 1~<see cref="DayBucketRetentionDays"/>。</param>
        public static IReadOnlyList<UsageDayBucket> GetRecentDays(int days)
        {
            if (days < 1) days = 1;
            if (days > DayBucketRetentionDays) days = DayBucketRetentionDays;

            EnsureLoaded();
            DateTime today = DateTime.Today;
            DateTime normalStart = today.AddDays(-days + 1);

            DateTime startDate = normalStart;
            lock (_lock)
            {
                DateTime? earliest = GetEarliestDataDateNoLock();
                // 如果历史起步晚于 normalStart（即前面尚无历史数据），
                // 则以最早有数据的一天为起点，向后顺延展示 days 天
                if (earliest.HasValue && earliest.Value > normalStart && earliest.Value <= today)
                {
                    startDate = earliest.Value;
                }
            }

            var result = new List<UsageDayBucket>(days);
            lock (_lock)
            {
                for (int i = 0; i < days; i++)
                {
                    DateTime d = startDate.AddDays(i);
                    string key = d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    long[] b;
                    if (!_dayBuckets.TryGetValue(key, out b)) b = null;
                    result.Add(new UsageDayBucket
                    {
                        Date = d,
                        Screenshot = b == null ? 0 : b[IdxScreenshot],
                        AiChat = b == null ? 0 : b[IdxAiChat],
                        Ocr = b == null ? 0 : b[IdxOcr],
                        Translate = b == null ? 0 : b[IdxTranslate],
                        Sticker = b == null ? 0 : b[IdxSticker],
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// 一键清空全部统计：五项计数归零并立即落盘（不保留历史，不可撤销）。
        /// </summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                _screenshotCount = 0;
                _aiChatCount = 0;
                _ocrCount = 0;
                _translateCount = 0;
                _stickerCount = 0;
                _dayBuckets.Clear();   // 趋势数据同步清空，否则图表还留着已被清掉的历史
                _loaded = true;   // 归零即为当前真相，无需再读旧文件
                _dirty = false;
            }
            SaveNow();
            AppLogger.Info("UsageStatistics: 已清空本机使用统计。");
        }

        /// <summary>
        /// 立即把内存计数同步写盘（应用退出前调用，防止节流窗口内的自增丢失）。
        /// </summary>
        public static void Flush()
        {
            bool need;
            lock (_lock) { need = _dirty; }
            if (need) SaveNow();
        }

        // -------------------- 持久化 --------------------

        /// <summary>首次访问时从磁盘加载（文件不存在按全 0 起算，不写盘）。</summary>
        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true; // 先置位：读取失败也按全 0 起算，不反复重试
                try
                {
                    if (!File.Exists(StatsPath)) return;

                    string[] lines = File.ReadAllLines(StatsPath);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (line == null) continue;
                        line = line.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int p = line.IndexOf('=');
                        if (p <= 0) continue;
                        string key = line.Substring(0, p).Trim();
                        string value = line.Substring(p + 1).Trim();

                        // v3 日桶（兼容旧四列，贴图缺省为 0）：Day_yyyyMMdd=截图,AI,OCR,翻译,贴图
                        if (key.StartsWith("Day_", StringComparison.OrdinalIgnoreCase))
                        {
                            ParseDayBucketNoLock(key, value);
                            continue;
                        }

                        if (!long.TryParse(value, out long n) || n < 0) continue;

                        switch (key)
                        {
                            case "ScreenshotCount": _screenshotCount = n; break;
                            case "AiChatCount": _aiChatCount = n; break;
                            case "OcrCount": _ocrCount = n; break;
                            case "TranslateCount": _translateCount = n; break;
                            case "StickerCount": _stickerCount = n; break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("UsageStatistics: 读取统计文件失败，按 0 起算。" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 解析一行日桶（须在 <see cref="_lock"/> 内调用）。
        /// 格式不合法、日期非法或超出保留窗口的行一律丢弃（不报错，视为脏数据自愈）。
        /// </summary>
        private static void ParseDayBucketNoLock(string key, string value)
        {
            string dayKey = key.Substring(4).Trim();
            if (dayKey.Length != 8) return;
            if (!DateTime.TryParseExact(dayKey, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime day)) return;
            if (day.Date < DateTime.Today.AddDays(-DayBucketRetentionDays + 1)) return;
            if (day.Date > DateTime.Today) return; // 系统时间曾被调后过，未来日期的桶无意义

            string[] parts = value.Split(',');
            if (parts.Length < 4) return;

            var bucket = new long[SeriesCount];
            for (int i = 0; i < Math.Min(parts.Length, SeriesCount); i++)
            {
                if (long.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v >= 0)
                    bucket[i] = v;
            }
            _dayBuckets[dayKey] = bucket;
        }

        /// <summary>标记待落盘并重置节流计时器（窗口内多次自增合并为一次写盘）。</summary>
        private static void MarkDirty()
        {
            lock (_lock)
            {
                _dirty = true;
                if (_flushTimer == null)
                {
                    _flushTimer = new Timer(OnFlushTimer, null, FlushDelayMs, Timeout.Infinite);
                }
                else
                {
                    // 已有计时器则重新计时：连续操作期间不写盘，安静下来后统一落一次
                    try { _flushTimer.Change(FlushDelayMs, Timeout.Infinite); } catch { }
                }
            }
        }

        private static void OnFlushTimer(object state)
        {
            try
            {
                bool need;
                lock (_lock) { need = _dirty; }
                if (need) SaveNow();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("UsageStatistics: 节流落盘失败。" + ex.Message);
            }
        }

        /// <summary>同步写盘（内存计数为唯一真相，整文件覆盖）。</summary>
        private static void SaveNow()
        {
            try
            {
                string dir = AppSettings.SettingsDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<string> lines = new List<string>(DayBucketRetentionDays + 8);
                lock (_lock)
                {
                    lines.Add("# yuSnip usage statistics v3 (本机数据，不参与云同步与导出)");
                    lines.Add("# 五项为全时段累计；Day_yyyyMMdd=截图,AI,OCR,翻译,贴图 为按天分桶（保留最近 "
                        + DayBucketRetentionDays + " 天，供趋势图使用）");
                    lines.Add("ScreenshotCount=" + _screenshotCount.ToString(CultureInfo.InvariantCulture));
                    lines.Add("AiChatCount=" + _aiChatCount.ToString(CultureInfo.InvariantCulture));
                    lines.Add("OcrCount=" + _ocrCount.ToString(CultureInfo.InvariantCulture));
                    lines.Add("TranslateCount=" + _translateCount.ToString(CultureInfo.InvariantCulture));
                    lines.Add("StickerCount=" + _stickerCount.ToString(CultureInfo.InvariantCulture));

                    // 落盘时裁掉超期桶：内存与文件同步收敛，文件规模恒定在百行以内
                    string cutoff = DateTime.Today.AddDays(-DayBucketRetentionDays + 1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    List<string> expired = null;
                    foreach (KeyValuePair<string, long[]> kv in _dayBuckets)
                    {
                        if (string.CompareOrdinal(kv.Key, cutoff) < 0)
                        {
                            if (expired == null) expired = new List<string>();
                            expired.Add(kv.Key);
                        }
                    }
                    if (expired != null)
                    {
                        for (int i = 0; i < expired.Count; i++) _dayBuckets.Remove(expired[i]);
                    }

                    // 按日期升序写出，便于人工阅读与 diff
                    List<string> keys = new List<string>(_dayBuckets.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    for (int i = 0; i < keys.Count; i++)
                    {
                        long[] b = _dayBuckets[keys[i]];
                        lines.Add("Day_" + keys[i] + "="
                            + b[IdxScreenshot].ToString(CultureInfo.InvariantCulture) + ","
                            + b[IdxAiChat].ToString(CultureInfo.InvariantCulture) + ","
                            + b[IdxOcr].ToString(CultureInfo.InvariantCulture) + ","
                            + b[IdxTranslate].ToString(CultureInfo.InvariantCulture) + ","
                            + b[IdxSticker].ToString(CultureInfo.InvariantCulture));
                    }
                    _dirty = false;
                }
                File.WriteAllLines(StatsPath, lines.ToArray());
            }
            catch (Exception ex)
            {
                AppLogger.Warn("UsageStatistics: 写入统计文件失败。" + ex.Message);
            }
        }
    }
}
