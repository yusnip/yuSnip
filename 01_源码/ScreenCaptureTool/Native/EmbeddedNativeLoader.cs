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
    /// 背景：发布为 NativeAOT 单 exe 后，第三方 native dll（Skia/HarfBuzz/ANGLE/
    /// OpenCvSharpExtern/onnxruntime）以及一整套 VC++ 运行库（CRT）必须随 exe 分发。
    /// 本类把它们以 EmbeddedResource 形式打进 exe（LogicalName 前缀 <c>yuSnip.Native.</c>），
    /// 进程启动时释放到配置目录的 native 子目录并完成加载，使分发产物只剩单个 exe。
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

        /// <summary>随 exe 分发的第三方 native dll。</summary>
        private static readonly string[] ThirdPartyFileNames =
        {
            "libSkiaSharp.dll",
            "libHarfBuzzSharp.dll",
            "av_libglesv2.dll",
            "OpenCvSharpExtern.dll",
            "onnxruntime.dll",
        };

        /// <summary>
        /// VC++ 运行库（CRT）app-local 副本，按依赖顺序排列（vcruntime 系在前，msvcp 系在后）。
        ///
        /// 为什么必须我们自己带一份：onnxruntime.dll 静态导入 MSVCP140 / MSVCP140_1 /
        /// VCRUNTIME140 / VCRUNTIME140_1，而不少机器的运行库处于"半更新"混装态——
        /// 旧版 redist 的 msvcp140/vcruntime140 没被新版覆盖，却补上了新版的卫星模块
        /// msvcp140_1（VS2017 15.3 才引入）。新卫星挂在老核心上，CRT 自初始化在
        /// DllMain 里直接失败，LoadLibrary 报 Win32 1114（ERROR_DLL_INIT_FAILED），
        /// 表现为"截图/贴图全正常，只有 OCR 挂"（Skia/OpenCV 那些是静态链接 CRT 的）。
        ///
        /// 生效原理：Windows 按模块基名去重，谁先进进程模块表，后续任何对同名 dll 的
        /// LoadLibrary（包括 onnxruntime 静态导入的解析）都会命中已加载模块。因此整套 CRT
        /// 必须在 onnxruntime 之前、且在主实例初始化第三方库前抢先载入，才能绕开用户机器的系统副本。
        /// </summary>
        private static readonly string[] VcRuntimeFileNames =
        {
            "vcruntime140.dll",
            "vcruntime140_1.dll",
            "vcruntime140_threads.dll",
            "msvcp140.dll",
            "msvcp140_1.dll",
            "msvcp140_codecvt_ids.dll",
        };

        /// <summary>所有需要释放的 native dll 文件名（CRT 在前，保证释放与预加载顺序一致）。</summary>
        private static readonly string[] NativeFileNames = CombineFileNames();

        private static string[] CombineFileNames()
        {
            var all = new List<string>(VcRuntimeFileNames.Length + ThirdPartyFileNames.Length);
            all.AddRange(VcRuntimeFileNames);
            all.AddRange(ThirdPartyFileNames);
            return all.ToArray();
        }

        /// <summary>需要挂 SetDllImportResolver 的托管程序集简单名。</summary>
        private static readonly string[] ResolverAssemblies = { "OpenCvSharp", "SkiaSharp", "HarfBuzzSharp", "Microsoft.ML.OnnxRuntime" };
        private static readonly HashSet<string> _resolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);

        private static string? _extractDir;
        private static int _initialized; // 0/1 via Interlocked

        /// <summary>
        /// 入口：主实例取得单实例锁后、任何 Avalonia/OpenCvSharp 初始化之前调用；纯 IPC 转发进程不调用。
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
                //    LoadLibrary 对同名 dll 会去重，后续 Avalonia/OpenCvSharpExtern/OnnxRuntime 再 Load 直接命中。
                //    顺序要紧：整套 CRT 必须抢在 onnxruntime 之前进来，否则 onnxruntime 解析静态导入时
                //    会先把系统目录那份（可能正是混装的老核心）拉进进程，app-local 就白带了。
                foreach (string crt in VcRuntimeFileNames)
                {
                    PreLoadFromDisk(crt);
                }
                PreLoadFromDisk("av_libglesv2.dll");
                PreLoadFromDisk("onnxruntime.dll");

                // 4. 给四个托管 wrapper 挂 resolver，拦截它们的 [DllImport]。
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

        internal static IntPtr PreLoadFromDisk(string fileName)
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
                    return IntPtr.Zero;
                }

                // 校验拿到的确实是 app-local 那份。Windows 按模块基名去重：若 System32 里的同名模块
                // 被更早载入，LoadLibraryExW 只会返回那个旧句柄——app-local 版本根本没进进程。
                // CRT 混装故障下这条日志就是判据（否则只能等用户报 1114 再猜）。
                var sb = new System.Text.StringBuilder(1024);
                if (NativeMethods.GetModuleFileNameW(h, sb, (uint)sb.Capacity) != 0)
                {
                    string actual = sb.ToString();
                    if (!string.Equals(Path.GetFullPath(actual), full, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Warn("EmbeddedNativeLoader: " + fileName + " 的 app-local 副本未生效，进程内已存在同名模块 " + actual);
                    }
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
            // 开头即可拿到；个别延迟加载的通过 AssemblyLoad 监听器动态补挂。
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (string simpleName in ResolverAssemblies)
            {
                Assembly? asm = FindAssembly(loaded, simpleName);
                if (asm != null)
                {
                    TrySetResolver(asm, simpleName);
                }
            }

            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
                {
                    try
                    {
                        string? name = args.LoadedAssembly.GetName().Name;
                        if (name != null && Array.IndexOf(ResolverAssemblies, name) >= 0)
                        {
                            TrySetResolver(args.LoadedAssembly, name);
                        }
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                AppLogger.Warn("EmbeddedNativeLoader: 注册 AssemblyLoad 监听失败：" + ex.Message);
            }
        }

        private static void TrySetResolver(Assembly asm, string simpleName)
        {
            lock (_resolvedAssemblies)
            {
                if (_resolvedAssemblies.Contains(simpleName)) return;
                try
                {
                    NativeLibrary.SetDllImportResolver(asm, ResolveDllImport);
                    _resolvedAssemblies.Add(simpleName);
                    AppLogger.Info("EmbeddedNativeLoader: SetDllImportResolver 注册成功 -> " + simpleName);
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
