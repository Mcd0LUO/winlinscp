using System.Diagnostics;
using System.IO;
using System.Text;
using WinLinScp.Models;

namespace WinLinScp.Services;

/// <summary>
/// 全应用唯一 spawn 进程的地方。规则：
/// - 用 ProcessStartInfo.ArgumentList（.NET 自动按 Windows 规则引号），杜绝手拼转义 bug；
/// - stdout/stderr 并发读取，防管道缓冲死锁；
/// - 超时/取消 → 杀整棵进程树，且排空管道有界等待，避免残留子进程持有句柄导致永久挂起。
/// </summary>
public sealed class ProcessRunner
{
    public async Task<SshResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct = default,
        int timeoutMs = 120_000,
        string? workingDirectory = null)
    {
        using var proc = Create(fileName, args, workingDirectory);
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new SshResult(-1, "", $"进程启动失败: {ex.Message}");
        }
        CloseStdin(proc);

        // 读管道不用调用方 token：超时/取消时我们自己控制排空，避免 Kill 后残留句柄导致 ReadToEnd 永久挂起
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var waitTask = proc.WaitForExitAsync(ct);

        try
        {
            if (timeoutMs > 0)
            {
                // WaitAsync 在任务完成时正确取消内部计时器，避免 Task.Delay 悬空泄漏
                try { await waitTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct); }
                catch (TimeoutException)
                {
                    Kill(proc);
                    var so = await DrainGrace(stdoutTask);
                    var se = await DrainGrace(stderrTask);
                    return new SshResult(-1, so, se, TimedOut: true);
                }
            }
            else
            {
                await waitTask;
            }

            // 正常退出也可能有残留子进程（ProxyJump）持有管道句柄，排空也做有界等待
            var outText = await DrainGrace(stdoutTask, graceMs: 3000);
            var errText = await DrainGrace(stderrTask, graceMs: 3000);
            return new SshResult(proc.ExitCode, outText, errText);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            var so = await DrainGrace(stdoutTask);
            var se = await DrainGrace(stderrTask);
            return new SshResult(-1, so, se, WasCancelled: true);
        }
    }

    /// <summary>
    /// 流式运行：stdout/stderr 每行回调（连接日志用），同时累积完整输出。
    /// </summary>
    public async Task<SshResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> args,
        Action<string>? onStdoutLine,
        Action<string>? onStderrLine,
        CancellationToken ct = default,
        int timeoutMs = 120_000)
    {
        using var proc = Create(fileName, args, workingDirectory: null);
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new SshResult(-1, "", $"进程启动失败: {ex.Message}");
        }
        CloseStdin(proc);

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        var drainOut = DrainLinesAsync(proc.StandardOutput, onStdoutLine, sbOut, ct);
        var drainErr = DrainLinesAsync(proc.StandardError, onStderrLine, sbErr, ct);
        var waitTask = proc.WaitForExitAsync(ct);

        try
        {
            if (timeoutMs > 0)
            {
                try { await waitTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct); }
                catch (TimeoutException)
                {
                    Kill(proc);
                    await DrainGrace(drainOut);
                    await DrainGrace(drainErr);
                    return new SshResult(-1, sbOut.ToString(), sbErr.ToString(), TimedOut: true);
                }
            }
            else
            {
                await waitTask;
            }
            await DrainGrace(drainOut);
            await DrainGrace(drainErr);
            return new SshResult(proc.ExitCode, sbOut.ToString(), sbErr.ToString());
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            await DrainGrace(drainOut);
            await DrainGrace(drainErr);
            return new SshResult(-1, sbOut.ToString(), sbErr.ToString(), WasCancelled: true);
        }
    }

    /// <summary>
    /// 读取 stdout 最多 maxBytes 字节后即终止进程（预览大文件用），返回原始字节与是否截断。
    /// </summary>
    public async Task<(byte[] Bytes, bool Truncated)> RunWithByteLimitAsync(
        string fileName,
        IReadOnlyList<string> args,
        int maxBytes,
        CancellationToken ct)
    {
        using var proc = Create(fileName, args, workingDirectory: null);
        try
        {
            proc.Start();
        }
        catch
        {
            return (Array.Empty<byte>(), false);
        }
        CloseStdin(proc);

        // 后台排空 stderr，防止其填满管道
        var stderrDrain = DrainToVoidAsync(proc.StandardError, ct);

        using var ms = new MemoryStream();
        using var stdout = proc.StandardOutput.BaseStream;
        var buffer = new byte[81920];
        bool truncated = false;

        try
        {
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                if (ms.Length + read > maxBytes)
                {
                    int take = maxBytes - (int)ms.Length;
                    if (take > 0) ms.Write(buffer, 0, take);
                    truncated = true;
                    break;
                }
                ms.Write(buffer, 0, read);
            }

            if (truncated)
            {
                Kill(proc);
            }
            else
            {
                await proc.WaitForExitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            return (ms.ToArray(), truncated);
        }

        return (ms.ToArray(), truncated);
    }

    private static async Task DrainLinesAsync(
        StreamReader reader,
        Action<string>? onLine,
        StringBuilder sb,
        CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            sb.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static async Task DrainToVoidAsync(StreamReader reader, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is not null) { }
    }

    /// <summary>等待读取任务完成，最多 graceMs；超时返回空（防止残留句柄导致永久挂起）。</summary>
    private static async Task<string> DrainGrace(Task<string> readTask, int graceMs = 2000)
    {
        var done = await Task.WhenAny(readTask, Task.Delay(graceMs));
        if (done == readTask)
        {
            try { return await readTask; }
            catch (OperationCanceledException) { return ""; }
            catch (ObjectDisposedException) { return ""; }
        }
        return "";
    }

    private static async Task DrainGrace(Task drainTask, int graceMs = 2000)
    {
        var done = await Task.WhenAny(drainTask, Task.Delay(graceMs));
        if (done == drainTask)
        {
            try { await drainTask; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
    }

    private static void CloseStdin(Process proc)
    {
        try { proc.StandardInput.Close(); }
        catch { /* 未重定向或已关闭 */ }
    }

    private static Process Create(string fileName, IReadOnlyList<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        SshAuthContext.ApplyAskPassEnv(psi); // 密码模式：注入 SSH_ASKPASS 免交互取密码
        return new Process { StartInfo = psi };
    }
}
