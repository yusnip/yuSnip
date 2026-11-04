#nullable disable

using System;
using System.Text.RegularExpressions;

namespace ScreenCaptureTool.Core
{
    public static class HtmlToMarkdownConverter
    {
        public static string Convert(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;

            // Extract the actual HTML fragment from the Windows Clipboard format
            string content = html;
            int startFragment = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            int endFragment = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            
            if (startFragment >= 0 && endFragment > startFragment)
            {
                content = html.Substring(
                    startFragment + "<!--StartFragment-->".Length, 
                    endFragment - startFragment - "<!--StartFragment-->".Length);
            }

            // Remove <head>, <style>, <script> entirely to avoid rendering their text content
            content = Regex.Replace(content, @"<head[^>]*>[\s\S]*?</head>", "", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);

            // Replace headings
            content = Regex.Replace(content, @"<h1[^>]*>(.*?)</h1>", "\n# $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<h2[^>]*>(.*?)</h2>", "\n## $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<h3[^>]*>(.*?)</h3>", "\n### $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<h4[^>]*>(.*?)</h4>", "\n#### $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<h5[^>]*>(.*?)</h5>", "\n##### $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<h6[^>]*>(.*?)</h6>", "\n###### $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Replace bold and strong
            content = Regex.Replace(content, @"<b\b[^>]*>(.*?)</b>", "**$1**", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<strong\b[^>]*>(.*?)</strong>", "**$1**", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Replace italic and em
            content = Regex.Replace(content, @"<i\b[^>]*>(.*?)</i>", "*$1*", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<em\b[^>]*>(.*?)</em>", "*$1*", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Replace code
            content = Regex.Replace(content, @"<code\b[^>]*>(.*?)</code>", "`$1`", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Replace blockquote
            content = Regex.Replace(content, @"<blockquote[^>]*>(.*?)</blockquote>", "\n> $1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Replace lists
            content = Regex.Replace(content, @"<li[^>]*>(.*?)</li>", "\n- $1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Replace horizontal rules
            content = Regex.Replace(content, @"<hr[^>]*>", "\n---\n", RegexOptions.IgnoreCase);

            // Replace breaks and paragraphs
            content = Regex.Replace(content, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            content = Regex.Replace(content, @"<p[^>]*>(.*?)</p>", "\n$1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            content = Regex.Replace(content, @"<div[^>]*>(.*?)</div>", "\n$1\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Remove all remaining HTML tags
            content = Regex.Replace(content, @"<[^>]+>", string.Empty);

            // Decode HTML entities (important to do before restoring newlines)
            content = System.Net.WebUtility.HtmlDecode(content);

            // Clean up excessive newlines
            content = Regex.Replace(content, @"\n{3,}", "\n\n");
            
            return content.Trim();
        }
    }
}
