#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenCaptureTool.Core
{
    /// <summary>截图主工具栏按钮元数据。</summary>
    public sealed class ToolbarButtonInfo
    {
        public string Id = "";
        public string Name = "";
        public string IconPath = "";
        /// <summary>true = Path 以 Fill 渲染；false = 以 Stroke 描边渲染（icon 风格）。</summary>
        public bool IsFill;
        /// <summary>描边图标使用的固定颜色（如取消红 / 完成绿）；null 表示跟随主题图标色。</summary>
        public string? StrokeColor;
        /// <summary>图标显示尺寸缩放（真实工具栏中马赛克为 14.5/16、抠图为 17.8/16——填充图标要按描边图标的实墨跨度取尺寸才能对齐）。</summary>
        public double IconScale = 1.0;
    }

    /// <summary>
    /// 主工具栏按钮注册表：集中定义所有可自定义按钮的 ID / 名称 / 图标，
    /// 并负责布局串（逗号分隔，"|" 表示分隔线）的解析与序列化。
    /// </summary>
    public static class ToolbarRegistry
    {
        public const string DividerToken = "|";

        /// <summary>截图、录屏预览及长截图共用的快速保存几何，描边方式沿用 Path.icon。</summary>
        public const string QuickSaveIcon = "M 12 4 V 14 M 7 9 L 12 14 L 17 9 M 4 15 V 17 A 3 3 0 0 0 7 20 H 17 A 3 3 0 0 0 20 17 V 15";

        /// <summary>组的边界与成员分隔符：组写成 <c>[画笔+箭头+文本]</c> 这样的单个 token。</summary>
        private const string GroupOpen = "[";
        private const string GroupClose = "]";
        private const char GroupMemberSep = '+';

        /// <summary>一组最少几枚：不足则散架回普通按钮，不留空壳。</summary>
        public const int MinGroupMembers = 2;
        // 组内不设枚数上限（2026-09-20 用户要求「拓展一下，改成无限制」）：组卡成员行与截图侧二级
        // 成员栏都已改成可换行，宽度不再由枚数决定，故这里刻意不留 Max 常量。
        /// <summary>整条最多几组：同时展开两组会比原样更宽。超出的组整体散架成独立格（不丢工具）。</summary>
        public const int MaxGroups = 3;

        /// <summary>出口键永不进组：收进组意味着"想存图还得先开组"，误点代价最大。</summary>
        public static readonly string[] NoGroupIds = { "saveas", "cancel", "confirm" };

        /// <summary>录屏图标几何（单一来源）。2026-09-29 按用户要求由"摄像机"改成**录制符号**：
        /// 一枚大圆环套一颗实心圆点（⏺）。
        /// <para>三枚同心圆，圆心 (8,8)：外圈半径 <b>8</b>（正好顶满 16×16 的紧包围盒，与同排描边件的
        /// 16 单位骨架一致）、环内孔半径 <b>6.2</b>（⇒ 环厚 1.8＝本排线粗档）、实心点半径 <b>3.2</b>
        /// （点径 6.4 ≈ 环内径的一半，留 3 单位的空隙，"套着"才读得出来）。</para>
        /// <para>🔴 走**填充**不走描边，且**方向必须交替**（外圈顺时针 → 内孔逆时针 → 实心点顺时针）：
        /// 这样 NonZero 与 EvenOdd 两种填充规则画出来是同一张图。两个消费方（截图工具栏那枚 <c>Path</c>
        /// 与设置页工具栏编辑器的 <c>CreateToolbarIcon</c>）都没有设过 <c>FillRule</c>，默认 NonZero——
        /// 若三枚圆同向，NonZero 下"内孔"的绕数仍是 2（非零）＝不掏空，整枚会糊成一个实心盘。
        /// 用描边做不到这件事：环宽与点径得是同一个 <c>StrokeThickness</c>，而本排恒为 1.8。</para>
        /// <para>元素盒取 <b>17.8</b>（<see cref="ToolbarButtonInfo.IconScale"/>＝1.1125）：同排描边件是
        /// "16 单位骨架 ＋ 1.8 描边"＝实墨 17.8，填充件的墨就是包围盒本身，不放大一圈会比邻居小 10%。
        /// 与 <c>matting</c> 同一处理（见 <c>CaptureWindow.axaml</c> 那侧的注释）。</para>
        /// <para>🔴 这份几何住在 Core 而不是主程序的 <c>OcrIconGeometry</c>：Core 工程不引用主程序工程，
        /// 引用方向只能反过来。旧写法是 <c>CaptureWindow.axaml</c> 内联一份彩色两件套、这里再抄一份单色合并版
        /// ⇒ 两处坐标，改一处必漏一处（设置页预览与真实工具栏长得不一样）。现两侧都引本常量，单一来源。</para>
        /// </summary>
        public const string RecordIconPath =
              "M 16,8 A 8,8 0 1 1 0,8 A 8,8 0 1 1 16,8 Z"
            + " M 1.8,8 A 6.2,6.2 0 1 0 14.2,8 A 6.2,6.2 0 1 0 1.8,8 Z"
            + " M 11.2,8 A 3.2,3.2 0 1 1 4.8,8 A 3.2,3.2 0 1 1 11.2,8 Z";

        /// <summary>
        /// 翻译（描边「文A」＋对角双角括号）：截图工具栏「翻译」钮的图标。
        /// 2026-09-30 起提为常量：贴图划词工具栏的「翻译」逐字复用同一份（用户口径
        /// 「翻译图标可直接复用截图工具栏中的翻译图标」），与 <see cref="RecordIconPath"/>
        /// 同一条「住在 Core、两边引」的单一来源纪律。
        /// </summary>
        public const string TranslateIconPath =
            "M 16 4 H 17 A 3 3 0 0 1 20 7 V 8 M 8 20 H 7 A 3 3 0 0 1 4 17 V 16 M 4 7 H 12 V 11 H 4 Z M 8 4 V 15 M 16 10 L 12 20 M 16 10 L 20 20 M 14 15 H 18";

        public static readonly ToolbarButtonInfo[] Buttons = new ToolbarButtonInfo[]
        {
            new ToolbarButtonInfo { Id = "aivision",  Name = "AI 识图", IsFill = true, IconPath = "m93.65,77.36c.54-1.12.95-1.95,1.33-2.79,5.63-12.67,12.36-24.72,20.74-35.79,8.43-11.12,18.38-20.63,30.25-28.03,7.93-4.95,16.4-8.59,25.71-10,13.59-2.07,26.27.01,37.48,8.47,9.22,6.96,14.23,16.22,14.89,27.76.62,10.83-1.66,21.15-6.04,30.98-2.63,5.9-5.68,11.62-8.61,17.38-4.45,8.75-9.13,17.4-13.42,26.23-6.73,13.88-10.49,28.61-11.63,43.97-.55,7.44-.3,14.87,1.75,22.11,3.34,11.82,11.47,18.27,23.3,20.27,4.8.81,9.37-.42,13.65-2.64,8.23-4.25,14.62-10.55,19.64-18.22,5.35-8.18,9.57-16.96,13.04-26.1,6.3-16.59,11.48-33.53,15.46-50.82,1.34-5.83,2.26-11.76,3.65-17.58,2.01-8.4,6.38-15.38,12.96-21.07,11.06-9.56,29.61-9.35,40.31,3.06,5.13,5.95,7.99,12.73,8.72,20.58.93,10.1-.69,19.9-2.75,29.7-1.37,6.48-2.83,12.96-3.8,19.5-1.77,11.97-1.67,23.89,2.08,35.56,2.18,6.78,5.47,12.92,10.36,18.16,3.49,3.74,6.99,7.47,10.34,11.33,8.44,9.72,11.02,21.17,9.7,33.73-.77,7.36-3.34,13.95-8.3,19.51-8.78,9.84-21.95,12.96-33.79,7.89-4.73-2.02-9.06-4.72-12.94-8.11-7.36-6.43-10.13-9.63-18.5-21.37-.6.64-1.19,1.23-1.74,1.85-8.29,9.35-17.09,18.16-27.22,25.54-8.84,6.44-18.27,11.84-28.99,14.41-21.92,5.27-42.53,1.96-61.49-10.37-18.91-12.3-31.93-29.4-40.26-50.23-4.51-11.29-7.03-23.03-8.24-35.09-.66-6.53-1.03-13.06-.79-19.62.14-3.89.45-7.78.7-12.05-.52.79-.89,1.27-1.18,1.81-14.36,26.94-30.19,53.03-46.09,79.07-4.16,6.82-8.3,13.65-12.5,20.44-3.34,5.4-7.75,9.73-13.3,12.85-17.85,10.04-41.17.76-46.83-18.67-2.66-9.13-1.05-17.74,3.61-25.82,2.75-4.77,5.8-9.37,8.76-14.02,7.6-11.93,14.94-24.01,21.49-36.55,1.48-2.83,2.85-5.72,4.11-8.65,3.86-8.98,4.49-18.17,1.93-27.64-2.17-8.04-4.8-15.87-8.71-23.27-9.14-17.3-18.17-34.66-27.27-51.98-2.13-4.05-4.08-8.15-4.73-12.73C-1.48,26.41,3.15,15.08,14.42,6.79,31.4-5.71,54.68-.12,64.29,18.67c5.13,10.02,9.84,20.26,14.84,30.34,4.44,8.94,9.02,17.81,13.54,26.71.24.47.54.91.97,1.63Z" },
            new ToolbarButtonInfo { Id = "mosaic",    Name = "马赛克", IconScale = 0.906, IconPath = "F0 M 7 4 H 17 A 3 3 0 0 1 20 7 V 17 A 3 3 0 0 1 17 20 H 7 A 3 3 0 0 1 4 17 V 7 A 3 3 0 0 1 7 4 Z M 12 12 H 20 V 7 A 3 3 0 0 0 17 4 H 12 Z M 12 12 H 4 V 17 A 3 3 0 0 0 7 20 H 12 Z", IsFill = true },
            new ToolbarButtonInfo { Id = "spotlight", Name = "聚光灯", IconPath = "M 7 4 H 17 A 3 3 0 0 1 20 7 V 17 A 3 3 0 0 1 17 20 H 7 A 3 3 0 0 1 4 17 V 7 A 3 3 0 0 1 7 4 Z M 12 8 A 4 4 0 1 0 12 16 A 4 4 0 1 0 12 8 Z", IsFill = true },
            new ToolbarButtonInfo { Id = "longscroll", Name = "长截图", IconPath = "M 4 10 V 7 A 3 3 0 0 1 7 4 H 17 A 3 3 0 0 1 20 7 V 10 M 10 18 A 2 2 0 1 1 6 18 A 2 2 0 1 1 10 18 M 18 18 A 2 2 0 1 1 14 18 A 2 2 0 1 1 18 18 M 9.3 16.4 L 16.5 7.5 M 14.7 16.4 L 7.5 7.5" },
            // 录屏：几何住在下面的 RecordIconPath 常量里（截图工具栏与本页编辑器**同一份**，不再两处各抄）。
            // 2026-09-29 起是填充族（环＋实心点，见该常量说明），IconScale 与 matting 同值＝把 16 的墨放大到描边件的 17.8。
            new ToolbarButtonInfo { Id = "record",    Name = "录屏", IconPath = RecordIconPath, IsFill = true, IconScale = 1.1125 },
            new ToolbarButtonInfo { Id = "ocr",       Name = "文字识别", IconPath = "M 8 4 H 7 A 3 3 0 0 0 4 7 V 8 M 16 4 H 17 A 3 3 0 0 1 20 7 V 8 M 20 16 V 17 A 3 3 0 0 1 17 20 H 16 M 8 20 H 7 A 3 3 0 0 1 4 17 V 16 M 12 7 L 8.5 17 M 12 7 L 15.5 17 M 10 13 H 14" },
            new ToolbarButtonInfo { Id = "translate", Name = "翻译", IconPath = TranslateIconPath },
            new ToolbarButtonInfo { Id = "matting",   Name = "智能抠图", IconScale = 1.1125, IconPath = "M 8 3.1 H 7 A 3.9 3.9 0 0 0 3.1 7 V 8 A 0.9 0.9 0 0 0 4.9 8 V 7 A 2.1 2.1 0 0 1 7 4.9 H 8 A 0.9 0.9 0 0 0 8 3.1 Z M 16 3.1 H 17 A 3.9 3.9 0 0 1 20.9 7 V 8 A 0.9 0.9 0 0 1 19.1 8 V 7 A 2.1 2.1 0 0 0 17 4.9 H 16 A 0.9 0.9 0 0 1 16 3.1 Z M 20.9 16 V 17 A 3.9 3.9 0 0 1 17 20.9 H 16 A 0.9 0.9 0 0 1 16 19.1 H 17 A 2.1 2.1 0 0 0 19.1 17 V 16 A 0.9 0.9 0 0 1 20.9 16 Z M 8 20.9 H 7 A 3.9 3.9 0 0 1 3.1 17 V 16 A 0.9 0.9 0 0 1 4.9 16 V 17 A 2.1 2.1 0 0 0 7 19.1 H 8 A 0.9 0.9 0 0 1 8 20.9 Z M 12 5.9 A 2.3 2.3 0 1 0 12 10.5 A 2.3 2.3 0 1 0 12 5.9 Z M 12 12 C 9.6 12 7.6 13.4 7.2 15.8 C 7 16.6 7.6 17.2 8.4 17.2 H 15.6 C 16.4 17.2 17 16.6 16.8 15.8 C 16.4 13.4 14.4 12 12 12 Z", IsFill = true },
            new ToolbarButtonInfo { Id = "brush",     Name = "画笔", IconPath = "M 4 20 L 4 16 L 16 4 A 2.8 2.8 0 0 1 20 8 L 8 20 Z M 14.5 5.5 L 18.5 9.5 M 4 16 L 8 20 M 4 20 L 6 18" },
            new ToolbarButtonInfo { Id = "arrow",     Name = "箭头", IconPath = "M 4 20 L 20 4 M 10 4 H 20 V 14" },
            new ToolbarButtonInfo { Id = "text",      Name = "文本", IconPath = "M 4 20 L 10 6 A 2 2 0 0 1 14 6 L 20 20 M 8 14 H 16" },
            new ToolbarButtonInfo { Id = "rectangle", Name = "形状", IconPath = "M 7 4 H 11 A 3 3 0 0 1 14 7 V 11 A 3 3 0 0 1 11 14 H 7 A 3 3 0 0 1 4 11 V 7 A 3 3 0 0 1 7 4 Z M 20 15 A 5 5 0 1 1 10 15 A 5 5 0 1 1 20 15 Z" },
            new ToolbarButtonInfo { Id = "counter",   Name = "序号", IconPath = "M 20 12 A 8 8 0 1 1 4 12 A 8 8 0 1 1 20 12 Z M 10 9 L 12 8 V 16 M 10 16 H 14" },
            new ToolbarButtonInfo { Id = "watermark", Name = "水印", IconPath = "M 4 12 L 12 4 M 4 20 L 20 4 M 12 20 L 20 12" },
            new ToolbarButtonInfo { Id = "eraser",    Name = "橡皮擦", IconPath = "M982.827818 408.890055a32.85895 32.85895 0 0 0 0-46.434253L629.96587 9.593854a32.85895 32.85895 0 0 0-46.434253 0L69.972616 523.104886a131.147983 131.147983 0 0 0 0 185.641074l278.845365 277.118471a131.33986 131.33986 0 0 0 92.484752 38.135569h421.64988a32.81098 32.81098 0 0 0 0-65.621961h-271.122312a65.66993 65.66993 0 0 1-46.434253-112.056214zM422.978472 875.966834a65.573992 65.573992 0 0 1-92.772568 0L116.502808 662.311707a65.573992 65.573992 0 0 1 0-92.772568L247.458914 438.439125l306.523633 306.523633-131.100014 131.004076z", IsFill = true },
            // 「清除全部标注」不再是截图主工具栏的一格（2026-10-06）：全清入口收在橡皮擦参数条里。
            // 贴图与录屏的工具栏仍各自持有它——那两枚按钮的图标走 AnnotationToolButton 自己那张表，不读本表。
            new ToolbarButtonInfo { Id = "undo",      Name = "撤销", IconPath = "M 9 10 L 4 15 L 9 20 M 4 15 H 14.5 A 5.5 5.5 0 0 0 20 9.5 A 5.5 5.5 0 0 0 14.5 4 H 10" },
            new ToolbarButtonInfo { Id = "sticker",   Name = "贴图", IconPath = "M686.08 133.12c-20.48-20.48-56.32-20.48-76.8 0l-76.8 76.8 35.84 35.84L404.48 409.6l-30.72-35.84c-20.48-20.48-56.32-20.48-76.8 0l-35.84 35.84c-20.48 20.48-20.48 56.32 0 76.8l327.68 322.56c20.48 20.48 56.32 20.48 76.8 0l30.72-30.72c20.48-20.48 20.48-56.32 0-76.8L665.6 665.6l168.96-163.84 35.84 35.84 71.68-71.68c20.48-20.48 20.48-56.32 0-76.8l-256-256zM563.2 588.8L481.28 506.88 680.96 307.2l76.8 76.8-194.56 204.8zM399.36 675.84c20.48 20.48 20.48 51.2 0 71.68L230.4 921.6c-20.48 20.48-56.32 20.48-76.8 0-20.48-20.48-20.48-51.2 0-71.68l168.96-168.96c25.6-25.6 56.32-25.6 76.8-5.12z", IsFill = true },
            new ToolbarButtonInfo { Id = "quicksave", Name = "快速保存", IconPath = QuickSaveIcon },
            new ToolbarButtonInfo { Id = "saveas",    Name = "另存为",   IconPath = "M15,9H5V5H15M12,19A3,3 0 0,1 9,16A3,3 0 0,1 12,13A3,3 0 0,1 15,16A3,3 0 0,1 12,19M17,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V7L17,3Z", IsFill = true },
            new ToolbarButtonInfo { Id = "cancel",    Name = "取消", IconPath = "M 5 5 L 19 19 M 19 5 L 5 19", StrokeColor = "#FF5555" },
            new ToolbarButtonInfo { Id = "confirm",   Name = "完成", IconPath = "M 4 13 L 10 19 L 20 4", StrokeColor = "#07C160" },
        };

        /// <summary>默认布局（与旧版 XAML 静态工具栏一致）。</summary>
        public const string DefaultLayout =
            "aivision,|,mosaic,spotlight,longscroll,record,ocr,translate,matting,|,brush,arrow,text,rectangle,counter,watermark,eraser,undo,|,sticker,quicksave,saveas,cancel,confirm";

        private static readonly Dictionary<string, ToolbarButtonInfo> _map =
            Buttons.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);

        public static ToolbarButtonInfo? Find(string id)
        {
            if (string.Equals(id, "save", StringComparison.OrdinalIgnoreCase))
            {
                return _map.TryGetValue("quicksave", out var qs) ? qs : null;
            }
            return _map.TryGetValue(id, out var info) ? info : null;
        }

        /// <summary>该 id 是否属于"永不进组"的出口键。</summary>
        public static bool IsNoGroupId(string? id) =>
            NoGroupIds.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>token 是否是一个组（形如 <c>[a+b+c]</c>）。</summary>
        public static bool IsGroupToken(string? token) =>
            !string.IsNullOrEmpty(token) && token!.Length > 2 && token[0] == '[' && token[token.Length - 1] == ']';

        /// <summary>取组的成员（规范 Id、已去重、已剔除未知，支持分隔线 |）；非组 token 返回空表。</summary>
        public static List<string> GroupMembers(string? token, Func<string, ToolbarButtonInfo?>? find = null)
        {
            List<string> ids = new List<string>();
            if (!IsGroupToken(token)) return ids;
            string body = token!.Substring(1, token.Length - 2);
            foreach (string raw in body.Split(GroupMemberSep, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim();
                if (string.Equals(item, DividerToken, StringComparison.Ordinal))
                {
                    // 分隔线容错去重：不能首位，不能连续
                    if (ids.Count > 0 && !string.Equals(ids[ids.Count - 1], DividerToken, StringComparison.Ordinal))
                    {
                        ids.Add(DividerToken);
                    }
                    continue;
                }
                if ((find ?? Find)(item) is { } info && !ids.Contains(info.Id, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(info.Id);
                }
            }
            while (ids.Count > 0 && string.Equals(ids[ids.Count - 1], DividerToken, StringComparison.Ordinal))
            {
                ids.RemoveAt(ids.Count - 1);
            }
            return ids;
        }

        /// <summary>清洗组内成员清单：去除连续分隔线、去除组头和组尾分隔线、去除重复工具并保留合法顺序。</summary>
        public static List<string> CleanGroup(IEnumerable<string> memberIds, Func<string, ToolbarButtonInfo?>? find = null)
        {
            List<string> ids = new List<string>();
            foreach (string id in memberIds)
            {
                if (string.Equals(id, DividerToken, StringComparison.Ordinal))
                {
                    if (ids.Count > 0 && !string.Equals(ids[ids.Count - 1], DividerToken, StringComparison.Ordinal))
                    {
                        ids.Add(DividerToken);
                    }
                    continue;
                }
                if ((find ?? Find)(id) is { } info && !ids.Contains(info.Id, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(info.Id);
                }
            }
            while (ids.Count > 0 && string.Equals(ids[ids.Count - 1], DividerToken, StringComparison.Ordinal))
            {
                ids.RemoveAt(ids.Count - 1);
            }
            return ids;
        }

        /// <summary>由成员 id 造一个组 token（去重去未知，保持传入顺序，支持分隔线 |）。</summary>
        public static string MakeGroupToken(IEnumerable<string> memberIds, Func<string, ToolbarButtonInfo?>? find = null)
        {
            List<string> ids = CleanGroup(memberIds, find);
            return GroupOpen + string.Join(GroupMemberSep, ids) + GroupClose;
        }

        /// <summary>
        /// 解析「未上栏组库」串：若干 <c>[a+b+c]</c> token 以逗号分隔，语法与布局串里的组完全一致。
        /// 库里的组不占工具栏任何一格，只在设置页的组区留着待用。
        /// 与 <see cref="Parse"/> 同一口径就地校正且不丢工具：组内不足 2 枚整条丢弃（成员自然退回工具池）、
        /// 出口键剔出、跨组重复只留首次。组内枚数不限。
        /// </summary>
        public static List<string> ParseLibrary(string? library)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(library)) return result;

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in library!.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IsGroupToken(raw.Trim())) continue;
                List<string> kept = new List<string>();
                foreach (string id in GroupMembers(raw.Trim()))
                {
                    if (string.Equals(id, DividerToken, StringComparison.Ordinal))
                    {
                        if (kept.Count > 0 && !string.Equals(kept[kept.Count - 1], DividerToken, StringComparison.Ordinal))
                        {
                            kept.Add(DividerToken);
                        }
                        continue;
                    }
                    if (IsNoGroupId(id) || !used.Add(id)) continue;
                    kept.Add(id);
                }
                while (kept.Count > 0 && string.Equals(kept[kept.Count - 1], DividerToken, StringComparison.Ordinal))
                {
                    kept.RemoveAt(kept.Count - 1);
                }
                int toolCount = kept.Count(x => !string.Equals(x, DividerToken, StringComparison.Ordinal));
                if (toolCount >= MinGroupMembers) result.Add(MakeGroupToken(kept));
            }
            return result;
        }

        /// <summary>序列化组库；只留合法组 token，空库返回空串（此时不写该键）。</summary>
        public static string SerializeLibrary(IEnumerable<string> groupTokens) =>
            string.Join(",", groupTokens.Where(IsGroupToken));

        /// <summary>该工具是否已在布局里（组内成员也算）。</summary>
        public static bool HasId(IEnumerable<string> tokens, string id)
        {
            if (string.Equals(id, DividerToken, StringComparison.Ordinal)) return false;
            foreach (string token in tokens)
            {
                if (IsGroupToken(token))
                {
                    if (GroupMembers(token).Contains(id, StringComparer.OrdinalIgnoreCase)) return true;
                }
                else if (string.Equals(Find(token)?.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>可见格数：组算一格，分隔线不计。</summary>
        public static int SlotCount(IEnumerable<string> tokens) =>
            tokens.Count(t => !string.Equals(t, DividerToken, StringComparison.Ordinal));

        /// <summary>
        /// 解析布局串为 token 列表（token 可能是按钮 id、分隔线、或一个组）。
        /// 未知 token 忽略；整条为空 / 组语法残缺时回退默认布局（宁回退也不静默少工具）；
        /// 同一工具重复出现只留首次；组按 MinGroupMembers/MaxGroups 与出口键白头校正（组内枚数不限），
        /// 校正不丢工具——被踢出组的成员一律退回独立格。连续分隔线折叠为一条，行首行尾的分隔线剔除。
        /// </summary>
        public static List<string> Parse(string? layout)
        {
            List<string> result = new List<string>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string source = string.IsNullOrWhiteSpace(layout) ? DefaultLayout : layout!;
            bool malformed = false;
            int groupCount = 0;

            foreach (string raw in source.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;

                if (string.Equals(token, DividerToken, StringComparison.Ordinal))
                {
                    if (result.Count == 0 || result[result.Count - 1] == DividerToken) continue;
                    result.Add(DividerToken);
                    continue;
                }

                bool looksLikeGroup = token[0] == '[' || token[token.Length - 1] == ']';
                if (looksLikeGroup && !IsGroupToken(token))
                {
                    malformed = true;
                    break;
                }

                if (looksLikeGroup)
                {
                    List<string> kept = new List<string>();
                    List<string> spill = new List<string>();
                    foreach (string id in GroupMembers(token))
                    {
                        if (string.Equals(id, DividerToken, StringComparison.Ordinal))
                        {
                            if (kept.Count > 0 && !string.Equals(kept[kept.Count - 1], DividerToken, StringComparison.Ordinal))
                            {
                                kept.Add(DividerToken);
                            }
                            continue;
                        }
                        if (!used.Add(id)) continue;
                        (IsNoGroupId(id) ? spill : kept).Add(id);
                    }
                    while (kept.Count > 0 && string.Equals(kept[kept.Count - 1], DividerToken, StringComparison.Ordinal))
                    {
                        kept.RemoveAt(kept.Count - 1);
                    }

                    int toolCount = kept.Count(x => !string.Equals(x, DividerToken, StringComparison.Ordinal));

                    bool asGroup = toolCount >= MinGroupMembers && groupCount < MaxGroups;
                    if (asGroup)
                    {
                        groupCount++;
                        result.Add(MakeGroupToken(kept));
                    }
                    else
                    {
                        foreach (string item in kept)
                        {
                            if (string.Equals(item, DividerToken, StringComparison.Ordinal))
                            {
                                if (result.Count > 0 && !string.Equals(result[result.Count - 1], DividerToken, StringComparison.Ordinal))
                                {
                                    result.Add(DividerToken);
                                }
                            }
                            else
                            {
                                result.Add(item);
                            }
                        }
                    }
                    result.AddRange(spill);
                    continue;
                }

                if (Find(token) is { } info && used.Add(info.Id))
                {
                    result.Add(info.Id);
                }
            }

            if (malformed)
            {
                AppLogger.Warn("ToolbarRegistry: 工具栏布局串里的组语法不完整，已整体回退默认布局：" + layout);
                return Parse(null);
            }
            return Finish(result);
        }

        /// <summary>收尾：剔除行尾分隔线；整条为空时回退默认布局（只回退一次，默认布局自身可信）。</summary>
        private static List<string> Finish(List<string> result)
        {
            if (result.Count > 0 && result[result.Count - 1] == DividerToken) result.RemoveAt(result.Count - 1);
            if (result.Count == 0)
            {
                List<string> fallback = new List<string>();
                foreach (string raw in DefaultLayout.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(raw, DividerToken, StringComparison.Ordinal))
                    {
                        if (fallback.Count == 0 || fallback[fallback.Count - 1] == DividerToken) continue;
                        fallback.Add(DividerToken);
                    }
                    else if (Find(raw) is { } info)
                    {
                        fallback.Add(info.Id);
                    }
                }
                return Finish(fallback);
            }
            return result;
        }

        /// <summary>规范化布局串（解析后再序列化），用于持久化前清理用户输入。</summary>
        public static string Normalize(string? layout) => string.Join(",", Parse(layout));

        /// <summary>
        /// 老配置平滑迁移用通用例程：把一枚新增按键插进用户已自定义的布局。依次试各锚点，
        /// 命中即插在它<b>之后</b>（锚点在组里也算命中，此时新格排在那一组的右侧，不让组散架）；
        /// 锚点全不在则退到第一条分隔线之前，连分隔线都没有就挂到末尾。
        /// 返回 true 表示本次做了修改（该键已在布局里时原样返回 false，用户主动丢回工具池的项不会被插回）。
        /// </summary>
        public static bool InsertTokenAfter(List<string> tokens, string id, params string[] anchorIds)
        {
            if (tokens == null) return false;
            if (HasId(tokens, id)) return false;

            foreach (string anchor in anchorIds)
            {
                int index = tokens.FindIndex(t => string.Equals(t, anchor, StringComparison.OrdinalIgnoreCase)
                    || (IsGroupToken(t) && GroupMembers(t).Contains(anchor, StringComparer.OrdinalIgnoreCase)));
                if (index >= 0)
                {
                    tokens.Insert(index + 1, id);
                    return true;
                }
            }

            int firstDivider = tokens.FindIndex(t => t == DividerToken);
            if (firstDivider >= 0) tokens.Insert(firstDivider, id);
            else tokens.Add(id);
            return true;
        }

        /// <summary>检查 tokens 是否包含智能抠图（matting），未包含则插到「翻译」右侧（历史迁移入口）。</summary>
        public static bool MigrateTokens(List<string> tokens) =>
            InsertTokenAfter(tokens, "matting", "translate", "ocr");

        /// <summary>对布局字符串执行平滑迁移，确保最新内置按钮（如抠图）被纳入。</summary>
        public static string MigrateLayout(string? layout)
        {
            if (string.IsNullOrWhiteSpace(layout)) return DefaultLayout;
            List<string> tokens = Parse(layout);
            MigrateTokens(tokens);
            return Normalize(string.Join(",", tokens));
        }

        /// <summary>布局串是否与默认一致。</summary>
        public static bool IsDefault(string? layout) =>
            string.Equals(Normalize(layout), DefaultLayout, StringComparison.OrdinalIgnoreCase);
    }
}
