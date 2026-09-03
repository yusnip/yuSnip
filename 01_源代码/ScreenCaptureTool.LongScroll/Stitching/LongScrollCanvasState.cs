using System.Drawing;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>长截图逻辑画布：维护条带、视口位置、上下扩展与 overlay。</summary>
public sealed class LongScrollCanvasState : IDisposable
{
    private readonly object _sync = new();
    private readonly List<StitchedStrip> _strips = new();
    private readonly int _maxTotalHeightPx;
    private int _totalHeight;
    private int _currentViewportY;
    private int _canvasTopY;
    private int _canvasBottomY;
    private int _viewportHeight;
    private bool _disposed;

    public LongScrollCanvasState(int maxTotalHeightPx)
    {
        _maxTotalHeightPx = Math.Max(1, maxTotalHeightPx);
        LastCanvasAction = "初始化";
    }

    public int CurrentViewportY
    {
        get { lock (_sync) { return _currentViewportY; } }
    }

    public int TotalHeight
    {
        get { lock (_sync) { return _totalHeight; } }
    }

    public int CanvasTopY
    {
        get { lock (_sync) { return _canvasTopY; } }
    }

    public bool MaxHeightReached { get; private set; }

    public string LastCanvasAction { get; private set; }

    public void Initialize(Bitmap firstFrame)
    {
        if (firstFrame == null) throw new ArgumentNullException(nameof(firstFrame));
        lock (_sync)
        {
            ThrowIfDisposed();
            ClearStripsLocked();
            _strips.Add(new StitchedStrip(firstFrame, 0));
            _totalHeight = firstFrame.Height;
            _currentViewportY = 0;
            _canvasTopY = 0;
            _canvasBottomY = firstFrame.Height;
            _viewportHeight = firstFrame.Height;
            MaxHeightReached = _totalHeight >= _maxTotalHeightPx;
            LastCanvasAction = "初始化画布";
        }
    }

    public bool TryApplyFrame(Bitmap currentFrame, int newViewportY, bool directionIsUp, int staticTop, int staticBottom, out bool positionUpdated)
    {
        positionUpdated = false;
        if (currentFrame == null) return false;

        try
        {
            int viewH = currentFrame.Height;
            int viewTop = newViewportY;
            int viewBottom = newViewportY + viewH;
            bool stitched = false;

            lock (_sync)
            {
                ThrowIfDisposed();
                if (_totalHeight <= 0 || _strips.Count == 0) return false;
                if (_viewportHeight <= 0) _viewportHeight = viewH;

                if (viewBottom <= viewTop)
                {
                    LastCanvasAction = "跳过可疑坐标";
                    return false;
                }

                int topOverflow = _canvasTopY - viewTop;
                int bottomOverflow = viewBottom - _canvasBottomY;

                if (topOverflow > 0 && bottomOverflow > 0)
                {
                    LastCanvasAction = "跳过可疑坐标";
                    return false;
                }

                int remaining = _maxTotalHeightPx - _totalHeight;
                if (remaining <= 0)
                {
                    MaxHeightReached = true;
                    LastCanvasAction = "达到最大高度";
                    return false;
                }

                if (topOverflow <= 0 && bottomOverflow <= 0)
                {
                    _currentViewportY = newViewportY;
                    LastCanvasAction = "回看已覆盖区域";
                    positionUpdated = true;
                    return false;
                }

                staticTop = Math.Clamp(staticTop, 0, currentFrame.Height);
                staticBottom = Math.Clamp(staticBottom, 0, currentFrame.Height - staticTop);

                if (topOverflow > 0)
                {
                    stitched = AddTopOverflowLocked(currentFrame, newViewportY, topOverflow, remaining, staticTop, staticBottom, out positionUpdated);
                }
                else if (bottomOverflow > 0)
                {
                    stitched = AddBottomOverflowLocked(currentFrame, newViewportY, bottomOverflow, remaining, staticTop, staticBottom, out positionUpdated);
                }
            }

            return stitched;
        }
        catch
        {
            LastCanvasAction = "跳过可疑坐标";
            return false;
        }
    }

    public List<StitchedStrip> SnapshotStrips()
    {
        lock (_sync)
        {
            return new List<StitchedStrip>(_strips);
        }
    }

