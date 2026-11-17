using System;
using Avalonia.Input;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Services;

/// <summary>
/// <see cref="AnnotationEditSession"/> 的滚轮半边：标注期滚轮的八档分派表，逐位搬自贴图侧
/// （与截图遮罩 <c>CaptureSession</c> 同一张表），使录屏接上的是同一份"滚一下改哪一格"。
///
/// <para>三条硬语义与截图同源：
/// ① 落笔/拖动中滚轮空消费——截图靠 <c>!drawingStroke…</c> 加 <c>activeAnnotationTool != None</c>
/// 两道守卫，这里合成一条 <c>IsDrawing</c> 判据（控制器 <c>Begin</c> 起笔即冻结参数，
/// 此刻改工具滑杆本就不回灌当前这一笔）；
/// ② 画笔滚轮只改工具默认，选中那条笔迹的粗细一律不动（protection.md ④「画笔粗细属于这一笔」）；
/// ③ 图元微调延迟提交、一段一条撤销——贴图的一次 Commit 要克隆整张干净底图，
/// 连续滚十几档若按截图那样一档一条，就是每秒几十次整图重烘。</para>
/// </summary>
internal sealed partial class AnnotationEditSession
{
    /// <summary>返回 true＝这一格滚轮已被标注消费掉，宿主不得再拿它去缩放窗口。</summary>
    public bool TryHandleWheel(PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return false;
        // 滚轮归标注的每一档都就地消费掉；Consume 只负责留一条"为什么没缩放"的日志
        bool Consume(string what)
        {
            _host.LogDiag("滚轮归标注 " + what);
            return true;
        }

        // 落笔中 / 拖动图元中：只消费、不改动。绝不让事件继续往下走到缩放——那一路会 Cancel 掉正在画的
        // 这一笔、连带退出标注，正是用户报的"绘制时滚轮退出绘制"。
        if (Controller.IsDrawing) return Consume("落笔中");
        if (_shapeEditor?.IsEditing == true) return Consume("图元拖动中");
        // 没有参数条就谈不上"调参"，交回原有语义（正常进标注时条必在）
        if (_host.EditToolbar is not { } tb) return false;

        ToolMode tool = _host.ActiveAnnotationTool;
        AnnotationShape? selected = SelectedShape();
        KeyModifiers mods = e.KeyModifiers;
        int dir = e.Delta.Y > 0 ? 1 : -1;

        // ① 马赛克工具 + 选中模糊图元：改这块的强度并回灌工具滑杆（截图同档也是"改图元 + Sync"两条动作）
        if (tool == ToolMode.Blur && selected is BlurShape blur)
        {
            int nextLevel = BlurIntensityMapper.ClampUiLevel(blur.UiLevel + dir);
            if (nextLevel == blur.UiLevel) return Consume("马赛克强度已到边界 level=" + blur.UiLevel);
            ReplaceSelectedShapeByWheel(blur.WithUiLevel(nextLevel));
            tb.BlurLevelSlider.Value = nextLevel;
            return Consume("马赛克强度→" + nextLevel);
        }

        // ② 聚光工具：滚轮只写滑杆，落地交给参数条那一条报点通路（StylePartChanged→SpotlightSettings→分派表）。
        if (tool == ToolMode.Spotlight)
        {
            // Ctrl+滚轮＝改边框粗细（2026-09-18 用户要求，与截图侧同一改口）：原先 Ctrl 是暗幕的细步档，
            // 现已让位给粗细。夹取区间一律取描边滑杆的 Min/Max（区间真源在滑杆上，这里不另写一份常量）。
            if (mods.HasFlag(KeyModifiers.Control))
            {
                double strokeStep = 1.0;
                var strokeSlider = tb.SpotlightStrokeSlider;
                if (selected is SpotlightShape spot)
                {
                    double nextStroke = Math.Clamp(Math.Round((double)spot.StrokeThickness) + dir * strokeStep,
                        strokeSlider.Minimum, strokeSlider.Maximum);
                    if (Math.Abs(nextStroke - spot.StrokeThickness) <= 0.0001)
                        return Consume("聚光描边已到边界 thickness=" + spot.StrokeThickness);
                    // 只换粗细：暗幕/形状类别/颜色/圆角全部原样透传，滚轮不该顺手改掉别的档
                    ReplaceSelectedShapeByWheel(spot.WithStyle(spot.Darkness, spot.ShapeKind,
                        spot.StrokeColor, (float)nextStroke));
                    strokeSlider.Value = nextStroke;   // 回灌工具默认：下一颗同档（与 ①马赛克、④序号同一套路）
                    return Consume("聚光描边→" + nextStroke);
                }
                strokeSlider.Value += dir * strokeStep;   // 没有选中聚光灯＝只改下一颗的默认粗细
                return Consume("聚光默认描边→" + tb.SpotlightStrokeThickness);
            }

            double step = 5.0;
            if (mods.HasFlag(KeyModifiers.Shift)) step = 10.0;
            tb.SpotlightDarknessSlider.Value += dir * step;   // Slider 自己夹到 Min/Max
            return Consume("聚光暗幕→" + tb.SpotlightDarkness.ToString("F2"));
        }

        // ③ 选中箭头：整体缩放 ×1.1，并把该值带走当下一支箭头的默认（截图同档也回灌 currentArrowScale）
        if (selected is ArrowShape arrow)
        {
            float factor = dir > 0 ? 1.1f : (1.0f / 1.1f);
            float nextScale = Math.Clamp(arrow.Scale * factor, 0.2f, 4.0f);
            if (Math.Abs(nextScale - arrow.Scale) <= 0.0001f) return Consume("箭头缩放已到边界 scale=" + arrow.Scale.ToString("F2"));
            ArrowScale = nextScale;
            ReplaceSelectedShapeByWheel(arrow.WithScale(nextScale));
            return Consume("箭头缩放→" + nextScale.ToString("F2"));
        }

        // ④ 选中序号：Ctrl+滚轮改编号、滚轮改大小并回灌工具滑杆（截图同档也调 SyncCounterRadiusSlider）
        if (selected is CounterShape counter)
        {
            if (mods.HasFlag(KeyModifiers.Control))
            {
                int numberStep = mods.HasFlag(KeyModifiers.Shift) ? 5 : 1;
                int nextNumber = Math.Max(1, counter.Number + dir * numberStep);
                if (nextNumber == counter.Number) return Consume("编号已到下限 number=" + counter.Number);
                // WithNumber 而不是重建：引线、说明文字、类型档、形状与底色都得原样留下
                ReplaceSelectedShapeByWheel(counter.WithNumber(nextNumber));
                return Consume("序号编号→" + nextNumber);
            }

            float step = mods.HasFlag(KeyModifiers.Shift) ? 8f : 2f;
            float nextRadius = Math.Clamp(counter.Radius + dir * step, 8f, 80f);
            if (Math.Abs(nextRadius - counter.Radius) <= 0.0001f) return Consume("序号半径已到边界 radius=" + counter.Radius);
            // WithRadius 同理：整颗重建会把 2026-09-18 那批新字段（引线/说明/类型/形状/底色）抹平
            ReplaceSelectedShapeByWheel(counter.WithRadius(nextRadius));
            tb.CounterRadiusSlider.Value = nextRadius;
            return Consume("序号半径→" + nextRadius);
        }

        // ⑤ 选中矩形：改描边粗细，夹到工具滑杆区间（截图同档同样不回灌工具默认）
        if (selected is RectangleShape rectangle)
        {
            int step = mods.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            // 图元粗细是 float、滑杆区间是 double，一律升到 double 再夹，取完再转回 float
            double nextThickness = Math.Clamp((double)rectangle.StrokeThickness + dir * step,
                tb.StrokeThicknessSlider.Minimum, tb.StrokeThicknessSlider.Maximum);
            if (Math.Abs(nextThickness - rectangle.StrokeThickness) <= 0.0001) return Consume("矩形粗细已到边界 thickness=" + rectangle.StrokeThickness);
            ReplaceSelectedShapeByWheel(rectangle.WithThickness((float)nextThickness));
            return Consume("矩形粗细→" + nextThickness);
        }

        // ⑥ 画笔/箭头/矩形工具：改工具默认粗细，只影响下一笔。注意这里刻意不碰选中的那条笔迹——
        // 笔迹类型不在这条 if 的名单里，protection.md ④ 就是靠这个名单守住的。
        if (tool is ToolMode.Brush or ToolMode.Arrow or ToolMode.Rect)
        {
            int step = mods.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            tb.StrokeThicknessSlider.Value += dir * step;
            return Consume("默认粗细→" + tb.StrokeThickness);
        }

        // ⑦ 文字工具：改默认字号；正选中某个文字层时，CurrentTextSize 的 setter 会连带改它
        if (tool == ToolMode.Text)
        {
            double size = _texts.CurrentTextSize;
            double step = size >= 48 ? 8 : size >= 24 ? 4 : 2;
            double nextSize = Math.Clamp(size + dir * step, 8.0, 200.0);
            if (Math.Abs(nextSize - size) <= 0.1) return Consume("字号已到边界 size=" + size);
            bool touchedLayer = _texts.SelectedText != null;
            _texts.CurrentTextSize = nextSize;
            // 只有真的改到了文字层才压历史：默认字号本身不进文档，
            // 压一条空历史会制造"按了撤销画面却不动"的假故障。
            if (touchedLayer) CommitTextChange();
            return Consume("字号→" + nextSize.ToString("F0") + " 连带文字层=" + touchedLayer);
        }

        // ⑧ 序号工具：改默认半径。截图这里本会直落"空消费"档，贴图按"选了工具就调参"补成可调
        if (tool == ToolMode.Counter)
        {
            int step = mods.HasFlag(KeyModifiers.Shift) ? 8 : 2;
            tb.CounterRadiusSlider.Value += dir * step;
            return Consume("默认序号半径→" + tb.CounterRadius);
        }

        // ⑨ 橡皮擦（仅画笔档）：滚轮改笔触大小，与 ⑥⑧ 同一套路——只写滑杆值，回显数字与"冻结进下一笔"
        // 都由滑杆那条 ValueChanged→ParametersChanged 现成通路负责，夹取区间归滑杆自身（不在此复制常数）。
        // 矩形档参数条上根本没有"大小"这一格，故不接管，交回宿主原有语义（贴图＝缩放、录屏＝不消费）。
        if (tool == ToolMode.Eraser && tb.EraserMode == EraserGestureMode.Brush)
        {
            double step = mods.HasFlag(KeyModifiers.Shift) ? 8 : 2;
            tb.EraserSizeSlider.Value += dir * step;   // Slider 自己夹到 Min/Max
            return Consume("橡皮擦笔触→" + tb.EraserDiameter);
        }

        // 还选着别的工具＝到此为止，绝不缩放（截图那条 activeAnnotationTool != None 的守卫）
        return tool != ToolMode.None;
    }

    /// <summary>用现算的新实例替换当前选中的矢量图元，并并进"一段连续改动只压一条撤销"的会话。
    /// 刻意复用方向键步进的那两个字段：滚轮段与按键段不会同时发生，而"换目标即先结清上一段"的规则一致，
    /// 两种手势交替时历史仍逐段分开。图层订阅了 <c>Document.Changed</c> 会自行重绘，无需在此 Invalidate。</summary>
    private void ReplaceSelectedShapeByWheel(AnnotationShape next)
    {
        var history = _host.History;
        if (history == null) return;
        if (_stepTargetId != next.Id)
        {
            FlushNudgeSession();
            _stepTargetId = next.Id;
        }
        history.Document.ReplaceShape(next);
        _stepPending = true;
        _host.LogDiag("滚轮微调图元 kind=" + next.Kind + " id=" + next.Id.ToString("N") + " 图元数=" + history.Document.Count);
    }
}
