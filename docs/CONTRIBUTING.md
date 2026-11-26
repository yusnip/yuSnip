# 参与贡献 / Contributing

**简体中文** · [English](#english)

欢迎提交可复现的 Bug、功能建议、文档修正和范围明确的 PR。涉及跨模块或交互行为的大改动，可以先在 Issue 里讨论使用场景。

1. 在 Windows x64 安装 [项目要求的 .NET SDK](../src/global.json)，从仓库根目录运行下面的命令。构建成功后可启动 `dist/debug/yuSnip.exe` 验证改动。
2. PR 请说明改动原因、关联 Issue、复现或验证步骤，以及实际验证结果。提交截图和日志前请脱敏。
3. 界面改动请附同一尺寸、内容和状态的改前改后截图，覆盖浅色、深色和跟随系统。涉及缩放或多屏时，请注明验证过的 DPI 和显示器组合。
4. 自动化和开发任务的构建产物放在 `dist/`，日志放在 `log/`，临时文件放在系统临时目录或 `dist/` 下。任务结束前检查根目录，清理本次任务生成的临时文件。正式版 EXE、开发版快捷方式和已有的发布入口按约定放在根目录。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

[提交反馈](https://github.com/yusnip/yuSnip/issues/new/choose) · [私密安全报告](SECURITY.md)

## English

Reproducible bug reports, feature requests, documentation fixes and focused pull requests are welcome. For changes spanning several components or changing user interactions, start by describing the use case in an issue.

1. Install the [.NET SDK specified by the project](../src/global.json) on Windows x64 and run the command above from the repository root. Launch `dist/debug/yuSnip.exe` to verify your changes.
2. In your PR, explain the reason for the change, link the relevant issue, and list the verification steps and results. Redact screenshots and logs before sharing.
3. For UI changes, attach before-and-after screenshots with the same window size, content and state. Cover light, dark and system themes. For scaling or multi-monitor changes, include the DPI and display setups you checked.
4. Put build output in `dist/`, logs in `log/`, and temporary files in the system temp directory or under `dist/`. Before finishing a development or automation task, check the repository root and clean up temporary files created by that task. Keep the release EXE, development shortcut and existing release entry points in the repository root.

[Open an issue](https://github.com/yusnip/yuSnip/issues/new/choose) · [Private security reporting](SECURITY.md)
