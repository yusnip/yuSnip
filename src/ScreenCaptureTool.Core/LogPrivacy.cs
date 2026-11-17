using System.Text.RegularExpressions;

namespace ScreenCaptureTool.Core;

/// <summary>所有日志通道共用的脱敏边界；过滤失败时仅留下占位，不降级输出原文。</summary>
public static class LogPrivacy
{
    private const string Hidden = "[已脱敏]";
    private static Regex Rule(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Content = Rule(@"(User prompt:|AI analysis:|Generated search query:|User submitted prompt from input:|全文\s*[:：]|REC 解码结果\s*[:：]|竖向文本行\s*[:：])[^\r\n]*(?:\r?\n(?!\d{4}-\d{2}-\d{2}[^\r\n]*\[(?:INFO|WARN|ERROR|DEBUG)\])[^\r\n]*)*");
    private static readonly Regex Credentials = Rule("""((?:\\?["'])?(?:api[\s_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|password|passwd|secret|client[_-]?secret|authorization|cookie|set-cookie|密钥|密码|凭证)(?:\\?["'])?\s*[:=]\s*)(?:\\?"[^"\r\n]*"|'[^'\r\n]*'|[^\r\n,;}&]+)""");
    private static readonly Regex Cookies = Rule(@"\b(?:cookie|set-cookie)\s*[:=][^\r\n]+");
    private static readonly Regex Bearer = Rule(@"\b(?:Bearer|Basic)\s+[A-Za-z0-9_+/.=\-]+");
    private static readonly Regex Keys = Rule(@"\b(?:sk-[A-Za-z0-9_\-]{8,}|eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+)\b");
    private static readonly Regex Urls = Rule(@"https?://[^\s\""'<>]+");
    private static readonly Regex Paths = Rule(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n\""'<>|]*|/(?:Users|home|tmp)/[^\r\n\""'<>|]*");
    private static readonly Regex Email = Rule(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b");

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        try
        {
            string text = Content.Replace(value, m => m.Groups[1].Value + Hidden);
            text = Cookies.Replace(text, "Cookie: " + Hidden);
            text = Bearer.Replace(text, "凭证 " + Hidden);
            text = Credentials.Replace(text, m => m.Groups[1].Value + Hidden);
            text = Keys.Replace(text, Hidden);
            text = Urls.Replace(text, "[网络地址已脱敏]");
            text = Paths.Replace(text, "[本地路径已脱敏]");
            return Email.Replace(text, "[账号已脱敏]");
        }
        catch { return "[日志已脱敏：过滤未完成]"; }
    }

    // 异常消息可回显任意用户输入；只保留异常类型、错误码与调用栈，不记录 Message/Data。
    public static string DescribeException(Exception exception)
    {
        string result = exception.GetType().FullName + " HResult=0x" + exception.HResult.ToString("X8")
            + Environment.NewLine + exception.StackTrace;
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) result += Environment.NewLine + DescribeException(inner);
        else if (exception.InnerException != null) result += Environment.NewLine + DescribeException(exception.InnerException);
        return Redact(result);
    }
}
