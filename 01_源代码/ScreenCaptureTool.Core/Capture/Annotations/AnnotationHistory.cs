using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 标注文档撤销/重做历史。第一阶段使用快照策略，简单可靠。
/// </summary>
public sealed class AnnotationHistory
{
    private readonly AnnotationDocument _document;
    private readonly int _maxSnapshots;
    private readonly Stack<IReadOnlyList<AnnotationShape>> _undo = new Stack<IReadOnlyList<AnnotationShape>>();
    private readonly Stack<IReadOnlyList<AnnotationShape>> _redo = new Stack<IReadOnlyList<AnnotationShape>>();

    public AnnotationHistory(AnnotationDocument document, int maxSnapshots = 100)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _maxSnapshots = Math.Max(1, maxSnapshots);
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// 在即将修改文档前调用，记录当前状态为可撤销快照。
    /// </summary>
    public void CommitSnapshot()
    {
        PushUndo(_document.CreateSnapshot());
        _redo.Clear();
    }

    public bool Undo()
    {
        if (!CanUndo) return false;

        _redo.Push(_document.CreateSnapshot());
        IReadOnlyList<AnnotationShape> previous = _undo.Pop();
        _document.ReplaceAll(previous);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;

        _undo.Push(_document.CreateSnapshot());
        IReadOnlyList<AnnotationShape> next = _redo.Pop();
        _document.ReplaceAll(next);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private void PushUndo(IReadOnlyList<AnnotationShape> snapshot)
    {
        // snapshot 来自 AnnotationDocument.CreateSnapshot()，那里已经完成深拷贝。
        // 这里不再二次 Clone，避免画笔点数较多时每次提交都重复复制整份文档。
        _undo.Push(snapshot);

        if (_undo.Count <= _maxSnapshots) return;

        // Stack 只能从顶部弹出；为丢弃最旧项，反转后截断再重建。
        var items = _undo.Reverse().Skip(1).ToList();
        _undo.Clear();
        foreach (var item in items)
        {
            _undo.Push(item);
        }
    }
}
