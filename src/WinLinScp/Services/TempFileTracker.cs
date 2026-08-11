using System.IO;
using System.Text;

namespace WinLinScp.Services;

/// <summary>「用默认程序打开」的临时文件跟踪 + 退出清理。</summary>
public static class TempFileTracker
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "WinLinScp");
    private static readonly List<string> Files = new();
    private static readonly object Gate = new();

    public static string GetTempPath(string remoteName)
    {
        Directory.CreateDirectory(TempDir);
        // 加随机前缀：同名远端文件互不覆盖
        var path = Path.Combine(TempDir, Guid.NewGuid().ToString("N")[..8] + "_" + Sanitize(remoteName));
        lock (Gate) Files.Add(path);
        return path;
    }

    public static void Register(string path)
    {
        lock (Gate) Files.Add(path);
    }

    /// <summary>退出时尽力清理本次会话产生的临时文件；被外部程序占用则静默跳过。</summary>
    public static void CleanupAll()
    {
        lock (Gate)
        {
            foreach (var f in Files)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { /* 被占用 */ }
            }
            Files.Clear();
        }

        // 顺带清理 7 天前的历史文件
        try
        {
            if (Directory.Exists(TempDir))
            {
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (var f in Directory.GetFiles(TempDir))
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { /* 忽略 */ }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.Length == 0 ? "file" : sb.ToString();
    }
}
