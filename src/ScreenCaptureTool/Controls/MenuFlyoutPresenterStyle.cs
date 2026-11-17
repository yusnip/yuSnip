using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 菜单外壳变体类的唯一挂载口：给框架现造出来的 <see cref="MenuFlyoutPresenter"/> 补上
/// 设计规范 §6.12 的全局变体类（<c>compact</c> 窄条 / <c>centered</c> 条目居中）。
///
/// <para>之所以要绕这一圈：本仓库的 Avalonia 11.2.8 还没有 <c>FlyoutBase.ClassName</c>（11.3 才加），
/// 而 presenter 是框架建弹出层时才 new 的，外部拿不到实例 ⇒ 只能借第一个条目"入树即回溯祖先"补这一笔。
/// 挂在 <see cref="Control.AttachedToVisualTree"/> 而不是 flyout 的 Opened 上：条目是在 ItemsPresenter
/// 的度量过程中被塞进树的，这一刻补类赶在 presenter 定宽之前，不会先弹出宽条再抽窄。</para>
///
/// <para>⚠ 本类只挂类、不带任何尺寸或几何值——数值一律写在全局样式那一处（<c>Themes/Controls.axaml</c>），
/// 否则又变成"每个窗口私改规范"。</para>
/// </summary>
internal static class MenuFlyoutPresenterStyle
{
    /// <summary>变体类名：窄条档（短文本条目，免吃 170 下限撑成空壳）。</summary>
    internal const string Compact = "compact";

    /// <summary>变体类名：条目内容水平居中（纯文字短档名菜单，配合 <see cref="Compact"/> 用）。
    /// 带小样/图标列的菜单不挂这一档——那一列必须左缘齐平才看得出是同一栏。</summary>
    internal const string Centered = "centered";

    /// <summary>把 <paramref name="classes"/> 挂到该条目所属菜单的 presenter 上（同一菜单只需调一次，传首条目）。</summary>
    internal static void Attach(MenuItem firstItem, params string[] classes)
    {
        firstItem.AttachedToVisualTree += (_, _) =>
        {
            if (firstItem.FindAncestorOfType<MenuFlyoutPresenter>() is not { } presenter) return;
            foreach (string cls in classes) presenter.Classes.Add(cls);
        };
    }
}
