using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenCaptureTool.Core;

/// <summary>工具栏场景只管理排列与显示，不接管宿主的命令和业务状态。</summary>
public enum ToolbarScene { Capture, StickerAnnotation, RecordPreparation, RecordControl, VideoPreview, LongScroll }

public sealed class ToolbarSceneDefinition
{
    public ToolbarScene Scene { get; }
    public string Name { get; }
    public string DefaultLayout { get; }
    public IReadOnlyList<ToolbarButtonInfo> Buttons { get; }
    private readonly string[] _required;
    private readonly string[] _atLeastOne;

    internal ToolbarSceneDefinition(ToolbarScene scene, string name, string layout, string[] required, string[] atLeastOne)
    {
        Scene = scene; Name = name; DefaultLayout = layout; _required = required; _atLeastOne = atLeastOne;
        Buttons = layout.Split(',').Where(id => id != "|").Distinct()
            .Select(id => scene == ToolbarScene.RecordControl && id == "finish"
                ? new ToolbarButtonInfo { Id = id, Name = "结束录制" } : ToolbarScenes.Find(id)).ToArray();
    }

    public ToolbarButtonInfo? Find(string id) => Buttons.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
    public bool CanGroup(string id) => Scene == ToolbarScene.Capture ? !ToolbarRegistry.IsNoGroupId(id) : !_required.Contains(id);
    public List<string> GroupMembers(string? token) => ToolbarRegistry.GroupMembers(token, Find);
    public string MakeGroupToken(IEnumerable<string> members) => ToolbarRegistry.MakeGroupToken(members, Find);
    public IEnumerable<string> ToolIds(IEnumerable<string> tokens) => tokens.SelectMany(t => ToolbarRegistry.IsGroupToken(t) ? (IEnumerable<string>)GroupMembers(t) : new[] { t }).Where(t => t != "|");
    public bool HasId(IEnumerable<string> tokens, string id) => ToolIds(tokens).Contains(id, StringComparer.OrdinalIgnoreCase);
    public bool CanHide(string id, IEnumerable<string> tokens)
    {
        var removed = ToolIds(new[] { id }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !removed.Any(_required.Contains) && (!removed.Any(_atLeastOne.Contains)
            || ToolIds(tokens).Any(t => !removed.Contains(t) && _atLeastOne.Contains(t)));
    }
    public List<string> ParseLibrary(string? library, IEnumerable<string>? usedOnBar = null)
    {
        if (Scene == ToolbarScene.Capture) return ToolbarRegistry.ParseLibrary(library);
        var used = ToolIds(usedOnBar ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string token in (library ?? "").Split(','))
        {
            if (!ToolbarRegistry.IsGroupToken(token.Trim())) continue;
            var members = ToolbarRegistry.CleanGroup(GroupMembers(token.Trim()).Where(t => t == "|" || CanGroup(t) && used.Add(t)), Find);
            if (members.Count(t => t != "|") >= ToolbarRegistry.MinGroupMembers) result.Add(MakeGroupToken(members));
        }
        return result;
    }

    public List<string> Parse(string? layout)
    {
        if (Scene == ToolbarScene.Capture) return ToolbarRegistry.Parse(layout);
        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int groups = 0;
        foreach (string raw in (string.IsNullOrWhiteSpace(layout) ? DefaultLayout : layout).Split(','))
        {
            string token = raw.Trim();
            if (token == "|") { if (result.Count > 0 && result[^1] != "|") result.Add(token); }
            else if (ToolbarRegistry.IsGroupToken(token))
            {
                var members = new List<string>(); var exits = new List<string>();
                foreach (string id in GroupMembers(token))
                {
                    if (id == "|") members.Add(id);
                    else if (used.Add(id)) (CanGroup(id) ? members : exits).Add(id);
                }
                members = ToolbarRegistry.CleanGroup(members, Find);
                if (members.Count(t => t != "|") >= ToolbarRegistry.MinGroupMembers && groups < ToolbarRegistry.MaxGroups)
                { result.Add(MakeGroupToken(members)); groups++; }
                else result.AddRange(members);
                result.AddRange(exits);
            }
            else if (token.StartsWith('[') || token.EndsWith(']')) return Parse(DefaultLayout);
            else if (Find(token) is { } info && used.Add(info.Id)) result.Add(info.Id);
        }
        if (!result.Any(t => t != "|")) return Parse(DefaultLayout);
        if (result[^1] == "|") result.RemoveAt(result.Count - 1);
        foreach (string id in _required) if (used.Add(id)) result.Add(id);
        if (_atLeastOne.Length > 0 && !ToolIds(result).Any(_atLeastOne.Contains)) result.Insert(0, _atLeastOne[0]);
        for (int i = result.Count - 1; i >= 0; i--)
            if (result[i] == "|" && (i == 0 || i == result.Count - 1 || result[i - 1] == "|")) result.RemoveAt(i);
        return result;
    }
    public string Normalize(string? layout) => string.Join(",", Parse(layout));
}

public static class ToolbarScenes
{
    private static readonly Dictionary<string, ToolbarButtonInfo> Extras = new(StringComparer.OrdinalIgnoreCase)
    {
        ["clearall"] = new() { Id = "clearall", Name = "清除全部标注" },
        ["quality"] = new() { Id = "quality", Name = "画质" },
        ["countdown"] = new() { Id = "countdown", Name = "倒计时" },
        ["audio"] = new() { Id = "audio", Name = "声音" },
        ["keymouse"] = new() { Id = "keymouse", Name = "键鼠操作" },
        ["fx"] = new() { Id = "fx", Name = "键鼠操作" },
        ["start"] = new() { Id = "start", Name = "开始录制" },
        ["pause"] = new() { Id = "pause", Name = "暂停／继续" },
        ["restart"] = new() { Id = "restart", Name = "重新录制" },
        ["finish"] = new() { Id = "finish", Name = "完成" },
        ["close"] = new() { Id = "close", Name = "关闭" },
        ["copy"] = new() { Id = "copy", Name = "复制" },
        ["playback"] = new() { Id = "playback", Name = "播放控制" },
        ["export"] = new() { Id = "export", Name = "导出选项" },
        ["axis"] = new() { Id = "axis", Name = "滚动轴向" },
        ["autoscroll"] = new() { Id = "autoscroll", Name = "自动滚动" },
        ["trim"] = new() { Id = "trim", Name = "手动裁剪" }
    };
    internal static ToolbarButtonInfo Find(string id) => ToolbarRegistry.Find(id) ?? Extras[id];
    public static readonly ToolbarSceneDefinition[] All =
    [
        new(ToolbarScene.Capture, "截图", ToolbarRegistry.DefaultLayout, [], []),
        new(ToolbarScene.StickerAnnotation, "贴图标注", "mosaic,spotlight,|,brush,arrow,text,rectangle,counter,eraser,clearall,undo", [], ["brush","arrow","text","rectangle","counter","mosaic","spotlight","eraser"]),
        new(ToolbarScene.RecordPreparation, "录屏准备", "quality,|,countdown,|,audio,keymouse,|,start", ["start"], []),
        new(ToolbarScene.RecordControl, "录制中", "fx,|,brush,arrow,text,rectangle,counter,spotlight,eraser,|,clearall,undo,|,pause,restart,finish", ["finish"], []),
        new(ToolbarScene.VideoPreview, "录屏预览", "playback,|,export,|,copy,quicksave,saveas,close", ["close"], ["copy","quicksave","saveas"]),
        new(ToolbarScene.LongScroll, "长截图", "axis,autoscroll,|,trim,sticker,quicksave,saveas,cancel,finish", ["cancel","finish"], [])
    ];
    public static ToolbarSceneDefinition Get(ToolbarScene scene) => All[(int)scene];
    public static string SettingKey(ToolbarScene scene) => scene switch
    {
        ToolbarScene.StickerAnnotation => "StickerAnnotationToolbarLayout",
        ToolbarScene.RecordPreparation => "RecordPreparationToolbarLayout",
        ToolbarScene.RecordControl => "RecordControlToolbarLayout",
        ToolbarScene.VideoPreview => "VideoPreviewToolbarLayout",
        ToolbarScene.LongScroll => "LongScrollToolbarLayout",
        _ => "ToolbarLayout"
    };
    public static string Read(AppSettings settings, ToolbarScene scene) => scene switch
    {
        ToolbarScene.Capture => settings.ToolbarLayout,
        ToolbarScene.StickerAnnotation => settings.StickerAnnotationToolbarLayout,
        ToolbarScene.RecordPreparation => settings.RecordPreparationToolbarLayout,
        ToolbarScene.RecordControl => settings.RecordControlToolbarLayout,
        ToolbarScene.VideoPreview => settings.VideoPreviewToolbarLayout,
        _ => settings.LongScrollToolbarLayout
    };
    public static void Write(AppSettings settings, ToolbarScene scene, string layout)
    {
        string value = Get(scene).Normalize(layout);
        switch (scene)
        {
            case ToolbarScene.Capture: settings.ToolbarLayout = value; break;
            case ToolbarScene.StickerAnnotation: settings.StickerAnnotationToolbarLayout = value; break;
            case ToolbarScene.RecordPreparation: settings.RecordPreparationToolbarLayout = value; break;
            case ToolbarScene.RecordControl: settings.RecordControlToolbarLayout = value; break;
            case ToolbarScene.VideoPreview: settings.VideoPreviewToolbarLayout = value; break;
            case ToolbarScene.LongScroll: settings.LongScrollToolbarLayout = value; settings.LongScrollStickerMigrated = true; break;
        }
    }

