using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Controls;

/// <summary>按时间与天气绘制问候天空；只接收快照，不在渲染时定位、联网或读盘。</summary>
public sealed class OverviewSky : Control
{
    private static readonly Geometry MoonShape = Geometry.Parse("M 0 -24 A 24 24 0 1 0 0 24 A 19 24 0 0 1 0 -24 Z");
    private static readonly Geometry CloudShape = Geometry.Parse("M -42 12 C -61 12 -63 -12 -45 -18 C -44 -42 -9 -45 -2 -24 C 15 -34 31 -18 28 -5 C 51 -8 56 20 34 23 L -38 23 C -44 23 -48 18 -42 12 Z");
    public static readonly StyledProperty<string> PeriodProperty =
        AvaloniaProperty.Register<OverviewSky, string>(nameof(Period), "Day");
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<OverviewSky, bool>(nameof(IsAnimated));
    public static readonly StyledProperty<WeatherScene> SceneProperty = AvaloniaProperty.Register<OverviewSky, WeatherScene>(nameof(Scene));
    public static readonly StyledProperty<double> WindKmhProperty = AvaloniaProperty.Register<OverviewSky, double>(nameof(WindKmh));
    public static readonly StyledProperty<double> CloudPercentProperty = AvaloniaProperty.Register<OverviewSky, double>(nameof(CloudPercent), 15);
    public static readonly StyledProperty<double> PrecipitationProperty = AvaloniaProperty.Register<OverviewSky, double>(nameof(Precipitation));
    private WeatherScene _previousScene;
    private double _blendStart = -10;
    private readonly Stopwatch _motionTime = new();
    private DispatcherTimer? _motionTimer;
    private bool _attached;

    public string Period { get => GetValue(PeriodProperty); set => SetValue(PeriodProperty, value); }
    public bool IsAnimated { get => GetValue(IsAnimatedProperty); set => SetValue(IsAnimatedProperty, value); }
    public WeatherScene Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public double WindKmh { get => GetValue(WindKmhProperty); set => SetValue(WindKmhProperty, value); }
    public double CloudPercent { get => GetValue(CloudPercentProperty); set => SetValue(CloudPercentProperty, value); }
    public double Precipitation { get => GetValue(PrecipitationProperty); set => SetValue(PrecipitationProperty, value); }

    static OverviewSky() => AffectsRender<OverviewSky>(PeriodProperty, SceneProperty, WindKmhProperty, CloudPercentProperty, PrecipitationProperty, IsAnimatedProperty, ThemeVariantScope.ActualThemeVariantProperty);

