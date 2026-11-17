using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

public partial class ThirdPartyNoticesWindow : Window
{
    public ThirdPartyNoticesWindow()
    {
        InitializeComponent();
        foreach (var component in ThirdPartyNotices.Components)
            ComponentList.Items.Add(new ListBoxItem { Content = component.Name, Tag = component });
        ComponentList.SelectedIndex = 0;
        Opened += (_, _) => ComponentList.Focus();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        }, RoutingStrategies.Tunnel);
    }

    private ThirdPartyNotices.Component? Selected => (ComponentList.SelectedItem as ListBoxItem)?.Tag as ThirdPartyNotices.Component;

    private void OnComponentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } component) return;
        ComponentName.Text = component.Name;
        ComponentMeta.Text = component.Version + " · " + component.License;
        ComponentPurpose.Text = component.Purpose;
        ToolTip.SetTip(ProjectLink, component.Url);
        StatusText.Text = "许可正文可离线查看、选中与复制。";
        try
        {
            NoticeText.Text = ThirdPartyNotices.ReadNotice(component);
            CopyNoticeButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("第三方许可资源读取失败", ex);
            NoticeText.Text = "无法读取随附许可正文，请通过上方链接查看项目原始声明。";
            CopyNoticeButton.IsEnabled = false;
        }
        // 切换条目后从正文开头阅读，避免保留上一个长声明的滚动位置。
        NoticeText.CaretIndex = 0;
        NoticeText.SelectionStart = NoticeText.SelectionEnd = 0;
    }

    private void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } component) return;
        try { Process.Start(new ProcessStartInfo(component.Url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Warn("第三方项目链接打开失败：" + ex.GetType().Name); StatusText.Text = "链接打开失败，可从正文中复制网址。"; }
    }

    private async void OnCopyNoticeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard) { StatusText.Text = "剪贴板暂不可用，可在正文中选中复制。"; return; }
            await clipboard.SetTextAsync(NoticeText.Text ?? "");
            StatusText.Text = "已复制本项版权与许可声明。";
        }
        catch (Exception ex) { AppLogger.Warn("第三方声明复制失败：" + ex.GetType().Name); StatusText.Text = "复制失败，请重试。"; }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }
}
