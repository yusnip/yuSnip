using System.Drawing;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>
/// Thread-safe logical canvas shared by vertical and horizontal long screenshots.
/// All positions are signed coordinates on <see cref="Axis"/>. Leading expansion
/// inserts a negative-position strip and never moves existing strips.
/// </summary>
public sealed class LongScrollCanvasState : IDisposable
{
    private readonly object _sync = new();
    private readonly List<StitchedStrip> _strips = new();
    private readonly int _maxAlongLengthPx;
    private int _totalAlongLength;
    private int _currentViewportAlong;
    private int _canvasStartAlong;
    private int _canvasEndAlong;
    private int _viewportAlongLength;
    private int _crossLength;
    private bool _maxReached;
    private string _lastAction = "Not initialized";
    private bool _disposed;

    public LongScrollCanvasState(int maxTotalHeightPx)
        : this(ScrollAxis.Vertical, maxTotalHeightPx)
    {
    }

    public LongScrollCanvasState(ScrollAxis axis, int maxAlongLengthPx)
    {
        AxisGeometry.Validate(axis);
        if (maxAlongLengthPx <= 0) throw new ArgumentOutOfRangeException(nameof(maxAlongLengthPx));
        Axis = axis;
        _maxAlongLengthPx = maxAlongLengthPx;
    }

    public ScrollAxis Axis { get; }

    public int CurrentViewportAlong
    {
        get { lock (_sync) { return _currentViewportAlong; } }
    }

    public int CanvasStartAlong
    {
        get { lock (_sync) { return _canvasStartAlong; } }
    }

    public int CanvasEndAlong
    {
        get { lock (_sync) { return _canvasEndAlong; } }
    }

    public int TotalAlongLength
    {
        get { lock (_sync) { return _totalAlongLength; } }
    }

    public int ViewportAlongLength
    {
        get { lock (_sync) { return _viewportAlongLength; } }
    }

    public int CrossLength
    {
        get { lock (_sync) { return _crossLength; } }
    }

    public bool MaxReached
    {
        get { lock (_sync) { return _maxReached; } }
    }

    public string LastAction
    {
        get { lock (_sync) { return _lastAction; } }
    }

    // Vertical compatibility aliases.
    public int CurrentViewportY => CurrentViewportAlong;
    public int TotalHeight => TotalAlongLength;
    public int CanvasTopY => CanvasStartAlong;
    public bool MaxHeightReached => MaxReached;
    public string LastCanvasAction => LastAction;

    /// <summary>
    /// Initializes or replaces the canvas and takes ownership of <paramref name="firstFrame"/>.
    /// Calling this method again safely releases all previously owned strips.
    /// </summary>
    public void Initialize(Bitmap firstFrame)
    {
        ArgumentNullException.ThrowIfNull(firstFrame);
        int frameAlong = AxisGeometry.AlongLength(Axis, firstFrame);
        int frameCross = AxisGeometry.CrossLength(Axis, firstFrame);
        if (frameAlong <= 0 || frameCross <= 0)
            throw new ArgumentException("The initial frame has invalid dimensions.", nameof(firstFrame));

        lock (_sync)
        {
            ThrowIfDisposed();
            Bitmap ownedFrame = firstFrame;
            int keptAlong = Math.Min(frameAlong, _maxAlongLengthPx);
            try
            {
                if (keptAlong != frameAlong)
                {
                    ownedFrame = firstFrame.Clone(
                        AxisGeometry.FullCrossRectangle(Axis, 0, keptAlong, frameCross),
                        firstFrame.PixelFormat);
                }

                var initialStrip = new StitchedStrip(ownedFrame, 0, frameCross, keptAlong, 0, Axis);
                ClearStripsLocked();
                if (!ReferenceEquals(ownedFrame, firstFrame))
                    firstFrame.Dispose();

                _strips.Add(initialStrip);
                _totalAlongLength = keptAlong;
                _currentViewportAlong = 0;
                _canvasStartAlong = 0;
                _canvasEndAlong = keptAlong;
                _viewportAlongLength = keptAlong;
                _crossLength = frameCross;
                _maxReached = keptAlong >= _maxAlongLengthPx;
                _lastAction = "Canvas initialized";
            }
            catch
            {
                if (!ReferenceEquals(ownedFrame, firstFrame))
                    ownedFrame.Dispose();
                throw;
            }
        }
    }

    public void ReplaceWithFrame(Bitmap frame) => Initialize(frame);

