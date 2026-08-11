using System.IO;
using WinLinScp.Models;

namespace WinLinScp.Services;

/// <summary>
/// 通过系统 scp.exe 上传/下载。关键坑：
/// 1. 本地 `C:` 冒号误判：scp 把「含 ':' 且本地不存在」当远端。修复 = WorkingDirectory 设为本地目录，
///    本地参数一律用相对名（`./名`），不含冒号。
/// 2. 远端 token 直接 `alias:path` 拼接，**不做引号**（scp 不经远端 shell，引号会成为文件名一部分）。
/// </summary>
public sealed class ScpService
{
    private readonly ProcessRunner _runner;

    /// <summary>按当前认证方式取基础选项（-q + 共用；密码模式切 BatchMode=no + 强制密码）。</summary>
    private static IReadOnlyList<string> BaseOptions => ["-q", .. SshOptions.CommonForCurrent()];

    public ScpService(ProcessRunner runner) => _runner = runner;

    /// <summary>上传本地文件/目录到远端目录（保留原名）。目录走 -r 递归。</summary>
    public async Task<SshResult> UploadAsync(
        string alias, string localPath, string remoteDir, CancellationToken ct)
    {
        var trimmed = localPath.TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        var parentDir = Path.GetDirectoryName(trimmed) ?? Environment.CurrentDirectory;

        // 盘符根（如 C:\）→ name 为空，scp 会退化成 -r ./ 上传整个盘，直接拒绝
        if (name.Length == 0)
            throw new RemoteOperationException("不能上传盘符根目录");

        var args = new List<string>(BaseOptions);
        if (Directory.Exists(localPath)) args.Add("-r");
        args.Add("./" + name);                                    // 源：cwd 相对名
        args.Add(RemoteToken(alias, RemotePath.Combine(remoteDir, name))); // 目标：远端目录 + 原名

        return await _runner.RunAsync(SshLocator.Scp, args, ct, timeoutMs: 0, workingDirectory: parentDir);
    }

    /// <summary>下载远端文件/目录到本地目录（保留原名）。recursive=true 走 -r 递归。</summary>
    public async Task<SshResult> DownloadAsync(
        string alias, string remotePath, string localDir, bool recursive, CancellationToken ct)
    {
        var name = RemotePath.Name(remotePath);

        var args = new List<string>(BaseOptions);
        if (recursive) args.Add("-r");
        args.Add(RemoteToken(alias, remotePath)); // 源：远端 token
        args.Add("./" + name);                    // 目标：cwd 相对名

        return await _runner.RunAsync(SshLocator.Scp, args, ct, timeoutMs: 0, workingDirectory: localDir);
    }

    /// <summary>下载远端文件到本地指定完整路径（查看器「用默认程序打开」用）。</summary>
    public async Task<SshResult> DownloadToFileAsync(
        string alias, string remotePath, string localFile, CancellationToken ct)
    {
        var localDir = Path.GetDirectoryName(localFile) ?? Environment.CurrentDirectory;
        var args = new List<string>(BaseOptions) { RemoteToken(alias, remotePath), Path.GetFileName(localFile) };
        return await _runner.RunAsync(SshLocator.Scp, args, ct, timeoutMs: 0, workingDirectory: localDir);
    }

    /// <summary>
    /// 远端 token 直接拼接 alias:path，**不做 shell 引号**。
    /// scp 把路径透传给 SFTP 层（不经远端 shell），加引号反而成为文件名的字面部分；
    /// 空格/单引号等字符 scp 自身能处理（已实测）。
    /// </summary>
    private static string RemoteToken(string alias, string remotePath) =>
        alias + ":" + remotePath;
}
