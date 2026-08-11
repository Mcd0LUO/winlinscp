using System.IO;
using System.Text.RegularExpressions;

namespace WinLinScp.Services;

/// <summary>从登录脚本 .ps1 中提取 $Target / $WorkDir（连接对话框预填用）。</summary>
public static class LoginScriptParser
{
    private static readonly Regex TargetRegex = new(@"\$Target\s*=\s*['""](?<t>[^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex WorkDirRegex = new(@"\$WorkDir\s*=\s*['""](?<w>[^'""]+)['""]", RegexOptions.Compiled);

    public static (string? Target, string? WorkDir) Parse(string ps1Text)
    {
        var t = TargetRegex.Match(ps1Text);
        var w = WorkDirRegex.Match(ps1Text);
        return (
            t.Success ? t.Groups["t"].Value : null,
            w.Success ? w.Groups["w"].Value : null);
    }

    public static (string? Target, string? WorkDir) ParseFile(string scriptPath)
    {
        try { return Parse(File.ReadAllText(scriptPath)); }
        catch { return (null, null); }
    }
}
