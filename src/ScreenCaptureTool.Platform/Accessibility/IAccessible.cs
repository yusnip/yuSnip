using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform.Accessibility
{
    /// <summary>
    /// MSAA <c>IAccessible</c> COM 接口定义。
    /// 阶段 2 搬迁自旧 <c>ScreenCapture.Native.cs</c>，IID 与 DispId 与系统一致。
    ///
    /// 设计为 <c>internal</c>：Platform 内部用，外部应通过 <see cref="AccessibilityHelper"/> 调用。
    /// 这样 Core/主项目都不需要引入 oleacc 类型。
    /// </summary>
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IAccessible
    {
        [DispId(-5000)]
        object accParent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

        [DispId(-5001)]
        int accChildCount { get; }

        [DispId(-5002)]
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object get_accChild([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5003)]
        string get_accName([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5004)]
        string get_accValue([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5005)]
        string get_accDescription([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5006)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accRole([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5007)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accState([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5008)]
        string get_accHelp([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5009)]
        int get_accHelpTopic(out string pszHelpFile, [MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5010)]
        string get_accKeyboardShortcut([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5011)]
        object accFocus { [return: MarshalAs(UnmanagedType.Struct)] get; }

        [DispId(-5012)]
        object accSelection { [return: MarshalAs(UnmanagedType.Struct)] get; }

        [DispId(-5013)]
        string get_accDefaultAction([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5014)]
        void accSelect(int flagsSelect, [MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5015)]
        void accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, [MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5016)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accNavigate(int navDir, [MarshalAs(UnmanagedType.Struct)] object varStart);

        [DispId(-5017)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accHitTest(int xLeft, int yTop);

        [DispId(-5018)]
        void accDoDefaultAction([MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5003)]
        void set_accName([MarshalAs(UnmanagedType.Struct)] object varChild, string pszName);

        [DispId(-5004)]
        void set_accValue([MarshalAs(UnmanagedType.Struct)] object varChild, string pszValue);
    }
}
