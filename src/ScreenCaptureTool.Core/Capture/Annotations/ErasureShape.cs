using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>一次擦除手势的笔尖区域；矩形模式只有一项，画笔模式沿轨迹累积圆形项。</summary>
public readonly record struct ErasureMark(RectangleF Bounds, bool IsCircle);

/// <summary>
/// 擦除只作用于起手时已经存在的图元与文字。后画的内容不受旧擦除区域影响；
/// 作为文档图元保存，原有的克隆、撤销及贴图存档可共用同一份状态。
/// </summary>
public sealed class ErasureShape : AnnotationShape
{
    private readonly Guid[] _targetShapes;
    private readonly Guid[] _targetTexts;
    private readonly ErasureMark[] _marks;

    public ErasureShape(IEnumerable<Guid> targetShapes, IEnumerable<Guid> targetTexts,
        IEnumerable<ErasureMark> marks, Guid id = default) : base(id)
    {
        _targetShapes = targetShapes?.Distinct().ToArray() ?? Array.Empty<Guid>();
        _targetTexts = targetTexts?.Distinct().ToArray() ?? Array.Empty<Guid>();
        _marks = marks?.ToArray() ?? Array.Empty<ErasureMark>();
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Erasure;
    public IReadOnlyList<Guid> TargetShapes => _targetShapes;
    public IReadOnlyList<Guid> TargetTexts => _targetTexts;
    public IReadOnlyList<ErasureMark> Marks => _marks;
    public bool AffectsShape(Guid id) => Array.IndexOf(_targetShapes, id) >= 0;
    public bool AffectsText(Guid id) => Array.IndexOf(_targetTexts, id) >= 0;

    public override RectangleF Bounds
    {
        get
        {
            if (_marks.Length == 0) return RectangleF.Empty;
            RectangleF bounds = _marks[0].Bounds;
            for (int i = 1; i < _marks.Length; i++) bounds = RectangleF.Union(bounds, _marks[i].Bounds);
            return bounds;
        }
    }

    public ErasureShape WithMarks(IEnumerable<ErasureMark> marks)
        => new(_targetShapes, _targetTexts, marks, Id);

    /// <summary>
    /// 把某个图元/文字层从这份擦除记录的"影响名单"里摘出去＝让它浮到橡皮擦之上（2026-10-06 的 Ctrl+点击）。
    /// 洞的形状与位置一律不动，改的只是"这个洞管谁"：撤销一步就把它放回洞里，
    /// 预览与导出都按同一份名单算，天然一致（不存在"屏上有成品没有"）。
    /// 名单里本来就没有它时返回 null，调用方据此免做无谓的替换。
    /// </summary>
    public ErasureShape? WithoutTargets(Guid shapeId, Guid textId)
    {
        Guid[] shapes = Without(_targetShapes, shapeId);
        Guid[] texts = Without(_targetTexts, textId);
        if (ReferenceEquals(shapes, _targetShapes) && ReferenceEquals(texts, _targetTexts)) return null;
        return new ErasureShape(shapes, texts, _marks, Id);
    }

    private static Guid[] Without(Guid[] source, Guid id)
        => id == Guid.Empty || Array.IndexOf(source, id) < 0
            ? source
            : source.Where(x => x != id).ToArray();

    public override AnnotationShape Clone() => new ErasureShape(_targetShapes, _targetTexts, _marks, Id);
}
