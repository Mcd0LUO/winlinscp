using WinLinScp.Models;

namespace WinLinScp.Services;

/// <summary>
/// 远端目录列举。emitter 用单个 GNU find -printf 进程（避免逐文件 fork stat/base64），
/// 输出 NUL 分隔记录：`%y|%s|%T@|%Y|%f`（类型|大小|epoch|链接目标类型|名称）。
/// 解析时只按前 4 个 '|' 切分，剩余部分为名称 → 文件名含 '|' 也不破坏行结构。
/// </summary>
public sealed class RemoteFileListing
{
    private readonly SshService _ssh;

    public RemoteFileListing(SshService ssh) => _ssh = ssh;

    public async Task<IReadOnlyList<RemoteFileInfo>> ListAsync(
        string alias, string workDir, CancellationToken ct = default)
    {
        var script = BuildEmitter(workDir);
        var result = await _ssh.RunBashAsync(alias, script, ct);

        if (result.TimedOut) throw new RemoteOperationException($"列目录超时：{workDir}");
        if (result.WasCancelled) throw new OperationCanceledException();

        if (result.StdErr.Contains("__CD_FAIL__") || result.ExitCode != 0)
        {
            throw new RemoteOperationException($"无法进入目录：{workDir}\n{CleanErr(result.StdErr)}");
        }

        var entries = new List<RemoteFileInfo> { new("..", RemoteFileKind.Directory, 0, default, false, IsParent: true) };
        foreach (var record in result.StdOut.Split('\0'))
        {
            if (record.Length == 0) continue;
            var parsed = ParseRecord(record);
            if (parsed is not null) entries.Add(parsed);
        }
        return entries;
    }

    private static string BuildEmitter(string workDir)
    {
        const string emitter = """
            cd -- @@WD@@ || { echo __CD_FAIL__ >&2; exit 1; }
            find . -mindepth 1 -maxdepth 1 -printf '%y|%s|%T@|%Y|%f\0'
            """;
        return emitter.Replace("@@WD@@", ShellQuote.Quote(workDir));
    }

    private static RemoteFileInfo? ParseRecord(string rec)
    {
        int i1 = rec.IndexOf('|');
        if (i1 <= 0) return null;
        int i2 = rec.IndexOf('|', i1 + 1);
        if (i2 < 0) return null;
        int i3 = rec.IndexOf('|', i2 + 1);
        if (i3 < 0) return null;
        int i4 = rec.IndexOf('|', i3 + 1);
        if (i4 < 0) return null;

        var name = rec[(i4 + 1)..];
        if (name.Length == 0) return null;

        char y = rec[0];
        char Y = i3 + 1 < rec.Length ? rec[i3 + 1] : '?';

        var kind = y switch
        {
            'd' => RemoteFileKind.Directory,
            'f' => RemoteFileKind.RegularFile,
            'l' => RemoteFileKind.Symlink,
            _ => RemoteFileKind.Other,
        };

        long size = long.TryParse(rec.AsSpan(i1 + 1, i2 - i1 - 1), out var s) ? s : 0;

        // epoch 形如 1784715836.1494467600，取整数部分
        var epochSpan = rec.AsSpan(i2 + 1, i3 - i2 - 1);
        int dot = epochSpan.IndexOf('.');
        if (dot >= 0) epochSpan = epochSpan[..dot];
        DateTime mtime = long.TryParse(epochSpan, out var e) && e > 0
            ? DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime
            : default;

        bool linkTargetIsDir = y == 'l' && Y == 'd';

        return new RemoteFileInfo(name, kind, size, mtime, linkTargetIsDir, IsParent: false);
    }

    private static string CleanErr(string stderr) => stderr.Trim().Length > 0 ? stderr.Trim() : "未知错误";
}
