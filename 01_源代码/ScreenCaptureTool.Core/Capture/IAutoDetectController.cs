using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 自动检测控制器接口。统一驱动光标下的"候选高亮区"，对外暴露当前候选。
    /// 实现由 T4.5b 完成（含状态机 + debounce + 缓存）。
    /// </summary>
    public interface IAutoDetectController : IDisposable
    {
        /// <summary>当前的检测模式。运行期可改，会立即影响后续候选计算。</summary>
        DetectMode Mode { get; set; }

        /// <summary>
        /// 当前候选高亮区（屏幕物理像素，与 <see cref="CapturedFrame.VirtualScreenRect"/> 同坐标系）。
        /// 没有候选时为 <c>null</c>。
        /// </summary>
        Rectangle? CurrentHighlight { get; }

        /// <summary>候选改变时触发（值为新候选；可能为 null）。</summary>
        event Action<Rectangle?>? HighlightChanged;

        /// <summary>启动检测。多次调用幂等。</summary>
        void Start();

        /// <summary>停止检测，清空候选。多次调用幂等。</summary>
        void Stop();

        /// <summary>
        /// 在外部已经知道"光标位置变了"时主动刷新候选（屏幕物理像素）。
        /// 实现内部应做 debounce，避免高频鼠标移动打满 IAccessible 调用。
        /// </summary>
        void NotifyPointerMoved(Point screenPoint);
    }
}
