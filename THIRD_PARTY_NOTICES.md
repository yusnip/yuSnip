# 第三方软件、模型与素材声明

根目录 MIT 许可证仅适用于 yuSnip 自有代码，不替代下列作品的许可。工单附件、第三方商标及未经授权的外部素材不因此获得 MIT 授权。再分发二进制时还应保留实际依赖包／原生库提供的许可证、NOTICE 和必要源码提供义务，不应只复制本表。

| 组件 | 本仓库使用情况 | 上游及许可 |
| --- | --- | --- |
| Avalonia UI | 11.2.8，UI 及主题 | https://github.com/AvaloniaUI/Avalonia ，MIT |
| Avalonia.Fonts.Inter / Inter | NuGet 字体依赖；字体许可单独适用 | https://github.com/rsms/inter ，SIL Open Font License 1.1 |
| OpenCvSharp4 / runtime.win | 4.10.0.20241108，含原生运行依赖 | https://github.com/shimat/opencvsharp ，Apache-2.0；打包原生依赖须核对各自许可 |
| OpenCV | 由 OpenCvSharp 原生包提供 | https://github.com/opencv/opencv ，Apache-2.0；可选编解码器等遵循各自许可 |
| System.Drawing.Common | 8.0.27 | https://github.com/dotnet/winforms ，MIT 及包内第三方声明 |
| RapidOCR-json | 官方 v0.2.0 的 Windows EXE，SHA-256 与官方发布包相同 | https://github.com/hiroi-sora/RapidOCR-json ，MIT，全文见 LICENSES/RapidOCR-json-MIT.txt |
| RapidOcrOnnx | OCR 引擎上游推理实现 | https://github.com/RapidAI/RapidOcrOnnx ，Apache-2.0，全文见 LICENSES/RapidOcrOnnx-Apache-2.0.txt |
| ONNX Runtime | OCR 上游运行依赖 | https://github.com/microsoft/onnxruntime ，MIT，全文见 LICENSES/ONNXRuntime-MIT.txt |
| PP-OCR 模型及字典 | 历史发布包中的三个 ONNX 模型和配套字典 | https://github.com/PaddlePaddle/PaddleOCR ，Apache-2.0，全文见 LICENSES/PaddleOCR-Apache-2.0.txt |

## OCR 来源核对

2026-10-11 将历史快照与上游 [RapidOCR-json v0.2.0 发布包](https://github.com/hiroi-sora/RapidOCR-json/releases/tag/v0.2.0) 比较：`RapidOCR-json.exe`、检测／分类／识别三个模型及 `configs.txt` 逐字节一致；三份字典按文本行比较一致，仅换行／编码形式存在差异。本次未修改这些资源，也未执行下载的 OCR EXE。模型与转换作品的许可边界另见 [RapidOCR 模型说明](https://rapidai.github.io/RapidOCRDocs/main/en/model_licenses/)。

本仓库未采用来源未核对的备用 `ocr.exe`；删除该未引用文件不改变 `PaddleOcrService` 实际使用的 `RapidOCR-json.exe`。

| 资源路径（相对 OCR 目录） | SHA-256 |
| --- | --- |
| `models/ch_PP-OCRv4_det_infer.onnx` | `d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9` |
| `models/ch_ppocr_mobile_v2.0_cls_infer.onnx` | `e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c` |
| `models/configs.txt` | `85a152b127b27188b8fac92368a73b6f8c92cfc5b4c191f68f00836f5fb1250c` |
| `models/dict_chinese.txt` | `d917d7489d2c20d2e4cee8c17068f8ac54f6ee406a04bf5ac3b1ff5dc38e1e52` |
| `models/dict_chinese_cht.txt` | `6fd2003890b5075e091f4f6c7843dcf61ef1dcc3b21d834a6e4b8af86eb26392` |
| `models/ppocr_keys_v1.txt` | `d917d7489d2c20d2e4cee8c17068f8ac54f6ee406a04bf5ac3b1ff5dc38e1e52` |
| `models/rec_ch_PP-OCRv4_infer.onnx` | `48fc40f24f6d2a207a2b1091d3437eb3cc3eb6b676dc3ef9c37384005483683b` |
| `RapidOCR-json.exe` | `616bd43a4672ee9fcf4d1ca607d7d9ec413b45d489482064d97e00a7efa9d76e` |