    public static string GroupsKey(ToolbarScene scene) => scene == ToolbarScene.Capture ? "ToolbarGroups" : SettingKey(scene).Replace("Layout", "Groups");
    public static string ReadGroups(AppSettings settings, ToolbarScene scene) => scene switch
    {
        ToolbarScene.Capture => settings.ToolbarGroups,
        ToolbarScene.StickerAnnotation => settings.StickerAnnotationToolbarGroups,
        ToolbarScene.RecordPreparation => settings.RecordPreparationToolbarGroups,
        ToolbarScene.RecordControl => settings.RecordControlToolbarGroups,
        ToolbarScene.VideoPreview => settings.VideoPreviewToolbarGroups,
        _ => settings.LongScrollToolbarGroups
    };
    public static void WriteGroups(AppSettings settings, ToolbarScene scene, string library, bool filterOnBar = true)
    {
        string value = string.Join(",", Get(scene).ParseLibrary(library, filterOnBar ? Get(scene).Parse(Read(settings, scene)) : null));
        switch (scene)
        {
            case ToolbarScene.Capture: settings.ToolbarGroups = value; break;
            case ToolbarScene.StickerAnnotation: settings.StickerAnnotationToolbarGroups = value; break;
            case ToolbarScene.RecordPreparation: settings.RecordPreparationToolbarGroups = value; break;
            case ToolbarScene.RecordControl: settings.RecordControlToolbarGroups = value; break;
            case ToolbarScene.VideoPreview: settings.VideoPreviewToolbarGroups = value; break;
            case ToolbarScene.LongScroll: settings.LongScrollToolbarGroups = value; break;
        }
    }

}