    public int CalculateCompositedHeight(int fallbackHeight)
    {
        lock (_sync)
        {
            int byStrips = LongScrollComposer.CalculateCompositedHeight(_strips, fallbackHeight);
            return Math.Max(_totalHeight, byStrips);
        }
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

    private bool AddTopOverflowLocked(Bitmap currentFrame, int newViewportY, int topOverflow, int remaining, int staticTop, int staticBottom, out bool positionUpdated)
    {
        positionUpdated = false;
        int addH = Math.Min(topOverflow, remaining);
        int dynBottom = currentFrame.Height - staticBottom;
        if (dynBottom < staticTop) dynBottom = staticTop;
        if (addH > dynBottom - staticTop) addH = dynBottom - staticTop;
        if (addH <= 0)
        {
            _currentViewportY = newViewportY;
            LastCanvasAction = "顶部无有效新增";
            positionUpdated = true;
            return false;
        }

        int srcY = staticTop;
        if (srcY + addH > currentFrame.Height) addH = currentFrame.Height - srcY;
        if (addH <= 0) return false;

        var stripBitmap = currentFrame.Clone(new Rectangle(0, srcY, currentFrame.Width, addH), currentFrame.PixelFormat);
        foreach (StitchedStrip strip in _strips)
        {
            strip.OffsetY(addH);
        }
        _strips.Insert(0, new StitchedStrip(stripBitmap, 0));
        _totalHeight += addH;
        _canvasTopY -= addH;
        _currentViewportY = newViewportY;
        if (_totalHeight >= _maxTotalHeightPx) MaxHeightReached = true;
        LastCanvasAction = "补充顶部 " + addH + "px";
        return true;
    }

    private bool AddBottomOverflowLocked(Bitmap currentFrame, int newViewportY, int bottomOverflow, int remaining, int staticTop, int staticBottom, out bool positionUpdated)
    {
        positionUpdated = false;
        int addH = Math.Min(bottomOverflow, remaining);
        int dynBottom = currentFrame.Height - staticBottom;
        int srcY = dynBottom - addH;
        if (srcY < staticTop) srcY = staticTop;
        if (srcY + addH > dynBottom) addH = dynBottom - srcY;
        if (addH <= 0)
        {
            _currentViewportY = newViewportY;
            LastCanvasAction = "底部无有效新增";
            positionUpdated = true;
            return false;
        }
        if (srcY + addH > currentFrame.Height) addH = currentFrame.Height - srcY;
        if (addH <= 0) return false;

        int cropEnd = currentFrame.Height;
        int viewForOverlay = _viewportHeight > 0 ? _viewportHeight : currentFrame.Height;
        int overlayH = viewForOverlay / 3 - addH;
        if (overlayH < 0) overlayH = 0;
        int minOverlayForFixedBottom = staticBottom > 0 ? staticBottom + 12 : 0;
        if (overlayH < minOverlayForFixedBottom) overlayH = minOverlayForFixedBottom;

        int maxOverlay = cropEnd - staticTop - addH;
        if (maxOverlay < 0) maxOverlay = 0;
        if (overlayH > maxOverlay) overlayH = maxOverlay;

        int cropH = addH + overlayH;
        int cropY = cropEnd - cropH;
        if (cropY < staticTop)
        {
            cropY = staticTop;
            cropH = cropEnd - cropY;
        }
        if (cropY + cropH > currentFrame.Height) cropH = currentFrame.Height - cropY;
        if (cropH <= 0) return false;
        if (overlayH > cropH - addH) overlayH = cropH - addH;
        if (overlayH < 0) overlayH = 0;

        var stripBitmap = currentFrame.Clone(new Rectangle(0, cropY, currentFrame.Width, cropH), currentFrame.PixelFormat);
        int y = _totalHeight - overlayH;
        _strips.Add(new StitchedStrip(stripBitmap, y, overlayH));
        _totalHeight += addH;
        _canvasBottomY += addH;
        _currentViewportY = newViewportY;
        if (_totalHeight >= _maxTotalHeightPx) MaxHeightReached = true;
        LastCanvasAction = "追加底部 " + addH + "px";
        return true;
    }

    private void ClearStripsLocked()
    {
        for (int i = 0; i < _strips.Count; i++)
        {
            try { _strips[i].Dispose(); } catch { }
        }
        _strips.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LongScrollCanvasState));
        }
    }
}
