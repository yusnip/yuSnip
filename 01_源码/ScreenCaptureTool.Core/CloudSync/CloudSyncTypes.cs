using System;

namespace ScreenCaptureTool.Core.CloudSync
{
    /// <summary>
    /// 云同步操作结果。
    /// </summary>
    public sealed class CloudSyncResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public bool AuthFailed { get; set; }
        public bool NotFound { get; set; }
        public string Data { get; set; } = "";

        /// <summary>恢复的仅是只含 settings 段的 v1 老信封：动作表与内置快捷键保持本机原状，调用方需如实提示。</summary>
        public bool LegacyEnvelope { get; set; }
        /// <summary>本次恢复确实覆盖了 actions.ini 或 shortcuts.ini，调用方据此重注册全局热键。</summary>
        public bool SidecarsRestored { get; set; }

        public static CloudSyncResult Success(string data = "") => new() { Ok = true, Data = data };
        public static CloudSyncResult Fail(string error, bool authFailed = false, bool notFound = false)
            => new() { Ok = false, Error = error, AuthFailed = authFailed, NotFound = notFound };
    }

    /// <summary>
    /// Google Drive 连接状态信息。
    /// </summary>
    public sealed class GoogleDriveStatus
    {
        public bool Connected { get; set; }
        public string UserEmail { get; set; } = "";
        public string UserAvatar { get; set; } = "";
        public string FileId { get; set; } = "";
        public long LastSyncUnixMs { get; set; }

        public DateTime? LastSyncTime =>
            LastSyncUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(LastSyncUnixMs).LocalDateTime
                : null;
    }
}
