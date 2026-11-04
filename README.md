# yuSnip

yuSnip 是一款免费开源的 Windows 截图、标注、贴图和录屏工具。截取屏幕内容后，可以直接标注、提取文字，或把截图钉在屏幕上作为参考。

[官网下载](https://yusnip.top/) · [版本发布](https://github.com/yusnip/yuSnip/releases) · [反馈问题](https://github.com/yusnip/yuSnip/issues)

## 主要功能

- **截图与标注**　区域截图、窗口和界面元素智能识别，以及箭头、文字、形状、马赛克、序号等工具。
- **长截图**　支持横向和纵向滚动拼接，可实时预览并裁掉多余部分。
- **贴图钉屏**　把图片或剪贴板内容固定在屏幕上，随时缩放、复制和保存。
- **OCR 与抠图**　安装对应组件后，可在本机识别图片文字和去除背景。
- **录屏**　支持选区录制、暂停、预览和导出。
- **翻译、AI 与自动化**　可配置翻译和 AI 服务，也可用命令行和脚本调用截图等功能。

OCR、抠图和录屏所需的额外组件可在「设置 → 依赖运行」中管理。组件下载与更新检查需要联网。使用翻译、AI 或云同步时，对应功能会按配置向所选服务发送文字、图片或同步数据。

从公开源码构建时，Google Drive 同步需要在本机 `settings.ini` 中配置自己的桌面 OAuth 客户端 `GoogleDriveClientId` 和 `GoogleDriveClientSecret`。请勿将本机配置提交到仓库。

## 从源码构建

支持 Windows 10 版本 2004（内部版本 19041）及以上和 Windows 11 x64。安装 .NET 10 SDK，具体版本见 [global.json](01_源码/global.json)。使用 NativeAOT 时还需要 Visual Studio 或 Build Tools 的「使用 C++ 的桌面开发」工作负载和 Windows SDK。

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NativeAot
```

第一条生成开发构建，输出在 `artifacts/debug/`。第二条生成 Windows x64 NativeAOT 发布目录，输出在 `artifacts/win-x64/`，运行时请保留该目录中的依赖文件。脚本会还原 NuGet 依赖并在本地编译，不启动 yuSnip 或上传文件。

## 许可与致谢

yuSnip 的自有源码使用 [MIT 许可证](LICENSE)，允许修改、再分发及商业使用，须保留版权与许可声明。第三方库、模型、字典、字体和组件沿用各自许可，详见 [第三方许可目录](01_源码/ScreenCaptureTool/Assets/ThirdParty/)。

## 赞赏

如果 yuSnip 帮你节省了时间，欢迎请我喝杯咖啡。谢谢你的支持。

<img src="01_源码/ScreenCaptureTool/Assets/WeChatAppreciation.jpg" alt="微信赞赏码" width="200">
