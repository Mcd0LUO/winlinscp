namespace WinLinScp.Models;

/// <summary>进程（ssh/scp 等）执行结果。</summary>
public sealed record SshResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut = false,
    bool WasCancelled = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !WasCancelled;
}
