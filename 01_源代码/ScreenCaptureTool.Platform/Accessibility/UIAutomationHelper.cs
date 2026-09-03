using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform.Accessibility
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct UiaPoint
    {
        public int x;
        public int y;
        public UiaPoint(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UiaRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    public static class UIAutomationHelper
    {
        private static readonly Guid CLSID_CUIAutomation = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");
        private static readonly Guid IID_IUIAutomation = new Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

        private static IntPtr _pAutomation = IntPtr.Zero;
        private static readonly object _lock = new object();
        private static bool _initAttempted = false;

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IntPtr ppv);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitialize(IntPtr pvReserved);

        public static Action<string>? LogHandler { get; set; }

        private static unsafe IntPtr GetAutomationInstance()
        {
            lock (_lock)
            {
                if (_pAutomation != IntPtr.Zero)
                    return _pAutomation;

                if (_initAttempted && _pAutomation == IntPtr.Zero)
                    return IntPtr.Zero;

                _initAttempted = true;

                int hr = CoCreateInstance(CLSID_CUIAutomation, IntPtr.Zero, 1, IID_IUIAutomation, out IntPtr pAutomation);
                if (hr == unchecked((int)0x800401F0)) // CO_E_NOTINITIALIZED
                {
                    CoInitialize(IntPtr.Zero);
                    hr = CoCreateInstance(CLSID_CUIAutomation, IntPtr.Zero, 1, IID_IUIAutomation, out pAutomation);
                }

                if (hr == 0 && pAutomation != IntPtr.Zero)
                {
                    _pAutomation = pAutomation;
                    LogHandler?.Invoke("UIAutomationHelper: Successfully created IUIAutomation instance.");
                }
                else
                {
                    LogHandler?.Invoke($"UIAutomationHelper: CoCreateInstance failed with hr = 0x{hr:X}");
                }

                return _pAutomation;
            }
        }

        public static unsafe Rectangle? GetElementRectAtPoint(Point screenPoint, IntPtr ignoredWindow)
        {
            IntPtr pAutomation = GetAutomationInstance();
            if (pAutomation == IntPtr.Zero)
                return null;

            // Use WS_EX_TRANSPARENT (hit-test bypass) for all windows.
            // UIA ElementFromPoint internally relies on WindowFromPoint which respects WS_EX_TRANSPARENT,
            // so it will correctly skip our overlay and hit the underlying OS elements directly and instantly.
            Rectangle? result = ScreenCaptureTool.Platform.WindowHelper.InvokeIgnoringWindow(
                ignoredWindow,
                () =>
                {
                    IntPtr pElement = IntPtr.Zero;
                    try
                    {
                        IntPtr* vtable = *(IntPtr**)pAutomation;
                        // Slot 7: ElementFromPoint
                        delegate* unmanaged[Stdcall]<IntPtr, UiaPoint, out IntPtr, int> elementFromPointFunc =
                            (delegate* unmanaged[Stdcall]<IntPtr, UiaPoint, out IntPtr, int>)vtable[7];

                        UiaPoint pt = new UiaPoint(screenPoint.X, screenPoint.Y);
                        int hr = elementFromPointFunc(pAutomation, pt, out pElement);

                        if (hr == 0 && pElement != IntPtr.Zero)
                        {
                            IntPtr* elVtable = *(IntPtr**)pElement;
                            delegate* unmanaged[Stdcall]<IntPtr, out UiaRect, int> getRectFunc =
                                (delegate* unmanaged[Stdcall]<IntPtr, out UiaRect, int>)elVtable[43];

                            UiaRect rect;
                            int rectHr = getRectFunc(pElement, out rect);
                            if (rectHr == 0)
                            {
                                int w = rect.right - rect.left;
                                int h = rect.bottom - rect.top;
                                if (w > 0 && h > 0)
                                {
                                    return (Rectangle?)new Rectangle(rect.left, rect.top, w, h);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHandler?.Invoke($"UIAutomationHelper (ElementFromPoint) exception: {ex.Message}");
                    }
                    finally
                    {
                        if (pElement != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr* elVtable = *(IntPtr**)pElement;
                                delegate* unmanaged[Stdcall]<IntPtr, uint> releaseFunc =
                                    (delegate* unmanaged[Stdcall]<IntPtr, uint>)elVtable[2];
                                releaseFunc(pElement);
                            }
                            catch { }
                        }
                    }
                    return (Rectangle?)null;
                });
            return result;
        }
    }
}
