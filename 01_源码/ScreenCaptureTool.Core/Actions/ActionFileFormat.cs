using System.Globalization;

namespace ScreenCaptureTool.Core.Actions;

/// <summary>动作文件的存储与同步共用格式契约，升级版本时同时更新列数。</summary>
internal static class ActionFileFormat
{
    public const int CurrentVersion = 6;
    public const string HeaderPrefix = "# yuSnip actions v";
    public static string Header => HeaderPrefix + CurrentVersion.ToString(CultureInfo.InvariantCulture);
    public static int ColumnCount(int version) => version switch
    {
        1 or 2 => 8,
        3 => 10,
        4 => 11,
        5 => 17,
        6 => 19,
        _ => 0
    };
}
