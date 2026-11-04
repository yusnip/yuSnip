namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// Signed direction on the selected scroll axis.
/// </summary>
public enum AxisDirection
{
    Leading = -1,
    Trailing = 1,
}

/// <summary>
/// Result of estimating the viewport movement between two captured frames.
/// Along-axis movement is signed: negative moves toward the leading edge and
/// positive moves toward the trailing edge.
/// </summary>
public readonly record struct ScrollEstimate(
    int ViewportDeltaAlong,
    int StaticLeading,
    int StaticTrailing,
    bool IsReliable,
    string Reason,
    double Score,
    string Diagnostics)
{
    public static ScrollEstimate Unreliable(
        string reason,
        int staticLeading = 0,
        int staticTrailing = 0,
        double score = 0.0,
        string diagnostics = "")
    {
        return new ScrollEstimate(
            0,
            Math.Max(0, staticLeading),
            Math.Max(0, staticTrailing),
            false,
            reason,
            Math.Clamp(score, 0.0, 1.0),
            diagnostics);
    }
}
