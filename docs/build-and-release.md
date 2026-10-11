# 第一阶段：本地构建与发布

本文只适用于本仓库第一阶段源码。第二阶段闭源发行版不对应这里的构建结果。

## 开发构建

使用 Windows x64 与 .NET 8 SDK（仓库固定 SDK 基线 8.0.421）。在仓库根目录：

```powershell
cd 01_源代码
dotnet build ScreenCaptureTool/ScreenCaptureTool.csproj -c Debug -p:Platform=x64 --nologo
dotnet run --project ScreenCaptureTool/ScreenCaptureTool.csproj -c Debug -p:Platform=x64 --no-build
```

首次构建会从 NuGet 获取工程声明的依赖。不要将其他版本的 `bin`／`obj`、个人配置或第二阶段文件复制进工程。代码变更后应完整构建，不能以只编译 C# 的目标替代 XAML 校验。

## NativeAOT 发布

额外安装 Visual Studio 2022「使用 C++ 的桌面开发」、MSVC 和 Windows SDK。在仓库根目录：

```powershell
cd 01_源代码
dotnet publish ScreenCaptureTool/ScreenCaptureTool.csproj -c Release -r win-x64 -p:Platform=x64 -p:DebugType=None --nologo
```

`PublishAot`、裁剪和优化设置已经在工程中声明；不叠加 `PublishSingleFile`。NativeAOT 产生原生主程序，提高分析成本，但不是加密、不能保证无法逆向。公开的通用编译命令不包含作者私人部署或签名流程。

请以实际 `publish` 目录为准，保留程序运行所需的原生 DLL、许可证和第三方声明；本仓库未承诺发布目录只包含一个 EXE。不要只复制主 EXE 后假定其余文件都可删除。再分发时保留根 `LICENSE`、`THIRD_PARTY_NOTICES.md` 与 `LICENSES/`，并核对所分发 NuGet 包及原生库自身的许可要求。

## OCR 资源

`RapidOCR文字识别引擎` 由 Core 工程嵌入，运行时释放到用户应用数据目录。所保留 `RapidOCR-json.exe` 和三个 ONNX 模型已与上游 v0.2.0 发布包逐字节核对；字典内容除换行编码差异外一致。资源校验值和许可证见第三方声明。

本仓库不包含个人服务器部署脚本、签名私钥、云账号配置或实际用户运行记录。

## 本次核验

2026-10-11：整理前与整理后均执行完整 Debug x64 构建；核验结果记录于 CHANGELOG。未以运行本程序的方式读取或修改开发者现有配置。NativeAOT 发布与运行仍应在目标环境单独验证，不能由 Debug 构建推定。
