namespace WinLinScp.Services;

/// <summary>
/// 远端增删改查的 bash 脚本构造。所有名称经 ShellQuote 引号，再由 SshService base64 传输，无转义问题。
/// </summary>
public static class RemoteOps
{
    public static string Delete(string workDir, IEnumerable<string> names) =>
        $"{Cd(workDir)}rm -rf -- {string.Join(" ", names.Select(Q))}";

    public static string Rename(string workDir, string oldName, string newName) =>
        $"{Cd(workDir)}mv -- {Q(oldName)} {Q(newName)}";

    /// <summary>把 workDir 下的多个条目移动到 targetDir（拖拽到目录行 = linux 内移动）。`--` 只在最前一次。</summary>
    public static string Move(string workDir, IEnumerable<string> names, string targetDir) =>
        $"{Cd(workDir)}mv -- {string.Join(" ", names.Select(Q))} {Q(targetDir)}";

    public static string MkDir(string workDir, string name) =>
        $"{Cd(workDir)}mkdir -p -- {Q(name)}";

    public static string Touch(string workDir, string name) =>
        $"{Cd(workDir)}touch -- {Q(name)}";

    public static string Chmod(string workDir, string mode, string name) =>
        $"{Cd(workDir)}chmod {mode} -- {Q(name)}";

    /// <summary>确保远端目录存在（连接时用）。</summary>
    public static string EnsureDir(string workDir) =>
        $"mkdir -p -- {Q(workDir)}";

    /// <summary>把 workDir 下的 name 压缩为 name.tar.gz。</summary>
    public static string CompressTarGz(string workDir, string name) =>
        $"{Cd(workDir)}tar -czf {Q(name + ".tar.gz")} -- {Q(name)}";

    /// <summary>把 workDir 下的 name 压缩为 name.zip（需远端装有 zip）。</summary>
    public static string CompressZip(string workDir, string name) =>
        $"{Cd(workDir)}zip -r {Q(name + ".zip")} {Q(name)}";

    /// <summary>解压 workDir 下的归档到 workDir。zip 用 unzip，其余（tar/tgz/tar.bz2/tar.xz）用 tar（GNU tar 自动检测压缩）。</summary>
    public static string Extract(string workDir, string name)
    {
        var full = RemotePath.Combine(workDir, name); // 归档完整路径（在 workDir 内）
        var lower = name.ToLowerInvariant();
        var cmd = lower.EndsWith(".zip")
            ? $"unzip -o {Q(full)}"
            : $"tar -xf {Q(full)}"; // -f 无条件吞掉下一个 token，名称以 '-' 开头也安全
        return $"{Cd(workDir)}{cmd}";
    }

    /// <summary>查看器用：单条 cat 远端命令（配合 RunWithByteLimitAsync）。</summary>
    public static string CatCommand(string path) =>
        $"cat -- {Q(path)}";

    /// <summary>打包上传后：把 workDir 下的归档解压到 workDir 并删除归档（解压失败保留归档便于重试）。</summary>
    public static string ExtractAndCleanup(string workDir, string archiveName, bool isZip)
    {
        var full = RemotePath.Combine(workDir, archiveName);
        var cmd = isZip
            ? $"unzip -o {Q(full)}"
            : $"tar -xf {Q(full)}"; // 解压到 cwd（workDir）
        return $"{Cd(workDir)}{cmd} && rm -f {Q(full)}";
    }

    /// <summary>统一前缀：进入工作目录，失败即止。</summary>
    private static string Cd(string workDir) =>
        $"cd -- {Q(workDir)} || exit 1\n";

    private static string Q(string s) => ShellQuote.Quote(s);
}
