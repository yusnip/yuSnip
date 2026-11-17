using System.Drawing;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// Converts axis-relative geometry (along/cross) to physical bitmap geometry (x/y).
/// Keeping this conversion in one place prevents horizontal code from accidentally
/// retaining vertical-only width/height assumptions.
/// </summary>
public static class AxisGeometry
{
    public static void Validate(ScrollAxis axis, string parameterName = "axis")
    {
        if (axis is not ScrollAxis.Vertical and not ScrollAxis.Horizontal)
        {
            throw new ArgumentOutOfRangeException(parameterName, axis, "Unsupported scroll axis.");
        }
    }

    public static void Validate(AxisDirection direction, string parameterName = "direction")
    {
        if (direction is not AxisDirection.Leading and not AxisDirection.Trailing)
        {
            throw new ArgumentOutOfRangeException(parameterName, direction, "Unsupported axis direction.");
        }
    }

    public static int AlongLength(ScrollAxis axis, Size size)
    {
        Validate(axis);
        return axis == ScrollAxis.Vertical ? size.Height : size.Width;
    }

    public static int AlongLength(ScrollAxis axis, Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return AlongLength(axis, image.Size);
    }

    public static int CrossLength(ScrollAxis axis, Size size)
    {
        Validate(axis);
        return axis == ScrollAxis.Vertical ? size.Width : size.Height;
    }

    public static int CrossLength(ScrollAxis axis, Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return CrossLength(axis, image.Size);
    }

    public static Size ToPhysicalSize(ScrollAxis axis, int alongLength, int crossLength)
    {
        Validate(axis);
        if (alongLength < 0) throw new ArgumentOutOfRangeException(nameof(alongLength));
        if (crossLength < 0) throw new ArgumentOutOfRangeException(nameof(crossLength));
        return axis == ScrollAxis.Vertical
            ? new Size(crossLength, alongLength)
            : new Size(alongLength, crossLength);
    }

    public static Rectangle ToPhysicalRectangle(
        ScrollAxis axis,
        int alongStart,
        int crossStart,
        int alongLength,
        int crossLength)
    {
        Size size = ToPhysicalSize(axis, alongLength, crossLength);
        return axis == ScrollAxis.Vertical
            ? new Rectangle(crossStart, alongStart, size.Width, size.Height)
            : new Rectangle(alongStart, crossStart, size.Width, size.Height);
    }

    public static Rectangle FullCrossRectangle(
        ScrollAxis axis,
        int alongStart,
        int alongLength,
        int crossLength)
    {
        return ToPhysicalRectangle(axis, alongStart, 0, alongLength, crossLength);
    }
}
