using System.Diagnostics;

namespace WinLinScp.Services;

/// <summary>
/// 当前连接的认证方式。默认走 ~/.ssh/config 别名 + 密钥；设为密码模式后，
/// ssh/scp 进程注入 SSH_ASKPASS 环境（本应用自身充当 askpass 助手输出密码），
/// SshOptions 切换为 BatchMode=no + 强制密码认证。
/// </summary>
public static class SshAuthContext
{
    public static bool PasswordMode { get; private set; }

    public static string? Password { get; private set; }

    public static void SetPassword(string password)
    {
        PasswordMode = true;
        Password = password;
    }

    public static void Clear()
    {
        PasswordMode = false;
        Password = null;
    }

    /// <summary>密码模式时给进程注入 SSH_ASKPASS 环境（WinLinScp.exe 自身充当 askpass 助手）。</summary>
    public static void ApplyAskPassEnv(ProcessStartInfo psi)
    {
        if (!PasswordMode) return;
        psi.EnvironmentVariables["SSH_ASKPASS"] = Environment.ProcessPath ?? "";
        psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
        psi.EnvironmentVariables["DISPLAY"] = "localhost:0";
        psi.EnvironmentVariables["WINLINSCP_ASKPASS"] = "1";
        psi.EnvironmentVariables["WINLINSCP_ASKPASS_PASSWORD"] = Password ?? "";
    }
}
