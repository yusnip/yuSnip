using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform;

public static partial class NativeMethods
{
    // Media Foundation 使用裸 COM 指针，避免 NativeAOT 的内置 COM 封送。
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out nint attributes, uint initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out nint mediaType);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint size, out nint buffer);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string url, nint byteStream, nint attributes, out nint writer);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
    [DllImport("ole32.dll", EntryPoint = "CoInitializeEx", ExactSpelling = true)]
    public static extern int MediaCoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", EntryPoint = "CoUninitialize", ExactSpelling = true)]
    public static extern void MediaCoUninitialize();
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StretchBlt(nint dest, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);
    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorInfo(ref RecordingCursorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetIconInfo(nint icon, out RecordingIconInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height,
        uint step, nint brush, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    public struct RecordingCursorInfo
    {
        public uint Size, Flags;
        public nint Cursor;
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct RecordingIconInfo
    {
        public int IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }
}
