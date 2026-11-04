# yuSnip

yuSnip 是一款免费开源的 Windows 截图与贴图工具。框选一块屏幕内容，就能在选区里标注、复制文字、录制操作，或把图片钉在屏幕上对照着用。

[下载最新版](https://github.com/yusnip/yuSnip/releases/latest) · [官方网站](https://yusnip.top/) · [反馈问题](https://github.com/yusnip/yuSnip/issues)

支持 Windows 10 版本 2004（内部版本 19041）及以上和 Windows 11 x64。

## 开始使用

1. 从发布页下载 `yuSnip.exe` 并运行。
2. 在「设置 → 快捷键与动作」查看或修改区域截图快捷键。
3. 框选后在工具栏里选择标注、复制、保存或贴图。需要 OCR、抠图或录屏组件时，可在「设置 → 依赖运行」安装。

## 常用功能

- **截取与标注**　手动框选或捕捉窗口和界面元素。用箭头、文字、序号说明操作步骤，也可以用马赛克遮挡敏感区域。
- **长截图**　横向或纵向滚动拼接，适合截取长网页、文档和宽表格。支持实时预览与裁剪。
- **贴图钉屏**　把截图或剪贴板内容置顶，查资料、对照设计稿或抄录内容时可以随手缩放、标注和复制。
- **OCR 与抠图**　在本机提取截图文字，或将人物与物体从背景中分离。
- **录屏**　录下选区内的操作，支持暂停、预览和导出。
- **扩展功能**　按需配置翻译、AI 服务与云同步，也可以通过命令行和脚本调用截图等功能。

翻译、AI 和云同步会按配置向所选服务发送相关内容。

## 从源码构建

下载或克隆源码，在 Windows x64 上安装 .NET 10 SDK，然后从仓库根目录运行。SDK 版本见 [global.json](01_源码/global.json)。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

开发构建输出到 `artifacts/debug/`。需要 NativeAOT 时，安装 Visual Studio 或 Build Tools 的「使用 C++ 的桌面开发」工作负载和 Windows SDK，在命令末尾加 `-NativeAot`，输出到 `artifacts/win-x64/`。运行时请保留输出目录中的依赖文件。

## 反馈与贡献

欢迎提交问题或 Pull Request。反馈时请附上 yuSnip 版本、Windows 版本和复现步骤，分享截图或日志前请遮盖私人信息。安全漏洞请走私下报告入口。 [Issues](https://github.com/yusnip/yuSnip/issues) · [安全报告](SECURITY.md)

## 许可与致谢

yuSnip 由北京林或西维护。项目自有源码采用 MIT 许可，允许使用、修改、分发和商业使用，须保留版权与许可声明。第三方库、模型和组件沿用各自许可。 [MIT 许可证](LICENSE) · [第三方许可](01_源码/ScreenCaptureTool/Assets/ThirdParty/)

## 赞赏

yuSnip 免费使用。如果它帮你节省了时间，欢迎通过微信赞赏支持后续维护。

<img src="01_源码/ScreenCaptureTool/Assets/WeChatAppreciation.jpg" alt="微信赞赏码" width="200">
