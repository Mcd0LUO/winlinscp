using System.IO.Compression;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinLinScp.ViewModels;

namespace WinLinScp.Views;

/// <summary>WPF 对话框实现（弹窗 / 文本输入 / 文件选择）。全部调用归一到 UI 线程——避免后台线程
/// MessageBox.Show(owner) 抛"调用线程无法访问此对象"。</summary>
public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;
    private static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

    private static void OnUiThread(Action action)
    {
        var d = UiDispatcher;
        if (d is null || d.CheckAccess()) { action(); return; }
        d.Invoke(action);
    }

    private static T OnUiThread<T>(Func<T> func)
    {
        var d = UiDispatcher;
        if (d is null || d.CheckAccess()) return func();
        return d.Invoke(func);
    }

    public string? PromptText(string title, string prompt, string initial = "") =>
        OnUiThread(() =>
        {
            var dlg = new TextInputDialog(title, prompt, initial) { Owner = Owner };
            return dlg.ShowDialog() == true ? dlg.Value : null;
        });

    public bool Confirm(string message, string title = "确认") =>
        OnUiThread(() => MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public void Info(string message, string title = "WinLinScp") =>
        OnUiThread(() => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information));

    public void Error(string message, string title = "WinLinScp") =>
        OnUiThread(() => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error));

    public string? SaveFile(string title, string defaultName) =>
        OnUiThread(() =>
        {
            var dlg = new SaveFileDialog { Title = title, FileName = defaultName, Filter = "所有文件|*.*" };
            return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
        });

    public string? OpenFile(string title, string filter) =>
        OnUiThread(() =>
        {
            var dlg = new OpenFileDialog { Title = title, Filter = filter };
            return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
        });

    public UploadPlan? ConfirmUpload(UploadPreview preview) =>
        OnUiThread(() =>
        {
            var dlg = new UploadConfirmDialog(preview) { Owner = Owner };
            return dlg.ShowDialog() == true ? dlg.Plan : null;
        });

    public CompressionLevel? ChooseCompressionLevel() =>
        OnUiThread(() =>
        {
            var dlg = new CompressionLevelDialog { Owner = Owner };
            return dlg.ShowDialog() == true ? dlg.Level : null;
        });

    public void ShowOutput(string title, string text) =>
        OnUiThread(() =>
        {
            var dlg = new OutputWindow(title, text) { Owner = Owner };
            dlg.ShowDialog();
        });
}