    public static string PeriodAt(int hour) => hour is >= 6 and < 17 ? "Day" : hour is >= 17 and < 19 ? "Dusk" : "Night";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateMotion();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        UpdateMotion();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnimatedProperty) UpdateMotion();
        if (change.Property == SceneProperty)
        {
            _previousScene = change.GetOldValue<WeatherScene>();
            _blendStart = _motionTime.Elapsed.TotalSeconds;
        }
    }

    private void UpdateMotion()
    {
        if (_attached && IsAnimated)
        {
            if (_motionTimer == null)
            {
                // 25 帧只重绘这张小卡；所有图形相位由实际经过时间计算，不积累 Tick 误差。
                _motionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                _motionTimer.Tick += (_, _) => InvalidateVisual();
            }
            _motionTime.Start();
            _motionTimer.Start();
        }
        else
        {
            _motionTimer?.Stop();
            _motionTime.Stop();
        }
    }

    private Color Token(string name) => this.TryFindResource("SctSky" + name + "Color", ActualThemeVariant, out var value)
        && value is Color color ? color : Colors.Transparent;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        double time = _motionTime.Elapsed.TotalSeconds;
        double blend = IsAnimated ? Math.Clamp((time - _blendStart) / 0.8, 0, 1) : 1;
        if (blend < 1)
        {
            DrawSky(context, bounds, time, _previousScene);
            using (context.PushOpacity(blend * blend * (3 - 2 * blend))) DrawSky(context, bounds, time, Scene);
        }
        else DrawSky(context, bounds, time, Scene);
    }

    private void DrawSky(DrawingContext context, Rect bounds, double time, WeatherScene scene)
    {
        bool night = Period == "Night";
        bool covered = scene is WeatherScene.Overcast or WeatherScene.Fog or WeatherScene.Rain or WeatherScene.Snow or WeatherScene.Thunder;
        var background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new(Token(Period + "Start"), 0), new(Token(Period + "Start"), 0.45),
                new(Token(covered ? (night ? "NightStorm" : "DayStorm") : Period + "End"), 1)
            }
        };
        context.DrawRectangle(background, null, bounds);
        // 装饰集中在右侧，左侧保留稳定的正文对比度。
        double x = bounds.Width * 0.82, y = bounds.Height * 0.27 + 2 * Math.Sin(time * 0.25);
        var light = new SolidColorBrush(Token(night ? "Star" : "Sun"));
        if (night && !covered)
        {
            using (context.PushOpacity(.045)) context.DrawEllipse(light, null, new Point(x, y), 44, 44);
            using (context.PushOpacity(.07)) context.DrawEllipse(light, null, new Point(x, y), 34, 34);
            using (context.PushTransform(Matrix.CreateTranslation(x, y))) context.DrawGeometry(light, null, MoonShape);
            int index = 0;
            foreach (var star in new[] { (0.60, 0.13, 1.1), (0.92, 0.17, 1.8), (0.72, 0.48, 1.3), (0.95, 0.56, 1.5), (0.65, 0.57, 1.2), (0.71, 0.08, 1.0), (0.98, 0.31, 1.0), (0.57, 0.36, 1.2) })
            {
                using (context.PushOpacity(0.65 + 0.30 * Math.Sin(time * 0.6 + index++ * 1.7)))
                    context.DrawEllipse(light, null, new Point(bounds.Width * star.Item1, bounds.Height * star.Item2), star.Item3, star.Item3);
            }
            var pen = new Pen(light, 1.2);
            var p = new Point(bounds.Width * 0.70, bounds.Height * 0.27);
            context.DrawLine(pen, p - new Vector(4, 0), p + new Vector(4, 0));
            context.DrawLine(pen, p - new Vector(0, 4), p + new Vector(0, 4));
            // 偶尔划过的小流星，局限装饰区；静态模式不显示移动轨迹。
            double shooting = time % 28;
            if (IsAnimated && scene == WeatherScene.Clear && shooting is > 22 and < 23.5)
            {
                double t = (shooting - 22) / 1.5;
                using (context.PushOpacity(Math.Sin(t * Math.PI) * .45))
                {
                    var head = new Point(bounds.Width * (.97 - .22 * t), bounds.Height * (.08 + .24 * t));
                    context.DrawLine(pen, head, head + new Vector(12, -6));
                }
            }
        }
        else if (!night && !covered)
        {
            using (context.PushOpacity(0.035 + 0.01 * Math.Sin(time * 0.35))) context.DrawEllipse(light, null, new Point(x, y), 51, 51);
            using (context.PushOpacity(0.075 + 0.015 * Math.Sin(time * 0.35))) context.DrawEllipse(light, null, new Point(x, y), 40, 40);
            using (context.PushOpacity(.65))
                for (int i = 0; i < 10; i++)
                {
                    double angle = i * Math.PI / 5 + time * .025;
                    var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
                    context.DrawLine(new Pen(light, 1.5), new Point(x, y) + direction * 32, new Point(x, y) + direction * 37);
                }
            context.DrawEllipse(light, null, new Point(x, y), 25, 25);
        }
        var cloud = new SolidColorBrush(Token(covered ? (night ? "NightCloud" : "DayCloud") : "Cloud"));
        double speed = .15 + Math.Clamp(WindKmh, 0, 50) / 180;
        using (context.PushOpacity(covered ? .65 : night ? .12 : .55))
        {
            if (!night && scene == WeatherScene.Clear)
            {
                // 晴天只留右侧边缘云；包含漂移余量，避免半透明云叠在日面上形成月牙状亮斑。
                double edgeX = Math.Max(bounds.Width * .99, x + 25 + 60 * .55 + 14 + 8);
                Cloud(context, cloud, edgeX + 14 * Math.Sin(time * speed), bounds.Height * .46, .55);
                Cloud(context, cloud, edgeX - 8 * Math.Sin(time * speed * .8), bounds.Height * .17, .5);
            }
            else
            {
                Cloud(context, cloud, bounds.Width * .86 + 14 * Math.Sin(time * speed), bounds.Height * (covered ? .24 : .46), covered ? 1.1 : .75);
                if (scene != WeatherScene.Clear || CloudPercent > 35)
                    Cloud(context, cloud, bounds.Width * .70 - 10 * Math.Sin(time * speed * .7), bounds.Height * .32, .7);
                Cloud(context, cloud, bounds.Width * .99 - 8 * Math.Sin(time * speed * .8), bounds.Height * .17, .5);
            }
        }
        using (context.PushClip(new Rect(bounds.Width * .53, 0, bounds.Width * .47, bounds.Height * .69)))
        {
            if (scene is WeatherScene.Rain or WeatherScene.Snow or WeatherScene.Thunder)
            {
                bool snow = scene == WeatherScene.Snow;
                var brush = new SolidColorBrush(Token(snow ? "Snow" : night ? "NightRain" : "DayRain"));
                int count = snow ? 26 : Precipitation >= 3 || scene == WeatherScene.Thunder ? 42 : 24;
                var pen = new Pen(brush, snow ? 1 : 1.1);
                for (int i = 0; i < count; i++)
                {
                    double seed = (i * .61803398875) % 1;
                    double px = bounds.Width * (.56 + .44 * seed) + (snow ? 5 * Math.Sin(time * .5 + i) : 0);
                    double py = 50 + ((time * (snow ? 17 : 62) + i * 27) % (bounds.Height * .57));
                    using (context.PushOpacity((night ? .40 : .55) * (.5 + seed * .5)))
                    {
                        if (snow)
                        {
                            context.DrawEllipse(brush, null, new Point(px, py), 1.1 + seed, 1.1 + seed);
                            if (i % 5 == 0)
                            {
                                context.DrawLine(pen, new Point(px - 3, py), new Point(px + 3, py));
                                context.DrawLine(pen, new Point(px, py - 3), new Point(px, py + 3));
                            }
                        }
                        else context.DrawLine(pen, new Point(px, py), new Point(px - 2 - Math.Min(WindKmh, 50) * .07, py + 9));
                    }
                }
            }
            if (scene == WeatherScene.Fog)
                for (int i = 0; i < 4; i++)
                    using (context.PushOpacity(.08 + i * .02))
                    {
                        double fogX = bounds.Width * .85 + 12 * Math.Sin(time * .12 + i);
                        context.DrawEllipse(cloud, null, new Point(fogX, 55 + i * 22), bounds.Width * .34, 20);
                    }
            if (scene == WeatherScene.Thunder)
            {
                // 云内缓慢泛光，不使用整卡闪白或高频闪烁。
                using (context.PushOpacity(.10 * Math.Pow(.5 + .5 * Math.Sin(time * .65), 6)))
                    context.DrawEllipse(new SolidColorBrush(Token("Star")), null, new Point(x, 48), 45, 18);
            }
        }
    }

    private static void Cloud(DrawingContext context, IBrush brush, double x, double y, double scale)
    {
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
        {
            // 云形合并成一条轮廓，避免半透明椭圆叠加产生接缝。
            context.DrawGeometry(brush, null, CloudShape);
        }
    }
}
