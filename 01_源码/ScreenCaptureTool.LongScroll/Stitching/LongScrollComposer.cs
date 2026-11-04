using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>Composes signed axis-relative strips into a normalized output bitmap.</summary>
public static class LongScrollComposer
{
    private const int DestinationBytesPerPixel = 4;

    public static int CalculateCompositedAlongLength(
        IReadOnlyList<StitchedStrip>? strips,
        int fallbackAlongLength)
    {
        if (!TryGetAlongRange(strips, out int minimum, out int maximum))
            return Math.Max(0, fallbackAlongLength);
        return checked(maximum - minimum);
    }

    public static int CalculateCompositedAlongLength(
        IReadOnlyList<StitchedStrip>? strips,
        int fallbackAlongLength,
        ScrollAxis axis)
    {
        AxisGeometry.Validate(axis);
        if (strips != null)
        {
            for (int i = 0; i < strips.Count; i++)
            {
                StitchedStrip? strip = strips[i];
                if (strip == null) continue;
                ValidateStripGeometry(strip, axis, expectedCrossLength: null);
            }
        }
        return CalculateCompositedAlongLength(strips, fallbackAlongLength);
    }

    public static int CalculateCompositedHeight(
        IReadOnlyList<StitchedStrip>? strips,
        int fallbackHeight)
    {
        return CalculateCompositedAlongLength(strips, fallbackHeight, ScrollAxis.Vertical);
    }

    public static Bitmap Compose(IReadOnlyList<StitchedStrip> strips, int fallbackHeight)
    {
        return ComposeCore(strips, fallbackHeight, null, null, ScrollAxis.Vertical, options: null);
    }

    public static Bitmap Compose(
        IReadOnlyList<StitchedStrip> strips,
        int fallbackHeight,
        Bitmap? fixedTop,
        Bitmap? fixedBottom)
    {
        return ComposeCore(strips, fallbackHeight, fixedTop, fixedBottom, ScrollAxis.Vertical, options: null);
    }

