# yuSnip (屏幕截图与标注工具)

这是一个开源、现代化、高性能的 Windows 屏幕截图与标注工具。项目基于 .NET 8 与 Avalonia UI 框架构建，支持长截图（基于 OpenCV 图像拼接）与本地离线 OCR 文字识别（基于 RapidOCR）。

---

## 📖 项目概述

* **软件名称**：yuSnip (内部项目名 `ScreenCaptureTool`)
* **开源属性**：本项目为一个完全开源的桌面应用项目。
* **目标平台**：Windows 10 (Build 19041 及以上) / Windows 11 (仅支持 x64 架构)

---

## 🛠️ 开发环境要求

* **开发 SDK**：[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* **开发语言**：C# 12
* **推荐 IDE**：Visual Studio 2022 (或 JetBrains Rider)

---

## 🏗️ 软件架构设计

项目采用清晰的多项目分层架构，各模块职责分明，易于维护与扩展：

1. **`ScreenCaptureTool` (主启动程序项目)**
   - 负责主窗口生命周期管理、托盘图标、全局系统热键注册。
   - 存放 Avalonia UI 视图界面（如截图画布、设置面板、贴图窗口等）以及 XAML 主题样式。
   - 包含单文件发布时的原生 DLL 内嵌打包与动态加载机制（`EmbeddedNativeLoader`）。
2. **`ScreenCaptureTool.Core` (核心业务逻辑层)**
   - 负责全局配置读写、快捷键响应、系统日志归集、进程防多开及管理等。
   - 封装了本地 OCR 引擎的进程拉起与通信管道逻辑。
3. **`ScreenCaptureTool.LongScroll` (长截图模块)**
   - 负责滚动截屏的多帧图像捕捉与拼接算法。
   - 使用 OpenCV 图像算法库进行多帧图像间的特征匹配与缝合，保证长图拼接的无缝与准确度。
4. **`ScreenCaptureTool.Ocr` (文字识别适配层)**
   - 提供文字识别的通用接口规范与数据模型定义。
5. **`ScreenCaptureTool.Platform` (系统平台互操作层)**
   - 封装 Win32 API 底层交互，提供键盘/鼠标全局钩子（`Windows Hook`）、MSAA/COM 接口调用以获取窗口边框等 Windows 系统底层操作。

---

## 📦 依赖的第三方开源库与引擎

1. **[Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (v11.2.8)**：现代高性能 XAML UI 框架，负责整个软件窗口的跨平台渲染及高级 UI 控件。
2. **[OpenCvSharp4](https://github.com/shimat/opencvsharp) (v4.10.0)**：OpenCV 的 C# 封装版本，广泛应用于滚动长截图的图像配准和无缝拼接算法中。
3. **[RapidOCR-json](https://github.com/RapidOCR/RapidOCR-json)**：本地离线高精度 OCR 引擎。基于 PaddleOCR 深度学习框架，通过进程间轻量级 JSON 通信，无需联网和上传数据即可在本地实现毫秒级的中英文文字识别。
4. **[System.Drawing.Common](https://github.com/dotnet/winforms)**：提供底层的 GDI+ 图像绘图支持。

---

## 🌟 主要功能特性

* **智能截图与实时标注**：自动捕捉窗口边缘；提供画笔、矩形、椭圆、箭头、荧光笔、文字标注、模糊/马赛克、序号步骤等丰富标注工具；支持放大镜和像素级 RGB 取色器。
* **滚动长截图**：向下滚动页面即可自动将多屏内容拼接为一张完整的高分辨率长图，适合抓取超长网页、聊天记录或长文档。
* **本地离线 OCR 文字识别**：快捷提取截图中的所有文字，支持一键段落整理和复制，完全离线运行，严防隐私泄漏。
* **屏幕贴图 (Pin)**：支持将截图“钉”在屏幕最上层，可任意缩放、调节透明度，并支持鼠标穿透，极度便利的参考图助手。
* **聚光灯演示模式**：突出显示屏幕特定区域，淡化背景，非常适用于授课、会议演示或屏幕录像。

---

## ⚖️ 开源协议与致谢

本项目遵循开源软件协议。

### 致谢 (Acknowledgements)
本项目集成并使用了以下优秀的开源项目，特此致谢：
- **[Avalonia UI](https://github.com/AvaloniaUI/Avalonia)** (遵循 MIT 开源协议) - 现代高性能跨平台 XAML UI 框架。
- **[OpenCvSharp](https://github.com/shimat/opencvsharp)** & **[OpenCV](https://github.com/opencv/opencv)** (遵循 Apache-2.0 开源协议) - 跨平台计算机视觉和图像处理库。
- **[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)** (遵循 Apache-2.0 开源协议) - 提供了高精度的深度学习 OCR 模型。
- **[RapidOCR-json](https://github.com/RapidOCR/RapidOCR-json)** (遵循 Apache-2.0 开源协议) - 提供了本地离线轻量化的 C++ 预测引擎及 JSON 接口封装。
- **[System.Drawing.Common](https://github.com/dotnet/winforms)** (遵循 MIT 开源协议) - 提供了底层的 GDI+ 图像绘图支持。

## 🤝 贡献与反馈说明 (Contribution & Feedback)

* **代码维护政策**：本项目由作者独立开发与持续维护，**目前暂不接受外部代码提交（Pull Request / PR）**。所有提交的 PR 将会被自动关闭，敬请理解。
* **Bug 反馈与新功能建议**：如果您在使用过程中发现了 Bug 或有任何改善软件体验的创意，非常欢迎在 **[Issues 反馈区](https://github.com/yusnip/yuSnip/issues)** 按照模板提交，作者会持续跟进并修复！
* **社区交流与讨论**：欢迎在 **[Discussions 社区讨论区](https://github.com/yusnip/yuSnip/discussions)** 或加入 **QQ 交流群 (453478357)** 交流使用技巧。

