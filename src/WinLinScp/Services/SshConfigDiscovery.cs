using System.IO;

namespace WinLinScp.Services;

/// <summary>~/.ssh/config 中的一个 Host 块（可连接别名，跳过通配符）。</summary>
public sealed class SshConfigEntry
{
    public string Alias { get; init; } = "";
    public string? HostName { get; set; }
    public string? User { get; set; }
    public int? Port { get; set; }
    public string? ProxyJump { get; set; }
    public string? IdentityFile { get; set; }

    /// <summary>连接对话框副标题，如 "user@10.0.0.10 (via jump-host)"。</summary>
    public string Subtitle => string.Join(" ", new[]
    {
        User is null && HostName is null ? "" : $"{User ?? "?"}@{HostName ?? "?"}",
        ProxyJump is null ? "" : $"(via {ProxyJump})",
    }.Where(s => s.Length > 0));

    /// <summary>下拉框显示文本：别名 + 副标题。</summary>
    public string Display => string.IsNullOrEmpty(Subtitle) ? Alias : $"{Alias}  ({Subtitle})";
}

/// <summary>手动解析 ~/.ssh/config 的 Host 块列表。</summary>
public static class SshConfigDiscovery
{
    public static IReadOnlyList<SshConfigEntry> ReadAliases(string? configPath = null)
    {
        configPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

        var entries = new List<SshConfigEntry>();
        if (!File.Exists(configPath)) return entries;

        SshConfigEntry? current = null;
        foreach (var raw in File.ReadAllLines(configPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            if (line.StartsWith("Host ", StringComparison.Ordinal))
            {
                current = null;
                var tokens = line["Host ".Length..].Split(' ', '\t');
                foreach (var token in tokens)
                {
                    // 通配符/取反不是可连接别名，跳过（其下选项也不归属任何真实别名）
                    if (token.Contains('*') || token.Contains('?') || token.Contains('!')) continue;
                    var entry = new SshConfigEntry { Alias = token };
                    entries.Add(entry);
                    current = entry;
                }
                continue;
            }

            if (current is null) continue;
            var idx = line.IndexOfAny([' ', '\t']);
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            switch (key)
            {
                case "HostName": current.HostName = value; break;
                case "User": current.User = value; break;
                case "Port": current.Port = int.TryParse(value, out var p) ? p : null; break;
                case "ProxyJump": current.ProxyJump = value; break;
                case "IdentityFile": current.IdentityFile = value; break;
            }
        }
        return entries;
    }
}