    public bool TryApplyFrame(
        Bitmap currentFrame,
        int newViewportAlong,
        AxisDirection direction,
        int staticLeading,
        int staticTrailing,
        out bool positionUpdated)
    {
        ArgumentNullException.ThrowIfNull(currentFrame);
        AxisGeometry.Validate(direction);
        positionUpdated = false;

        int viewAlong = AxisGeometry.AlongLength(Axis, currentFrame);
        int viewCross = AxisGeometry.CrossLength(Axis, currentFrame);
        if (viewAlong <= 0 || viewCross <= 0) return Reject("Frame dimensions are invalid");

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_strips.Count == 0 || _totalAlongLength <= 0)
                return RejectLocked("Canvas is not initialized");
            if (viewCross != _crossLength)
                return RejectLocked("Frame cross length does not match the canvas");
            if (_viewportAlongLength <= 0) _viewportAlongLength = viewAlong;
            if (viewAlong != _viewportAlongLength)
                return RejectLocked("Frame along length does not match the viewport");

            long viewEndLong = (long)newViewportAlong + viewAlong;
            if (viewEndLong > int.MaxValue || viewEndLong < int.MinValue)
                return RejectLocked("Viewport coordinate overflow");
            int viewEnd = (int)viewEndLong;

            long viewportDelta = (long)newViewportAlong - _currentViewportAlong;
            if ((direction == AxisDirection.Leading && viewportDelta >= 0) ||
                (direction == AxisDirection.Trailing && viewportDelta <= 0))
            {
                return RejectLocked("Viewport movement conflicts with direction");
            }

            int leadingOverflow = PositiveDifference(_canvasStartAlong, newViewportAlong);
            int trailingOverflow = PositiveDifference(viewEnd, _canvasEndAlong);
            if (leadingOverflow > 0 && trailingOverflow > 0)
                return RejectLocked("Viewport crosses both canvas edges");
            if (direction == AxisDirection.Leading && trailingOverflow > 0 && leadingOverflow <= 0)
                return RejectLocked("Movement direction conflicts with canvas edge");
            if (direction == AxisDirection.Trailing && leadingOverflow > 0 && trailingOverflow <= 0)
                return RejectLocked("Movement direction conflicts with canvas edge");

            if (leadingOverflow <= 0 && trailingOverflow <= 0)
            {
                _currentViewportAlong = newViewportAlong;
                _lastAction = "Viewport moved inside captured content";
                positionUpdated = true;
                return false;
            }

            int remaining = _maxAlongLengthPx - _totalAlongLength;
            if (remaining <= 0)
            {
                _maxReached = true;
                return RejectLocked("Maximum along length reached");
            }

            staticLeading = Math.Clamp(staticLeading, 0, viewAlong);
            staticTrailing = Math.Clamp(staticTrailing, 0, viewAlong - staticLeading);

            if (leadingOverflow > 0)
            {
                return AddLeadingOverflowLocked(
                    currentFrame,
                    newViewportAlong,
                    Math.Min(leadingOverflow, remaining),
                    staticLeading,
                    staticTrailing,
                    out positionUpdated);
            }

