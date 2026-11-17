using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 擦除裁切按手势、每 16 枚落点分批保存。完整批次跨文档版本复用，只重建变化的尾批。
/// 多份「画布减去本批洞」依次裁切，等价于画布减去全部洞的并集，不改变圆形落点。
/// </summary>
internal sealed class ErasureClipCache
{
    private const int BatchSize = 16;
    private readonly Dictionary<Guid, Gesture> _gestures = new();
    private Rect _canvas;
    private Matrix _transform;
    private Geometry[] _combinedChunks = Array.Empty<Geometry>();
    private Geometry? _combined;

    private sealed class Gesture
    {
        public ErasureShape? Source;
        public readonly List<Chunk> Chunks = new();
    }

    private sealed class Chunk
    {
        public required ErasureMark[] Marks;
        public Rect Bounds;
        public Geometry? Clip;
    }

    public void Clear()
    {
        _gestures.Clear();
        _combinedChunks = Array.Empty<Geometry>();
        _combined = null;
    }

    public void Configure(Rect canvas, Matrix transform)
    {
        if (_canvas == canvas && _transform == transform) return;
        Clear();
        _canvas = canvas;
        _transform = transform;
    }

    /// <summary>撤销、全清或换文档后，释放已经不存在的手势，避免缓存随操作次数增长。</summary>
    public void Retain(IReadOnlyList<ErasureShape> erasures)
    {
        var ids = erasures.Select(x => x.Id).ToHashSet();
        foreach (Guid id in _gestures.Keys.Where(id => !ids.Contains(id)).ToArray())
            _gestures.Remove(id);
    }

    public Geometry[] GetClips(IReadOnlyList<ErasureShape> erasures,
        Func<ErasureShape, bool> affects, Rect? relevantBounds = null)
    {
        var clips = new List<Geometry>();
        foreach (ErasureShape erase in erasures)
        {
            if (!affects(erase)) continue;
            if (!_gestures.TryGetValue(erase.Id, out Gesture? gesture))
                _gestures[erase.Id] = gesture = new Gesture();
            if (!ReferenceEquals(gesture.Source, erase)) UpdateChunks(gesture, erase);
            foreach (Chunk chunk in gesture.Chunks)
            {
                // 按整批筛选，保持批次边界稳定；框外的额外洞不会影响图元，却能让多个图元共用平台路径。
                if (relevantBounds is Rect bounds && !chunk.Bounds.Intersects(bounds)) continue;
                if (chunk.Clip == null)
                {
                    var holes = new GeometryGroup { FillRule = FillRule.NonZero };
                    foreach (ErasureMark mark in chunk.Marks)
                    {
                        var rect = new Rect(mark.Bounds.X, mark.Bounds.Y, mark.Bounds.Width, mark.Bounds.Height);
                        holes.Children.Add(mark.IsCircle ? new EllipseGeometry(rect) : new RectangleGeometry(rect));
                    }
                    holes.Transform = new MatrixTransform(_transform);
                    chunk.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                        new RectangleGeometry(_canvas), holes);
                }
                clips.Add(chunk.Clip);
            }
        }
        return clips.ToArray();
    }

    private void UpdateChunks(Gesture gesture, ErasureShape erase)
    {
        int count = (erase.Marks.Count + BatchSize - 1) / BatchSize;
        if (gesture.Chunks.Count > count) gesture.Chunks.RemoveRange(count, gesture.Chunks.Count - count);
        for (int index = 0; index < count; index++)
        {
            int start = index * BatchSize, length = Math.Min(BatchSize, erase.Marks.Count - start);
            if (index < gesture.Chunks.Count && Matches(gesture.Chunks[index].Marks, erase.Marks, start, length))
                continue;
            var marks = new ErasureMark[length];
            var bounds = erase.Marks[start].Bounds;
            float reach = 0;
            for (int i = 0; i < length; i++)
            {
                marks[i] = erase.Marks[start + i];
                bounds = System.Drawing.RectangleF.Union(bounds, marks[i].Bounds);
                reach = Math.Max(reach, Math.Max(marks[i].Bounds.Width, marks[i].Bounds.Height));
            }
            // 与原来的筛点余量一致，兜住描边溢出与抗锯齿；文字旋转后按变换后的包络筛选。
            var chunk = new Chunk
            {
                Marks = marks,
                Bounds = new Rect(bounds.X - reach, bounds.Y - reach,
                    bounds.Width + reach * 2, bounds.Height + reach * 2).TransformToAABB(_transform)
            };
            if (index < gesture.Chunks.Count) gesture.Chunks[index] = chunk;
            else gesture.Chunks.Add(chunk);
        }
        gesture.Source = erase;
    }

    private static bool Matches(ErasureMark[] cached, IReadOnlyList<ErasureMark> marks, int start, int length)
    {
        if (cached.Length != length) return false;
        for (int i = 0; i < length; i++)
            if (cached[i] != marks[start + i]) return false;
        return true;
    }

    /// <summary>活文字的 Control.Clip 只能接一份几何，按平衡树合成交集，并复用未变的结果。</summary>
    public Geometry? Combine(Geometry[] clips)
    {
        if (_combinedChunks.AsSpan().SequenceEqual(clips)) return _combined;
        _combinedChunks = clips;
        _combined = clips.Length == 0 ? null : Intersect(clips, 0, clips.Length);
        return _combined;
    }

    private static Geometry Intersect(Geometry[] clips, int start, int count)
    {
        if (count == 1) return clips[start];
        int half = count / 2;
        return new CombinedGeometry(GeometryCombineMode.Intersect,
            Intersect(clips, start, half), Intersect(clips, start + half, count - half));
    }

    public static IDisposable? Push(DrawingContext context, Geometry[] clips)
    {
        if (clips.Length == 0) return null;
        var scope = new ClipScope(clips.Length);
        try
        {
            foreach (Geometry clip in clips) scope.Add(context.PushGeometryClip(clip));
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private sealed class ClipScope(int capacity) : IDisposable
    {
        private readonly DrawingContext.PushedState[] _states = new DrawingContext.PushedState[capacity];
        private int _count;
        public void Add(DrawingContext.PushedState state) => _states[_count++] = state;
        public void Dispose()
        {
            while (_count > 0) _states[--_count].Dispose();
        }
    }
}
