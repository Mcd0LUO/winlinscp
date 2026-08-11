using System.Text;
using WinLinScp.Models;

namespace WinLinScp.Services;

/// <summary>
/// 通过系统 ssh.exe 执行远程命令。
/// 优先走常驻会话（PersistentSshSession，一条连接复用）；通道故障时回退一次性连接。
/// 关键：host 参数永远是 ~/.ssh/config 中的主机别名（如 my-ubuntu），
/// 绝不能拼 user@host —— ProxyJump/密钥/known_hosts 全部依赖 config 块。
/// </summary>
public sealed class SshService
{
    private readonly ProcessRunner _runner;
    private readonly object _sessionLock = new();
    private PersistentSshSession? _session;

    public SshService(ProcessRunner runner) => _runner = runner;

    /// <summary>执行一段 bash 脚本。走常驻会话（快）；通道故障回退一次性 base64 传输。</summary>
    public async Task<SshResult> RunBashAsync(string alias, string bashScript, CancellationToken ct = default)
    {
        var session = GetOrCreateSession(alias);
        if (session is not null)
        {
            try
            {
                return await session.ExecuteAsync(bashScript, ct, timeoutMs: 120_000);
            }
            catch (RemoteOperationException)
            {
                // 通道故障：回退一次性连接（会话下次自动重建）
            }
        }
        return await RunBashOneShotAsync(alias, bashScript, ct);
    }

    /// <summary>简单单条命令（连通性测试等）。也走会话。</summary>
    public Task<SshResult> RunSimpleAsync(string alias, string command, CancellationToken ct = default)
        => RunBashAsync(alias, command, ct);

    /// <summary>执行单条远端命令并读取原始字节（预览大文件用，最多 maxBytes）。独立一次性连接，可中途截断。</summary>
    public async Task<(byte[] Bytes, bool Truncated)> RunCommandByteLimitAsync(
        string alias, string remoteCommand, int maxBytes, CancellationToken ct)
    {
        var args = new List<string>(SshOptions.CommonForCurrent()) { alias, remoteCommand };
        return await _runner.RunWithByteLimitAsync(SshLocator.Ssh, args, maxBytes, ct);
    }

    /// <summary>关闭常驻会话（断开/退出时调用）。</summary>
    public void StopSession()
    {
        lock (_sessionLock)
        {
            _session?.Close();
            _session = null;
        }
    }

    private PersistentSshSession? GetOrCreateSession(string alias)
    {
        lock (_sessionLock)
        {
            if (_session is { IsAlive: true } && _session.Alias == alias)
                return _session;

            _session?.Close();
            var s = new PersistentSshSession(alias);
            try
            {
                s.EnsureStarted();
                _session = s;
                return s;
            }
            catch
            {
                s.Close();
                return null; // 启动失败 → 一次性回退
            }
        }
    }

    private async Task<SshResult> RunBashOneShotAsync(string alias, string bashScript, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(bashScript));
        var remote = $"echo {b64} | base64 -d | bash";
        var args = new List<string>(SshOptions.CommonForCurrent()) { alias, remote };
        return await _runner.RunAsync(SshLocator.Ssh, args, ct, timeoutMs: 60_000);
    }
}
