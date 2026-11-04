using System;
using System.Collections.Generic;
using System.Linq;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 标注历史簿记：图元 + 文字层两份状态一起进撤销/重做栈，<b>只管模型不管像素</b>。
///
/// <para>从 <see cref="StickerAnnotationSession"/> 里原样上提（2026-09-22），使录屏标注能用同一份
/// 提交语义：贴图侧每次落历史要重烘成品位图，录屏侧画布是活的（ffmpeg 直接抓屏），两边只差
/// <paramref name="rebuild"/> 这一个委托。历史算法（谁压栈、谁出栈、上限 100）只留这一处。</para>
///
/// <para><b>提交口径</b>：压栈的是"上一份已提交状态"，故一段连续改动（滚轮微调、拖滑杆、方向键步进）
/// 攒到收口时落一次 <see cref="Commit"/> 即等于"一段一条撤销"。宿主侧的攒档闸门在
/// <see cref="AnnotationEditSession"/>。</para>
/// </summary>
internal sealed class AnnotationEditHistory
{
    internal sealed record Snapshot(IReadOnlyList<AnnotationShape> Shapes,
        IReadOnlyList<TextLayerSnapshot> Texts);

    /// <summary>历史变动后重建一次显示内容；录屏侧没有成品位图，传 null＝整段跳过。</summary>
    private readonly Action? _rebuild;
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private Snapshot _committed = new(Array.Empty<AnnotationShape>(), Array.Empty<TextLayerSnapshot>());

    public AnnotationEditHistory(Action? rebuild = null)
    {
        _rebuild = rebuild;
    }

    public AnnotationDocument Document { get; } = new();
    public IReadOnlyList<TextLayerSnapshot> Texts { get; private set; } = Array.Empty<TextLayerSnapshot>();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>文字快照的属性均为 init；复制集合即可隔离宿主的后续编辑。</summary>
    public void SetTextSnapshots(IEnumerable<TextLayerSnapshot> texts)
        => Texts = texts.ToArray();

    public void Commit()
    {
        var next = Capture();
        try
        {
            _rebuild?.Invoke();
        }
        catch
        {
            Restore(_committed);
            throw;
        }
        Push(_undo, _committed);
        _redo.Clear();
        _committed = next;
    }

    /// <summary>
    /// 只推历史、不重建显示内容。给文本层用：文本是活可视树，编辑期由它自己画，而贴图侧的重建
    /// 每次都要克隆整张干净底图并重渲染全部图元——字号/透明度滑杆一次拖动就是几十次 tick，
    /// 按 <see cref="Commit"/> 的口径会把几十次整图重绘压在手指移动的每一帧上。
    /// ⚠ 代价是成品位图此后可能是旧的：贴图侧所有取成品的入口都先调一次重建。
    /// </summary>
    public void CommitHistoryOnly()
    {
        var next = Capture();
        Push(_undo, _committed);
        _redo.Clear();
        _committed = next;
    }

    public bool Undo() => MoveHistory(_undo, _redo);
    public bool Redo() => MoveHistory(_redo, _undo);

    public void ClearAnnotations()
    {
        if (Document.Count == 0 && Texts.Count == 0) return;
        Document.Clear();
        Texts = Array.Empty<TextLayerSnapshot>();
        Commit();
    }

    private bool MoveHistory(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0) return false;
        var target = from[^1];
        Restore(target);
        try { _rebuild?.Invoke(); }
        catch { Restore(_committed); throw; }
        from.RemoveAt(from.Count - 1);
        Push(to, _committed);
        _committed = target;
        return true;
    }

    private Snapshot Capture() => new(Document.CreateSnapshot(), Texts.ToArray());

    private void Restore(Snapshot snapshot)
    {
        Document.ReplaceAll(snapshot.Shapes);
        Texts = snapshot.Texts;
    }

    private static void Push(List<Snapshot> history, Snapshot snapshot)
    {
        history.Add(snapshot);
        if (history.Count > 100) history.RemoveAt(0);
    }
}
