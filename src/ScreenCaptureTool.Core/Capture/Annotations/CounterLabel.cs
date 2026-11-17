using System;
using System.Text;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 序号数值 → 显示文本。截图预览（Avalonia）、成品导出（GDI+）、存档三侧都必须走这一份，
/// 否则同一张图上的编号会在"看到的"与"存下来的"之间对不上。
/// </summary>
public static class CounterLabel
{
    static readonly (int Value, string Numeral)[] RomanTable =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    /// <summary>罗马数字用经典表能写出的最大值，再往上要借用上加线等记法，故到此为止。</summary>
    public const int RomanMax = 3999;

    /// <summary>汉字数字用「十百千」能写出的最大值（与起始值上限同档），再往上要引入「万」及以上的记法。</summary>
    public const int ChineseMax = 9999;

    public static string Format(int number, CounterSerialKind kind)
    {
        int n = Math.Max(1, number);
        return kind switch
        {
            CounterSerialKind.Letter => ToLetters(n),
            CounterSerialKind.Roman => n <= RomanMax ? ToRoman(n) : n.ToString(),
            CounterSerialKind.Chinese => n <= ChineseMax ? ToChinese(n) : n.ToString(),
            _ => n.ToString(),
        };
    }

    /// <summary>Excel 列名式进位：1→A … 26→Z，27→AB 的前一个即 AA，不复用单字母。</summary>
    static string ToLetters(int n)
    {
        var sb = new StringBuilder(4);
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    static string ToRoman(int n)
    {
        var sb = new StringBuilder(16);
        foreach ((int value, string numeral) in RomanTable)
        {
            while (n >= value)
            {
                sb.Append(numeral);
                n -= value;
            }
        }
        return sb.ToString();
    }

    static readonly string ChineseDigits = "一二三四五六七八九";

    /// <summary>千 / 百 / 十 三档（个位单独处理：它没有单位字，且是"零"判据的分界）。</summary>
    static readonly (int Place, char Unit)[] ChinesePlaces = { (1000, '千'), (100, '百'), (10, '十') };

    /// <summary>
    /// 简体中文读法：一…九、十、十一…十九、二十、二十一…、一百、一百零五、一千零五十。
    /// 两条最容易写错的口径照日常读法：① 10–19 写作「十X」而非「一十X」（但百位以上不省，110 是一百一十）；
    /// ② 中间的零只写一次、末尾的零不写（105→一百零五，150→一百五十，1005→一千零五）。
    /// </summary>
    static string ToChinese(int n)
    {
        if (n < 10) return ChineseDigits[n - 1].ToString();

        var sb = new StringBuilder(8);
        bool zeroPending = false;
        foreach ((int place, char unit) in ChinesePlaces)
        {
            int d = n / place;
            n %= place;
            if (d == 0)
            {
                // 高位已经写出来过才算"缺一档"，还没开写时的前导零不补
                if (sb.Length > 0) zeroPending = true;
                continue;
            }
            if (zeroPending) sb.Append('零');
            if (!(d == 1 && place == 10 && sb.Length == 0)) sb.Append(ChineseDigits[d - 1]);
            sb.Append(unit);
            zeroPending = false;
        }
        if (n > 0)
        {
            if (zeroPending) sb.Append('零');
            sb.Append(ChineseDigits[n - 1]);
        }
        return sb.ToString();
    }
}
