# 开发与公开发布

记修改记录到根目录 `更新记录.txt`，以 `### 更新日志（YYYY-MM-DD）` 标记最新一节。根目录 `版本号.txt` 提供版本号。

- `开发运行.bat` 将 Debug 版编译到 `dist/dev/`，并在根目录写入 `yuSnip 开发版.lnk`。
- 将审查通过的改动和新版本推送到 `yusnip/yuSnip` 后，运行 `编译并发布.bat`（调用 `tools/PublishAll.ps1`），自动依次完成三个渠道的发布：
  1. **GitHub**：调用 `tools/PublishGitHub.ps1`，从公开提交源码构建 `GitHub` 渠道 NativeAOT，创建 Release 并上传 EXE、版本清单和依赖清单。
  2. **官网软件本体**：调用 `tools/PublishWebsite.ps1`，从同一公开提交源码构建 `Website` 渠道 NativeAOT，通过 `.local-private/website-deploy/` 中的 SSH 配置上传 `yuSnip-<版本>.exe`（并链接为 `yuSnip.exe`）与 `version.json`。
  3. **官网更新日志**：调用 `tools/sync_changelog.py` 将根目录 `更新记录.txt` 转换为 `changelog.html` 与 `data/changelog.json`，同步部署到官网。
  - 使用 `-CheckOnly` 做只读预检。
- OCR、抠图、FFmpeg 按 GitHub 组件清单下载，组件包通过独立 Release 分发。