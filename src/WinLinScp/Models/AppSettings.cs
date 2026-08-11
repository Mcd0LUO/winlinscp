namespace WinLinScp.Models;

/// <summary>应用级设置（与 profiles 一起持久化）。</summary>
public sealed class AppSettings
{
    public string? LastProfileName { get; set; }
    public string LastLocalDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LastRemoteDir { get; set; } = "/";

    /// <summary>LastRemoteDir 对应的配置名（只有同配置才恢复远端目录，避免跨主机串台）。</summary>
    public string? LastRemoteProfile { get; set; }

    public bool ShowHidden { get; set; }
    public bool AutoConnect { get; set; }
}
