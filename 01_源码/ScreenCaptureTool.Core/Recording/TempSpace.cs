using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>
    /// 临时工作目录管理（计划 §3.6）。根目录 %TEMP%\yuSnip\rec\&lt;yyyyMMdd-HHmmss&gt;\。
    /// 保存成功→30 秒后删整目录；放弃→立即删；意外中断→保留供手动恢复；启动清扫超 24h 的残留。
    /// </summary>
    public static class TempSpace
    {
        public static string Root => Path.Combine(Path.GetTempPath(), "yuSnip", "rec");

        /// <summary>为一次新会话创建工作目录，返回其绝对路径。</summary>
        public static string CreateSessionDir()
        {
            string dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            int n = 1;
            while (Directory.Exists(dir))
                dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + n++);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>立即删除会话目录（放弃场景）。</summary>
        public static void DeleteNow(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        /// <summary>延迟删除（保存成功场景）。异步等待 30s 后尽力删，失败静默。</summary>
        public static async void DeleteDelayed(string dir, int delayMs = 30_000)
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }

        /// <summary>
        /// 应用启动清扫：删 mtime 超 24h 的 rec\* 残留目录。
        /// 30 分钟内的目录（可能是进行中被中断会话）跳过不清（D23 丙）。
        /// </summary>
        public static void SweepOnStartup()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                DateTime now = DateTime.Now;
                foreach (string dir in Directory.GetDirectories(Root))
                {
                    try
                    {
                        DateTime mtime = Directory.GetLastWriteTime(dir);
                        double ageHours = (now - mtime).TotalHours;
                        if (ageHours > 24)
                            Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
