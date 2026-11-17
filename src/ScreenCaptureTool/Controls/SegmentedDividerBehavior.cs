using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.LogicalTree;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 分段轨道（<c>ContinuousBorder.segmentedGroup</c>）的分割线联动：与高亮段相邻的那根线自动隐去，
/// 只有两个未选中段之间才留线。由 Themes/Controls.axaml 的 <c>segmentedGroup</c> 样式统一挂上，
/// 各窗口不需要逐个接线（2026-09-07 起取代 SettingsWindow 里的同名私有实现）。
///
/// 为什么只能在代码里判：Avalonia 的选择器没有兄弟选择器，"这根线两侧的段选中了没有"
/// 用样式表达不出来（线的左右邻居各是谁，只有布局容器自己知道）。
/// </summary>
public class SegmentedDividerBehavior
{
    /// <summary>只用静态成员；类本身仅作为附加属性的宿主类型出现（RegisterAttached 的 TOwner 不能是静态类）。</summary>
    private SegmentedDividerBehavior() { }

    /// <summary>打开本行为。样式里设成 True 即可，不需要窗口侧调用。</summary>
    public static readonly AttachedProperty<bool> AutoCollapseProperty =
        AvaloniaProperty.RegisterAttached<SegmentedDividerBehavior, Decorator, bool>("AutoCollapse");

    /// <summary>已经接好线的标记：样式可能重复应用，防止重复订阅。</summary>
    private static readonly AttachedProperty<bool> WiredProperty =
        AvaloniaProperty.RegisterAttached<SegmentedDividerBehavior, Decorator, bool>("Wired");

    /// <summary>已挂钩子集合变化的容器，防同一 Panel 被嵌套轨道重复订阅。</summary>
    private static readonly ConditionalWeakTable<Panel, object> HookedPanels = new();

    static SegmentedDividerBehavior()
    {
        AutoCollapseProperty.Changed.AddClassHandler<Decorator>((owner, e) =>
        {
            if (e.NewValue is true) Wire(owner);
        });

        // 勾选态一律用全局类处理器接，不逐按钮订阅：设置页的工具栏/快捷键页是代码现建的轨道，
        // 后加进来的段要能自动纳入，否则就得像旧实现那样每次重载后手动补刷一遍。
        ToggleButton.IsCheckedProperty.Changed.AddClassHandler<ToggleButton>((owner, _) =>
        {
            Decorator? group = FindGroup(owner);
            if (group != null) Refresh(group);
        });
    }

    public static void SetAutoCollapse(Decorator owner, bool value) => owner.SetValue(AutoCollapseProperty, value);

    public static bool GetAutoCollapse(Decorator owner) => owner.GetValue(AutoCollapseProperty);

    /// <summary>段所属的分段轨道（沿逻辑祖先上溯，只认开了本行为的 Decorator）。</summary>
    private static Decorator? FindGroup(Control c)
    {
        foreach (ILogical n in c.GetLogicalAncestors())
        {
            if (n is Decorator b && b.GetValue(AutoCollapseProperty)) return b;
        }
        return null;
    }

    private static void Wire(Decorator group)
    {
        if (group.GetValue(WiredProperty)) return;
        group.SetValue(WiredProperty, true);

        // GetObservable 订阅当场就会发一次现值，之后 Child 每被换掉再发一次——
        // 覆盖"轨道外壳先生成、段和线后塞进来"的动态构建（设置页整表重载正是这种）。
        group.GetObservable(Decorator.ChildProperty).Subscribe(new ChildWatcher(() =>
        {
            HookPanels(group);
            Refresh(group);
        }));
    }

    private sealed class ChildWatcher : IObserver<Control?>
    {
        private readonly Action _onNext;
        public ChildWatcher(Action onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Control? value) => _onNext();
    }

    private static void HookPanels(Decorator group)
    {
        foreach (Panel panel in group.GetLogicalDescendants().OfType<Panel>())
        {
            if (HookedPanels.TryGetValue(panel, out _)) continue;
            HookedPanels.Add(panel, new object());
            ((INotifyCollectionChanged)panel.Children).CollectionChanged += (_, _) =>
            {
                // 容器里可能又嵌了新容器（延迟铺开的页面），接完线再刷。
                HookPanels(group);
                Refresh(group);
            };
        }
    }

    /// <summary>两侧都是未选中段才留线；任一侧是高亮段、或某一侧根本找不到段，都隐去。</summary>
    private static void Refresh(Decorator group)
    {
        foreach (Panel panel in group.GetLogicalDescendants().OfType<Panel>())
        {
            var children = panel.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not Rectangle divider) continue;
                if (!divider.Classes.Contains("segmentedDivider")) continue;

                ToggleButton? left = NeighbourButton(children, i, -1);
                ToggleButton? right = NeighbourButton(children, i, +1);
                divider.IsVisible = left != null && right != null
                    && left.IsChecked != true && right.IsChecked != true;
            }
        }
    }

    /// <summary>
    /// 取分割线某一侧最近的段：两侧都必须存在。分割线与段之间夹了别的装饰（图标、间距块）时
    /// 跨过它去找最近的段，与旧实现（SettingsWindow.UpdateSegmentedDividers）口径一致。
    /// </summary>
    private static ToggleButton? NeighbourButton(Avalonia.Controls.Controls children, int index, int step)
    {
        for (int p = index + step; p >= 0 && p < children.Count; p += step)
        {
            if (children[p] is ToggleButton button) return button;
        }
        return null;
    }
}
