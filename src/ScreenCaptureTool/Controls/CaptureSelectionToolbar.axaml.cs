using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool.Controls;

public partial class CaptureSelectionToolbar : UserControl
{
    public CaptureSelectionMode Mode { get; private set; }
    public event Action<CaptureSelectionMode>? ModeChanged;
    public event Action? CancelRequested;
    public event Action? FullScreenRequested;
    public CaptureSelectionToolbar() => InitializeComponent();
    public void SelectMode(CaptureSelectionMode mode)
    {
        Mode = mode;
        BtnRectangle.Classes.Set("selected", mode == CaptureSelectionMode.Rectangle);
        BtnFreehand.Classes.Set("selected", mode == CaptureSelectionMode.Freehand);
        BtnPen.Classes.Set("selected", mode == CaptureSelectionMode.Pen);
        ModeChanged?.Invoke(mode);
    }
    private void OnFullScreenClick(object? sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void OnRectangleClick(object? sender, RoutedEventArgs e) => SelectMode(CaptureSelectionMode.Rectangle);
    private void OnPenClick(object? sender, RoutedEventArgs e) => SelectMode(CaptureSelectionMode.Pen);
    private void OnFreehandClick(object? sender, RoutedEventArgs e) => SelectMode(CaptureSelectionMode.Freehand);
}
