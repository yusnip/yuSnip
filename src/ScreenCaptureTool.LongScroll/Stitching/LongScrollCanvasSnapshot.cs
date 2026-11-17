namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>Actual pixels removed by an along-axis trim operation.</summary>
public readonly record struct CanvasTrimResult(int Leading, int Trailing)
{
    public int Total => checked(Leading + Trailing);
}

/// <summary>
/// Immutable metadata plus owned bitmap clones captured from a canvas at one instant.
/// Callers must dispose the snapshot when rendering is complete.
/// </summary>
public sealed class LongScrollCanvasSnapshot : IDisposable
{
    private readonly List<StitchedStrip> _strips;
    private bool _disposed;

    internal LongScrollCanvasSnapshot(
        ScrollAxis axis,
        int currentViewportAlong,
        int canvasStartAlong,
        int canvasEndAlong,
        int viewportAlongLength,
        int crossLength,
        IEnumerable<StitchedStrip> ownedStrips)
    {
        Axis = axis;
        CurrentViewportAlong = currentViewportAlong;
        CanvasStartAlong = canvasStartAlong;
        CanvasEndAlong = canvasEndAlong;
        ViewportAlongLength = viewportAlongLength;
        CrossLength = crossLength;
        _strips = new List<StitchedStrip>(ownedStrips);
    }

    public ScrollAxis Axis { get; }
    public int CurrentViewportAlong { get; }
    public int CanvasStartAlong { get; }
    public int CanvasEndAlong { get; }
    public int TotalAlongLength => checked(CanvasEndAlong - CanvasStartAlong);
    public int ViewportAlongLength { get; }
    public int CrossLength { get; }
    public IReadOnlyList<StitchedStrip> Strips => _strips;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (StitchedStrip strip in _strips)
        {
            try { strip.Dispose(); } catch { }
        }
        _strips.Clear();
    }
}
