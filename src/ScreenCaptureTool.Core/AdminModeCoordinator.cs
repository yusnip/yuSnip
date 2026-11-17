using System;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core
{
    /// <summary>管理员模式切换的结果档位。</summary>
    public enum AdminModeOutcome
    {
        /// <summary>即时生效，无需重启（含仅注册/清理计划任务两种情形）。</summary>
        AppliedInPlace,
        /// <summary>同权限或提权接力实例已启动，调用方必须立刻退出本进程把单实例锁交给它。</summary>
        Restarted,
        /// <summary>用户在 UAC 里取消：设置已回滚，进程照常运行。</summary>
        Declined,
        /// <summary>失败：设置已回滚，Error 带原因。</summary>
        Failed,
    }

    /// <summary>切换结果：面向用户的文案由协调器统一给，两个调用方（设置页 / 托盘）措辞必须一致。</summary>
    public sealed class AdminModeResult
    {
        public AdminModeOutcome Outcome { get; init; }
        public string Message { get; init; } = "";
        public string? Error { get; init; }
    }

    /// <summary>
    /// 管理员模式切换的决策树，设置页与托盘共用同一份。
    ///
    /// 三条不可动摇的前提（改动前先读 docs/protection.md）：
    /// 1. **自己 Load + Save 设置实例**，不接收调用方手里的 _settings——否则调用方那份旧实例
    ///    会在下一次自动保存时把 RunAsAdmin 又写回去，开关弹回原位。
    /// 2. **阻塞式 Process.Start（UseShellExecute + Verb）一律包在 Task.Run 里**——
    ///    两个调用方各自包必漏一个，漏的那个 UI 线程直接卡死。
    /// 3. 提权被拒或失败时必须回滚 RunAsAdmin，且**不退出进程**。
    /// </summary>
    public static class AdminModeCoordinator
    {
        /// <summary>
        /// 切换到目标档位。
        /// </summary>
        /// <param name="wantAdmin">true = 开启管理员模式，false = 关闭。</param>
        /// <param name="onBusy">需要用户等待时的提示文案回调（UAC 弹窗前后各一次），可为 null。</param>
        public static async Task<AdminModeResult> ToggleAsync(bool wantAdmin, Action<string>? onBusy = null)
        {
            try
            {
                AppSettings settings = AppSettings.Load();
                settings.RunAsAdmin = wantAdmin;
                settings.Save();
                // 落盘后立即回写进程内共享缓存（AppSettings.Current）：设置窗口与截图会话
                // 的 _settings 都引用该实例，不同步的话它们下一次整体 Save() 会把旧值写回磁盘，
                // 造成"托盘已提权、设置页仍显示关闭"的状态脱节。
                AppSettings.SyncRunAsAdmin(wantAdmin);

                if (wantAdmin)
                {
                    if (ElevationHelper.IsElevated())
                    {
                        // 已在管理员进程内开启：无需重启，直接注册提权自启任务
                        AutostartCoordinator.Apply(settings);
                        return Done("已开启管理员模式");
                    }

                    onBusy?.Invoke("正在请求管理员权限，请在弹窗中确认…");
                    string? error = null;
                    RestartElevatedResult started = await Task.Run(() => ElevationHelper.TryRestartElevated(out error));
                    switch (started)
                    {
                        case RestartElevatedResult.Started:
                            return new AdminModeResult { Outcome = AdminModeOutcome.Restarted };
                        case RestartElevatedResult.Declined:
                            Revert();
                            return new AdminModeResult
                            {
                                Outcome = AdminModeOutcome.Declined,
                                Message = "已取消，仍以普通权限运行",
                            };
                        default:
                            Revert();
                            return new AdminModeResult
                            {
                                Outcome = AdminModeOutcome.Failed,
                                Error = error ?? "未知原因",
                            };
                    }
                }

                // 关闭：上面已落盘 false；普通权限且无残留计划任务时即时生效，无需重启
                if (ElevationHelper.IsElevated())
                {
                    AutostartCoordinator.Apply(settings); // 清理提权计划任务、恢复 Run 键
                    onBusy?.Invoke("正在退出管理员模式…");
                    if (await Task.Run(() => ElevationHelper.TryRestartDeElevated()))
                    {
                        return new AdminModeResult { Outcome = AdminModeOutcome.Restarted };
                    }
                    return Done("已关闭管理员模式，重启软件后回到普通权限");
                }

                if (ScheduledTaskService.IsRegistered())
                {
                    // 残留的提权计划任务：任务属主是当前用户，普通权限通常也能直接删除
                    if (ScheduledTaskService.Remove())
                    {
                        AutostartCoordinator.Apply(settings);
                        return Done("已关闭管理员模式");
                    }

                    // 删除任务需要管理员权限：接力提权完成清理，新实例清理后会自动降回普通模式
                    onBusy?.Invoke("正在清理管理员自启任务，请在弹窗中确认…");
                    string? cleanupError = null;
                    RestartElevatedResult cleanup = await Task.Run(() => ElevationHelper.TryRestartElevated(out cleanupError));
                    if (cleanup == RestartElevatedResult.Started)
                    {
                        return new AdminModeResult { Outcome = AdminModeOutcome.Restarted };
                    }
                    return Done("已关闭管理员模式；自启任务本次未清理，下次开机时会自动完成");
                }

                AutostartCoordinator.Apply(settings);
                return Done("已关闭管理员模式");
            }
            catch (Exception ex)
            {
                AppLogger.Error("AdminModeCoordinator.ToggleAsync 失败", ex);
                return new AdminModeResult { Outcome = AdminModeOutcome.Failed, Error = ex.Message };
            }
        }

        /// <summary>
        /// 外部（托盘）改完 RunAsAdmin 后，把实际权限状态推给已打开的设置窗口回显。
        /// 只读不写：这里的"同步"指让界面追上落盘值，绝不触发切换。
        /// </summary>
        public static bool ReadRunAsAdmin()
        {
            try
            {
                return AppSettings.Load().RunAsAdmin;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AdminModeCoordinator.ReadRunAsAdmin 失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>回滚到"未开启管理员模式"并落盘（UAC 被拒 / 提权失败时用）。</summary>
        private static void Revert()
        {
            AppSettings settings = AppSettings.Load();
            settings.RunAsAdmin = false;
            settings.Save();
            AppSettings.SyncRunAsAdmin(false);
        }

        private static AdminModeResult Done(string message)
            => new AdminModeResult { Outcome = AdminModeOutcome.AppliedInPlace, Message = message };
    }
}
