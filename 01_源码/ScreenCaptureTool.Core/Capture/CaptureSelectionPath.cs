using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.Core.Capture;

public enum CaptureSelectionMode { Rectangle = 0, Pen = 2, Freehand = 3 }

/// <summary>屏幕物理像素坐标；控制点使用绝对坐标，直线节点的两个控制点等于节点本身。</summary>
public readonly record struct CapturePathNode(PointF Position, PointF Incoming, PointF Outgoing)
{
    public CapturePathNode(PointF position) : this(position, position, position) { }
    public CapturePathNode Translate(float dx, float dy) => new(
        new(Position.X + dx, Position.Y + dy), new(Incoming.X + dx, Incoming.Y + dy), new(Outgoing.X + dx, Outgoing.Y + dy));
}

/// <summary>不可变的闭合选区快照，遮罩、命中和成图使用同一组贝塞尔段。</summary>
public sealed class CaptureSelectionPath
{
    public IReadOnlyList<CapturePathNode> Nodes { get; }
    public Rectangle Bounds { get; }
    public bool IsCollinear { get; }

    public CaptureSelectionPath(IEnumerable<CapturePathNode> nodes)
    {
        var copy = nodes.ToArray();
        if (copy.Length < 3 || copy.Length > 2048) throw new ArgumentException("选区需要 3 至 2048 个节点");
        foreach (var n in copy)
            if (!Finite(n.Position) || !Finite(n.Incoming) || !Finite(n.Outgoing)) throw new ArgumentException("选区坐标无效");
        Nodes = Array.AsReadOnly(copy);
        var points = copy.SelectMany(n => new[] { n.Position, n.Incoming, n.Outgoing }).ToArray();
        PointF origin = copy[0].Position;
        PointF direction = points.FirstOrDefault(p => Math.Abs(p.X - origin.X) + Math.Abs(p.Y - origin.Y) > .01f);
        IsCollinear = !points.Any(p => Math.Abs((direction.X - origin.X) * (p.Y - origin.Y)
            - (direction.Y - origin.Y) * (p.X - origin.X)) > .01f);
        // 解导数极值而非把控制点当边界，避免长柄让导出产生大片多余透明边。
        double left = copy[0].Position.X, right = left, top = copy[0].Position.Y, bottom = top;
        for (int i = 0; i < copy.Length; i++)
        {
            var a = copy[i]; var b = copy[(i + 1) % copy.Length];
            Extents(a.Position.X, a.Outgoing.X, b.Incoming.X, b.Position.X, ref left, ref right);
            Extents(a.Position.Y, a.Outgoing.Y, b.Incoming.Y, b.Position.Y, ref top, ref bottom);
        }
        Bounds = Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
    }

    private static bool Finite(PointF p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && Math.Abs(p.X) < 1_000_000 && Math.Abs(p.Y) < 1_000_000;
    private static void Extents(double p0, double p1, double p2, double p3, ref double low, ref double high)
    {
        low = Math.Min(low, Math.Min(p0, p3)); high = Math.Max(high, Math.Max(p0, p3));
        double a = -p0 + 3 * p1 - 3 * p2 + p3, b = 2 * (p0 - 2 * p1 + p2), c = p1 - p0;
        if (Math.Abs(a) < 1e-10) { if (Math.Abs(b) > 1e-10) Include(-c / b, p0, p1, p2, p3, ref low, ref high); return; }
        double d = b * b - 4 * a * c;
        if (d < 0) return;
        Include((-b + Math.Sqrt(d)) / (2 * a), p0, p1, p2, p3, ref low, ref high);
        Include((-b - Math.Sqrt(d)) / (2 * a), p0, p1, p2, p3, ref low, ref high);
    }
    private static void Include(double t, double p0, double p1, double p2, double p3, ref double low, ref double high)
    {
        if (t <= 0 || t >= 1) return;
        double u = 1 - t, v = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        low = Math.Min(low, v); high = Math.Max(high, v);
    }

    public GraphicsPath CreateGraphicsPath(float offsetX = 0, float offsetY = 0)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        PointF Local(PointF p) => new(p.X - offsetX, p.Y - offsetY);
        for (int i = 0; i < Nodes.Count; i++)
        {
            var a = Nodes[i]; var b = Nodes[(i + 1) % Nodes.Count];
            path.AddBezier(Local(a.Position), Local(a.Outgoing), Local(b.Incoming), Local(b.Position));
        }
        path.CloseFigure();
        return path;
    }

    public bool Contains(PointF point) { using var p = CreateGraphicsPath(); return p.IsVisible(point); }

    /// <summary>只改变选区外 Alpha，不重采样已捕获颜色；完全透明处同时清零 RGB。</summary>
    public unsafe void ApplyAlphaMask(Bitmap image, Rectangle screenRect)
    {
        using var mask = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(mask))
        using (var p = CreateGraphicsPath(screenRect.X, screenRect.Y))
        {
            g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.FillPath(Brushes.White, p);
        }
        var rect = new Rectangle(0, 0, image.Width, image.Height);
        var source = image.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var alpha = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < image.Height; y++)
                {
                    byte* dst = (byte*)source.Scan0 + (long)y * source.Stride;
                    byte* src = (byte*)alpha.Scan0 + (long)y * alpha.Stride;
                    for (int x = 0; x < image.Width; x++, dst += 4, src += 4)
                    {
                        dst[3] = (byte)((dst[3] * src[3] + 127) / 255);
                        if (dst[3] == 0) dst[0] = dst[1] = dst[2] = 0;
                    }
                }
            }
            finally { mask.UnlockBits(alpha); }
        }
        finally { image.UnlockBits(source); }
    }
}
