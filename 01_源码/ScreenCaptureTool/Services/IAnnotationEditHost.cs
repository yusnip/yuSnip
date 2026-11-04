using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;
using DrawingRectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 标注编辑会话的宿主接口：贴图窗口与录屏覆盖层各自实现这一份，<see cref="AnnotationEditSession"/>
/// 里那套"选中 / 端点手柄 / 拖动缩放 / 滚轮微调 / 参数条回灌 / 两枚浮条 / 文字层"的接线两侧共用一份。
///
/// <para>继承 <see cref="IAnnotationHost"/> 是为了让会话能直接拿宿主去建
/// <see cref="AnnotationInteractionController"/>——落笔那条链的坐标口径与提交口径本就该和编辑链同一个宿主。</para>
///
/// <para><b>接口成员只放"宿主之间真的不一样"的那些</b>：坐标换算、可重做的像素/成品提交、以及各自的
/// 可视对象引用。任何一条判据或算式都不许进这个接口——那正是这次上提要消除的重复。</para>
/// </summary>
internal interface IAnnotationEditHost : IAnnotationHost
{
    /// <summary>图元 + 文字层的撤销/重做簿记。宿主还没进标注态时为 null，会话各处据此早退。</summary>
    AnnotationEditHistory? History { get; }

    /// <summary>矢量图元渲染层（高亮、手柄、预览都画在它上面）。</summary>
    AnnotationLayer? EditLayer { get; }

    /// <summary>共享参数条。录屏侧长在另一扇窗（控制条），取不到即整段回显与同步跳过。</summary>
    AnnotationToolbar? EditToolbar { get; }

    /// <summary>圆角浮条与序号浮条（可为 null：宿主还没建出这两格时）。</summary>
    SpotlightRadiusPill? RadiusPill { get; }
    CounterPill? CounterPillView { get; }

    /// <summary>文字层挂载的画布（截图＝遮罩画布，贴图＝贴图宿主层，录屏＝覆盖层画布）。</summary>
    Canvas TextLayerCanvas { get; }
    Rect ErasureToTextCanvasRect(DrawingRectangleF mark);

    /// <summary>拖动 / 旋转期间指针捕获的目标，一律是宿主窗口本身（换了目标就换了事件路由链）。</summary>
    IInputElement PointerCaptureTarget { get; }

    /// <summary>两枚浮条的夹取矩形＝宿主承载它们的那块面板尺寸（DIP），越界只夹不翻边。</summary>
    Rect PillClampBounds { get; }

    /// <summary>命中容差倍率（截图/录屏恒 1，贴图按显示倍率放大）。</summary>
    float HitTolerance { get; }

    /// <summary>聚光灯可被拖到的范围（内容坐标）；null＝底图还没就绪，不夹取。</summary>
    DrawingRectangleF? SpotlightContentBounds { get; }

    /// <summary>窗口 DIP → 内容坐标，<b>不做</b>边界裁剪（拖动时指针必然甩出画板外）。</summary>
    Point ToContentPoint(Point windowPoint);

    /// <summary>文字可活动范围（画布 DIP）。</summary>
    Rect GetTextValidRect();

    /// <summary>是否有"选区"这一档（贴图/录屏＝整张内容，恒 true 也行，判据与截图同源时才有意义）。</summary>
    bool HasTextRegion { get; }

    /// <summary>选区矩形（画布 DIP）；<see cref="HasTextRegion"/> 为假时不会被读取。</summary>
    Rect TextSelectionDip { get; }

    /// <summary>按当前主题态取资源（文本面板自建 Popup 的底色/描边用），必须走 <c>ActualThemeVariant</c>。</summary>
    bool TryFindPanelResource(string key, out object? value);

    /// <summary>起笔当帧冻结的 18 档参数；<paramref name="arrowScale"/> 是会话里的箭头缩放档。</summary>
    AnnotationGestureSettings CreateGestureSettings(float arrowScale);

    /// <summary>把宿主切到该图元类型对应的工具（点中哪种就切哪种；不带"再点即退出"的开关语义）。</summary>
    void ForceToolForShape(AnnotationShapeKind kind);

    /// <summary>把宿主切到文本工具，同上。</summary>
    void ForceToolForText();

    /// <summary>捕获 / 释放指针到手宿主窗口：图元能被拖出画板外，不捕获就收不到后续的 Moved/Released。</summary>
    void CapturePointer(IPointer pointer);
    void ReleasePointer(IPointer pointer);

    /// <summary>拖本体期间的 <c>SizeAll</c> 光标；抓手柄时宿主不改光标（截图侧同一口径）。</summary>
    void SetMoveCursor(bool on);

    /// <summary>把一次改动推到渲染帧里执行（合帧闸门：贴图每渲染帧一次，录屏同为 TopLevel 同一条）。</summary>
    void PostRenderFrame(Action callback);

    /// <summary>只压一次文本层历史、不重建成品（滑杆一次拖动几十档不几十次重烘）。
    /// 完整提交走继承来的 <see cref="IAnnotationHost.CommitAnnotationChange"/>：压历史 + 重建显示
    /// （贴图重烘成品，录屏只重绘图层）。</summary>
    void CommitTextHistory();

    /// <summary>宿主的标注诊断日志通道（各家前缀不同，内容算式同源）。</summary>
    void LogDiag(string message);
}
