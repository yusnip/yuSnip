namespace ScreenCaptureTool.Core.Matting;

/// <summary>
/// 智能抠图模型类型
/// </summary>
public enum MattingModelType
{
    /// <summary>
    /// MODNet 人像精细抠图（发丝级连续 Alpha，适合人物、自拍、人像肖像）
    /// </summary>
    Portrait,

    /// <summary>
    /// U2-NetP 通用主体显著抠图（适合宠物、静物、商品、图元、Logo）
    /// </summary>
    General
}

/// <summary>
/// 智能抠图结果的去向（设置项「抠图结果」，对截图工具栏/快捷键、贴图右键菜单与脚本指令的所有抠图入口生效）
/// </summary>
public enum MattingResultDestination
{
    /// <summary>
    /// 仅写入剪贴板：不改动贴图内容；截图工具栏/脚本流程临时创建的贴图在抠图完成后自动关闭
    /// </summary>
    Clipboard,

    /// <summary>
    /// 仅生成贴图：贴图显示抠图结果，不写入剪贴板
    /// </summary>
    Sticker,

    /// <summary>
    /// 写入剪贴板并生成贴图（默认，即历史行为）
    /// </summary>
    ClipboardAndSticker
}
