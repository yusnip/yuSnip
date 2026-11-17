using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>随程序内嵌的第三方声明；只读取本地资源，不请求网络或用户配置，兼容 NativeAOT。</summary>
public static class ThirdPartyNotices
{
    public sealed record Component(string Name, string Version, string Purpose, string License, string Url, params string[] Files);

    public static IReadOnlyList<Component> Components { get; } = new Component[]
    {
        new("Avalonia UI", "11.2.8", "桌面界面、Fluent 主题、表格与取色控件。包含同版本的 Avalonia 运行组件。", "MIT", "https://github.com/AvaloniaUI/Avalonia", "Avalonia"),
        new("SkiaSharp / Skia", "2.88.9", "界面与图像绘制；附带底层 Skia 及相关第三方声明。", "MIT / BSD 等", "https://github.com/mono/SkiaSharp", "SkiaSharp"),
        new("HarfBuzzSharp / HarfBuzz", "7.3.0.3", "字体整形与文字排版；附带底层 HarfBuzz 声明。", "MIT 等", "https://github.com/mono/SkiaSharp", "HarfBuzzSharp"),
        new("ANGLE", "2.1.22045.20230930", "Windows 图形渲染支持。", "BSD 等（见正文）", "https://github.com/google/angle", "ANGLE"),
        new("MicroCom.Runtime", "0.11.0", "Avalonia 使用的 COM 互操作支持。", "MIT", "https://github.com/kekekeks/MicroCom", "MicroCom"),
        new(".NET 与基础库", ".NET 10 / NuGet 8.x、9.x", ".NET 运行时；System.Drawing.Common 8.0.27、ProtectedData 8.0.0、SystemEvents 8.0.0、Numerics.Tensors 9.0.0。", "MIT 及附带声明", "https://github.com/dotnet/runtime", "DotNet"),
        new("OpenCvSharp / OpenCV", "4.10.0.20241108 / 4.10.0", "图像处理与长截图拼接。OpenCvSharp 的 Windows 原生绑定随程序分发。", "Apache-2.0 及附带声明", "https://github.com/shimat/opencvsharp", "OpenCvSharp", "OpenCV"),
        new("ONNX Runtime", "1.22.1", "OCR 与智能抠图的本地模型推理。", "MIT 及附带声明", "https://github.com/microsoft/onnxruntime", "ONNXRuntime"),
        new("Inter 字体", "随 Avalonia.Fonts.Inter 11.2.8", "Avalonia 字体资源。字体本身与加载组件分别适用各自许可。", "SIL OFL-1.1", "https://github.com/rsms/inter", "Inter"),
        new("PaddleOCR / PP-OCRv6", "small det / rec", "文字检测、识别模型与字典，按需下载。ONNX 模型用于本地推理。", "Apache-2.0", "https://github.com/PaddlePaddle/PaddleOCR", "PaddleOCR"),
        new("MODNet", "人像抠图模型", "人像精细抠图，按需下载，使用 ONNX 格式模型。", "Apache-2.0", "https://github.com/ZHKKKe/MODNet", "MODNet"),
        new("U²-NetP", "通用抠图模型", "通用主体抠图，按需下载，使用 ONNX 格式模型。", "Apache-2.0", "https://github.com/xuebinqin/U-2-Net", "U2Net"),
        new("FFmpeg", "9.0.2 · gyan.dev essentials", "录屏与视频处理，作为独立进程按需下载。此处列出当前组件包构建；手动指定的其他构建适用其自身声明。", "GPL-3.0-or-later", "https://www.gyan.dev/ffmpeg/builds/", "FFmpeg", "GPL-3.0"),
        new("AreaCity 地区数据", "2025.251231.260403", "天气区县目录与代表坐标。对上游三级目录作筛选、父节点补齐、拼音索引与近似坐标转换。", "MIT", "https://github.com/xiangyuecn/AreaCity-JsSpider-StatsGov", "AreaCity"),
        new("Open-Meteo 天气数据", "在线服务", "概览天气。天气码转换为中文与视觉场景，数据用于区县代表点的模型天气展示。数据许可与 API 服务条款分别适用。", "CC BY 4.0 / 服务条款", "https://open-meteo.com/en/terms", "OpenMeteo", "CC-BY-4.0"),
        new("Tmds.DBus.Protocol", "0.20.0", "Avalonia Desktop 的跨平台传递依赖；Windows 主功能不使用 D-Bus。", "MIT", "https://github.com/tmds/Tmds.DBus", "TmdsDBus"),
        new("Microsoft VC++ 运行库", "随原生依赖分发", "原生图形与推理组件所需的 Microsoft 运行库，适用 Microsoft 再分发条款。", "Microsoft 专有许可", "https://visualstudio.microsoft.com/license-terms/", "VCRuntime"),
        new("Material Design Icons", "部分图标路径", "OCR、翻译及共用操作图标使用或基于 Google Material 图标路径调整；颜色与承载样式由 yuSnip 定义。", "Apache-2.0", "https://github.com/google/material-design-icons", "MaterialIcons"),
    };

    public static string ReadNotice(Component component)
    {
        var text = new StringBuilder();
        text.AppendLine(component.Name + " · " + component.Version);
        text.AppendLine("许可：" + component.License);
        text.AppendLine("来源：" + component.Url);
        text.AppendLine(component.Purpose);
        foreach (string file in component.Files)
        {
            text.AppendLine().AppendLine("──────── " + file + " ────────").AppendLine();
            using var stream = AssetLoader.Open(new Uri("avares://yuSnip/Assets/ThirdParty/" + file + ".txt"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd());
        }
        return text.ToString();
    }
}
