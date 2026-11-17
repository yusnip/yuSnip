using System;
using System.Collections.Generic;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 画笔输入整形：One-Euro 位置平滑 + 最小点距去重。截图遮罩与贴图标注共用这一份实现，
/// 「平滑」滑条在两侧才有同一手感；算术与截图侧原实现逐位一致，手感修正一律改这里，宿主不得另起炉灶。
///
/// <para><b>单位约定</b>：坐标与 <paramref name="nowMs"/> 时间轴必须合起来构成<b>屏幕像素/毫秒</b>口径。
/// 滤波器的截止频率、笔锋的速度都由此算出。截图侧内容与屏幕 1:1，天然满足；
/// 贴图侧 1 内容像素只有 <c>scale</c> 个屏幕像素，宿主须把时间轴按 <c>1/scale</c> 拉伸后再喂入，
/// 否则同手速在缩小档会被当成高速移动、笔锋莫名变细。</para>
/// </summary>
public sealed class BrushInputSmoother
{
    /// <summary>画笔采样最小点距（屏幕物理像素）。微小位移不记录，过滤重复不动事件。</summary>
    public const float MinDistancePx = 0.5f;

    private const double VelocityCutoffHz = 1.0;
    private const double Beta = 0.007;

    // 滤波后的位置与其速度估计、上一采样时刻（高精度毫秒）；_tickMs == 0 表示尚未起笔
    private PointF _pos;
    private PointF _vel;
    private double _tickMs;

    /// <summary>
    /// 复位：起笔、以及截图侧中途按/松 Shift 切换直线吸附时都要调用。
    /// 不复位会让滤波拿着上一段的动量去平滑新轨迹，起笔第一笔直接甩出去。
    /// </summary>
    public void Reset() => _tickMs = 0.0;

    /// <summary>
    /// 画笔输入 One-Euro 滤波：慢速时压低鼠标抖动，快速时按速度自动放宽截止频率以免笔迹拖尾。
    /// 平滑度为 0 时完全旁路，直接返回原始点。
    /// </summary>
    public PointF Push(PointF raw, double nowMs, float smoothing)
    {
        if (_tickMs == 0.0 || smoothing <= 0f)
        {
            _pos = raw;
            _vel = PointF.Empty;
            _tickMs = nowMs;
            return raw;
        }

        double dt = (nowMs - _tickMs) / 1000.0;
        if (dt <= 0.0) return _pos;

        double invDt = 1.0 / dt;
        double alphaV = Alpha(VelocityCutoffHz, dt);
        double vx = (raw.X - _pos.X) * invDt;
        double vy = (raw.Y - _pos.Y) * invDt;
        _vel = new PointF(
            (float)(alphaV * vx + (1.0 - alphaV) * _vel.X),
            (float)(alphaV * vy + (1.0 - alphaV) * _vel.Y));

        double speed = Math.Sqrt(
            (double)_vel.X * _vel.X + (double)_vel.Y * _vel.Y);
        // 平滑越强 → 静止截止频率越低（默认档 ≈20Hz）；速度越快 → 截止频率越高，不拖尾
        double cutoffHz = 30.0 - 24.0 * smoothing + Beta * speed;
        double alpha = Alpha(cutoffHz, dt);
        _pos = new PointF(
            (float)(alpha * raw.X + (1.0 - alpha) * _pos.X),
            (float)(alpha * raw.Y + (1.0 - alpha) * _pos.Y));
        _tickMs = nowMs;
        return _pos;
    }

    private static double Alpha(double cutoffHz, double dtSec)
    {
        double r = 2.0 * Math.PI * cutoffHz * dtSec;
        return r / (1.0 + r);
    }

    /// <summary>与上一个已记录点比较点距，决定是否再落一个采样。</summary>
    public static bool ShouldAddPoint(IReadOnlyList<StrokePoint> points, PointF next, float minDistancePx = MinDistancePx)
    {
        if (points.Count == 0) return true;
        StrokePoint last = points[points.Count - 1];
        float dx = next.X - last.X;
        float dy = next.Y - last.Y;
        return dx * dx + dy * dy >= minDistancePx * minDistancePx;
    }
}
