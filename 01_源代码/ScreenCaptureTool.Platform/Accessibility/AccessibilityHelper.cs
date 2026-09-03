using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform.Accessibility
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct VARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr data1;
        public IntPtr data2;

        public const ushort VT_I4 = 3;
        public const ushort VT_DISPATCH = 9;
        public const ushort VT_UNKNOWN = 13;
    }

    /// <summary>
    /// 一次 MSAA 命中：根 IAccessible 指针 + 子 ID + 句柄信息。
    /// COM 对象指针由本类持有，必须由调用方负责 ReleaseSafely 释放。
    /// </summary>
    public sealed class AccessibleHit
    {
        /// <summary>顶级窗口的根 IAccessible 指针。</summary>
        internal IntPtr RootAccessible { get; }

        /// <summary>accHitTest 直接返回的子 IAccessible 指针。</summary>
        internal IntPtr ChildAccessible { get; }

        /// <summary>命中的子 ID（若 hit-test 返回的是某个 child id）；否则为 CHILDID_SELF=0。</summary>
        public int ChildId { get; }

        /// <summary>真正承载这次命中的句柄。</summary>
        public IntPtr Hwnd { get; }

        internal AccessibleHit(IntPtr root, int childId, IntPtr hwnd, IntPtr childAccessible = default)
        {
            RootAccessible = root;
            ChildAccessible = childAccessible;
            ChildId = childId;
            Hwnd = hwnd;
        }
    }

    /// <summary>
    /// MSAA 高层封装。Core 通过本类访问可访问对象，不直接接触 COM 接口类型，以兼容 NativeAOT 模式下 COM 互操作的限制。
    /// </summary>
    public static class AccessibilityHelper
    {
        public static Action<string>? LogHandler { get; set; }

        public static AccessibleHit? GetAccessibleAtPoint(Point screenPoint)
        {
            return GetAccessibleAtPoint(screenPoint, IntPtr.Zero);
        }

        public static unsafe AccessibleHit? GetAccessibleAtPoint(Point screenPoint, IntPtr ignoredWindow)
        {
            IntPtr hwnd = ignoredWindow != IntPtr.Zero
                ? WindowHelper.GetTopLevelWindowFromPointIgnoringWindow(screenPoint, ignoredWindow)
                : WindowHelper.GetTopLevelWindowFromPoint(screenPoint);
            if (hwnd == IntPtr.Zero || hwnd == ignoredWindow) return null;

            IntPtr pAcc = IntPtr.Zero;
            try
            {
                Guid iid = NativeMethods.IID_IAccessible;
                int hr = NativeMethods.AccessibleObjectFromWindow(
                    hwnd, NativeMethods.OBJID_CLIENT, ref iid, out pAcc);

                if (hr != 0 || pAcc == IntPtr.Zero)
                {
                    if (hr != 0) LogHandler?.Invoke($"AccessibleObjectFromWindow failed with hr = 0x{hr:X}");
                    return null;
                }

                IntPtr* vtable = *(IntPtr**)pAcc;
                // accHitTest at slot 24: HRESULT accHitTest(long xLeft, long yTop, VARIANT *pvarChild);
                delegate* unmanaged[Stdcall]<IntPtr, int, int, out VARIANT, int> accHitTestFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, int, int, out VARIANT, int>)vtable[24];

                int childId = NativeMethods.CHILDID_SELF;
                IntPtr childAccessible = IntPtr.Zero;

                try
                {
                    VARIANT hit;
                    int hitHr = accHitTestFunc(pAcc, screenPoint.X, screenPoint.Y, out hit);
                    if (hitHr == 0)
                    {
                        if (hit.vt == VARIANT.VT_I4)
                        {
                            childId = (int)hit.data1;
                            if (childId == NativeMethods.CHILDID_SELF)
                            {
                                // 命中根节点本身（没命中任何子元素），对截图意义不大，丢弃。
                                delegate* unmanaged[Stdcall]<IntPtr, uint> releaseFunc = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
                                releaseFunc(pAcc);
                                return null;
                            }
                        }
                        else if (hit.vt == VARIANT.VT_DISPATCH || hit.vt == VARIANT.VT_UNKNOWN)
                        {
                            childAccessible = hit.data1;
                            childId = NativeMethods.CHILDID_SELF;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogHandler?.Invoke("accHitTest threw: " + ex.Message);
                    childId = NativeMethods.CHILDID_SELF;
                }

                return new AccessibleHit(pAcc, childId, hwnd, childAccessible);
            }
            catch (Exception ex)
            {
                LogHandler?.Invoke("GetAccessibleAtPoint exception: " + ex.ToString());
                if (pAcc != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr* vtable = *(IntPtr**)pAcc;
                        delegate* unmanaged[Stdcall]<IntPtr, uint> releaseFunc = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
                        releaseFunc(pAcc);
                    }
                    catch { }
                }
                return null;
            }
        }

        public static unsafe Rectangle GetBounds(AccessibleHit hit)
        {
            if (hit == null || hit.RootAccessible == IntPtr.Zero) return Rectangle.Empty;

            try
            {
                IntPtr target = hit.ChildAccessible != IntPtr.Zero ? hit.ChildAccessible : hit.RootAccessible;
                IntPtr* vtable = *(IntPtr**)target;

                // accLocation at slot 22: HRESULT accLocation(long *pxLeft, long *pyTop, long *pcxWidth, long *pcyHeight, VARIANT varChild);
                delegate* unmanaged[Stdcall]<IntPtr, out int, out int, out int, out int, VARIANT, int> accLocationFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, out int, out int, out int, out int, VARIANT, int>)vtable[22];

                VARIANT varChild = default;
                if (hit.ChildAccessible != IntPtr.Zero)
                {
                    varChild.vt = VARIANT.VT_I4;
                    varChild.data1 = (IntPtr)NativeMethods.CHILDID_SELF;
                }
                else
                {
                    varChild.vt = VARIANT.VT_I4;
                    varChild.data1 = (IntPtr)hit.ChildId;
                }

                int x, y, w, h;
                int locHr = accLocationFunc(target, out x, out y, out w, out h, varChild);

                if (locHr != 0 || w <= 0 || h <= 0) return Rectangle.Empty;
                return new Rectangle(x, y, w, h);
            }
            catch
            {
                return Rectangle.Empty;
            }
        }

        public static unsafe void ReleaseSafely(AccessibleHit? hit)
        {
            if (hit == null) return;
            if (hit.ChildAccessible != IntPtr.Zero)
            {
                try
                {
                    IntPtr* vtable = *(IntPtr**)hit.ChildAccessible;
                    delegate* unmanaged[Stdcall]<IntPtr, uint> releaseFunc = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
                    releaseFunc(hit.ChildAccessible);
                }
                catch { }
            }
            if (hit.RootAccessible != IntPtr.Zero)
            {
                try
                {
                    IntPtr* vtable = *(IntPtr**)hit.RootAccessible;
                    delegate* unmanaged[Stdcall]<IntPtr, uint> releaseFunc = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
                    releaseFunc(hit.RootAccessible);
                }
                catch { }
            }
        }
    }
}
