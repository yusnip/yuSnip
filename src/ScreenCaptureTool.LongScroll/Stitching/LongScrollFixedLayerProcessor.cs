using System.Drawing;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>Extracts fixed leading/trailing layers on the configured scroll axis.</summary>
public sealed class LongScrollFixedLayerProcessor : IDisposable
{
    private readonly LongScrollOptions _options;
    private readonly object _sync = new();
    private Bitmap? _fixedLeading;
    private Bitmap? _fixedTrailing;
    private int _crossLength;
    private bool _disposed;

    public LongScrollFixedLayerProcessor(LongScrollOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        AxisGeometry.Validate(options.Axis);
    }

    public ScrollAxis Axis => _options.Axis;
    public bool Enabled => _options.EnableFixedLayerMode;

    public int FixedLeadingLength { get; private set; }
    public int FixedTrailingLength { get; private set; }

    // Vertical compatibility aliases. In horizontal mode these still mean along length.
    public int FixedTopHeight => FixedLeadingLength;
    public int FixedBottomHeight => FixedTrailingLength;

    public void Initialize(Bitmap firstFrame)
    {
        ArgumentNullException.ThrowIfNull(firstFrame);
        int alongLength = AxisGeometry.AlongLength(Axis, firstFrame);
        int crossLength = AxisGeometry.CrossLength(Axis, firstFrame);
        CalculateFixedLayerLengths(alongLength, out int leading, out int trailing);

        lock (_sync)
        {
            ThrowIfDisposed();
            Bitmap? fixedLeading = null;
            Bitmap? fixedTrailing = null;
            try
            {
                fixedLeading = CropFixedLeading(firstFrame, leading);
                fixedTrailing = CropFixedTrailing(firstFrame, trailing);
                FixedLeadingLength = leading;
                FixedTrailingLength = trailing;
                _crossLength = crossLength;
                ReplaceLeadingLocked(fixedLeading);
                fixedLeading = null;
                ReplaceTrailingLocked(fixedTrailing);
                fixedTrailing = null;
            }
            finally
            {
                SafeDispose(fixedLeading);
                SafeDispose(fixedTrailing);
            }
        }
    }

