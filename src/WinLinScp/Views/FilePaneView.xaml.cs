using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinLinScp.ViewModels;

namespace WinLinScp.Views;

public partial class FilePaneView : UserControl
{
    private Point _dragStart;
    private bool _mouseDownForDrag;
    private IReadOnlyList<FilePaneItem>? _dragItems;
    private Button? _lastHighlight;

    private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xFF));

    public FilePaneView()
    {
        InitializeComponent();
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 把 ListView 的多选集同步到 VM（删除等批量操作需要）
        if (DataContext is not FilePaneViewModel vm) return;
        vm.SelectedItems.Clear();
        foreach (var item in EntryList.SelectedItems)
            if (item is FilePaneItem fi) vm.SelectedItems.Add(fi);
    }

    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FilePaneViewModel vm && vm.SelectedItem is not null)
            vm.OpenCommand.Execute(null);
    }

    // ---------------- 面包屑地址栏 ----------------

    private void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FilePaneViewModel vm) return;
        if ((sender as FrameworkElement)?.DataContext is BreadcrumbSegment seg && seg.FullPath != vm.CurrentPath)
            _ = vm.NavigateAsync(seg.FullPath);
    }

    private void EditPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FilePaneViewModel vm) return;
        PathEdit.Text = vm.CurrentPath;
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        PathEdit.Visibility = Visibility.Visible;
        PathEdit.Focus();
        PathEdit.SelectAll();
    }

    private void PathEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var path = PathEdit.Text.Trim();
            HidePathEdit();
            if (path.Length > 0 && DataContext is FilePaneViewModel vm)
                _ = vm.NavigateAsync(path);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HidePathEdit();
            e.Handled = true;
        }
    }

    private void PathEdit_LostFocus(object sender, RoutedEventArgs e) => HidePathEdit();

    private void HidePathEdit()
    {
        PathEdit.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FilePaneViewModel vm || vm.PathHistory.Count == 0) return;
        var menu = new ContextMenu();
        foreach (var path in vm.PathHistory)
        {
            var mi = new MenuItem { Header = path };
            var p = path;
            mi.Click += (_, _) => _ = vm.NavigateAsync(p);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    // ---------------- 面包屑拖放：文件可拖到指定块 ----------------

    private void Breadcrumb_DragOver(object sender, DragEventArgs e)
    {
        if (DataContext is not FilePaneViewModel vm) return;
        var payload = ToPayload(e.Data);
        if (payload is null || !vm.CanAcceptDrop(payload))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        HighlightSegment(e);
        var copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void Breadcrumb_DragLeave(object sender, DragEventArgs e) => ClearHighlight();

    private async void Breadcrumb_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not FilePaneViewModel vm) return;
            var payload = ToPayload(e.Data);
            if (payload is null || !vm.CanAcceptDrop(payload)) return;
            ClearHighlight();
            var copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            var seg = FindDropTargetSegment(e);
            var targetDir = seg?.FullPath ?? vm.CurrentPath; // 拖到空白处 = 当前目录
            e.Handled = true;
            await vm.HandleDropAsync(payload, targetDir, copy);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"拖放失败：{ex.Message}", "WinLinScp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HighlightSegment(DragEventArgs e)
    {
        var btn = FindSegmentButton(e);
        if (ReferenceEquals(btn, _lastHighlight)) return;
        ClearHighlight();
        _lastHighlight = btn;
        if (btn is not null) btn.Background = HighlightBrush;
    }

    private void ClearHighlight()
    {
        if (_lastHighlight is null) return;
        _lastHighlight.Background = Brushes.Transparent;
        _lastHighlight = null;
    }

    private static Button? FindSegmentButton(DragEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not Button)
            dep = VisualTreeHelper.GetParent(dep);
        return dep as Button;
    }

    private static BreadcrumbSegment? FindDropTargetSegment(DragEventArgs e) =>
        FindSegmentButton(e)?.DataContext as BreadcrumbSegment;

    // ---------------- 面板级快捷键 ----------------

    private void Pane_KeyDown(object sender, KeyEventArgs e)
    {
        if (PathEdit.IsKeyboardFocusWithin) return;
        if (DataContext is not FilePaneViewModel vm) return;

        switch (e.Key)
        {
            case Key.F5:
                vm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                vm.RenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                vm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when vm.SelectedItem is not null:
                vm.OpenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Back:
                vm.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                vm.GoBackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                vm.GoForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                vm.DownloadCommand?.Execute(null);
                e.Handled = true;
                break;
            case Key.U when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                vm.UploadCommand?.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ---------------- 拖拽（跨系统/系统内） ----------------

    private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(EntryList);
        _mouseDownForDrag = true;
        // 在 ListView 处理点击（可能把多选收拢为单选）之前，快照完整多选集；
        // 点的是未选中项则视为单选该项。
        _dragItems = null;
        var pressed = FindItem(e.OriginalSource);
        if (pressed is null || pressed.IsParent) return;
        _dragItems = EntryList.SelectedItems.Contains(pressed)
            ? EntryList.SelectedItems.Cast<FilePaneItem>().Where(i => !i.IsParent).ToList()
            : new List<FilePaneItem> { pressed };
    }

    private void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDownForDrag || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(EntryList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _mouseDownForDrag = false;
        if (_dragItems is not { Count: > 0 }) return;
        if (DataContext is not FilePaneViewModel vm) return;
        var payload = vm.CreateDragPayload(_dragItems);
        if (payload is null) return;

        var data = new DataObject();
        if (payload.Format == DragFormats.LocalFileDrop)
        {
            // 外部可读路径；内部拖拽额外携带完整条目（含 Size/IsDirectory），避免对方重建丢元数据
            data.SetData(DataFormats.FileDrop, payload.Items.Select(i => i.FullPath).ToArray());
            data.SetData(DragFormats.LocalFileDrop, payload.Items.ToArray());
        }
        else
        {
            data.SetData(DragFormats.RemoteItem, payload.Items.ToArray());
        }

        try { DragDrop.DoDragDrop(EntryList, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch { /* 拖拽中断等 */ }
    }

    private void EntryList_DragOver(object sender, DragEventArgs e)
    {
        if (DataContext is FilePaneViewModel vm && ToPayload(e.Data) is { } payload && vm.CanAcceptDrop(payload))
        {
            var copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void EntryList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not FilePaneViewModel vm) return;
            var payload = ToPayload(e.Data);
            if (payload is null || !vm.CanAcceptDrop(payload)) return;
            var copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            var targetDir = GetDropTargetDir(vm, e);
            e.Handled = true;
            await vm.HandleDropAsync(payload, targetDir, copy);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"拖放失败：{ex.Message}", "WinLinScp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>落点目录：拖到目录行（含 ..）= 移入该目录；否则当前目录。</summary>
    private string GetDropTargetDir(FilePaneViewModel vm, DragEventArgs e)
    {
        var target = FindItem(e.OriginalSource);
        if (target is { IsDirectory: true })
            return target.FullPath;
        return vm.CurrentPath;
    }

    private static FilePaneItem? FindItem(object originalSource)
    {
        var dep = originalSource as DependencyObject;
        while (dep is not null and not ListViewItem)
            dep = VisualTreeHelper.GetParent(dep);
        return (dep as ListViewItem)?.DataContext as FilePaneItem;
    }

    private static DragPayload? ToPayload(IDataObject data)
    {
        // 内部拖拽优先：携带完整条目（含 Size/IsDirectory），不重建
        if (data.GetDataPresent(DragFormats.LocalFileDrop) && data.GetData(DragFormats.LocalFileDrop) is FilePaneItem[] localItems)
            return new DragPayload { Format = DragFormats.LocalFileDrop, Items = localItems };
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var items = paths.Select(p => new FilePaneItem
            {
                Name = Path.GetFileName(p.TrimEnd('\\')),
                FullPath = p,
                IsDirectory = Directory.Exists(p),
            }).ToList();
            return new DragPayload { Format = DragFormats.LocalFileDrop, Items = items };
        }
        if (data.GetDataPresent(DragFormats.RemoteItem) && data.GetData(DragFormats.RemoteItem) is FilePaneItem[] remoteItems)
            return new DragPayload { Format = DragFormats.RemoteItem, Items = remoteItems };
        return null;
    }
}
