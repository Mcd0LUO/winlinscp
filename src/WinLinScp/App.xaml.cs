using System.Text;
using System.Windows;
using System.Windows.Threading;
using WinLinScp.Services;

namespace WinLinScp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 密码模式下的 SSH_ASKPASS 助手：被系统 ssh 拉起时，读密码输出到 stdout 后立即退出。
        // （ssh 会以继承 stdout 管道的方式创建本进程，即使本应用是 GUI 子系统也可写。）
        if (Environment.GetEnvironmentVariable("WINLINSCP_ASKPASS") == "1")
        {
            var pw = Environment.GetEnvironmentVariable("WINLINSCP_ASKPASS_PASSWORD") ?? "";
            try
            {
                using var stdout = Console.OpenStandardOutput();
                var bytes = Encoding.UTF8.GetBytes(pw);
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
            catch { /* 无 stdout 句柄：忽略 */ }
            Environment.Exit(0);
            return;
        }

        // 无头自检模式：不创建任何窗口，逐项验证后退出。
        // 注意：不能直接在 UI 线程 GetResult()——WPF 的 SynchronizationContext 会把 await 延续
        // 封送回被阻塞的 Dispatcher，造成死锁。放到线程池线程（无同步上下文）执行。
        if (e.Args.Contains("-selftest"))
        {
            int code = Task.Run(SelfTest.RunAsync).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // 组合根：无 DI 容器，手工装配
        var dialogs = new Views.WpfDialogService();
        var runner = new ProcessRunner();
        var ssh = new SshService(runner);
        var scp = new ScpService(runner);
        var store = new ProfileStore();
        var vm = new ViewModels.MainViewModel(store, runner, ssh, scp, dialogs);

        var win = new MainWindow(vm);
        MainWindow = win;
        win.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"发生未处理异常：\n{e.Exception.Message}", "WinLinScp",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
