using System.IO;
using System.Text;
using WinLinScp.Services;

namespace WinLinScp.Services;

/// <summary>
/// 无头自检（WinLinScp.exe -selftest）：逐项连真实主机验证，退出码 0/1。
/// 目标主机别名/工作目录从登录脚本解析或环境变量覆盖（WINLINSCP_LOGIN_SCRIPT /
/// WINLINSCP_HOST / WINLINSCP_WORKDIR），否则用占位值 —— 本仓库不写死个人主机信息。
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = new UTF8Encoding(false); // 重定向时输出 UTF-8，避免中文乱码
        var scriptPath = Environment.GetEnvironmentVariable("WINLINSCP_LOGIN_SCRIPT") ?? "";
        var (tgt, wd) = scriptPath.Length > 0 && File.Exists(scriptPath) ? LoginScriptParser.ParseFile(scriptPath) : (null, null);
        var alias = tgt ?? Environment.GetEnvironmentVariable("WINLINSCP_HOST") ?? "ubuntu-host";
        var workDir = wd ?? Environment.GetEnvironmentVariable("WINLINSCP_WORKDIR") ?? "/home/user/work";

        var runner = new ProcessRunner();
        var ssh = new SshService(runner);
        var scp = new ScpService(runner);
        var listing = new RemoteFileListing(ssh);

        Console.WriteLine($"WinLinScp selftest  —  alias={alias}  workdir={workDir}");
        Console.WriteLine();

        var results = new List<(string Name, bool Ok, string Msg)>();

        async Task Run(string name, Func<Task<(bool Ok, string Msg)>> test)
        {
            try
            {
                var (ok, msg) = await test();
                results.Add((name, ok, msg));
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
                if (msg.Length > 0) Console.WriteLine($"        {msg}");
            }
            catch (Exception ex)
            {
                results.Add((name, false, ex.Message));
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"        异常: {ex.Message}");
            }
        }

        // ① 定位二进制
        await Run("① 定位 ssh/scp 二进制", () => Task.FromResult((
            SshLocator.Find("ssh.exe") != "" && SshLocator.Find("scp.exe") != "",
            $"ssh={SshLocator.Ssh}  scp={SshLocator.Scp}")));

        // ② 解析 ssh config
        var entries = SshConfigDiscovery.ReadAliases();
        await Run("② 解析 ~/.ssh/config", () => Task.FromResult((
            entries.Any(e => e.Alias == alias),
            $"发现 {entries.Count} 个别名: {string.Join(", ", entries.Select(e => e.Alias))}")));

        // ③ 解析登录脚本
        var (t, w) = scriptPath.Length > 0 && File.Exists(scriptPath) ? LoginScriptParser.ParseFile(scriptPath) : (null, null);
        await Run("③ 解析登录脚本", () => Task.FromResult((
            scriptPath.Length == 0 || (t == alias && w == workDir),
            scriptPath.Length == 0 ? "未配置登录脚本（WINLINSCP_LOGIN_SCRIPT）" : $"Target={t}  WorkDir={w}")));

        // ④ 别名直连
        await Run("④ 别名直连", async () =>
        {
            var r = await ssh.RunSimpleAsync(alias, "echo WinLinScp-OK");
            return (r.Ok && r.StdOut.Contains("WinLinScp-OK"), $"exit={r.ExitCode}  out={r.StdOut.Trim()}  err={r.StdErr.Trim()}");
        });

        // ⑤ 登录脚本一次性模式
        if (scriptPath.Length == 0)
        {
            await Run("⑤ 脚本一次性模式", () => Task.FromResult((true, "未配置登录脚本，跳过")));
        }
        else
        {
            await Run("⑤ 脚本一次性模式", async () =>
            {
                var psArgs = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-RemoteCommand", "echo WinLinScp-OK" };
                var r = await runner.RunAsync("powershell", psArgs, CancellationToken.None, timeoutMs: 120_000);
                return (r.Ok && r.StdOut.Contains("WinLinScp-OK"), $"exit={r.ExitCode}  out={r.StdOut.Trim()}");
            });
        }

        // ⑥ 列目录
        await Run("⑥ 远端列目录", async () =>
        {
            var list = await listing.ListAsync(alias, workDir);
            var real = list.Where(e => !e.IsParent).ToList();
            return (list.Any(e => e.IsParent) && real.Count > 0,
                $"共 {real.Count} 项, 首项={real.FirstOrDefault()?.Name}, 含 parent={list.Any(e => e.IsParent)}");
        });

        // ⑦-⑪ 远端文件往返（全部在 /tmp 下，finally 兜底清理）
        var ts = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        var localUp = Path.Combine(Path.GetTempPath(), $"ds_selftest_{ts}.txt");
        var localDown = Path.Combine(Path.GetTempPath(), $"ds_selftest_out_{ts}.txt");
        var remoteA = $"/tmp/ds_selftest_{ts}.txt";
        var remoteB = $"/tmp/ds_selftest_{ts}_b.txt";
        var remoteDir = $"/tmp/ds_selftest_dir_{ts}";
        var content = "WinLinScp selftest 内容 " + ts;

        try
        {
            // ⑦ 上传 + 校验
            await Run("⑦ 上传 + cat 校验", async () =>
            {
                File.WriteAllText(localUp, content, new UTF8Encoding(false));
                var up = await scp.UploadAsync(alias, localUp, "/tmp", CancellationToken.None);
                var cat = await ssh.RunSimpleAsync(alias, $"cat {ShellQuote.Quote(remoteA)}");
                return (up.Ok && cat.Ok && cat.StdOut.Trim() == content,
                    $"up_exit={up.ExitCode}  cat_exit={cat.ExitCode}  回读 {cat.StdOut.Length} 字节");
            });

            // ⑧ 重命名
            await Run("⑧ 远端重命名", async () =>
            {
                var rn = await ssh.RunBashAsync(alias, RemoteOps.Rename("/tmp", RemotePath.Name(remoteA), RemotePath.Name(remoteB)));
                var chk = await ssh.RunSimpleAsync(alias, $"test -f {ShellQuote.Quote(remoteB)} && echo Y");
                return (rn.Ok && chk.StdOut.Trim() == "Y", $"exit={rn.ExitCode}");
            });

            // ⑨ 下载回校验
            await Run("⑨ 下载回校验", async () =>
            {
                var dl = await scp.DownloadToFileAsync(alias, remoteB, localDown, CancellationToken.None);
                var got = File.Exists(localDown) ? File.ReadAllText(localDown, new UTF8Encoding(false)) : "";
                return (dl.Ok && got == content, $"dl_exit={dl.ExitCode}  本地 {got.Length} 字节");
            });

            // ⑩ 删除
            await Run("⑩ 远端删除", async () =>
            {
                var del = await ssh.RunBashAsync(alias, RemoteOps.Delete("/tmp", [RemotePath.Name(remoteB)]));
                var chk = await ssh.RunSimpleAsync(alias, $"test ! -e {ShellQuote.Quote(remoteB)} && echo Y");
                return (del.Ok && chk.StdOut.Trim() == "Y", $"exit={del.ExitCode}");
            });

            // ⑪ mkdir / rmdir
            await Run("⑪ 远端 mkdir/rmdir", async () =>
            {
                var mk = await ssh.RunBashAsync(alias, RemoteOps.MkDir("/tmp", RemotePath.Name(remoteDir)));
                var rm = await ssh.RunBashAsync(alias, $"cd -- {ShellQuote.Quote("/tmp")} && rmdir -- {ShellQuote.Quote(RemotePath.Name(remoteDir))}");
                return (mk.Ok && rm.Ok, $"mkdir_exit={mk.ExitCode}  rmdir_exit={rm.ExitCode}");
            });

            // ⑪b 归档：tar.gz 压缩/解压往返
            await Run("⑪b 远端 tar.gz 压缩/解压", async () =>
            {
                var adir = remoteDir + "_ar";
                var mk = await ssh.RunBashAsync(alias, RemoteOps.MkDir("/tmp", RemotePath.Name(adir)));
                var wf = await ssh.RunBashAsync(alias, $"cd -- {ShellQuote.Quote(adir)} && echo ar-content > f.txt");
                var cz = await ssh.RunBashAsync(alias, RemoteOps.CompressTarGz("/tmp", RemotePath.Name(adir)));
                var ex = await ssh.RunBashAsync(alias, RemoteOps.Extract("/tmp", RemotePath.Name(adir) + ".tar.gz"));
                var cat = await ssh.RunSimpleAsync(alias, $"cat {ShellQuote.Quote(RemotePath.Combine(adir, "f.txt"))}");
                return (mk.Ok && wf.Ok && cz.Ok && ex.Ok && cat.StdOut.Trim() == "ar-content",
                    $"mkdir={mk.Ok} compress={cz.Ok} extract={ex.Ok} cat='{cat.StdOut.Trim()}'");
            });
        }
        finally
        {
            // 兜底清理远端可能残留的文件
            await ssh.RunBashAsync(alias, RemoteOps.Delete("/tmp", [RemotePath.Name(remoteA), RemotePath.Name(remoteB), RemotePath.Name(remoteDir),
                RemotePath.Name(remoteDir) + "_ar", RemotePath.Name(remoteDir) + "_ar.tar.gz"]));
        }

        // ⑫ 清理本地临时文件
        await Run("⑫ 清理本地临时", () =>
        {
            try { if (File.Exists(localUp)) File.Delete(localUp); } catch { }
            try { if (File.Exists(localDown)) File.Delete(localDown); } catch { }
            return Task.FromResult((true, ""));
        });

        Console.WriteLine();
        var passed = results.Count(r => r.Ok);
        Console.WriteLine($"{(passed == results.Count ? "全部通过" : "存在失败")} ({passed}/{results.Count})");

        ssh.StopSession(); // 关闭常驻会话，避免 selftest 退出后残留 ssh 进程
        return passed == results.Count ? 0 : 1;
    }
}
