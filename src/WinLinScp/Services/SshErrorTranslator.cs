using WinLinScp.Models;

namespace WinLinScp.Services;

/// <summary>ssh/scp 执行结果 → 中文错误消息。</summary>
public static class SshErrorTranslator
{
    public static string Describe(SshResult r, string opName)
    {
        if (r.TimedOut) return $"{opName}超时";
        if (r.WasCancelled) return $"{opName}已取消";
        if (r.ExitCode == 0) return "";

        var err = r.StdErr ?? "";
        if (err.Contains("Permission denied")) return "认证失败：请检查 SSH 密钥与用户名（~/.ssh/config）";
        if (err.Contains("Host key verification failed")) return "主机密钥校验失败：known_hosts 可能已变更";
        if (err.Contains("Could not resolve hostname")) return "无法解析主机别名：请检查 ~/.ssh/config";
        if (err.Contains("Connection timed out")) return "连接超时：请检查跳板机与网络连通性";
        if (err.Contains("Connection refused")) return "连接被拒绝：目标端口未开放或被防火墙拦截";
        if (err.Contains("No such file or directory")) return "目标路径不存在";
        if (err.Contains("Operation not permitted")) return "权限不足";
        if (r.ExitCode == 255) return "SSH 连接失败（退出码 255）";

        var tail = err.Trim();
        if (tail.Length > 120) tail = tail[..120] + "…";
        return $"{opName}失败（退出码 {r.ExitCode}）：{tail}";
    }
}