    public Bitmap? CropScrollableMiddle(Bitmap fullFrame)
    {
        if (fullFrame == null) return null;
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateFrameCrossLengthLocked(fullFrame);
            return CropScrollableMiddle(fullFrame, FixedLeadingLength, FixedTrailingLength);
        }
    }

    public void UpdateLeading(Bitmap fullFrame)
    {
        if (fullFrame == null) return;
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateFrameCrossLengthLocked(fullFrame);
            Bitmap? leading = CropFixedLeading(fullFrame, FixedLeadingLength);
            if (leading == null && FixedLeadingLength > 0) return;
            ReplaceLeadingLocked(leading);
        }
    }

    public void UpdateTrailing(Bitmap fullFrame)
    {
        if (fullFrame == null) return;
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateFrameCrossLengthLocked(fullFrame);
            Bitmap? trailing = CropFixedTrailing(fullFrame, FixedTrailingLength);
            if (trailing == null && FixedTrailingLength > 0) return;
            ReplaceTrailingLocked(trailing);
        }
    }

    public void UpdateTop(Bitmap fullFrame) => UpdateLeading(fullFrame);
    public void UpdateBottom(Bitmap fullFrame) => UpdateTrailing(fullFrame);

    /// <summary>Returns a non-owning reference valid only until the next update/dispose.</summary>
    public Bitmap? SnapshotFixedLeading()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _fixedLeading;
        }
    }

    /// <summary>Returns a non-owning reference valid only until the next update/dispose.</summary>
    public Bitmap? SnapshotFixedTrailing()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _fixedTrailing;
        }
    }

    public Bitmap? SnapshotFixedTop() => SnapshotFixedLeading();
    public Bitmap? SnapshotFixedBottom() => SnapshotFixedTrailing();

    public Bitmap? CloneFixedLeading()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _fixedLeading == null ? null : (Bitmap)_fixedLeading.Clone();
        }
    }

    public Bitmap? CloneFixedTrailing()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _fixedTrailing == null ? null : (Bitmap)_fixedTrailing.Clone();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            SafeDispose(_fixedLeading);
            SafeDispose(_fixedTrailing);
            _fixedLeading = null;
            _fixedTrailing = null;
        }
    }

    private void CalculateFixedLayerLengths(int frameAlong, out int leading, out int trailing)
    {
        leading = 0;
        trailing = 0;
        if (!Enabled || frameAlong <= 0) return;

        double leadingRatio = _options.FixedLeadingRatio;
        double trailingRatio = _options.FixedTrailingRatio;
        if (!double.IsFinite(leadingRatio) || leadingRatio < 0)
            throw new InvalidOperationException("Fixed leading ratio is invalid.");
        if (!double.IsFinite(trailingRatio) || trailingRatio < 0)
            throw new InvalidOperationException("Fixed trailing ratio is invalid.");

        int maxFixed = Math.Max(0, _options.MaxFixedLayerAlongLengthPx);
        leading = Math.Clamp((int)Math.Round(frameAlong * leadingRatio), 0, maxFixed);
        trailing = Math.Clamp((int)Math.Round(frameAlong * trailingRatio), 0, maxFixed);

        int minimumMiddle = Math.Max(80, (int)Math.Round(frameAlong * 0.65));
        minimumMiddle = Math.Min(minimumMiddle, frameAlong);
        int currentMiddle = frameAlong - leading - trailing;
        if (currentMiddle >= minimumMiddle) return;

        int allowedFixed = Math.Max(0, frameAlong - minimumMiddle);
        double totalFixed = (double)leading + trailing;
        if (totalFixed <= 0)
        {
            leading = 0;
            trailing = 0;
            return;
        }

        leading = (int)Math.Round(allowedFixed * (leading / totalFixed));
        trailing = allowedFixed - leading;
    }

    private Bitmap? CropFixedLeading(Bitmap bitmap, int length)
    {
        if (length <= 0) return null;
        int along = AxisGeometry.AlongLength(Axis, bitmap);
        int cross = AxisGeometry.CrossLength(Axis, bitmap);
        length = Math.Min(length, along);
        return bitmap.Clone(
            AxisGeometry.FullCrossRectangle(Axis, 0, length, cross),
            bitmap.PixelFormat);
    }

    private Bitmap? CropFixedTrailing(Bitmap bitmap, int length)
    {
        if (length <= 0) return null;
        int along = AxisGeometry.AlongLength(Axis, bitmap);
        int cross = AxisGeometry.CrossLength(Axis, bitmap);
        length = Math.Min(length, along);
        return bitmap.Clone(
            AxisGeometry.FullCrossRectangle(Axis, along - length, length, cross),
            bitmap.PixelFormat);
    }

    private Bitmap? CropScrollableMiddle(Bitmap bitmap, int leading, int trailing)
    {
        int along = AxisGeometry.AlongLength(Axis, bitmap);
        int cross = AxisGeometry.CrossLength(Axis, bitmap);
        leading = Math.Clamp(leading, 0, along);
        trailing = Math.Clamp(trailing, 0, along - leading);
        if (leading == 0 && trailing == 0) return (Bitmap)bitmap.Clone();
        int middle = along - leading - trailing;
        if (middle <= 0) return null;
        return bitmap.Clone(
            AxisGeometry.FullCrossRectangle(Axis, leading, middle, cross),
            bitmap.PixelFormat);
    }

    private void ValidateFrameCrossLengthLocked(Bitmap frame)
    {
        int cross = AxisGeometry.CrossLength(Axis, frame);
        if (_crossLength > 0 && cross != _crossLength)
            throw new ArgumentException("Frame cross length does not match the initialized fixed layers.", nameof(frame));
    }

    private void ReplaceLeadingLocked(Bitmap? bitmap)
    {
        SafeDispose(_fixedLeading);
        _fixedLeading = bitmap;
    }

    private void ReplaceTrailingLocked(Bitmap? bitmap)
    {
        SafeDispose(_fixedTrailing);
        _fixedTrailing = bitmap;
    }

    private static void SafeDispose(Bitmap? bitmap)
    {
        try { bitmap?.Dispose(); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LongScrollFixedLayerProcessor));
    }
}
