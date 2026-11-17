using System;
using System.Collections.Generic;
using System.Drawing;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Platform.Accessibility;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// 单个顶级窗口的 UIA 元素矩形快照。
    ///
    /// 设计动机（2026-08-25 重构，取代旧的 ElementFromPoint 逐点探测）：
    /// - UIA <c>ElementFromPoint</c> 依赖提供方/核心的命中下钻，对 Chromium/Electron 等自绘应用
    ///   经常只返回到窗口/渲染层（表现为"探测不进去"）；而这些应用的控件子树本身是完整的。
    /// - 截图会话期间屏幕内容被冻结，元素几何在一个会话内不会变化——快照天然贴合该语义。
    ///
    /// 工作方式：
    /// - <see cref="Build"/> 用带 <c>TreeScope_Subtree</c> 的 CacheRequest 一次 COM 往返拉取整棵
    ///   控件子树的包围矩形（实测 100~1200 节点的窗口约 60~180ms，每窗口每会话仅一次）。
    /// - <see cref="HitTestChain"/> 在内存中做纯几何命中：包含点的矩形按面积升序即为
    ///   "最深层元素 → 窗口"的层级链，无需 TreeWalker 父链回溯。
    ///
    /// 线程模型：Build 可在任意线程调用（CUIAutomation 为 free-threaded）；
    /// 构建完成后对象不可变，可安全跨线程读取。
    /// </summary>
    internal sealed class ElementSnapshot
    {
        // UIA_BoundingRectanglePropertyId / UIA_IsOffscreenPropertyId
        private static readonly int[] CachedPropertyIds = { 30001, 30022 };

        // 防御性上限：病态巨型树（某些 Chromium 页面可达数万节点）直接截断，避免内存与时间失控
        private const int MaxNodes = 6000;

        /// <summary>快照对应的顶级窗口句柄。</summary>
        public IntPtr Hwnd { get; }

        /// <summary>窗口外框（DWM 延展框，与用户视觉一致），作为命中链的最终兜底层级。</summary>
        public Rectangle WindowRect { get; }

        /// <summary>全部有效元素矩形（屏幕物理像素）。不含窗口根元素自身（由 <see cref="WindowRect"/> 承担）。</summary>
        public Rectangle[] Rects { get; }

        private ElementSnapshot(IntPtr hwnd, Rectangle windowRect, Rectangle[] rects)
        {
            Hwnd = hwnd;
            WindowRect = windowRect;
            Rects = rects;
        }

        /// <summary>
        /// 构建指定顶级窗口的元素快照。窗口无可用 UIA 子树（未激活/不支持）时返回 null。
        /// 耗时随节点数线性增长（一次批量 COM 往返），调用方应放在后台线程。
        /// </summary>
        public static ElementSnapshot? Build(IntPtr hwnd, Rectangle windowRect)
        {
            if (hwnd == IntPtr.Zero) return null;

            IntPtr pReq = IntPtr.Zero;
            IntPtr pRoot = IntPtr.Zero;
            try
            {
                pReq = UIAutomationHelper.CreateCacheRequest(CachedPropertyIds, UIAutomationHelper.TreeScopeSubtree);
                if (pReq == IntPtr.Zero) return null;

                // 一次 COM 往返：整棵子树的 BoundingRectangle/IsOffscreen 随元素一起缓存返回
                pRoot = UIAutomationHelper.ElementFromHandleBuildCache(hwnd, pReq);
                if (pRoot == IntPtr.Zero) return null;

                var rects = new List<Rectangle>(256);
                var stack = new Stack<IntPtr>();
                stack.Push(pRoot);
                pRoot = IntPtr.Zero; // 所有权移交给栈，循环内统一释放
                bool isRoot = true;

                while (stack.Count > 0)
                {
                    IntPtr pElement = stack.Pop();
                    try
                    {
                        if (!UIAutomationHelper.GetCachedIsOffscreen(pElement))
                        {
                            Rectangle? r = UIAutomationHelper.GetCachedBoundingRectangle(pElement);
                            // 根元素自身矩形跳过（由 WindowRect 作为链尾兜底，避免与 DWM 边框矩形重复）
                            if (!isRoot && r.HasValue && r.Value.Width >= 2 && r.Value.Height >= 2)
                                rects.Add(r.Value);
                        }
                        isRoot = false;

                        if (rects.Count >= MaxNodes) break;

                        IntPtr pArray = UIAutomationHelper.GetCachedChildren(pElement);
                        if (pArray != IntPtr.Zero)
                        {
                            try
                            {
                                int count = UIAutomationHelper.GetElementArrayLength(pArray);
                                for (int i = 0; i < count; i++)
                                {
                                    IntPtr pChild = UIAutomationHelper.GetElementArrayItem(pArray, i);
                                    if (pChild != IntPtr.Zero) stack.Push(pChild);
                                }
                            }
                            finally
                            {
                                UIAutomationHelper.ReleaseCOM(pArray);
                            }
                        }
                    }
                    finally
                    {
                        UIAutomationHelper.ReleaseCOM(pElement);
                    }
                }

                // 栈中剩余元素（因 MaxNodes 截断）也要释放
                while (stack.Count > 0)
                    UIAutomationHelper.ReleaseCOM(stack.Pop());

                return new ElementSnapshot(hwnd, windowRect, rects.ToArray());
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ElementSnapshot.Build threw: " + ex.Message);
                if (pRoot != IntPtr.Zero) UIAutomationHelper.ReleaseCOM(pRoot);
                return null;
            }
            finally
            {
                if (pReq != IntPtr.Zero) UIAutomationHelper.ReleaseCOM(pReq);
            }
        }

        /// <summary>
        /// 内存命中测试：返回"最深层元素 → 窗口"的矩形链（链首最小，滚轮可逐级放大）。
        /// 无命中时返回 null（调用方应退回窗口级兜底）。
        /// </summary>
        public Rectangle[]? HitTestChain(Point screenPoint, int maxDepth = 15)
        {
            // 1. 收集所有包含点的矩形，按面积升序（最小 = 最深层）
            List<Rectangle>? hits = null;
            Rectangle[] rects = Rects;
            for (int i = 0; i < rects.Length; i++)
            {
                if (rects[i].Contains(screenPoint))
                    (hits ??= new List<Rectangle>(16)).Add(rects[i]);
            }
            if (hits == null || hits.Count == 0) return null;

            hits.Sort(static (a, b) => ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height));

            // 2. 以链首（最小矩形）中心为锚，剔除"包含光标但并非其祖先"的重叠兄弟节点：
            //    祖先链上的矩形必然同时包含光标与链首中心
            Rectangle head = hits[0];
            var anchor = new Point(head.Left + head.Width / 2, head.Top + head.Height / 2);

            var chain = new List<Rectangle>(maxDepth);
            foreach (Rectangle r in hits)
            {
                if (chain.Count >= maxDepth) break;
                if (!r.Equals(head) && !r.Contains(anchor)) continue;
                if (ContainsSimilar(chain, r)) continue; // 近似去重（Chromium 常见多层同矩形嵌套）
                chain.Add(r);
            }

            // 3. 窗口矩形收尾（DWM 视觉边框），保证链尾一定是窗口级
            if (WindowRect.Width > 0 && WindowRect.Height > 0 &&
                WindowRect.Contains(screenPoint) && !ContainsSimilar(chain, WindowRect) &&
                chain.Count < maxDepth)
            {
                chain.Add(WindowRect);
            }

            return chain.Count > 0 ? chain.ToArray() : null;
        }

        /// <summary>近似去重：四边偏差均在小容差内视为同一矩形（±8px 覆盖 DWM 不可见边框差）。</summary>
        private static bool ContainsSimilar(List<Rectangle> chain, Rectangle r)
        {
            const int tol = 8;
            for (int i = 0; i < chain.Count; i++)
            {
                Rectangle c = chain[i];
                if (Math.Abs(c.Left - r.Left) <= tol && Math.Abs(c.Top - r.Top) <= tol &&
                    Math.Abs(c.Width - r.Width) <= tol * 2 && Math.Abs(c.Height - r.Height) <= tol * 2)
                    return true;
            }
            return false;
        }
    }
}
