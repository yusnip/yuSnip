using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.LongScroll.Diagnostics;

internal static class LongScrollMemoryDiagnostics
{
    private const int GrGdiObjects = 0;
    private const int GrUserObjects = 1;

    public static void Log(string stage)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            long workingSet = process.WorkingSet64;
            long privateBytes = process.PrivateMemorySize64;
            long managed = GC.GetTotalMemory(false);
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            long heap = gcInfo.HeapSizeBytes;
            long fragmented = gcInfo.FragmentedBytes;
            int handles = 0;
            int threads = 0;
            int gdi = 0;
            int user = 0;

            try { handles = process.HandleCount; } catch { }
            try { threads = process.Threads.Count; } catch { }
            try { gdi = GetGuiResources(process.Handle, GrGdiObjects); } catch { }
            try { user = GetGuiResources(process.Handle, GrUserObjects); } catch { }

            AppLogger.Info(
                "MEM " + stage
                + " WS=" + ToMb(workingSet)
                + " Private=" + ToMb(privateBytes)
                + " Managed=" + ToMb(managed)
                + " Heap=" + ToMb(heap)
                + " Frag=" + ToMb(fragmented)
                + " GDI=" + gdi
                + " USER=" + user
                + " Handles=" + handles
                + " Threads=" + threads);
        }
        catch (Exception ex)
        {
            try { AppLogger.Warn("MEM " + stage + " 记录失败：" + ex.Message); } catch { }
        }
    }

    public static void LogImage(string stage, int width, int height)
    {
        try
        {
            double dibMb = width > 0 && height > 0 ? width * (double)height * 4.0 / 1024.0 / 1024.0 : 0.0;
            AppLogger.Info("MEM " + stage + " Image=" + width + "x" + height + " EstimatedDib=" + dibMb.ToString("F1") + "MB");
        }
        catch { }

        Log(stage);
    }

    private static string ToMb(long bytes)
    {
        return (bytes / 1024.0 / 1024.0).ToString("F1") + "MB";
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);
}
