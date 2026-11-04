using System.Drawing;

namespace ScreenCaptureTool.LongScroll;

/// <summary>由截图快捷键目录传入的会话键：Win32 主键／修饰键与工具栏展示文字。</summary>
public readonly record struct LongScrollShortcut(uint Modifiers, uint VirtualKey, string DisplayText);

/// <summary>Configuration shared by vertical and horizontal long-scroll sessions.</summary>
public sealed class LongScrollOptions
{
    /// <summary>单张成品图的字节天花板；会话实际预算由 <see cref="LongScrollBudget"/> 按本机空余内存折算。</summary>
    public const long DefaultMaxEstimatedOutputBytes = LongScrollBudget.MaxBudgetBytes;

    private readonly object _alongLimitLock = new();
    private ScrollAxis _axis = ScrollAxis.Vertical;
    // 0＝未指定，首次读取时按本机可用内存折算并锁定（见 MaxAlongLengthPx）。
    private int _maxAlongLengthPx;
    private double _fixedLeadingRatio = 0.12;
    private double _fixedTrailingRatio = 0.12;
    private int _maxFixedLayerAlongLengthPx = 120;

    public LongScrollOptions(Rectangle captureRegion)
    {
        CaptureRegion = captureRegion;
    }

    /// <summary>Capture viewport in physical screen pixels.</summary>
    public Rectangle CaptureRegion { get; }

    /// <summary>复用截图的取消、完成绑定；独立调用长截图时沿用出厂默认。</summary>
    public LongScrollShortcut CancelShortcut { get; init; } = new(0, 0x1B, "Esc");
    public LongScrollShortcut FinishShortcut { get; init; } = new(0, 0x0D, "Enter");

    public ScrollAxis Axis
    {
        get => _axis;
        set
        {
            AxisGeometry.Validate(value, nameof(Axis));
            _axis = value;
        }
    }

    /// <summary>
    /// Maximum length of the scrollable middle content on the selected axis.
    /// 未被显式赋值时＝按会话启动那一刻的空余物理内存折算出的字节预算换算而来，
    /// 且<b>只算一次就锁死</b>：滚动途中重新取值必须得到同一个数，否则边界会随内存起伏
    /// "时到时不到"。显式赋值（算法自测工程）优先。
    /// </summary>
    public int MaxAlongLengthPx
    {
        get
        {
            int locked = Volatile.Read(ref _maxAlongLengthPx);
            if (locked > 0) return locked;
            lock (_alongLimitLock)
            {
                if (_maxAlongLengthPx <= 0)
                {
                    // 固定层（首尾吸附条）在成品里也要占沿轴长度，先把这份预留扣掉。
                    _maxAlongLengthPx = LongScrollBudget.ResolveAlongLength(
                        _axis, CaptureRegion, checked(2 * _maxFixedLayerAlongLengthPx));
                }
                return _maxAlongLengthPx;
            }
        }
        init => _maxAlongLengthPx = value;
    }

    /// <summary>
    /// Compatibility alias for the original vertical-only option. In horizontal mode
    /// this still means the maximum along-axis length, not a physical height.
    /// </summary>
    public int MaxTotalHeightPx
    {
        get => MaxAlongLengthPx;
        init => _maxAlongLengthPx = value;
    }

    /// <summary>
    /// Estimated output allocation ceiling. Composition assumes a 32-bpp destination,
    /// so the minimum estimate is width * height * 4 bytes. 这是最后一道兜底闸：
    /// 采集期每帧已经按同一份字节预算收口，正常情况下永远轮不到它触发。
    /// </summary>
    public long MaxEstimatedOutputBytes { get; init; } = DefaultMaxEstimatedOutputBytes;

    public bool EnableFixedLayerMode { get; init; }

    public double FixedLeadingRatio
    {
        get => _fixedLeadingRatio;
        init => _fixedLeadingRatio = value;
    }

    public double FixedTrailingRatio
    {
        get => _fixedTrailingRatio;
        init => _fixedTrailingRatio = value;
    }

    public int MaxFixedLayerAlongLengthPx
    {
        get => _maxFixedLayerAlongLengthPx;
        init => _maxFixedLayerAlongLengthPx = value;
    }

    // Vertical names retained while the production session is migrated.
    public double FixedTopRatio
    {
        get => _fixedLeadingRatio;
        init => _fixedLeadingRatio = value;
    }

    public double FixedBottomRatio
    {
        get => _fixedTrailingRatio;
        init => _fixedTrailingRatio = value;
    }

    public int MaxFixedLayerHeightPx
    {
        get => _maxFixedLayerAlongLengthPx;
        init => _maxFixedLayerAlongLengthPx = value;
    }

    public int CaptureIntervalMs { get; init; } = 16;

    /// <summary>
    /// Optional unattended verification timeout. Zero keeps the normal interactive
    /// workflow; a positive value copies the composed result to the clipboard when
    /// the timeout expires. This is only populated by the local verification hook.
    /// </summary>
    public int VerificationAutoFinishAfterMs { get; init; }

    public bool IsDarkMode { get; init; }

    public bool IsValid =>
        CaptureRegion.Width > 1 &&
        CaptureRegion.Height > 1 &&
        MaxAlongLengthPx > 0 &&
        MaxEstimatedOutputBytes >= 4;

    public void Validate()
    {
        AxisGeometry.Validate(_axis, nameof(Axis));
        if (CaptureRegion.Width <= 1 || CaptureRegion.Height <= 1)
            throw new ArgumentOutOfRangeException(nameof(CaptureRegion), "Capture region must be at least 2x2 pixels.");
        if (MaxAlongLengthPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAlongLengthPx));
        if (MaxEstimatedOutputBytes < 4)
            throw new ArgumentOutOfRangeException(nameof(MaxEstimatedOutputBytes));
        if (CaptureIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(CaptureIntervalMs));
        if (VerificationAutoFinishAfterMs < 0)
            throw new ArgumentOutOfRangeException(nameof(VerificationAutoFinishAfterMs));
        if (!double.IsFinite(_fixedLeadingRatio) || _fixedLeadingRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(FixedLeadingRatio));
        if (!double.IsFinite(_fixedTrailingRatio) || _fixedTrailingRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(FixedTrailingRatio));
        if (_maxFixedLayerAlongLengthPx < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFixedLayerAlongLengthPx));
    }
}
