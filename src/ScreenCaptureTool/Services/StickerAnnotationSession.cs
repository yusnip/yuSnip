using System;
using System.Drawing;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 贴图标注事务。源内容由宿主持有；这里只保留<b>成品位图</b>与重建管线，
/// 每次提交都重新取干净底图，禁止把上次成品作为下一次底图。
///
/// <para>模型历史（图元 + 文字层的撤销/重做簿记）已上提给 <see cref="AnnotationEditHistory"/>，
/// 录屏标注走同一份；本类只补贴图独有的那一脚——<see cref="Rebuild"/> 重烘成品位图。</para>
/// </summary>
internal sealed class StickerAnnotationSession : IDisposable
{
    private readonly Func<Bitmap> _captureSource;
    private readonly Action<Bitmap, AnnotationDocument, IReadOnlyList<TextLayerSnapshot>> _render;
    private bool _disposed;
    private Bitmap? _composite;

    /// <summary>图元/文字层的撤销重做簿记；构造时把 <see cref="Rebuild"/> 作为重建委托交进去，
    /// 故每一次提交与回退都会顺带重烘成品位图。</summary>
    private readonly AnnotationEditHistory _history;

    /// <summary>宿主共享编辑会话经 <see cref="IAnnotationEditHost.History"/> 取的就是这一份。</summary>
    public AnnotationEditHistory History => _history;
    public AnnotationDocument Document => _history.Document;
    public IReadOnlyList<TextLayerSnapshot> Texts => _history.Texts;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public long Version { get; private set; }

    public StickerAnnotationSession(Func<Bitmap> captureSource,
        Action<Bitmap, AnnotationDocument, IReadOnlyList<TextLayerSnapshot>> render)
    {
        _captureSource = captureSource ?? throw new ArgumentNullException(nameof(captureSource));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _history = new AnnotationEditHistory(Rebuild);
    }

    public void SetTextSnapshots(IEnumerable<TextLayerSnapshot> texts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _history.SetTextSnapshots(texts);
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _history.Commit();
    }

    /// <summary>
    /// 只推历史、不重烘成品位图。给文本层用：<see cref="Rebuild"/> 每次都要克隆整张干净底图
    /// 并重渲染全部图元，而字号/透明度滑杆一次拖动就是几十次 tick。
    /// ⚠ 代价是成品位图此后可能是旧的：所有 <see cref="CopyComposite"/> 消费点必须先调一次 <see cref="Rebuild"/>。
    /// </summary>
    public void CommitHistoryOnly()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _history.CommitHistoryOnly();
    }

    public bool Undo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _history.Undo();
    }

    public bool Redo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _history.Redo();
    }

    public void ClearAnnotations()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _history.ClearAnnotations();
    }

    /// <summary>重排或主题改变只重建结果，不新增标注历史，也不改内容坐标。</summary>
    public void Rebuild()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Bitmap? next = _captureSource();
        try
        {
            _render(next, Document, Texts);
            var old = _composite;
            _composite = next;
            next = null;
            Version++;
            old?.Dispose();
        }
        finally
        {
            next?.Dispose();
        }
    }

    /// <summary>输出副本交给调用方，避免宿主显示仍引用已释放的成品。</summary>
    public Bitmap? CopyComposite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _composite?.Clone(new Rectangle(0, 0, _composite.Width, _composite.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _composite?.Dispose();
        _composite = null;
        Document.Clear();
        _history.SetTextSnapshots(Array.Empty<TextLayerSnapshot>());
    }
}
