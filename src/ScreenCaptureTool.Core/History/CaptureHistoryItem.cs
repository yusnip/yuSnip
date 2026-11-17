using System;

namespace ScreenCaptureTool.Core.History
{
    /// <summary>
    /// 截图历史的来源类型。决定该条记录是否落入当前收集档位，也用于历史窗口的来源筛选。
    /// </summary>
    public enum CaptureHistorySource
    {
        /// <summary>截图保存为文件。</summary>
        Save = 0,
        /// <summary>截图复制到剪贴板。</summary>
        Clipboard = 1,
        /// <summary>截图转为贴图（含剪贴板快速贴图与脚本贴图）。</summary>
        Sticker = 2,
        /// <summary>长截图产出。</summary>
        LongScroll = 3,
    }

    /// <summary>
    /// 一条截图历史记录（纯数据快照）。
    ///
    /// 刻意不持有任何位图或画刷：位图按需从 <see cref="CaptureHistoryStore.ResolveExistingImagePath"/> /
    /// <see cref="CaptureHistoryStore.ResolveExistingThumbPath"/> 懒加载，配色一律由 XAML 样式按状态类渲染
    /// （设计规范 §2「C# 无画刷原则」）。
    /// </summary>
    public sealed class CaptureHistoryItem
    {
        /// <summary>记录唯一标识，同时是图片文件名（不含扩展名）。格式 <c>yyyyMMdd_HHmmss_fff_xxx</c>，按名升序即时间升序。</summary>
        public string Id { get; set; } = "";

        /// <summary>产出时间（本地时间）。</summary>
        public DateTime CapturedAt { get; set; }

        /// <summary>图片像素宽。</summary>
        public int Width { get; set; }

        /// <summary>图片像素高。</summary>
        public int Height { get; set; }

        /// <summary>来源类型。</summary>
        public CaptureHistorySource Source { get; set; }

        /// <summary>
        /// 原始保存路径。仅 <see cref="CaptureHistorySource.Save"/> 与
        /// <see cref="CaptureHistorySource.LongScroll"/> 两档非空，供历史窗口「打开文件位置」使用；
        /// 用户可能已把原文件删除或移动，使用前必须判存在。
        /// </summary>
        public string SavedPath { get; set; } = "";

        /// <summary>历史目录内该条全图 PNG 的字节数（容量裁剪的计量依据，不含缩略图）。</summary>
        public long FileBytes { get; set; }

        /// <summary>
        /// 可搜文本（索引第 9 列，读写两侧都按上限截断）。文本 / Markdown / 颜色贴图存**源文本**，
        /// 图片贴图存 **OCR 识别文字**；截图与长截图本轮不做识别，故恒为空串。
        /// ⚠ 空串与"这条从没被识别过"不可区分——阶段④的回填扫描正是拿这个判据挑待办条目。
        /// 这里是**原样**（多行、全角都保留，详情面板要按它展示），检索一律走 <see cref="SearchText"/>。
        /// </summary>
        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                _searchText = null;
            }
        }

        private string _text = "";
        private string? _searchText;

        /// <summary>
        /// 归一后的检索副本（全角折半角、换行与连续空白压成一个空格），与查询串共用
        /// <see cref="HistoryQuery.NormalizeForSearch"/>——两侧不同一份实现就是 issue #9 的成因。
        /// 每条只算一次，<see cref="Text"/> 变了才重算（老库不需要迁移，读进来即用）。
        /// </summary>
        public string SearchText => _searchText ??= HistoryQuery.NormalizeForSearch(_text);

        /// <summary>来源类型的中文显示名（历史窗口徽标用）。</summary>
        public string SourceDisplayName
        {
            get
            {
                switch (Source)
                {
                    case CaptureHistorySource.Clipboard: return "截图";
                    case CaptureHistorySource.Sticker: return "贴图";
                    case CaptureHistorySource.LongScroll: return "长截图";
                    default: return "保存";
                }
            }
        }
    }
}
