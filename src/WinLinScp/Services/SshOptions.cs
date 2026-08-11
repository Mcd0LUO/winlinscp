namespace WinLinScp.Services;

/// <summary>ssh/scp 共用选项：BatchMode 禁止交互提示、连接超时、自动接受新主机密钥（与用户 config 语义一致）。</summary>
public static class SshOptions
{
    public static string[] Common =>
    [
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15",
        "-o", "StrictHostKeyChecking=accept-new",
    ];

    /// <summary>按当前认证方式返回选项：密码模式切 BatchMode=no 并强制密码认证（密钥被禁用），
    /// 否则原样返回 Common（密钥/别名）。</summary>
    public static IReadOnlyList<string> CommonForCurrent() => SshAuthContext.PasswordMode
        ? new[]
        {
            "-o", "BatchMode=no",
            "-o", "ConnectTimeout=15",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "PreferredAuthentications=password,keyboard-interactive",
            "-o", "PubkeyAuthentication=no",
        }
        : Common;
}
