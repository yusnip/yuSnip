using Avalonia;
using Avalonia.Controls;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core.Capture;
using P = System.Drawing.PointF;

namespace ScreenCaptureTool.Windows;

public partial class CaptureWindow
{
    internal CapturePathEditor? PathEditor { get; private set; }
    internal CaptureSelectionToolbar ModeToolbar => SelectionModeBar;
    internal System.Drawing.Rectangle SelectionModeScreenBounds { get; private set; }
    internal bool HasAnnotationTool => _activeToolKey != null;
    internal void HideRectangularEffectPill() => InfoPill.IsVisible = false;
    private CaptureSelectionMode? _hintSelectionMode;
    private bool _hintPathClosed;
    internal void RefreshSelectionModeHints()
    {
        var mode = PathEditor?.Mode;
        bool closed = PathEditor?.IsClosed ?? false;
        if (_hintSelectionMode == mode && _hintPathClosed == closed) return;
        _hintSelectionMode = mode; _hintPathClosed = closed;
        // 节点移动不改变操作说明，不在每个指针事件里重读快捷键目录并重建提示布局。
        UpdateShortcutHintText();
    }

    internal CapturePathEditor InitializeSelectionModes(bool enabled)
    {
        var editor = new CapturePathEditor
        {
            ToScreen = dip => { var p = WindowDipToScreenPoint(dip); return new P(p.X, p.Y); },
            ToDip = p => new Point((p.X - Position.X) / SafeScaling, (p.Y - Position.Y) / SafeScaling)
        };
        PathEditor = editor; OverlayCanvas.Children.Add(editor);
        // 浮层定位取当前显示器完整范围，不以多屏虚拟桌面的中线替代当前屏幕。
        void PlaceToolbar()
        {
            editor.Width = Bounds.Width; editor.Height = Bounds.Height;
            if (!enabled || !SelectionModeBar.IsVisible || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var cursor);
            var screen = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y)) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var bounds = screen?.Bounds ?? new PixelRect(Position, new PixelSize((int)(Bounds.Width * SafeScaling), (int)(Bounds.Height * SafeScaling)));
            var visibleScreen = bounds.Intersect(new PixelRect(Position,
                new PixelSize((int)(Bounds.Width * SafeScaling), (int)(Bounds.Height * SafeScaling))));
            if (visibleScreen.Width > 0 && visibleScreen.Height > 0) bounds = visibleScreen;
            // 定位与全屏按钮共用这次选定的完整屏幕范围，包含任务栏且不随点击位置改屏。
            SelectionModeScreenBounds = new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            SelectionModeBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = (bounds.X + bounds.Width / 2.0 - Position.X) / SafeScaling - SelectionModeBar.DesiredSize.Width / 2;
            Canvas.SetLeft(SelectionModeBar, left); Canvas.SetTop(SelectionModeBar, (bounds.Y - Position.Y) / SafeScaling + 20);
        }
        SizeChanged += (_, _) => PlaceToolbar();
        Opened += (_, _) =>
        {
            SelectionModeBar.IsVisible = enabled;
            // Opened 时 Bounds 可能还未布局，定位放到布局之后，避免首帧以零尺寸量错中线。
            Avalonia.Threading.Dispatcher.UIThread.Post(PlaceToolbar, Avalonia.Threading.DispatcherPriority.Loaded);
        };
        return editor;
    }
}
