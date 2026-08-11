using System.IO;
using System.Text.Json.Serialization;

namespace WinLinScp.Models;

/// <summary>一个已保存的连接配置。脚本路径与主机别名二选一（脚本优先，其内部 $Target 可被 hostAlias 覆盖）。</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";

    /// <summary>登录脚本 .ps1 的绝对路径；为空则直接用 hostAlias 连接。</summary>
    public string? ScriptPath { get; set; }

    /// <summary>~/.ssh/config 中的主机别名（如 my-ubuntu）。</summary>
    public string HostAlias { get; set; } = "";

    /// <summary>直接连接的目标 IP/主机名（IP+密码登录）；非空即视为密码登录配置。</summary>
    public string? Host { get; set; }

    /// <summary>密码登录用户名。</summary>
    public string User { get; set; } = "";

    /// <summary>密码登录密码（明文存于 profiles.json，注意保管配置文件）。</summary>
    public string Password { get; set; } = "";

    /// <summary>远端起始工作目录（如 /home/user/work）。</summary>
    public string WorkDir { get; set; } = "";

    [JsonIgnore]
    public bool IsPasswordAuth => !string.IsNullOrWhiteSpace(Host);

    /// <summary>密码登录的连接目标串 user@host（作为 ssh/scp 的 host 参数）。</summary>
    [JsonIgnore]
    public string UserHost => $"{User}@{Host}";

    [JsonIgnore]
    public string Display => IsPasswordAuth
        ? $"{Name}  ({User}@{Host})"
        : string.IsNullOrEmpty(ScriptPath)
            ? $"{Name}  ({HostAlias})"
            : $"{Name}  [{Path.GetFileName(ScriptPath)}]";
}
