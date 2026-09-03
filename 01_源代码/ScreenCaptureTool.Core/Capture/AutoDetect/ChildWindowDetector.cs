using System;
using System.Drawing;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// 轻量子窗口检测：只使用 Win32 HWND 命中，不走 MSAA/COM。
    /// 用于 Auto 模式的“窗口优先 + 原生子控件吸附”，避免自动模式卡顿。
    /// </summary>
    public sealed class ChildWindowDetector
    {
        public IntPtr IgnoredWindow { get; set; }

        public Rectangle? DetectChildWindowAt(Point screenPoint, Rectangle? parentWindowBounds)
        {
            if (!parentWindowBounds.HasValue || parentWindowBounds.Value.IsEmpty) return null;

            try
            {
                IntPtr raw = WindowHelper.GetWindowFromPointIgnoringWindow(screenPoint, IgnoredWindow);
                if (raw == IntPtr.Zero || raw == IgnoredWindow) return null;

                IntPtr root = NativeMethods.GetAncestor(raw, NativeMethods.GA_ROOT);
                if (root == IntPtr.Zero || root == raw) return null;
                if (root == IgnoredWindow) return null;

                Rectangle childBounds = WindowHelper.GetWindowBounds(raw);
                if (!IsUsefulChildBounds(childBounds, parentWindowBounds.Value, screenPoint)) return null;

                return childBounds;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUsefulChildBounds(Rectangle child, Rectangle parent, Point pointer)
        {
            if (child.IsEmpty || child.Width < 8 || child.Height < 8) return false;
            if (!child.Contains(pointer)) return false;
            if (!parent.Contains(pointer)) return false;

            Rectangle clipped = Rectangle.Intersect(child, parent);
            if (clipped.Width < 8 || clipped.Height < 8) return false;

            double parentArea = Math.Max(1.0, (double)parent.Width * parent.Height);
            double childArea = (double)clipped.Width * clipped.Height;
            if (childArea > parentArea * 0.45) return false;
            if (clipped.Width > parent.Width * 0.82 || clipped.Height > parent.Height * 0.82) return false;

            return true;
        }
    }
}
