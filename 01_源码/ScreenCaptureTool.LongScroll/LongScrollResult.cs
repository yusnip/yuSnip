using System.Drawing;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// 长截图会话结果。
/// </summary>
public sealed class LongScrollResult : IDisposable
{
    private bool _disposed;

    private LongScrollResult(Bitmap? bitmap, bool canceled, string? savedPath, string? message)
    {
        Bitmap = bitmap;
        Canceled = canceled;
        SavedPath = savedPath;
        Message = message;
    }

    public Bitmap? Bitmap { get; private set; }

    public bool Canceled { get; }

    public string? SavedPath { get; }

    public string? Message { get; }

    public bool HasBitmap => Bitmap != null;

    /// <summary>把成片所有权交给贴图窗口；结果释放时不再释放已交出的位图。</summary>
    public Bitmap? TakeBitmap()
    {
        Bitmap? bitmap = Bitmap;
        Bitmap = null;
        return bitmap;
    }

    public static LongScrollResult FromBitmap(Bitmap bitmap, string? message = null)
    {
        return new LongScrollResult(bitmap ?? throw new ArgumentNullException(nameof(bitmap)), false, null, message);
    }

    public static LongScrollResult Saved(string path, string? message = null)
    {
        return new LongScrollResult(null, false, path, message);
    }

    public static LongScrollResult Cancel(string? message = null)
    {
        return new LongScrollResult(null, true, null, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Bitmap?.Dispose();
        Bitmap = null;
    }
}
