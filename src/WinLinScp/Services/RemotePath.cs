namespace WinLinScp.Services;

/// <summary>远端 '/' 路径工具。</summary>
public static class RemotePath
{
    public static string Combine(string dir, string name)
    {
        if (dir == "/") return "/" + name;
        return dir.TrimEnd('/') + "/" + name;
    }

    /// <summary>父目录；根目录返回 null。</summary>
    public static string? GetParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed)) return null;
        int idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";
        return trimmed[..idx];
    }

    public static string Name(string path) => path.TrimEnd('/') switch
    {
        "" => "/",
        var p => p[(p.LastIndexOf('/') + 1)..],
    };
}
