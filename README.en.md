# yuSnip

[简体中文](README.md) · **English**

yuSnip is a free, open-source screenshot and pinning tool for Windows. It is built for work that continues after you capture the screen, such as writing a tutorial, documenting a bug, collecting text from a document, or keeping a design reference in view. Once you select an area, you can edit the image, extract its text, or pin it on top of your other windows.

[Download the latest release](https://github.com/yusnip/yuSnip/releases/latest) · [Official website](https://yusnip.top/) · [Report an issue](https://github.com/yusnip/yuSnip/issues)

Supports Windows 10 version 2004 (build 19041) or later and Windows 11 on x64.

## What you can do with yuSnip

### Annotate a screenshot while the context is still on screen

Snap the selection to a window or a single interface element, or use the pen to outline an irregular shape. Add arrows, labels and step numbers for a walkthrough. Use the spotlight to draw attention to one area or pixelate details you want to cover. You can select and move existing annotations, undo edits, and then copy, save or pin the result.

### Capture a long page or a wide table in one image

Scroll vertically through a document or horizontally across a spreadsheet. You can scroll manually or automatically, preview the stitched image as it grows, and scroll back to include a section you missed. yuSnip detects repeated fixed bars and tries to keep a single copy. You can trim either end or pin the finished image directly.

### Keep a reference image on screen while you work

Pin a screenshot, a clipboard image or text above your other windows. To line up a design reference with the window beneath it, lower the opacity and enable click-through. Select the pinned image and press `Space` to add notes later. After a normal exit, yuSnip can restore your open pins and their annotations.

### Copy the part of an image that you need as text

On a pinned image, hold `Alt` and drag over the words you want to copy. For more complicated layouts, use the OCR window to compare recognized text with the original image, make corrections and copy or export the result. OCR and background removal run locally.

### Record how an issue happens

Start recording from the same area you would use for a screenshot. Pause and resume as needed, then preview the recording before exporting. You can arrange the tools you use most on the capture, pinning and recording toolbars, and assign shortcuts that fit your workflow.

### Turn repeated steps into one action

If you often capture text to translate it, combine area capture, OCR and translation into one action and assign it a shortcut. You can also translate a screenshot and get an image with the translated text in place. Translation, AI and cloud sync are optional, and each sends relevant content to the service you select when used.

## Get started

1. Download and run `yuSnip.exe`. [Download the latest release](https://github.com/yusnip/yuSnip/releases/latest)
2. Find your area capture shortcut under Settings → Shortcuts and Actions (「快捷键与动作」), then capture a reference image.
3. Choose Pin (「贴图」) in the capture toolbar. Drag the image into view as a reference, or add annotations to it later.
4. To try image text selection, background removal or recording features that need additional components, install the relevant components under Settings → Runtime Components (「依赖运行」).

## Build from source

Download or clone this repository and install the .NET 10 SDK on Windows x64. Run the build script from the repository root. [build.ps1](build.ps1) · [SDK version](01_源码/global.json)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The development build is written to `artifacts/debug/`. For a NativeAOT build, install the Desktop development with C++ workload and the Windows SDK using Visual Studio or Build Tools, then add `-NativeAot` to the command. That build is written to `artifacts/win-x64/`. Keep the files in the output folder together when running a local build.

## Feedback and contributions

Issues and pull requests are welcome. When reporting a bug, include your yuSnip version, your Windows version and steps to reproduce it. Redact private information before sharing screenshots or logs. Report security vulnerabilities privately. [Issues](https://github.com/yusnip/yuSnip/issues) · [Security reporting](SECURITY.md)

## License and credits

yuSnip is maintained by 北京林或西. The project's own source code is available under the MIT License. You may use, modify and redistribute it, including for commercial use, as long as you keep the copyright and license notices. Third-party libraries, models and components retain their respective licenses. [MIT License](LICENSE) · [Third-party licenses](01_源码/ScreenCaptureTool/Assets/ThirdParty/)

## Support

yuSnip is free to use. If it saves you time, you can support ongoing maintenance through WeChat.

<img src="01_源码/ScreenCaptureTool/Assets/WeChatAppreciation.jpg" alt="WeChat donation QR code" width="200">
