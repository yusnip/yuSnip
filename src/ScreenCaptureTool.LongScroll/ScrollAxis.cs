namespace ScreenCaptureTool.LongScroll;

/// <summary>The physical axis along which a long screenshot grows.</summary>
public enum ScrollAxis
{
    Vertical = 0,
    Horizontal = 1,
}

/// <summary>A physical screen-space displacement in pixels.</summary>
public readonly record struct ScrollVector(int X, int Y)
{
    public static ScrollVector Zero => new(0, 0);

    public int Along(ScrollAxis axis)
    {
        AxisGeometry.Validate(axis);
        return axis == ScrollAxis.Horizontal ? X : Y;
    }

    public int Cross(ScrollAxis axis)
    {
        AxisGeometry.Validate(axis);
        return axis == ScrollAxis.Horizontal ? Y : X;
    }

    public ScrollVector WithAxis(ScrollAxis axis, int delta)
    {
        AxisGeometry.Validate(axis);
        return axis == ScrollAxis.Horizontal ? new ScrollVector(delta, 0) : new ScrollVector(0, delta);
    }

    public bool IsAlongAxis(ScrollAxis axis)
    {
        AxisGeometry.Validate(axis);
        return axis == ScrollAxis.Horizontal ? Y == 0 : X == 0;
    }
}
