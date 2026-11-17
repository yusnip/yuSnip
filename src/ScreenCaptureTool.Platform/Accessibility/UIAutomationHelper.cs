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

        /// <summary>UIA TreeScope：仅元素自身。</summary>
        public const int TreeScopeElement = 1;
        /// <summary>UIA TreeScope：元素自身 + 全部后代（Element|Children|Descendants）。用于一次性批量缓存整棵子树。</summary>
        public const int TreeScopeSubtree = 7;

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

        /// <summary>
        /// 通过 UIA ElementFromPoint 获取光标下的 UI 元素指针。
        /// 临时设置 WS_EX_TRANSPARENT 让遮罩窗口穿透，使 WindowFromPoint 命中底层应用。
        /// 返回的指针由调用方负责 ReleaseCOM 释放。
        /// </summary>
        public static unsafe IntPtr ElementFromPoint(Point screenPoint, IntPtr ignoredWindow)
        {
            IntPtr pAutomation = GetAutomationInstance();
            if (pAutomation == IntPtr.Zero)
                return IntPtr.Zero;

            return ScreenCaptureTool.Platform.WindowHelper.InvokeIgnoringWindow(
                ignoredWindow,
                () =>
                {
                    try
                    {
                        IntPtr* vtable = *(IntPtr**)pAutomation;
                        // Slot 7: ElementFromPoint
                        delegate* unmanaged[Stdcall]<IntPtr, UiaPoint, out IntPtr, int> elementFromPointFunc =
                            (delegate* unmanaged[Stdcall]<IntPtr, UiaPoint, out IntPtr, int>)vtable[7];

                        UiaPoint pt = new UiaPoint(screenPoint.X, screenPoint.Y);
                        int hr = elementFromPointFunc(pAutomation, pt, out IntPtr pElement);
                        return hr == 0 ? pElement : IntPtr.Zero;
                    }
                    catch (Exception ex)
                    {
                        LogHandler?.Invoke($"UIAutomationHelper (ElementFromPoint) exception: {ex.Message}");
                        return IntPtr.Zero;
                    }
                });
        }

        /// <summary>
        /// 获取元素的非缓存 BoundingRectangle（用于 ElementFromPoint 返回的未缓存元素）。
        /// Slot 43: get_CurrentBoundingRectangle
        /// </summary>
        public static unsafe Rectangle? GetCurrentBoundingRectangle(IntPtr pElement)
        {
            if (pElement == IntPtr.Zero) return null;

            try
            {
                IntPtr* vtable = *(IntPtr**)pElement;
                delegate* unmanaged[Stdcall]<IntPtr, out UiaRect, int> getRectFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, out UiaRect, int>)vtable[43];

                UiaRect rect;
                int hr = getRectFunc(pElement, out rect);
                if (hr == 0)
                {
                    int w = rect.right - rect.left;
                    int h = rect.bottom - rect.top;
                    if (w > 0 && h > 0)
                        return new Rectangle(rect.left, rect.top, w, h);
                }
            }
            catch (Exception ex)
            {
                LogHandler?.Invoke($"UIAutomationHelper (GetCurrentBoundingRectangle) exception: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 获取元素的非缓存 IsOffscreen 属性。
        /// Slot 38: get_CurrentIsOffscreen
        /// （slot 40 是 get_CurrentFrameworkId，返回 BSTR；错用会把 8 字节指针截断成非零 int，
        ///  导致链首元素永远被判定离屏而丢弃，并伴随 8 字节写 4 字节变量的栈越界写。2026-08-24 已实测核对）
        /// </summary>
        public static unsafe bool GetCurrentIsOffscreen(IntPtr pElement)
        {
            if (pElement == IntPtr.Zero) return true;

            try
            {
                IntPtr* vtable = *(IntPtr**)pElement;
                // Slot 38: get_CurrentIsOffscreen
                delegate* unmanaged[Stdcall]<IntPtr, out int, int> getIsOffscreenFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)vtable[38];

                int isOffscreen;
                int hr = getIsOffscreenFunc(pElement, out isOffscreen);
                return hr == 0 && isOffscreen != 0;
            }
            catch
            {
                return true;
            }
        }

        public static unsafe void ReleaseCOM(IntPtr pUnknown)
        {
            if (pUnknown == IntPtr.Zero) return;
            try
            {
                IntPtr* vtable = *(IntPtr**)pUnknown;
                delegate* unmanaged[Stdcall]<IntPtr, uint> releaseFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
                releaseFunc(pUnknown);
            }
            catch { }
        }

        public static unsafe void AddRefCOM(IntPtr pUnknown)
        {
            if (pUnknown == IntPtr.Zero) return;
            try
            {
                IntPtr* vtable = *(IntPtr**)pUnknown;
                delegate* unmanaged[Stdcall]<IntPtr, uint> addRefFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[1];
                addRefFunc(pUnknown);
            }
            catch { }
        }

        public static unsafe IntPtr ElementFromHandle(IntPtr hwnd)
        {
            IntPtr pAutomation = GetAutomationInstance();
            if (pAutomation == IntPtr.Zero) return IntPtr.Zero;

            IntPtr* vtable = *(IntPtr**)pAutomation;
            // Slot 6: ElementFromHandle
            delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int> elementFromHandleFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)vtable[6];

            IntPtr pElement = IntPtr.Zero;
            int hr = elementFromHandleFunc(pAutomation, hwnd, out pElement);
            return hr == 0 ? pElement : IntPtr.Zero;
        }

        /// <summary>
        /// 通过窗口句柄获取 UIA 元素（带缓存），用于后续的向上回溯获取父链。
        /// Slot 10: ElementFromHandleBuildCache
        /// </summary>
        public static unsafe IntPtr ElementFromHandleBuildCache(IntPtr hwnd, IntPtr pCacheRequest)
        {
            IntPtr pAutomation = GetAutomationInstance();
            if (pAutomation == IntPtr.Zero || hwnd == IntPtr.Zero || pCacheRequest == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr* vtable = *(IntPtr**)pAutomation;
            // Slot 10: ElementFromHandleBuildCache
            delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, out IntPtr, int> elementFromHandleBuildCacheFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, out IntPtr, int>)vtable[10];

            IntPtr pElement = IntPtr.Zero;
            int hr = elementFromHandleBuildCacheFunc(pAutomation, hwnd, pCacheRequest, out pElement);
            return hr == 0 ? pElement : IntPtr.Zero;
        }

        public static unsafe IntPtr GetControlViewWalker()
        {
            IntPtr pAutomation = GetAutomationInstance();
            if (pAutomation == IntPtr.Zero) return IntPtr.Zero;

            IntPtr* vtable = *(IntPtr**)pAutomation;
            // Slot 14: get_ControlViewWalker
            delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int> getControlViewWalkerFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)vtable[14];

            IntPtr pWalker = IntPtr.Zero;
            int hr = getControlViewWalkerFunc(pAutomation, out pWalker);
            return hr == 0 ? pWalker : IntPtr.Zero;
        }

        public static unsafe IntPtr CreateCacheRequest(int[] propertyIds, int treeScope)
        {
            IntPtr pAutomation = GetAutomationInstance();
            if (pAutomation == IntPtr.Zero) return IntPtr.Zero;

            IntPtr* vtable = *(IntPtr**)pAutomation;
            // Slot 20: CreateCacheRequest
            delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int> createCacheRequestFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)vtable[20];

            IntPtr pCacheRequest = IntPtr.Zero;
            int hr = createCacheRequestFunc(pAutomation, out pCacheRequest);
            if (hr != 0 || pCacheRequest == IntPtr.Zero) return IntPtr.Zero;

            IntPtr* reqVtable = *(IntPtr**)pCacheRequest;
            // Slot 3: AddProperty
            delegate* unmanaged[Stdcall]<IntPtr, int, int> addPropertyFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, int, int>)reqVtable[3];
            // Slot 7: put_TreeScope
            delegate* unmanaged[Stdcall]<IntPtr, int, int> putTreeScopeFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, int, int>)reqVtable[7];

            putTreeScopeFunc(pCacheRequest, treeScope);

            foreach (int propId in propertyIds)
            {
                addPropertyFunc(pCacheRequest, propId);
            }

            return pCacheRequest;
        }

        public static unsafe Rectangle? GetCachedBoundingRectangle(IntPtr pElement)
        {
            if (pElement == IntPtr.Zero) return null;

            IntPtr* vtable = *(IntPtr**)pElement;
            // Slot 75: get_CachedBoundingRectangle
            delegate* unmanaged[Stdcall]<IntPtr, out UiaRect, int> getCachedRectFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, out UiaRect, int>)vtable[75];

            UiaRect rect;
            int hr = getCachedRectFunc(pElement, out rect);
            if (hr == 0)
            {
                int w = rect.right - rect.left;
                int h = rect.bottom - rect.top;
                if (w > 0 && h > 0)
                {
                    return new Rectangle(rect.left, rect.top, w, h);
                }
            }
            return null;
        }

        public static unsafe bool GetCachedIsOffscreen(IntPtr pElement)
        {
            if (pElement == IntPtr.Zero) return true;

            IntPtr* vtable = *(IntPtr**)pElement;
            // Slot 70: get_CachedIsOffscreen
            delegate* unmanaged[Stdcall]<IntPtr, out int, int> getCachedIsOffscreenFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)vtable[70];

            int isOffscreen;
            int hr = getCachedIsOffscreenFunc(pElement, out isOffscreen);
            return hr == 0 && isOffscreen != 0;
        }

        public static unsafe IntPtr GetParentElementBuildCache(IntPtr pWalker, IntPtr pElement, IntPtr pCacheRequest)
        {
            if (pWalker == IntPtr.Zero || pElement == IntPtr.Zero || pCacheRequest == IntPtr.Zero) return IntPtr.Zero;

            IntPtr* vtable = *(IntPtr**)pWalker;
            // Slot 9: GetParentElementBuildCache
            delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, out IntPtr, int> getParentFunc =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, out IntPtr, int>)vtable[9];

            IntPtr pParent = IntPtr.Zero;
            int hr = getParentFunc(pWalker, pElement, pCacheRequest, out pParent);
            return hr == 0 ? pParent : IntPtr.Zero;
        }

        /// <summary>
        /// 获取元素的缓存子元素数组（IUIAutomationElementArray）。
        /// Slot 19: GetCachedChildren（2026-08-25 已用 ctypes 实测核对：
        /// 与已验证锚点 slot 38/40/43（Current 属性区）保持声明顺序一致；
        /// 配合 TreeScope_Subtree 的 CacheRequest 可实现"一次 COM 往返拉取整棵子树"）。
        /// 返回的数组指针由调用方负责 ReleaseCOM 释放；无缓存子元素或失败返回 Zero。
        /// </summary>
        public static unsafe IntPtr GetCachedChildren(IntPtr pElement)
        {
            if (pElement == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                IntPtr* vtable = *(IntPtr**)pElement;
                // Slot 19: GetCachedChildren
                delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int> getCachedChildrenFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)vtable[19];

                IntPtr pArray;
                int hr = getCachedChildrenFunc(pElement, out pArray);
                return hr == 0 ? pArray : IntPtr.Zero;
            }
            catch (Exception ex)
            {
                LogHandler?.Invoke($"UIAutomationHelper (GetCachedChildren) exception: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// IUIAutomationElementArray::get_Length。Slot 3（2026-08-25 已实测）。
        /// 失败返回 0。
        /// </summary>
        public static unsafe int GetElementArrayLength(IntPtr pArray)
        {
            if (pArray == IntPtr.Zero) return 0;

            try
            {
                IntPtr* vtable = *(IntPtr**)pArray;
                // Slot 3: get_Length
                delegate* unmanaged[Stdcall]<IntPtr, out int, int> getLengthFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)vtable[3];

                int length;
                int hr = getLengthFunc(pArray, out length);
                return hr == 0 ? length : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// IUIAutomationElementArray::GetElement。Slot 4（2026-08-25 已实测）。
        /// 返回的元素指针引用计数已 +1，由调用方负责 ReleaseCOM 释放；失败返回 Zero。
        /// </summary>
        public static unsafe IntPtr GetElementArrayItem(IntPtr pArray, int index)
        {
            if (pArray == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                IntPtr* vtable = *(IntPtr**)pArray;
                // Slot 4: GetElement
                delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int> getElementFunc =
                    (delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)vtable[4];

                IntPtr pElement;
                int hr = getElementFunc(pArray, index, out pElement);
                return hr == 0 ? pElement : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
