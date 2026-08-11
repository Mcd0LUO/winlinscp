using System.Windows;
using System.Windows.Controls;
using WinLinScp.Services;
using WinLinScp.ViewModels;

namespace WinLinScp.Views;

/// <summary>
/// 图片行的异步图标加载：字形先显示，shell 图标后台提取到位后覆盖。
/// 绑定 FilePaneItem → 后台 SHGetFileInfo → UI 线程创建 BitmapSource。
/// </summary>
public static class IconLoader
{
    public static readonly DependencyProperty IconPathProperty = DependencyProperty.RegisterAttached(
        "IconPath", typeof(FilePaneItem), typeof(IconLoader), new PropertyMetadata(null, OnIconPathChanged));

    public static void SetIconPath(DependencyObject d, FilePaneItem? value) => d.SetValue(IconPathProperty, value);
    public static FilePaneItem? GetIconPath(DependencyObject d) => (FilePaneItem?)d.GetValue(IconPathProperty);

    private static async void OnIconPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var img = (Image)d;
        var item = e.NewValue as FilePaneItem;
        if (item is null || item.IsParent)
        {
            img.Source = null; // 回退字形
            return;
        }
        try
        {
            var source = await ShellIcon.GetIconAsync(item.FullPath, item.IsDirectory || item.IsDrive, item.IsDrive);
            // 行可能已被虚拟化回收复用于其它条目：仅当仍是同一条目才设图标，避免错位
            if (ReferenceEquals(GetIconPath(img), item))
                img.Source = source;
        }
        catch
        {
            // 图标提取失败：保留字形回退，不抛出
        }
    }
}
