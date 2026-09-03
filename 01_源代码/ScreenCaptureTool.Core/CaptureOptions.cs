#nullable disable

using System;
using System.Collections.Generic;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 一次截图运行的配置快照。由 <see cref="AppSettings.ToCaptureOptions"/> 产出。
    /// 阶段 2 搬迁自旧项目，按 D009 改为 <c>public</c>。
    /// </summary>
    public sealed class CaptureOptions
    {
        public string SaveDirectory { get; set; }
        public string FileNameTemplate { get; set; }
        public string ImageFormat { get; set; }
        public int JpegQuality { get; set; }
        public int DelaySeconds { get; set; }
        public string AutoDetectMode { get; set; }
        public bool CopyToClipboardAfterSave { get; set; }
        public bool ShowSaveNotification { get; set; }
        public bool PlaySaveSound { get; set; }
        public bool DarkMode { get; set; }
        public uint HotKeyModifiers { get; set; }
        public uint HotKeyVirtualKey { get; set; }

        /// <summary>
        /// 旧版本中 <c>ScreenCapture</c> 引擎依赖一个键值字典作为入口参数。
        /// 这里保留兼容映射，阶段 4 重构截图引擎时再决定是否退役。
        /// </summary>
        public Dictionary<string, object> ToLegacyVars()
        {
            Dictionary<string, object> vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            vars["文件路径"] = SaveDirectory ?? "";
            vars["文件名"] = FileNameTemplate ?? "";
            vars["延时时间"] = Math.Max(0, DelaySeconds).ToString();
            vars["UI检测"] = string.IsNullOrEmpty(AutoDetectMode) ? "仅窗口" : AutoDetectMode;
            vars["截图区域"] = "";
            vars["深色模式"] = DarkMode.ToString();
            vars["动作扩展配置"] = "False";
            vars["mechanism"] = "";
            vars["plugin_menu"] = "";
            vars["StickerCloseAction"] = "";
            vars["CloseProxyAction"] = "";
            vars["图片格式"] = "PNG"; // 截图统一为 PNG，保留元数据
            vars["JPEG质量"] = JpegQuality.ToString();
            vars["复制到剪贴板"] = CopyToClipboardAfterSave.ToString();
            vars["显示保存通知"] = ShowSaveNotification.ToString();
            vars["播放保存音效"] = PlaySaveSound.ToString();
            return vars;
        }
    }
}
