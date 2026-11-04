using System;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace ScreenCaptureTool.Services
{
    public static class ColorHelper
    {
        private static readonly Regex RgbRegex = new Regex(@"^rgb\s*\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RgbaRegex = new Regex(@"^rgba\s*\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*([\d.]+)\s*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HexRegex = new Regex(@"^#?([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

        public static bool TryParseColor(string text, out Color color, out string originalFormat)
        {
            color = Colors.Transparent;
            originalFormat = string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;

            text = text.Trim();
            
            // Handle rgb(r, g, b)
            var rgbMatch = RgbRegex.Match(text);
            if (rgbMatch.Success)
            {
                if (byte.TryParse(rgbMatch.Groups[1].Value, out byte r) &&
                    byte.TryParse(rgbMatch.Groups[2].Value, out byte g) &&
                    byte.TryParse(rgbMatch.Groups[3].Value, out byte b))
                {
                    color = Color.FromRgb(r, g, b);
                    originalFormat = text;
                    return true;
                }
            }

            // Handle rgba(r, g, b, a)
            var rgbaMatch = RgbaRegex.Match(text);
            if (rgbaMatch.Success)
            {
                if (byte.TryParse(rgbaMatch.Groups[1].Value, out byte r) &&
                    byte.TryParse(rgbaMatch.Groups[2].Value, out byte g) &&
                    byte.TryParse(rgbaMatch.Groups[3].Value, out byte b) &&
                    double.TryParse(rgbaMatch.Groups[4].Value, out double a))
                {
                    color = Color.FromArgb((byte)(a * 255), r, g, b);
                    originalFormat = text;
                    return true;
                }
            }

            // Handle HEX without # like 9050E0
            if (!text.StartsWith("#") && HexRegex.IsMatch("#" + text) && !text.Contains(",") && !text.Contains(" "))
            {
                if (Color.TryParse("#" + text, out var hexColor))
                {
                    color = hexColor;
                    originalFormat = text;
                    return true;
                }
            }

            // Handle standard names and #HEX
            if (Color.TryParse(text, out var avaloniaColor))
            {
                // We only want to convert actual color formats or known color names, to avoid converting random words into colors.
                // Avalonia's TryParse supports a lot of formats. Let's ensure it's either a hex string or a known named color.
                if (text.StartsWith("#") || IsNamedColor(text))
                {
                    color = avaloniaColor;
                    originalFormat = text;
                    return true;
                }
            }

            return false;
        }

        private static bool IsNamedColor(string text)
        {
            try
            {
                return typeof(Colors).GetProperty(text, System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null;
            }
            catch
            {
                return false;
            }
        }

        public static string FormatColorInfo(Color color, string originalFormat)
        {
            ColorToHsv(color, out double h, out double s, out double v);
            ColorToHsl(color, out double hslH, out double hslS, out double hslL);

            string rgbStr = $"RGB: {color.R,3}, {color.G,3}, {color.B,3}";
            string hsvStr = $"HSV: {Math.Round(h),3}, {Math.Round(s * 100),3}, {Math.Round(v * 100),3}";
            string hslStr = $"HSL: {Math.Round(hslH),3}, {Math.Round(hslS * 100),3}, {Math.Round(hslL * 100),3}";
            
            string hexValue = $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            // If alpha is FF, strip it for a 6-char hex
            if (hexValue.Length == 8 && hexValue.StartsWith("FF"))
            {
                hexValue = hexValue.Substring(2);
            }
            string hexStr = $"HEX: {hexValue,9}"; // align with others

            string header = string.IsNullOrEmpty(originalFormat) ? $"#{hexValue}" : originalFormat;
            
            // Format to look like the image
            return $"  {header}\n{rgbStr}\n{hsvStr}\n{hslStr}\n{hexStr}";
        }

        private static void ColorToHsv(Color color, out double h, out double s, out double v)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));

            v = max;
            double delta = max - min;

            if (max == 0)
            {
                s = 0;
                h = 0;
            }
            else
            {
                s = delta / max;

                if (delta == 0) h = 0;
                else if (r == max) h = (g - b) / delta;
                else if (g == max) h = 2 + (b - r) / delta;
                else h = 4 + (r - g) / delta;

                h *= 60;
                if (h < 0) h += 360;
            }
        }

        private static void ColorToHsl(Color color, out double h, out double s, out double l)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));

            l = (max + min) / 2.0;

            if (max == min)
            {
                h = 0;
                s = 0;
            }
            else
            {
                double delta = max - min;
                s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

                if (r == max) h = (g - b) / delta + (g < b ? 6 : 0);
                else if (g == max) h = (b - r) / delta + 2;
                else h = (r - g) / delta + 4;

                h /= 6.0;
            }
            h *= 360;
        }
    }
}
