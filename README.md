# yuSnip 第一阶段源码 · MIT 开源

Windows 截图与标注工具的历史源码，欢迎学习、修改、再分发和商业使用。

> **源码与当前软件版本的关系**
>
> 本仓库公开的是 yuSnip 第一阶段源码，基于 .NET 8 / C# 12 / Avalonia UI 11.2.8，不对应当前第二阶段发行版。
>
> 第二阶段经过大量重构与功能扩展，目前以闭源形式独立维护。未来是否开源尚未确定，本说明不构成开源时间承诺。
>
> **本仓库 Issues 默认接收当前第二阶段闭源软件的 Bug 反馈与功能建议。** 反馈问题不需要源码公开；请填写当前软件版本。第一阶段源码的构建或学习问题，请在标题和模板中注明「第一阶段源码」。

## 开源许可

本项目自有代码使用 [MIT 许可证](LICENSE)：允许学习、使用、修改、分发及商业使用，也允许在遵守许可条件的前提下用于闭源产品；再分发时须保留版权及许可声明。软件按现状提供，无担保。

第三方依赖、OCR 引擎、模型和字典遵循各自许可证，详见 [第三方声明](THIRD_PARTY_NOTICES.md)。项目 MIT 许可不替代第三方许可，也不将工单附件重新授权。衍生版本请明确其维护者和来源，避免使用户误认为官方发行版。

## 第一阶段功能

- 区域截图与窗口／元素检测。
- 画笔、矩形、椭圆、箭头、文字、马赛克、聚光灯及序号标注。
- 长截图与 OpenCV 图像拼接。
- 本地离线 OCR，通过 RapidOCR-json 进程识别。
- 截图贴图、缩放、透明度及鼠标穿透。

以上描述仅对应本仓库源码；当前第二阶段软件的功能和界面可能不同。

## 源码结构

| 路径 | 内容 |
| --- | --- |
| `01_源代码/ScreenCaptureTool` | Avalonia 主程序、窗口与控件 |
| `01_源代码/ScreenCaptureTool.Core` | 截图、标注、配置与 OCR 进程适配 |
| `01_源代码/ScreenCaptureTool.LongScroll` | 长截图采集、拼接与预览 |
| `01_源代码/ScreenCaptureTool.Ocr` | OCR 接口与结果模型 |
| `01_源代码/ScreenCaptureTool.Platform` | Windows 平台互操作 |
| `RapidOCR文字识别引擎` | 经核对来源的 OCR 引擎、模型与字典 |

## 本地构建

需要 Windows 10（19041+）／Windows 11 x64 与 .NET 8 SDK；`global.json` 指定 8.0.421，并允许同系列兼容 SDK。源码、依赖与通用本地构建说明均公开，不需要作者的服务器、账号或密钥。

在仓库根目录运行：

```powershell
cd 01_源代码
dotnet build ScreenCaptureTool/ScreenCaptureTool.csproj -c Debug -p:Platform=x64 --nologo
dotnet run --project ScreenCaptureTool/ScreenCaptureTool.csproj -c Debug -p:Platform=x64 --no-build
```

NativeAOT 发布要求 Visual Studio 2022 的「使用 C++ 的桌面开发」工作负载与 Windows SDK，具体命令及分发边界见 [构建说明](docs/build-and-release.md)。NativeAOT 是原生编译，不是加密，也不保证无法逆向。

## 当前软件反馈与社区

- [当前第二阶段软件的 Bug 与功能建议](https://github.com/yusnip/yuSnip/issues)：填写版本、复现步骤和经过脱敏的截图。
- [讨论区](https://github.com/yusnip/yuSnip/discussions)：使用交流；第一阶段源码问题请明确注明。
- [官方网站](https://yusnip.top)：当前软件信息与下载入口。

第一阶段源码作为历史学习版本保留，不承诺同步第二阶段功能或持续回移修复。目前暂不接受外部代码合并；Issues 与 Discussions 继续用于反馈和交流。安全问题请遵循 [安全反馈说明](SECURITY.md)，不要公开贴出凭据、原始日志或私人聊天。

更新记录见 [CHANGELOG](docs/CHANGELOG.md)。
