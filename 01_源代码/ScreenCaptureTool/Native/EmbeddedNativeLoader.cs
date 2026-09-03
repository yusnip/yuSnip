using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Native
{
    /// <summary>
    /// 启动期 native dll 内嵌加载器。
    ///
    /// 背景：发布为 NativeAOT 单 exe 后，5 个第三方 native dll（Skia/HarfBuzz/ANGLE/
    /// OpenCvSharpExtern/opencv_videoio_ffmpeg）必须随 exe 分发。本类把它们以
    /// EmbeddedResource 形式打进 exe（LogicalName 前缀 <c>yuSnip.Native.</c>），
    /// 进程启动时释放到 %TEMP%\yuSnip_native 并完成加载，使分发产物只剩单个 exe。
    ///
    /// 加载机制分两类，必须双管齐下：
    ///  1. 通过 [DllImport] 调用的（libSkiaSharp / libHarfBuzzSharp / OpenCvSharpExtern）
    ///     → 用 NativeLibrary.SetDllImportResolver 拦截，按名命中 temp 目录。
    ///  2. 原生→原生 LoadLibrary 的（av_libglesv2 由 Avalonia.Win32 加载；
    ///     opencv_videoio_ffmpeg 由 OpenCvSharpExtern 内部加载）
    ///     → SetDllDirectoryW 把 temp 目录加入进程搜索路径，并主动 LoadLibraryExW 预加载。
    ///
    /// 全程 try/catch 兜底：失败只记日志，不抛异常。即使资源缺失（例如非单 exe 发布），
    /// 让其退回系统默认 dll 搜索路径——此时 dll 仍在 exe 旁，照样能跑。
    /// </summary>
    internal static class EmbeddedNativeLoader
    {
        /// <summary>内嵌资源的 LogicalName 前缀，需与 csproj 中 Target 设置一致。</summary>
        private const string ResourcePrefix = "yuSnip.Native.";

        /// <summary>所有需要释放的 native dll 文件名。</summary>
        private static readonly string[] NativeFileNames =
        {
            "libSkiaSharp.dll",
            "libHarfBuzzSharp.dll",
            "av_libglesv2.dll",
            "OpenCvSharpExtern.dll",
        };

        /// <summary>需要挂 SetDllImportResolver 的托管程序集简单名。</summary>
        private static readonly string[] ResolverAssemblies = { "OpenCvSharp", "SkiaSharp", "HarfBuzzSharp" };

        private static string? _extractDir;
        private static int _initialized; // 0/1 via Interlocked

        /// <summary>
        /// 入口：必须在 Main 第一行、任何 Avalonia/OpenCvSharp 初始化之前调用。
        /// 幂等，多次调用安全。
        /// </summary>
        public static void Initialize()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                _extractDir = ResolveExtractDirectory();

                // 1. 释放内嵌 dll 到 temp 目录（无则跳过，退回默认搜索路径）。
                var extracted = ExtractAll(assembly, _extractDir);
                if (extracted.Count == 0)
                {
                    // 没有内嵌资源——非单 exe 发布或资源未生成，直接退出，走默认机制。
                    return;
                }

                // 2. temp 加入进程 dll 搜索路径：覆盖原生→原生 LoadLibrary 的两个 dll。
                if (!NativeMethods.SetDllDirectoryW(_extractDir))
                {
                    AppLogger.Warn("EmbeddedNativeLoader: SetDllDirectoryW 失败，LastError=" + Marshal.GetLastWin32Error());
                }

                // 3. 预加载无法被托管 resolver 拦截的 dll，让它们先进进程模块表。
                //    LoadLibrary 对同名 dll 会去重，后续 Avalonia/OpenCvSharpExtern 再 Load 直接命中。
                PreLoadFromDisk("av_libglesv2.dll");

                // 4. 给三个托管 wrapper 挂 resolver，拦截它们的 [DllImport]。
                InstallResolvers();

                AppLogger.Info("EmbeddedNativeLoader 初始化完成，释放目录=" + _extractDir);
            }
            catch (Exception ex)
            {
                // 致命兜底：加载器自身崩溃绝不能让主程序起不来。
                AppLogger.Error("EmbeddedNativeLoader 初始化异常", ex);
            }
        }

        // ---------------- 释放 ----------------

        private static string ResolveExtractDirectory()
        {
            // 释放到配置文件夹下的 native 子目录，避免被系统清理工具当成临时垃圾文件清除
            return Path.Combine(AppSettings.SettingsDirectory, "native");
        }

        /// <summary>
        /// 把所有内嵌 native 资源释放到 destDir。已存在且大小相同的文件跳过（快速校验）。
        /// 返回成功释放的文件列表（绝对路径）。
        /// </summary>
        private static List<string> ExtractAll(Assembly assembly, string destDir)
        {
            var done = new List<string>();
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                string[] allNames = assembly.GetManifestResourceNames();
                foreach (string fileName in NativeFileNames)
                {
                    string resourceName = ResourcePrefix + fileName;
                    int idx = Array.IndexOf(allNames, resourceName);
                    if (idx < 0)
                    {
                        // 该资源未内嵌——可能本次构建没开启 EmbedNativeForSingleExe。
                        // 不报错（默认搜索路径仍可工作），仅调试日志。
                        continue;
                    }

                    string destFile = Path.Combine(destDir, fileName);
                    if (NeedsRefresh(assembly, resourceName, destFile))
                    {
                        using Stream? rs = assembly.GetManifestResourceStream(resourceName);
                        if (rs == null) continue;
                        using var gs = new System.IO.Compression.GZipStream(rs, System.IO.Compression.CompressionMode.Decompress);
                        using var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write);
                        gs.CopyTo(fs);
                    }
                    done.Add(destFile);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("EmbeddedNativeLoader 释放资源失败", ex);
            }
            return done;
        }

        /// <summary>如果目标文件不存在，或当前运行的 exe 写入时间新于已释放的 DLL，则需要重新释放。</summary>
        private static bool NeedsRefresh(Assembly assembly, string resourceName, string destFile)
        {
            try
            {
                if (!File.Exists(destFile)) return true;
                
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    if (File.GetLastWriteTime(exePath) > File.GetLastWriteTime(destFile))
                        return true;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        // ---------------- 原生预加载 ----------------

        private static IntPtr PreLoadFromDisk(string fileName)
        {
            try
            {
                if (_extractDir == null) return IntPtr.Zero;
                string full = Path.Combine(_extractDir, fileName);
                if (!File.Exists(full)) return IntPtr.Zero;

                IntPtr h = NativeMethods.LoadLibraryExW(full, IntPtr.Zero, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
                if (h == IntPtr.Zero)
                {
                    AppLogger.Warn("EmbeddedNativeLoader: LoadLibraryExW 失败 " + fileName + "，LastError=" + Marshal.GetLastWin32Error());
                }
                return h;
            }
            catch (Exception ex)
            {
                AppLogger.Error("EmbeddedNativeLoader: 预加载异常 " + fileName, ex);
                return IntPtr.Zero;
            }
        }

        // ---------------- 托管 DllImport 拦截 ----------------

        private static void InstallResolvers()
        {
            // AOT 下所有引用程序集在进程启动即静态加载，AppDomain.CurrentDomain.GetAssemblies()
            // 开头即可拿到三者；个别尚未加载也无妨——后续首次 P/Invoke 时 CLR 走默认搜索路径兜底。
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (string simpleName in ResolverAssemblies)
            {
                Assembly? asm = FindAssembly(loaded, simpleName);
                if (asm == null) continue;
                try
                {
                    NativeLibrary.SetDllImportResolver(asm, ResolveDllImport);
                }
                catch (Exception ex)
                {
                    // 同一程序集重复挂会抛 ArgumentException——幂等保护。
                    AppLogger.Warn("EmbeddedNativeLoader: SetDllImportResolver 失败 " + simpleName + "：" + ex.Message);
                }
            }
        }

        private static Assembly? FindAssembly(Assembly[] loaded, string simpleName)
        {
            foreach (Assembly a in loaded)
            {
                try { if (a.GetName().Name == simpleName) return a; } catch { }
            }
            return null;
        }

        /// <summary>
        /// DllImport 解析回调：把传入的库名映射到 temp 目录下的实际 dll。
        /// 返回 IntPtr.Zero 表示"我不认识，交回 CLR 默认机制"。
        /// </summary>
        private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            try
            {
                if (_extractDir == null) return IntPtr.Zero;

                // libraryName 可能带或不带 .dll 后缀，统一规范化。
                string file = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? libraryName : libraryName + ".dll";
                string full = Path.Combine(_extractDir, file);

                if (File.Exists(full))
                {
                    IntPtr h = NativeMethods.LoadLibraryExW(full, IntPtr.Zero, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (h != IntPtr.Zero) return h;
                }
            }
            catch
            {
                // 落到默认解析。
            }
            return IntPtr.Zero;
        }
    }
}
