namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 开机自启方式协调器：按「管理员模式 + 开机启动」组合把自启挂到正确的载体上。
    /// <list type="bullet">
    /// <item>管理员模式 + 开机启动 → 计划任务（登录免 UAC 提权启动），并关闭注册表 Run 键防止双启动；</item>
    /// <item>其余情况 → 注册表 Run 键（普通自启），并在有权限时清理残留的提权计划任务。</item>
    /// </list>
    /// 本方法幂等，可在应用启动、设置保存、管理员模式切换时反复调用。
    /// </summary>
    public static class AutostartCoordinator
    {
        /// <summary>
        /// 串行化 <see cref="Apply"/> 的全部调用点（应用启动、设置页保存、权限切换协调器共 6 处）。
        ///
        /// <para><b>为什么需要</b>：<see cref="ScheduledTaskService"/> 每次查询/注册/删除都会派生一个
        /// <c>schtasks.exe</c> 并阻塞等待其输出。启动路径把 <c>Apply</c> 挪到后台线程后，
        /// 若用户在这期间打开设置页切换「开机启动」，两个 <c>Apply</c> 会并发跑，
        /// 对同一个计划任务做「查询 → 删除 / 注册」的交错操作，结果不可预测。</para>
        ///
        /// <para>锁放在协调器内部而非各调用方：调用方有 6 处且分散在受保护的权限切换决策树里，
        /// 逐处加锁必漏；单点串行化对所有调用方一次性生效，且不改变任何一处决策逻辑。</para>
        /// </summary>
        private static readonly object ApplyLock = new object();

        /// <summary>按当前设置维护自启载体（计划任务 / 注册表 Run 键）。</summary>
        public static bool Apply(AppSettings settings)
        {
            if (settings == null) return false;

            lock (ApplyLock)
            {
                return ApplyCore(settings);
            }
        }

        private static bool ApplyCore(AppSettings settings)
        {
            bool elevated = ElevationHelper.IsElevated();
            bool wantAdminStartup = settings.RunAsAdmin && settings.RunOnStartup;

            if (elevated)
            {
                if (wantAdminStartup)
                {
                    if (ScheduledTaskService.EnsureRegistered())
                    {
                        // 计划任务已接管自启：关闭 Run 键，避免开机时两个实例抢单实例锁
                        return StartupRegistry.Apply(false);
                    }
                    // 计划任务注册失败（极少见）：退回 Run 键，至少保住普通权限自启
                    AppLogger.Warn("AutostartCoordinator: 计划任务注册失败，开机自启退回注册表 Run 键（不提权）");
                    StartupRegistry.Apply(true);
                    return false;
                }

                // 非管理员组合：清理残留的提权计划任务，Run 键按设置开关
                if (ScheduledTaskService.IsRegistered())
                {
                    if (!ScheduledTaskService.Remove()) return false;
                }
                return StartupRegistry.Apply(settings.RunOnStartup);
            }

            // 普通权限实例不能创建提权任务；已有任务接管自启，关闭时尝试清理并返回实际结果。
            bool taskRegistered = ScheduledTaskService.IsRegistered();
            if (wantAdminStartup && taskRegistered)
            {
                // 提权计划任务已存在并接管自启：关闭 Run 键防止双启动
                return StartupRegistry.Apply(false);
            }
            else
            {
                // 普通实例也要尝试清理旧任务；权限不足时报告失败，不能把仍会自启误报为关闭成功。
                if (taskRegistered && !ScheduledTaskService.Remove()) return false;
                return StartupRegistry.Apply(settings.RunOnStartup);
            }
        }
    }
}
