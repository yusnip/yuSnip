using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 一张截图上的标注文档。保存所有形状并发出变更通知。
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<AnnotationShape> _shapes = new List<AnnotationShape>();

    public event Action? Changed;

    public IReadOnlyList<AnnotationShape> Shapes => _shapes;

    public int Count => _shapes.Count;

    public void AddShape(AnnotationShape shape)
    {
        if (shape == null) throw new ArgumentNullException(nameof(shape));
        _shapes.Add(shape.Clone());
        RaiseChanged();
    }

    public bool TryGetShape(Guid id, out AnnotationShape? shape)
    {
        AnnotationShape? found = _shapes.FirstOrDefault(x => x.Id == id);
        shape = found?.Clone();
        return found != null;
    }

    public bool ReplaceShape(AnnotationShape shape)
    {
        if (shape == null) throw new ArgumentNullException(nameof(shape));
        int index = _shapes.FindIndex(x => x.Id == shape.Id);
        if (index < 0) return false;

        _shapes[index] = shape.Clone();
        RaiseChanged();
        return true;
    }

    public bool RemoveShape(Guid id)
    {
        int index = _shapes.FindIndex(x => x.Id == id);
        if (index < 0) return false;

        _shapes.RemoveAt(index);
        RaiseChanged();
        return true;
    }

    public int RemoveWhere(Func<AnnotationShape, bool> predicate)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        int removed = _shapes.RemoveAll(x => predicate(x));
        if (removed > 0) RaiseChanged();
        return removed;
    }

    public void Clear()
    {
        if (_shapes.Count == 0) return;
        _shapes.Clear();
        RaiseChanged();
    }

    public void ReplaceAll(IEnumerable<AnnotationShape> shapes)
    {
        _shapes.Clear();
        if (shapes != null)
        {
            _shapes.AddRange(shapes.Select(x => x.Clone()));
        }
        RaiseChanged();
    }

    public IReadOnlyList<AnnotationShape> CreateSnapshot()
    {
        return _shapes.Select(x => x.Clone()).ToList();
    }

    private void RaiseChanged()
    {
        Changed?.Invoke();
    }
}
