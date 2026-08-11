using System.Diagnostics;
using System.IO;
using System.Text;
using WinLinScp.Models;

namespace WinLinScp.Services;

/// <summary>
/// 常驻 SSH 管道：一条长连接 ssh 进程，远端运行循环从 stdin 读 base64 命令、
/// 执行并捕获到临时文件，再用长度前缀帧回传 stdout/stderr/退出码。
/// 所有命令经 SemaphoreSlim 串行化。通道损坏/超时 → 关闭进程，下次命令自动重建。
/// 协议（每帧）：
///   client → `&lt;base64 脚本&gt;\n`
///   remote → `DSH &lt;rc&gt; &lt;outLen&gt; &lt;errLen&gt;\0` + outLen 字节 stdout + errLen 字节 stderr
/// </summary>
public sealed class PersistentSshSession
{
    private readonly string _alias;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _procLock = new();
    private Process? _proc;
    private StreamWriter? _stdin;

    public PersistentSshSession(string alias) => _alias = alias;

    public string Alias => _alias;

    public bool IsAlive
    {
        get
        {
            lock (_procLock)
            {
                return _proc is { HasExited: false };
            }
        }
    }

    /// <summary>拉起会话进程（真正建连在第一条命令时完成）。</summary>
    public void EnsureStarted()
    {
        lock (_procLock)
        {
            if (_proc is { HasExited: false }) return;
            StartProcess();
        }
    }

    private void StartProcess()
    {
        // 远端循环：读一行 base64 → bash 执行 → 长度前缀帧输出。trap 兜底清理临时文件（会话被杀时）。
        const string loop = """
            while IFS= read -r line; do
              if [ "$line" = "__DS_EXIT__" ]; then break; fi
              out=$(mktemp); err=$(mktemp)
              trap 'rm -f "$out" "$err"' EXIT
              bash -c "$(printf '%s' "$line" | base64 -d)" >"$out" 2>"$err"
              rc=$?
              printf 'DSH %d %d %d\0' "$rc" "$(wc -c <"$out")" "$(wc -c <"$err")"
              cat "$out"; cat "$err"
              rm -f "$out" "$err"
              trap - EXIT
            done
            """;
        var b64Loop = Convert.ToBase64String(Encoding.UTF8.GetBytes(loop));

        var psi = new ProcessStartInfo
        {
            FileName = SshLocator.Ssh,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var opt in SshOptions.CommonForCurrent()) psi.ArgumentList.Add(opt);
        SshAuthContext.ApplyAskPassEnv(psi); // 密码模式：注入 SSH_ASKPASS 免交互取密码
        psi.ArgumentList.Add(_alias);
        // 脚本用 -c 传给 bash（命令替换），让循环的 stdin 保持为 SSH 通道。
        // 注意：不能用管道 `echo | base64 | bash`，那样 bash 的 stdin 是管道而非通道，循环会立即 EOF。
        psi.ArgumentList.Add($"bash -c \"$(echo {b64Loop} | base64 -d)\"");

        var proc = new Process { StartInfo = psi };
        proc.Start();
        _proc = proc;
        _stdin = proc.StandardInput;
        // ssh 自身连接类消息走 stderr，后台排空即可（远端脚本的 stderr 已在帧内）
        _ = DrainToVoidAsync(proc.StandardError);
    }

    /// <summary>执行单条远端命令。串行化；通道/协议/超时故障抛 RemoteOperationException（下次命令自动重建会话）。</summary>
    public async Task<SshResult> ExecuteAsync(string remoteScript, CancellationToken ct = default, int timeoutMs = 120_000)
    {
        // 排队等待期间取消：不碰共享进程，直接传播（避免误杀正在执行其它命令的会话）
        await _gate.WaitAsync(ct);
        try
        {
            EnsureStarted();
            var stdin = _stdin;
            if (stdin is null) throw new RemoteOperationException("无法建立 SSH 会话");

            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(remoteScript));
            await stdin.WriteLineAsync(b64.AsMemory(), ct);
            await stdin.FlushAsync(ct);

            return await ReadFrameAsync(ct, timeoutMs);
        }
        catch (OperationCanceledException)
        {
            // 命令已发出或读帧中被取消：通道状态未知，关闭重建
            CloseProcess();
            throw;
        }
        catch (RemoteOperationException)
        {
            CloseProcess();
            throw;
        }
        catch
        {
            CloseProcess();
            throw new RemoteOperationException("SSH 会话执行异常");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>关闭会话：先关 stdin 让远端循环 EOF 自然退出（主 ssh + 跳板子进程都干净退出），
    /// 短暂等待后仍存活才兜底杀进程树。</summary>
    public void Close()
    {
        Process? proc;
        lock (_procLock)
        {
            proc = _proc;
            _proc = null;
            _stdin = null;
        }
        if (proc is null) return;

        try { proc.StandardInput.Close(); } catch { }
        try
        {
            // 优雅退出优先：stdin EOF → 远端循环结束 → ssh 退出（含 -W 跳板子进程）
            if (!proc.WaitForExit(3000))
                proc.Kill(entireProcessTree: true);
        }
        catch { }
        proc.Dispose();
    }

    private async Task<SshResult> ReadFrameAsync(CancellationToken ct, int timeoutMs)
    {
        var readTask = ReadFrameCoreAsync(ct);
        var done = timeoutMs > 0
            ? await Task.WhenAny(readTask, Task.Delay(timeoutMs, ct))
            : readTask;
        if (done != readTask)
        {
            // 取消 vs 超时区分开：取消抛 OCE（上层可正确响应），超时抛协议异常
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new RemoteOperationException("命令超时");
        }
        return await readTask;
    }

    private async Task<SshResult> ReadFrameCoreAsync(CancellationToken ct)
    {
        var (rc, outLen, errLen) = await ReadHeaderAsync(ct);
        var outBytes = await ReadExactlyAsync(outLen, ct);
        var errBytes = await ReadExactlyAsync(errLen, ct);
        return new SshResult(rc, Encoding.UTF8.GetString(outBytes), Encoding.UTF8.GetString(errBytes));
    }

    /// <summary>读 header 到第一个 '\0'，格式 `DSH rc outlen errlen`。</summary>
    private async Task<(int rc, int outLen, int errLen)> ReadHeaderAsync(CancellationToken ct)
    {
        var stream = _proc!.StandardOutput.BaseStream;
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(), ct);
            if (n == 0) throw new RemoteOperationException("SSH 会话已断开");
            if (one[0] == 0) break;
            sb.Append((char)one[0]);
        }
        var parts = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || parts[0] != "DSH")
            throw new RemoteOperationException("SSH 会话协议错误");
        if (!int.TryParse(parts[1], out var rc)
            || !int.TryParse(parts[2], out var outLen)
            || !int.TryParse(parts[3], out var errLen))
            throw new RemoteOperationException("SSH 会话协议错误");
        return (rc, outLen, errLen);
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        var stream = _proc!.StandardOutput.BaseStream;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new RemoteOperationException("SSH 会话已断开");
            read += n;
        }
        return buf;
    }

    private static async Task DrainToVoidAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } }
        catch { /* 会话关闭时忽略 */ }
    }

    private void CloseProcess()
    {
        lock (_procLock)
        {
            var proc = _proc;
            _proc = null;
            _stdin = null;
            if (proc is null) return;
            try
            {
                // 先尝试优雅（关 stdin），短等后仍存活再杀树
                try { proc.StandardInput.Close(); } catch { }
                if (!proc.WaitForExit(1500))
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
            proc.Dispose();
        }
    }
}
