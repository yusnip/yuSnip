using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// AI 提示输入框的粘贴通路。框架的 Ctrl+V 在「AI 识图提问弹窗」里不生效（同应用 OCR 窗的输入框
/// 走框架那条却正常，差别只在这个窗自身），而 <c>TextBox.Paste()</c> 在读不到内容时是
/// <b>静默 return</b>（源码里 <c>catch (TimeoutException) {}</c> 与 <c>if (string.IsNullOrEmpty(text)) return;</c>
/// 两条出口都不留痕迹），用户看到的就只是"按了没反应"。这里显式接管按键，并把"读不到"说清楚。
/// </summary>
internal static class AiPromptPaste
{
    /// <summary>
    /// 粘贴按键：Ctrl+V，或 Shift+Insert。判定只在这里写一份，两个 AI 输入框共用。
    /// </summary>
    public static bool IsPasteGesture(KeyEventArgs e) =>
        (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        || (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift));

    /// <summary>
    /// 带反馈的粘贴：先读一次剪贴板只为判定成败（框架在这种情况下什么都不提示），确有内容再交回
    /// <see cref="TextBox.Paste"/> 执行——它负责撤销快照与 Pasting 事件语义，本类不自己改文本、
    /// 不复制一份插入逻辑。
    /// </summary>
    public static async Task PasteWithFeedbackAsync(TextBox box)
    {
        if (box == null) return;

        string? text = null;
        try
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(box)?.Clipboard;
            if (clipboard != null) text = await clipboard.GetTextAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AiPromptPaste: 读取剪贴板异常：" + ex.Message);
        }

        if (string.IsNullOrEmpty(text))
        {
            AppLogger.Warn("AiPromptPaste: 剪贴板中没有纯文本（或读取失败），粘贴无内容");
            StickerToastWindow.ShowAtTop("剪贴板里没有可粘贴的文字");
            return;
        }

        box.Paste();
    }
}
