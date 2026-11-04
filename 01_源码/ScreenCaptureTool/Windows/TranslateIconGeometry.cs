using Avalonia.Media;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 翻译两个窗口（极简浮窗 / 编辑主窗口）共用的单色 SVG 图标几何。
/// 全部为 Material 轮廓风格，颜色一律由样式类绑定 Sct* 主题资源，禁止在代码中写死色值。
/// 与 OCR 等窗口图形一致的图标一律别名为 <see cref="OcrIconGeometry"/>（共享图标库），禁止再抄一份路径数据。
/// </summary>
public static class TranslateIconGeometry
{
    /// <summary>译：语言/翻译</summary>
    public const string Translate = "M12.87 15.07l-2.54-2.51.03-.03c1.74-1.94 2.98-4.17 3.71-6.53H17V4h-7V2H8v2H1v1.99h11.17C11.5 7.92 10.44 9.75 9 11.35 8.07 10.32 7.3 9.19 6.69 8h-2c.73 1.63 1.73 3.17 2.98 4.56l-5.09 5.02L4 19l5-5 3.11 3.11.76-2.04zM18.5 10h-2L12 22h2l1.12-3h4.75L21 22h2l-4.5-12zm-2.62 7l1.62-4.33L19.12 17h-3.24z";

    /// <summary>交换：互换源语言与目标语言</summary>
    public const string Swap = "M6.99 11L3 15l3.99 4v-3H14v-2H6.99v-3zM21 9l-3.99-4v3H10v2h7.01v3L21 9z";

    /// <summary>复制</summary>
    public const string Copy = OcrIconGeometry.Copy;

    /// <summary>对勾：操作成功反馈</summary>
    public const string Check = OcrIconGeometry.Proof;

    /// <summary>关闭</summary>
    public const string Close = OcrIconGeometry.Close;

    /// <summary>在新窗口打开：从浮窗升级到编辑主窗口</summary>
    public const string OpenInNew = OcrIconGeometry.OpenInNew;

    /// <summary>星火/模型：AI 引擎徽标</summary>
    public const string Spark = OcrIconGeometry.Spark;

    /// <summary>朗读语音</summary>
    public const string Voice = "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z";

    /// <summary>扫帚 / 清空</summary>
    public const string Broom = "M19.36 2.72L20.78 4.14 15.06 9.85 16.48 11.28 22.2 5.56 23.61 6.98 16.83 13.76 14.41 16.18 8.7 10.46 11.12 8.05 17.9 14.83 16.49 16.24 10.78 10.53 9.36 11.94 15.08 17.66 13.66 19.07 7.95 13.36 6.53 14.78 12.25 20.5 10.83 21.91 5.12 16.19 3.71 17.61z";

    /// <summary>警告：错误态</summary>
    public const string Warn = "M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z";

    /// <summary>重试 / 重新翻译</summary>
    public const string Refresh = OcrIconGeometry.Refresh;

    /// <summary>文档：空态占位</summary>
    public const string Document = "M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z";

    /// <summary>箭头：分割线中央收展原文（四向同族，中心均在 24 视框的 12 上）</summary>
    public const string ChevronLeft = "M15.41 16.59L10.83 12l4.58-4.59L14 6l-6 6 6 6 1.41-1.41z";

    /// <summary>箭头右</summary>
    public const string ChevronRight = "M8.59 16.59L13.17 12 8.59 7.41 10 6l6 6-6 6-1.41-1.41z";

    /// <summary>箭头上</summary>
    public const string ChevronUp = "M7.41 15.41L12 10.83l4.59 4.58L18 14l-6-6-6 6 1.41 1.41z";

    /// <summary>箭头下</summary>
    public const string ChevronDown = "M7.41 8.59L12 13.17l4.59-4.58L18 10l-6 6-6-6 1.41-1.41z";

    /// <summary>最大化 / 还原</summary>
    public const string Maximize = "M3 3h18v18H3V3zm2 2v14h14V5H5z";

    /// <summary>按几何字符串创建单色图标（颜色由样式类承担）。</summary>
    public static Avalonia.Controls.Shapes.Path CreateIcon(string geometry, string styleClass)
    {
        return new Avalonia.Controls.Shapes.Path
        {
            Classes = { styleClass },
            Data = StreamGeometry.Parse(geometry)
        };
    }
}
