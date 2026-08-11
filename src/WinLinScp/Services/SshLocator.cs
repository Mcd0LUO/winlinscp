using System.IO;

namespace WinLinScp.Services;

/// <summary>定位 ssh.exe / scp.exe。优先 Windows OpenSSH 目录，其次 PATH。</summary>
public static class SshLocator
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Ssh => Find("ssh.exe");
    public static string Scp => Find("scp.exe");

    public static string Find(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached ?? "";
        string? found = null;

        // Windows OpenSSH 默认位置
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
        var candidate = Path.Combine(system32, name);
        if (File.Exists(candidate)) found = candidate;

        if (found is null)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) { found = candidate; break; }
                }
            }
        }

        Cache[name] = found ?? "";
        return found ?? "";
    }
}
