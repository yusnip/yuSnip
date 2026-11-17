namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 画笔的单个原始采样点。坐标采用截图位图坐标系（物理像素），与 <see cref="AnnotationShape"/> 一致。
/// 存原始采样而不是烘焙后的像素：改粗细/颜色/笔刷画像只需重新栅格化，数据本身不变。
/// </summary>
public readonly struct StrokePoint
{
    /// <summary><see cref="Pressure"/> 的哨兵值：无压感设备。</summary>
    public const float NoPressure = -1.0f;

    public StrokePoint(float x, float y, double timestampMs)
    {
        X = x;
        Y = y;
        TimestampMs = timestampMs;
    }

    public StrokePoint(float x, float y, long timestampMs)
        : this(x, y, (double)timestampMs)
    {
    }

    public StrokePoint(float x, float y, float pressure, long timestampMs)
        : this(x, y, (double)timestampMs)
    {
    }

    public StrokePoint(float x, float y, float pressure, double timestampMs)
        : this(x, y, timestampMs)
    {
    }

    public float X { get; }

    public float Y { get; }

    /// <summary>压感哨兵值（统一使用速度笔锋，恒为 -1）。</summary>
    public float Pressure => NoPressure;

    /// <summary>采样时刻（毫秒高精度口径），用于速度笔锋；0 表示未知。</summary>
    public double TimestampMs { get; }
}
