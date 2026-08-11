using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using WinLinScp.Services;
using WinLinScp.ViewModels;
using WinLinScp.Views;

namespace WinLinScp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.ConnectRequested += OpenConnectDialog;
        vm.RemotePane.ViewFileRequested += OpenViewer;
        vm.AboutRequested += OpenAbout;
    }

    private void OpenAbout()
    {
        var dlg = new AboutDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenConnectDialog()
    {
        var connectVm = new ConnectViewModel(
            _vm.Store, _vm.Runner, _vm.Ssh, new WpfDialogService());
        var dlg = new ConnectDialog(connectVm) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Profile is { } profile)
            _vm.ApplyConnection(profile);
    }

    private void OpenViewer(string remotePath, string name)
    {
        var viewerVm = new ViewerViewModel(
            _vm.Ssh, _vm.Scp, new WpfDialogService(), _vm.RemotePane.Alias, remotePath, name);
        var win = new RemoteViewerWindow(viewerVm) { Owner = this };
        win.Show();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenConnectDialog();
            e.Handled = true;
        }
    }

    // ---------------- 任务卡片区拖拽调高 ----------------

    private bool _taskResizing;
    private double _taskResizeStartHeight;
    private double _taskResizeStartY;

    private void TaskResize_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _taskResizing = true;
        _taskResizeStartHeight = _vm.TransferQueue.TaskPanelHeight;
        _taskResizeStartY = e.GetPosition(this).Y;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void TaskResize_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_taskResizing) return;
        var dy = e.GetPosition(this).Y - _taskResizeStartY;
        // 分隔线在任务区顶部：拖向上 = 分隔线上移 = 任务区变大；拖向下 = 变小
        _vm.TransferQueue.TaskPanelHeight = Math.Max(40, _taskResizeStartHeight - dy);
        e.Handled = true;
    }

    private void TaskResize_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_taskResizing) return;
        _taskResizing = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 传输进行中 → 确认后取消
        if (_vm.TransferQueue.IsActive)
        {
            var res = MessageBox.Show(this,
                "仍有传输正在进行，确定退出？未完成的传输将被取消。",
                "退出确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _vm.TransferQueue.CancelAllCommand.Execute(null);
        }

        _vm.SaveCurrentState();
        _vm.Ssh.StopSession();
        TempFileTracker.CleanupAll();
        base.OnClosing(e);
    }
}
