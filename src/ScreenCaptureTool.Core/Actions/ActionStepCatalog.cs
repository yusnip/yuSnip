using System;
using System.Collections.Generic;
using System.Linq;
using ScreenCaptureTool.Core.Scripting;

namespace ScreenCaptureTool.Core.Actions;

/// <summary>
/// 动作步骤目录门面：对外兼容执行脚本规范化与列表摘要展示，底层委托给 Schema 驱动的 <see cref="ActionModuleRegistry"/>。
/// </summary>
public static class ActionStepCatalog
{
    public const string DefaultScript = ActionModuleRegistry.DefaultScript;

    /// <summary>能完整回读的旧自定义组合动作升级为共享内容流程；预设与无法转换的高级指令保持原样。</summary>
    public static string ExecutionScript(ActionDefinition action) => !action.IsBuiltin
        && ScriptExecutor.TryParse(action.Script, out var calls, out _, validateOrder: false)
        && calls.Count(call => !call.IsWait) > 1
        && ActionModuleRegistry.TryParseSteps(action.Script, out var steps)
            ? ActionModuleRegistry.BuildScript(steps)
            : action.Script;

    /// <summary>在设置页动作列表中展示简洁直观的中文步骤摘要。</summary>
    public static string Summary(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return "空动作";
        var preset = ActionsStore.FindPresetByScript(script);
        if (preset != null) return preset.Value.Name;

        if (ActionModuleRegistry.TryParseSteps(script, out var steps) && steps.Count > 0)
            return string.Join(" → ", steps.Select(ActionModuleRegistry.DescribeStep));

        return "自定义脚本";
    }
}