            return AddTrailingOverflowLocked(
                currentFrame,
                newViewportAlong,
                Math.Min(trailingOverflow, remaining),
                staticLeading,
                staticTrailing,
                out positionUpdated);
        }
    }

    /// <summary>Vertical compatibility overload.</summary>
    public bool TryApplyFrame(
        Bitmap currentFrame,
        int newViewportY,
        bool directionIsUp,
        int staticTop,
        int staticBottom,
        out bool positionUpdated)
    {
        if (Axis != ScrollAxis.Vertical)
            throw new InvalidOperationException("The vertical compatibility overload cannot be used on a horizontal canvas.");
        return TryApplyFrame(
            currentFrame,
            newViewportY,
            directionIsUp ? AxisDirection.Leading : AxisDirection.Trailing,
            staticTop,
            staticBottom,
            out positionUpdated);
    }

    /// <summary>
    /// Moves the logical viewport without changing captured strips. When unclamped this
    /// may temporarily place the viewport outside the canvas so the next frame can fill it.
    /// </summary>
    public int ShiftViewportAlong(int delta, bool clampToCanvas = false)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            int candidate = checked(_currentViewportAlong + delta);
            if (clampToCanvas)
            {
                int maxStart = Math.Max(_canvasStartAlong, _canvasEndAlong - _viewportAlongLength);
                candidate = Math.Clamp(candidate, _canvasStartAlong, maxStart);
            }

            _currentViewportAlong = candidate;
            _lastAction = $"Viewport shifted by {delta}px";
            return candidate;
        }
    }

    /// <summary>
    /// Removes pixels from both logical ends. Boundary strips are cloned and clipped;
    /// surviving coordinates remain in the original signed coordinate space.
    /// </summary>
    public CanvasTrimResult TrimAlong(int leadingPixels, int trailingPixels)
    {
        if (leadingPixels < 0) throw new ArgumentOutOfRangeException(nameof(leadingPixels));
        if (trailingPixels < 0) throw new ArgumentOutOfRangeException(nameof(trailingPixels));

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_strips.Count == 0 || _totalAlongLength <= 1)
                return default;

            int actualLeading = Math.Min(leadingPixels, _totalAlongLength - 1);
            int availableTrailing = _totalAlongLength - actualLeading - 1;
            int actualTrailing = Math.Min(trailingPixels, availableTrailing);
            if (actualLeading == 0 && actualTrailing == 0) return default;

            int newStart = checked(_canvasStartAlong + actualLeading);
            int newEnd = checked(_canvasEndAlong - actualTrailing);
            var replacements = new List<StitchedStrip>(_strips.Count);
            try
            {
                foreach (StitchedStrip strip in _strips)
                {
                    int keptStart = Math.Max(strip.AlongStart, newStart);
                    int keptEnd = Math.Min(strip.AlongEnd, newEnd);
                    int keptLength = keptEnd - keptStart;
                    if (keptLength <= 0) continue;

                    int sourceAlong = keptStart - strip.AlongStart;
                    Bitmap clone = strip.Bitmap.Clone(
                        AxisGeometry.FullCrossRectangle(Axis, sourceAlong, keptLength, _crossLength),
                        strip.Bitmap.PixelFormat);
                    int overlap = Math.Max(0, strip.OverlapLength - sourceAlong);
                    overlap = Math.Min(overlap, keptLength);
                    replacements.Add(new StitchedStrip(
                        clone,
                        keptStart,
                        _crossLength,
                        keptLength,
                        overlap,
                        Axis));
                }
            }
            catch
            {
                DisposeStrips(replacements);
                throw;
            }

            if (replacements.Count == 0)
            {
                DisposeStrips(replacements);
                return default;
            }

            ClearStripsLocked();
            _strips.AddRange(replacements);
            _canvasStartAlong = newStart;
            _canvasEndAlong = newEnd;
            _totalAlongLength = newEnd - newStart;
            _maxReached = _totalAlongLength >= _maxAlongLengthPx;
            ClampViewportLocked();
            _lastAction = $"Trimmed leading {actualLeading}px, trailing {actualTrailing}px";
            return new CanvasTrimResult(actualLeading, actualTrailing);
        }
    }

    /// <summary>
    /// Compatibility snapshot. The returned list is detached, but its bitmaps are owned
    /// by this canvas and must not be disposed by the caller.
    /// </summary>
    public List<StitchedStrip> SnapshotStrips()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new List<StitchedStrip>(_strips);
        }
    }

    /// <summary>Executes an immediate read under the canvas lock.</summary>
    public void WithReadOnlyStrips(Action<IReadOnlyList<StitchedStrip>> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_sync)
        {
            ThrowIfDisposed();
            reader(_strips);
        }
    }

    public T WithReadOnlyStrips<T>(Func<IReadOnlyList<StitchedStrip>, T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_sync)
        {
            ThrowIfDisposed();
            return reader(_strips);
        }
    }

    public LongScrollCanvasSnapshot CreateSnapshot()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var clones = new List<StitchedStrip>(_strips.Count);
            try
            {
                foreach (StitchedStrip strip in _strips)
                    clones.Add(strip.CloneOwned(Axis));
                return new LongScrollCanvasSnapshot(
                    Axis,
                    _currentViewportAlong,
                    _canvasStartAlong,
                    _canvasEndAlong,
                    _viewportAlongLength,
                    _crossLength,
                    clones);
            }
            catch
            {
                DisposeStrips(clones);
                throw;
            }
        }
    }

    public int CalculateCompositedAlongLength(int fallbackAlongLength)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return LongScrollComposer.CalculateCompositedAlongLength(_strips, fallbackAlongLength);
        }
    }

    public int CalculateCompositedHeight(int fallbackHeight)
    {
        if (Axis != ScrollAxis.Vertical)
            throw new InvalidOperationException("Physical height is not the along length of a horizontal canvas.");
        return CalculateCompositedAlongLength(fallbackHeight);
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ClearStripsLocked();
        }
    }

    private bool AddLeadingOverflowLocked(
        Bitmap currentFrame,
        int newViewportAlong,
        int requestedLength,
        int staticLeading,
        int staticTrailing,
        out bool positionUpdated)
    {
        positionUpdated = false;
        int dynamicLength = _viewportAlongLength - staticLeading - staticTrailing;
        int addLength = Math.Min(requestedLength, Math.Max(0, dynamicLength));
        if (addLength <= 0)
        {
            _currentViewportAlong = newViewportAlong;
            _lastAction = "No valid leading content";
            positionUpdated = true;
            return false;
        }

        Bitmap stripBitmap = currentFrame.Clone(
            AxisGeometry.FullCrossRectangle(Axis, staticLeading, addLength, _crossLength),
            currentFrame.PixelFormat);
        int stripStart = checked(_canvasStartAlong - addLength);
        try
        {
            _strips.Insert(0, new StitchedStrip(stripBitmap, stripStart, _crossLength, addLength, 0, Axis));
        }
        catch
        {
            stripBitmap.Dispose();
            throw;
        }

        _totalAlongLength += addLength;
        _canvasStartAlong = stripStart;
        _currentViewportAlong = newViewportAlong;
        _maxReached = _totalAlongLength >= _maxAlongLengthPx;
        _lastAction = $"Added leading {addLength}px";
        return true;
    }

    private bool AddTrailingOverflowLocked(
        Bitmap currentFrame,
        int newViewportAlong,
        int requestedLength,
        int staticLeading,
        int staticTrailing,
        out bool positionUpdated)
    {
        positionUpdated = false;
        int dynamicEnd = _viewportAlongLength - staticTrailing;
        int sourceAlong = dynamicEnd - requestedLength;
        if (sourceAlong < staticLeading) sourceAlong = staticLeading;
        int addLength = Math.Min(requestedLength, dynamicEnd - sourceAlong);
        if (addLength <= 0)
        {
            _currentViewportAlong = newViewportAlong;
            _lastAction = "No valid trailing content";
            positionUpdated = true;
            return false;
        }

        int cropEnd = _viewportAlongLength;
        int overlapLength = Math.Max(0, _viewportAlongLength / 3 - addLength);
        int minimumFixedOverlap = staticTrailing > 0 ? staticTrailing + 12 : 0;
        overlapLength = Math.Max(overlapLength, minimumFixedOverlap);
        int maxOverlap = Math.Max(0, cropEnd - staticLeading - addLength);
        overlapLength = Math.Min(overlapLength, maxOverlap);

        int cropLength = addLength + overlapLength;
        int cropStart = cropEnd - cropLength;
        if (cropStart < staticLeading)
        {
            cropStart = staticLeading;
            cropLength = cropEnd - cropStart;
            overlapLength = Math.Max(0, cropLength - addLength);
        }
        if (cropLength <= 0) return false;

        Bitmap stripBitmap = currentFrame.Clone(
            AxisGeometry.FullCrossRectangle(Axis, cropStart, cropLength, _crossLength),
            currentFrame.PixelFormat);
        int stripStart = checked(_canvasEndAlong - overlapLength);
        try
        {
            _strips.Add(new StitchedStrip(
                stripBitmap,
                stripStart,
                _crossLength,
                cropLength,
                overlapLength,
                Axis));
        }
        catch
        {
            stripBitmap.Dispose();
            throw;
        }

        _totalAlongLength += addLength;
        _canvasEndAlong = checked(_canvasEndAlong + addLength);
        _currentViewportAlong = newViewportAlong;
        _maxReached = _totalAlongLength >= _maxAlongLengthPx;
        _lastAction = $"Added trailing {addLength}px";
        return true;
    }

    private bool Reject(string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return RejectLocked(reason);
        }
    }

    private bool RejectLocked(string reason)
    {
        _lastAction = reason;
        return false;
    }

    private void ClampViewportLocked()
    {
        int maxStart = Math.Max(_canvasStartAlong, _canvasEndAlong - _viewportAlongLength);
        _currentViewportAlong = Math.Clamp(_currentViewportAlong, _canvasStartAlong, maxStart);
    }

    private void ClearStripsLocked()
    {
        DisposeStrips(_strips);
        _strips.Clear();
    }

    private static void DisposeStrips(IEnumerable<StitchedStrip> strips)
    {
        foreach (StitchedStrip strip in strips)
        {
            try { strip.Dispose(); } catch { }
        }
    }

    private static int PositiveDifference(int greater, int smaller)
    {
        long difference = (long)greater - smaller;
        if (difference <= 0) return 0;
        return difference > int.MaxValue ? int.MaxValue : (int)difference;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LongScrollCanvasState));
    }
}
