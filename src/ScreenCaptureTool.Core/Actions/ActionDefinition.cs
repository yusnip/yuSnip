using System;

namespace ScreenCaptureTool.Core.Actions
{
    /// <summary>
    /// 动作定义：一个可被全局快捷键或外部命令行（<c>yuSnip.exe -a "名称"</c>）触发的命名脚本。
    ///
    /// 内置动作（<see cref="IsBuiltin"/> = true）：
    /// - 不可删除、脚本内容固定（加载时强制还原为 canonical 脚本，防止被改坏）；
    /// - 名称/快捷键/启用状态可由用户修改；
    /// - Id 以 "builtin." 开头，跨版本稳定（新增内置动作在加载时自动补录）。
    ///
    /// 自定义动作：Id 为 "custom." + GUID，全部字段可编辑。
    /// </summary>
    public sealed class ActionDefinition
    {
        /// <summary>稳定标识。内置动作为 "builtin.xxx"，自定义动作为 "custom.&lt;guid&gt;"。</summary>
        public string Id = "";

        /// <summary>显示名称（动作列表、外部调用 -a 参数均使用）。</summary>
        public string Name = "";

        /// <summary>脚本指令（可多行），由 ScriptExecutor 执行。</summary>
        public string Script = "";

        /// <summary>内置动作不可删除、脚本不可修改。</summary>
        public bool IsBuiltin;

        /// <summary>原生修饰键低四位，附加 CapsLock／Tab／Space 使用 ActionKeyboardBinding 高位。</summary>
        public uint HotKeyModifiers;

        /// <summary>热键虚拟键码。与 <see cref="HotKeyModifiers"/> 同时为 0 表示未绑定热键。</summary>
        public uint HotKeyVirtualKey;

        /// <summary>全局鼠标独立绑定；必须包含辅助键或一个引导键。</summary>
        public uint MouseModifiers;
        public ActionMouseButton MouseButton;
        public ActionMouseTrigger MouseTrigger;
        public ActionMouseKey MousePrefixKey;

        public bool HasMouseBinding => MouseModifiers <= 15
            && (MousePrefixKey.IsValid || (MousePrefixKey == default && MouseModifiers > 0))
            && MouseButton is >= ActionMouseButton.Left and <= ActionMouseButton.X2
            && MouseTrigger is ActionMouseTrigger.Click or ActionMouseTrigger.Drag;

        /// <summary>是否启用。禁用的动作不注册热键、拒绝外部调用。</summary>
        public bool Enabled = true;

        /// <summary>是否绑定了热键。</summary>
        public bool HasHotKey => HotKeyVirtualKey != 0;

        public ActionDefinition Clone()
        {
            return new ActionDefinition
            {
                Id = Id,
                Name = Name,
                Script = Script,
                IsBuiltin = IsBuiltin,
                HotKeyModifiers = HotKeyModifiers,
                HotKeyVirtualKey = HotKeyVirtualKey,
                MouseModifiers = MouseModifiers,
                MouseButton = MouseButton,
                MouseTrigger = MouseTrigger,
                MousePrefixKey = MousePrefixKey,
                Enabled = Enabled,
            };
        }

        /// <summary>为自定义动作生成新 Id。</summary>
        public static string NewCustomId()
        {
            return "custom." + Guid.NewGuid().ToString("N");
        }
    }
}