    /// <summary>
    /// Compatibility-friendly axis-aware overload used by the production session.
    /// Fixed bitmaps are leading/trailing on the selected axis.
    /// </summary>
    public static Bitmap Compose(
        IReadOnlyList<StitchedStrip> strips,
        int fallbackAlongLength,
        Bitmap? fixedLeading,
        Bitmap? fixedTrailing,
        ScrollAxis axis,
        LongScrollOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Axis != axis)
            throw new ArgumentException("Composition axis does not match options.Axis.", nameof(axis));
        return ComposeCore(strips, fallbackAlongLength, fixedLeading, fixedTrailing, axis, options);
    }

    /// <summary>Axis-aware overload with conservative default allocation limits.</summary>
    public static Bitmap Compose(
        IReadOnlyList<StitchedStrip> strips,
        int fallbackAlongLength,
        Bitmap? fixedLeading,
        Bitmap? fixedTrailing,
        ScrollAxis axis)
    {
        return ComposeCore(strips, fallbackAlongLength, fixedLeading, fixedTrailing, axis, options: null);
    }

    private static Bitmap ComposeCore(
        IReadOnlyList<StitchedStrip> strips,
        int fallbackAlongLength,
        Bitmap? fixedLeading,
        Bitmap? fixedTrailing,
        ScrollAxis axis,
        LongScrollOptions? options)
    {
        AxisGeometry.Validate(axis);
        if (strips == null || strips.Count == 0)
            throw new ArgumentException("At least one strip is required.", nameof(strips));

        StitchedStrip first = strips[0] ?? throw new ArgumentException("The first strip cannot be null.", nameof(strips));
        int crossLength = first.CrossLength;
        if (crossLength <= 0) throw new InvalidOperationException("Strip cross length is invalid.");

        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip strip = strips[i] ?? throw new ArgumentException($"Strip {i} is null.", nameof(strips));
            ValidateStripGeometry(strip, axis, crossLength);
        }

        if (!TryGetAlongRange(strips, out int minimumAlong, out int maximumAlong))
            throw new InvalidOperationException("No strip has valid geometry.");
        int middleAlongLength = checked(maximumAlong - minimumAlong);
        if (middleAlongLength <= 0)
            middleAlongLength = Math.Max(1, fallbackAlongLength);

        int fixedLeadingLength = ValidateFixedLayer(fixedLeading, axis, crossLength, nameof(fixedLeading));
        int fixedTrailingLength = ValidateFixedLayer(fixedTrailing, axis, crossLength, nameof(fixedTrailing));
        int finalAlongLength = checked(checked(fixedLeadingLength + middleAlongLength) + fixedTrailingLength);
        Size outputSize = AxisGeometry.ToPhysicalSize(axis, finalAlongLength, crossLength);

        ValidateAllocation(outputSize, middleAlongLength, options);

        Bitmap? output = null;
        try
        {
            output = new Bitmap(outputSize.Width, outputSize.Height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(output);
            graphics.Clear(Color.White);

            if (fixedLeading != null && fixedLeadingLength > 0)
                DrawFullLayer(graphics, fixedLeading, axis, 0, fixedLeadingLength, crossLength);

            for (int i = 0; i < strips.Count; i++)
            {
                StitchedStrip strip = strips[i];
                int normalizedAlong = checked(strip.AlongStart - minimumAlong);
                int destinationAlong = checked(fixedLeadingLength + normalizedAlong);
                Rectangle destination = AxisGeometry.FullCrossRectangle(
                    axis,
                    destinationAlong,
                    strip.AlongLength,
                    crossLength);
                Rectangle source = AxisGeometry.FullCrossRectangle(
                    axis,
                    0,
                    strip.AlongLength,
                    crossLength);
                graphics.DrawImage(strip.Bitmap, destination, source, GraphicsUnit.Pixel);
            }

            if (fixedTrailing != null && fixedTrailingLength > 0)
            {
                DrawFullLayer(
                    graphics,
                    fixedTrailing,
                    axis,
                    checked(fixedLeadingLength + middleAlongLength),
                    fixedTrailingLength,
                    crossLength);
            }

            return output;
        }
        catch
        {
            output?.Dispose();
            throw;
        }
    }

    private static void ValidateStripGeometry(
        StitchedStrip strip,
        ScrollAxis axis,
        int? expectedCrossLength)
    {
        if (strip.CrossLength <= 0 || strip.AlongLength <= 0)
            throw new InvalidOperationException("Strip dimensions must be positive.");
        if (expectedCrossLength.HasValue && strip.CrossLength != expectedCrossLength.Value)
            throw new InvalidOperationException("All strips must have exactly the same cross length.");
        if (AxisGeometry.CrossLength(axis, strip.Bitmap) != strip.CrossLength)
            throw new InvalidOperationException("Strip bitmap cross length does not match its declared geometry.");
        if (AxisGeometry.AlongLength(axis, strip.Bitmap) < strip.AlongLength)
            throw new InvalidOperationException("Strip bitmap is shorter than its declared along length.");
        _ = strip.AlongEnd; // checked overflow validation
    }

    private static int ValidateFixedLayer(
        Bitmap? layer,
        ScrollAxis axis,
        int expectedCrossLength,
        string parameterName)
    {
        if (layer == null) return 0;
        int cross = AxisGeometry.CrossLength(axis, layer);
        int along = AxisGeometry.AlongLength(axis, layer);
        if (cross != expectedCrossLength)
            throw new ArgumentException("Fixed layer cross length must match all strips.", parameterName);
        if (along <= 0)
            throw new ArgumentException("Fixed layer along length must be positive.", parameterName);
        return along;
    }

    private static void ValidateAllocation(
        Size outputSize,
        int middleAlongLength,
        LongScrollOptions? options)
    {
        if (outputSize.Width <= 0 || outputSize.Height <= 0)
            throw new InvalidOperationException("Output dimensions are invalid.");

        // 只留字节这一把尺：旧的"像素数上限"与"字节上限"是同一个量的两种写法
        // （250M 像素 × 4 字节＝1e9 字节），两档并存既没有额外保护，又会先撞上像素档
        // 而报出一个用户读不懂的数字。真正的收口发生在采集期逐帧判定，这里是兜底闸。
        int maxAlong = options?.MaxAlongLengthPx ?? int.MaxValue;
        long maxBytes = options?.MaxEstimatedOutputBytes ?? LongScrollBudget.MaxBudgetBytes;
        if (maxAlong <= 0 || maxBytes < DestinationBytesPerPixel)
            throw new InvalidOperationException("Composition limits are invalid.");
        if (middleAlongLength > maxAlong)
            throw new InvalidOperationException($"Scrollable content exceeds the {maxAlong}px along-axis limit.");

        long estimatedBytes;
        try
        {
            estimatedBytes = checked((long)outputSize.Width * outputSize.Height * DestinationBytesPerPixel);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("Output allocation size overflowed.", ex);
        }

        if (estimatedBytes > maxBytes)
            throw new InvalidOperationException($"Estimated output is {estimatedBytes:N0} bytes; limit is {maxBytes:N0}.");
    }

    private static void DrawFullLayer(
        Graphics graphics,
        Bitmap bitmap,
        ScrollAxis axis,
        int destinationAlong,
        int alongLength,
        int crossLength)
    {
        Rectangle destination = AxisGeometry.FullCrossRectangle(
            axis,
            destinationAlong,
            alongLength,
            crossLength);
        Rectangle source = AxisGeometry.FullCrossRectangle(axis, 0, alongLength, crossLength);
        graphics.DrawImage(bitmap, destination, source, GraphicsUnit.Pixel);
    }

    private static bool TryGetAlongRange(
        IReadOnlyList<StitchedStrip>? strips,
        out int minimum,
        out int maximum)
    {
        minimum = 0;
        maximum = 0;
        bool found = false;
        if (strips == null) return false;

        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip? strip = strips[i];
            if (strip == null || strip.AlongLength <= 0) continue;
            int end = strip.AlongEnd;
            if (!found)
            {
                minimum = strip.AlongStart;
                maximum = end;
                found = true;
            }
            else
            {
                minimum = Math.Min(minimum, strip.AlongStart);
                maximum = Math.Max(maximum, end);
            }
        }
        return found;
    }
}
