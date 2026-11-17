# 开发与公开发布

记修改记录到根目录 `更新记录.txt`，以 `### 更新日志（YYYY-MM-DD）` 标记最新一节。根目录 `版本号.txt` 提供版本号。

- `开发运行.bat` 将 Debug 版编译到 `dist/dev/`，并在根目录写入 `yuSnip 开发版.lnk`。
- 将审查通过的改动和新版本推送到 `yusnip/yuSnip` 后，运行 `编译并发布.bat`。脚本独立下载公开提交的源码来构建 NativeAOT，创建 Release 并上传GitHub 版 EXE、版本清单和依赖清单。发布说明取自该提交的更新日志。使用 `-CheckOnly` 做只读预检。
- OCR、抠图、FFmpeg 按 GitHub 组件清单下载，组件包通过独立 Release 分发。