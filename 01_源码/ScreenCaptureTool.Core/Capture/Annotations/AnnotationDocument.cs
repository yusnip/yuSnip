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

    /// <summary>
    /// 变更版本号。所有增删改都必经 <see cref="RaiseChanged"/>，故这一个自增点就能代表"文档内容动过"。
    /// 供渲染侧重匹配擦除目标与裁切批次列表，完整批次的平台几何不随此版本整表作废。
    /// </summary>
    public ulong Revision { get; private set; }

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

    /// <summary>把一个图元替换为同一图层位置上的多个图元（涂抹擦除把一笔切成几段时使用）。</summary>
    public bool ReplaceShapeWithMany(Guid id, IEnumerable<AnnotationShape> replacements)
    {
        if (replacements == null) throw new ArgumentNullException(nameof(replacements));
        int index = _shapes.FindIndex(x => x.Id == id);
        if (index < 0) return false;

        List<AnnotationShape> copies = replacements.Select(x => x.Clone()).ToList();
        _shapes.RemoveAt(index);
        _shapes.InsertRange(index, copies);
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

    /// <summary>
    /// 删掉某个序号之后，把比它大的序号整体前移 1，保持 1..N 连续。两侧宿主（截图遮罩、贴图标注）
    /// 共用这一份，避免各自写一遍后编号规则漂移。必须配合先前的一次 <see cref="RemoveShape"/> 调用。
    /// 返回被改动的计数器个数。
    /// </summary>
    public int RenumberCountersAfterDeletion(int deletedNumber)
    {
        int changed = 0;
        for (int i = 0; i < _shapes.Count; i++)
        {
            if (_shapes[i] is not CounterShape counter || counter.Number <= deletedNumber) continue;
            // WithNumber 而不是 new CounterShape(...)：类型档、外形档、引线、说明文字都必须原样留下
            _shapes[i] = counter.WithNumber(counter.Number - 1);
            changed++;
        }
        // 逐个 ReplaceShape 会让图层重绘 N 次，这里一次改完只通知一次
        if (changed > 0) RaiseChanged();
        return changed;
    }

    /// <summary>
    /// 下一个该新建的序号：起始值与「现有最大值 + 1」取较大者。前者让"手动指定起始值"生效
    /// （PixPin：后续新建的序列号会从该值开始），后者保证不会和已经画在图上的撞号。
    /// </summary>
    public int NextCounterNumber(int startValue)
    {
        int max = 0;
        foreach (AnnotationShape shape in _shapes)
        {
            if (shape is CounterShape counter && counter.Number > max) max = counter.Number;
        }
        return Math.Max(Math.Max(1, startValue), max + 1);
    }

    /// <summary>把图上所有序号的类型档改成同一个（切类型时已有编号要一起变，不能只影响后画的）。</summary>
    public int ApplyCounterSerialKind(CounterSerialKind kind)
    {
        int changed = 0;
        for (int i = 0; i < _shapes.Count; i++)
        {
            if (_shapes[i] is not CounterShape counter || counter.Serial == kind) continue;
            _shapes[i] = counter.WithSerial(kind);
            changed++;
        }
        if (changed > 0) RaiseChanged();
        return changed;
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
        Revision++;
        Changed?.Invoke();
    }
}
