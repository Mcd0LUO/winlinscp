using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using WinLinScp.Models;
using WinLinScp.Services;
using WinLinScp.ViewModels;

// 无 UI 驱动完整 VM 栈：连接 → 列目录 → 导航 → 传输队列上传/下载 → 校验
// 配置目录用临时隔离目录，避免污染真实 %AppData%\WinLinScp
var dialogs = new FakeDialogs();
var runner = new ProcessRunner();
var ssh = new SshService(runner);
var scp = new ScpService(runner);
var cfgDir = Path.Combine(Path.GetTempPath(), "ds_cfg_" + Guid.NewGuid().ToString("N")[..6]);
var store = new ProfileStore(cfgDir);

var vm = new MainViewModel(store, runner, ssh, scp, dialogs);
var ok = true;
// 主机别名/工作目录：默认占位，用环境变量覆盖（仓库不写死个人主机信息）
var host = Environment.GetEnvironmentVariable("WINLINSCP_HOST") ?? "ubuntu-host";
var workDir = Environment.GetEnvironmentVariable("WINLINSCP_WORKDIR") ?? "/home/user/work";

async Task Check(string name, bool cond, string detail = "")
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}  {detail}");
    if (!cond) ok = false;
}

// 1. 本地面板导航到用户主目录
await Task.Delay(1000);
await Check("本地面板已导航", vm.LocalPane.CurrentPath.Length > 0, $"path={vm.LocalPane.CurrentPath}");

// 2. 连接远端
vm.ApplyConnection(new ConnectionProfile { Name = "probe", HostAlias = host, WorkDir = workDir });
await Task.Delay(4000);
await Check("远端面板已列出", vm.RemotePane.Entries.Count > 2, $"path={vm.RemotePane.CurrentPath} entries={vm.RemotePane.Entries.Count}");
await Check("连接状态文本", vm.ConnectedText.Contains(host), vm.ConnectedText);

// 3. 导航进入子目录
await vm.RemotePane.NavigateAsync(workDir + "/config");
await Task.Delay(2500);
await Check("导航到 config", vm.RemotePane.CurrentPath == workDir + "/config", $"entries={vm.RemotePane.Entries.Count}");

// 4. 传输队列：上传一个文件
var tmpFile = Path.Combine(Path.GetTempPath(), "vm_selftest.txt");
var content = "VM-TEST-CONTENT-" + DateTime.Now.Ticks;
File.WriteAllText(tmpFile, content);
vm.TransferQueue.Enqueue([new TransferItem
{
    Direction = TransferDirection.Upload,
    LocalPath = tmpFile,
    RemotePath = "/tmp",
    DisplayName = Path.GetFileName(tmpFile),
    IsDirectory = false,
    Size = new FileInfo(tmpFile).Length,
}]);
await Task.Delay(4000);
await Check("上传完成", vm.TransferQueue.DoneCount == vm.TransferQueue.TotalCount, $"done={vm.TransferQueue.DoneCount}/{vm.TransferQueue.TotalCount}");

var cat = await ssh.RunSimpleAsync(host, "cat /tmp/vm_selftest.txt");
await Check("远端内容一致", cat.Ok && cat.StdOut.Trim() == content, $"got='{cat.StdOut.Trim()}'");

// 5. 传输队列：下载回并校验（下载保留远端原名）
var localDown = Path.Combine(Path.GetTempPath(), "vm_selftest.txt");
vm.TransferQueue.Enqueue([new TransferItem
{
    Direction = TransferDirection.Download,
    RemotePath = "/tmp/vm_selftest.txt",
    LocalPath = Path.GetTempPath(),
    DisplayName = "vm_selftest.txt",
    IsDirectory = false,
}]);
await Task.Delay(4000);
var got = File.Exists(localDown) ? File.ReadAllText(localDown) : "";
await Check("下载内容一致", got == content, $"got='{got}'");

// 6. 归档：tar.gz 压缩/解压 + zip 压缩
await ssh.RunBashAsync(host, RemoteOps.MkDir("/tmp", "vm_archive_dir"));
await ssh.RunBashAsync(host, $"cd -- {ShellQuote.Quote("/tmp/vm_archive_dir")} && echo archive-content > file.txt");
var cz = await ssh.RunBashAsync(host, RemoteOps.CompressTarGz("/tmp", "vm_archive_dir"));
var czT = await ssh.RunSimpleAsync(host, "test -f /tmp/vm_archive_dir.tar.gz && echo Y");
await Check("tar.gz 压缩", cz.Ok && czT.StdOut.Trim() == "Y");
await ssh.RunBashAsync(host, RemoteOps.MkDir("/tmp", "vm_extract"));
var ex = await ssh.RunBashAsync(host, RemoteOps.Extract("/tmp", "vm_archive_dir.tar.gz"));
var exT = await ssh.RunSimpleAsync(host, "cat /tmp/vm_archive_dir/file.txt");
await Check("tar.gz 解压", ex.Ok && exT.StdOut.Trim() == "archive-content", "stderr=" + ex.StdErr.Trim());
var czz = await ssh.RunBashAsync(host, RemoteOps.CompressZip("/tmp", "vm_archive_dir"));
var czzT = await ssh.RunSimpleAsync(host, "test -f /tmp/vm_archive_dir.zip && echo Y");
await Check("zip 压缩", czz.Ok && czzT.StdOut.Trim() == "Y", "stderr=" + czz.StdErr.Trim());
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp",
    ["vm_archive_dir", "vm_archive_dir.tar.gz", "vm_archive_dir.zip", "vm_extract"]));

// 7. 目录切换速度（会话复用）：未访问目录也应是毫秒级，不再需要 ~1s 重建连接
var sw = Stopwatch.StartNew();
await vm.RemotePane.NavigateAsync(RemotePath.GetParent(workDir) ?? "/");
var coldMs = sw.ElapsedMilliseconds;
sw.Restart();
await vm.RemotePane.NavigateAsync(workDir); // 已访问 → 缓存命中
var warmMs = sw.ElapsedMilliseconds;
await Check("目录切换快（会话复用）", coldMs < 400 && warmMs < 150, $"cold={coldMs}ms warm={warmMs}ms");

// 8. 会话复用：单条远端命令应毫秒级
var swT = Stopwatch.StartNew();
var sr = await ssh.RunSimpleAsync(host, "echo session-fast");
swT.Stop();
await Check("会话复用命令快", sr.Ok && swT.ElapsedMilliseconds < 300, $"{swT.ElapsedMilliseconds}ms");

// 9. 传输测速：上传完成后应显示平均速度
var upItem = vm.TransferQueue.Items.OfType<TransferItem>().FirstOrDefault(i => i.Direction == TransferDirection.Upload && i.DisplayName == "vm_selftest.txt");
await Check("上传测速显示", upItem?.SpeedText.Contains("平均") == true, $"speed='{upItem?.SpeedText}'");

// 10. 本地 zip 压缩/解压（右键路径）
var localTmp = Path.Combine(Path.GetTempPath(), "ds_local_ar_" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(Path.Combine(localTmp, "src"));
File.WriteAllText(Path.Combine(localTmp, "src", "f.txt"), "local-zip-content");

await vm.LocalPane.NavigateAsync(localTmp);
await Task.Delay(800);
vm.LocalPane.SelectedItem = vm.LocalPane.Entries.FirstOrDefault(x => x.Name == "src");
await vm.LocalPane.CompressZipCommand.ExecuteAsync(null);
await Task.Delay(1200);
var zipPath = Path.Combine(localTmp, "src.zip");
await Check("本地 zip 压缩", File.Exists(zipPath));

vm.LocalPane.SelectedItem = vm.LocalPane.Entries.FirstOrDefault(x => x.Name == "src.zip");
await vm.LocalPane.ExtractCommand.ExecuteAsync(null);
await Task.Delay(1200);
var extFile = Path.Combine(localTmp, "src", "f.txt");
var extOk = File.Exists(extFile) && File.ReadAllText(extFile) == "local-zip-content";
await Check("本地 zip 解压", extOk);

try { Directory.Delete(localTmp, true); } catch { }

// 11. 拖拽 VM 逻辑：跨系统上传/下载 + 系统内移动
var dragTmp = Path.Combine(Path.GetTempPath(), "ds_drag_" + Guid.NewGuid().ToString("N")[..6]);
var dragSrc = Path.Combine(dragTmp, "src");
Directory.CreateDirectory(dragSrc);
File.WriteAllText(Path.Combine(dragSrc, "d.txt"), "drag-content");

// 本地 → 远端：拖拽上传（目标=远端当前目录 /tmp）
await vm.RemotePane.NavigateAsync("/tmp");
await Task.Delay(800);
var dItem = new FilePaneItem { Name = "d.txt", FullPath = Path.Combine(dragSrc, "d.txt") };
var dPayload = new DragPayload { Format = DragFormats.LocalFileDrop, Items = [dItem] };
await Check("远端可接收本地拖拽", vm.RemotePane.CanAcceptDrop(dPayload));
await vm.RemotePane.HandleDropAsync(dPayload, "/tmp", false);
await Task.Delay(3500);
var catDrag = await ssh.RunSimpleAsync(host, "cat /tmp/d.txt");
await Check("拖拽上传远端", catDrag.Ok && catDrag.StdOut.Trim() == "drag-content");
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["d.txt"]));

// 远端 → 本地：拖拽下载（目标=本地面板当前目录 dragTmp）
var rItem = new FilePaneItem { Name = "vm_selftest.txt", FullPath = "/tmp/vm_selftest.txt" };
var rPayload = new DragPayload { Format = DragFormats.RemoteItem, Items = [rItem] };
await Check("本地可接收远端拖拽", vm.LocalPane.CanAcceptDrop(rPayload));
await vm.LocalPane.NavigateAsync(dragTmp);
await Task.Delay(800);
await vm.LocalPane.HandleDropAsync(rPayload, dragTmp, false);
await Task.Delay(3500);
await Check("拖拽下载本地", File.Exists(Path.Combine(dragTmp, "vm_selftest.txt")));

// 本地 → 本地：系统内移动（d.txt 从 src 拖到 dragTmp 根）
await vm.LocalPane.HandleDropAsync(dPayload, dragTmp, false);
await Task.Delay(800);
var movedOk = !File.Exists(Path.Combine(dragSrc, "d.txt")) && File.Exists(Path.Combine(dragTmp, "d.txt"));
await Check("本地拖拽移动", movedOk);

// linux 内拖动：把远端文件拖到远端目录行（targetDir=该目录）→ ssh mv 移入
await ssh.RunBashAsync(host, RemoteOps.MkDir("/tmp", "vm_movedir"));
var movePayload = new DragPayload { Format = DragFormats.RemoteItem, Items = [rItem] };
await vm.RemotePane.HandleDropAsync(movePayload, "/tmp/vm_movedir", false);
await Task.Delay(1500);
var movedRemote = await ssh.RunSimpleAsync(host, "test -f /tmp/vm_movedir/vm_selftest.txt && echo Y");
await Check("linux内拖动移动", movedRemote.Ok && movedRemote.StdOut.Trim() == "Y");

// 同目录 no-op：拖到自身所在目录不应有变化、不报错
await ssh.RunBashAsync(host, "touch /tmp/vm_nop.txt");
var nopPayload = new DragPayload { Format = DragFormats.RemoteItem, Items = [new FilePaneItem { Name = "vm_nop.txt", FullPath = "/tmp/vm_nop.txt" }] };
await vm.RemotePane.HandleDropAsync(nopPayload, "/tmp", false);
await Task.Delay(1200);
var nop = await ssh.RunSimpleAsync(host, "test -f /tmp/vm_nop.txt && echo Y");
await Check("拖拽同目录 no-op", nop.Ok && nop.StdOut.Trim() == "Y");
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["vm_nop.txt", "vm_movedir", "vm_selftest.txt"]));

try { Directory.Delete(dragTmp, true); } catch { }

// 12. 冷启动自动连接：独立实例 + 独立配置，模拟应用刚启动（复现"自动连接易失败"）
var cfgCold = Path.Combine(Path.GetTempPath(), "ds_cfg_cold_" + Guid.NewGuid().ToString("N")[..6]);
var storeCold = new ProfileStore(cfgCold);
storeCold.Settings.AutoConnect = true;
storeCold.Settings.LastProfileName = "MoFa";
storeCold.Upsert(new ConnectionProfile
{
    Name = "MoFa",
    HostAlias = host,
    WorkDir = workDir,
    ScriptPath = Environment.GetEnvironmentVariable("WINLINSCP_LOGIN_SCRIPT"),
});
storeCold.Save();

var runner2 = new ProcessRunner();
var ssh2 = new SshService(runner2);
var vmCold = new MainViewModel(storeCold, runner2, ssh2, new ScpService(runner2), dialogs);
await Task.Delay(6000);
await Check("冷启动自动连接", vmCold.RemotePane.IsConnected && vmCold.RemotePane.Entries.Count > 2,
    $"connected={vmCold.RemotePane.IsConnected} path={vmCold.RemotePane.CurrentPath} entries={vmCold.RemotePane.Entries.Count} status={vmCold.RemotePane.StatusText}");
ssh2.StopSession();
try { Directory.Delete(cfgCold, true); } catch { }

// ---- smoke 基准测量 ----
Console.WriteLine();
Console.WriteLine("== smoke 基准 ==");

// 本地枚举
var swB = Stopwatch.StartNew();
await vm.LocalPane.NavigateAsync(Path.GetTempPath());
swB.Stop();
Console.WriteLine($"  本地枚举({Path.GetTempPath()}): {swB.ElapsedMilliseconds}ms 共{vm.LocalPane.Entries.Count}项");

// 图标提取：10 种类型各 5 次（首次=shell 调用，其余=缓存）
swB.Restart();
var exts = new[] { "txt", "png", "zip", "cs", "md", "mp3", "mp4", "exe", "json", "xml" };
var iconOk = true;
for (int i = 0; i < 50; i++)
{
    try { ShellIcon.GetIcon(Path.Combine(Path.GetTempPath(), $"b{i}.{exts[i % 10]}"), false); }
    catch { iconOk = false; }
}
swB.Stop();
Console.WriteLine($"  图标提取 50次(10类型, MTA{(iconOk ? "" : "受限")}): {swB.ElapsedMilliseconds}ms");

// 常驻会话命令延迟（已建立会话）
swB.Restart();
for (int i = 0; i < 5; i++) await ssh.RunSimpleAsync(host, "echo x");
swB.Stop();
Console.WriteLine($"  会话命令 5次: {swB.ElapsedMilliseconds}ms (均 {(double)swB.ElapsedMilliseconds / 5:0}ms)");

// 冷启动自动连接耗时（建连 + 列目录，轮询等待完成）
var swCold = Stopwatch.StartNew();
var cfgB = Path.Combine(Path.GetTempPath(), "ds_cfg_b_" + Guid.NewGuid().ToString("N")[..6]);
var storeB = new ProfileStore(cfgB);
storeB.Settings.AutoConnect = true;
storeB.Settings.LastProfileName = "b";
storeB.Upsert(new ConnectionProfile { Name = "b", HostAlias = host, WorkDir = workDir });
storeB.Save();
var runnerB = new ProcessRunner();
var sshB = new SshService(runnerB);
var vmB = new MainViewModel(storeB, runnerB, sshB, new ScpService(runnerB), dialogs);
while (swCold.ElapsedMilliseconds < 15000 && !(vmB.RemotePane.CurrentPath == workDir && vmB.RemotePane.Entries.Count > 2))
    await Task.Delay(100);
swCold.Stop();
Console.WriteLine($"  冷启动自动连接(建连+列目录): {swCold.ElapsedMilliseconds}ms");
sshB.StopSession();
try { Directory.Delete(cfgB, true); } catch { }

// 13. 目录恢复：重启后本地/远端回到上次退出位置
store.Upsert(new ConnectionProfile { Name = "restore", HostAlias = host, WorkDir = workDir });
vm.ApplyConnection(store.Find("restore")!);
await Task.Delay(3000);
await vm.LocalPane.NavigateAsync(Path.GetTempPath());
await Task.Delay(500);
await vm.RemotePane.NavigateAsync("/tmp");
await Task.Delay(1500);
vm.SaveCurrentState();
store.Settings.AutoConnect = true;
store.Save();

var runnerR = new ProcessRunner();
var sshR = new SshService(runnerR);
var vmR = new MainViewModel(store, runnerR, sshR, new ScpService(runnerR), dialogs);
await Task.Delay(5000);
await Check("本地目录恢复", vmR.LocalPane.CurrentPath == Path.GetTempPath(), $"got={vmR.LocalPane.CurrentPath}");
await Check("远端目录恢复", vmR.RemotePane.CurrentPath == "/tmp", $"got={vmR.RemotePane.CurrentPath}");
sshR.StopSession();

// 14. 批量删除：多选后删除所有选中项（不只一个）
var delDir = Path.Combine(Path.GetTempPath(), "ds_del_" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(delDir);
File.WriteAllText(Path.Combine(delDir, "a.txt"), "a");
File.WriteAllText(Path.Combine(delDir, "b.txt"), "b");
File.WriteAllText(Path.Combine(delDir, "c.txt"), "c");
await vm.LocalPane.NavigateAsync(delDir);
await Task.Delay(600);
foreach (var it in vm.LocalPane.Entries.Where(x => x.Name is "a.txt" or "b.txt" or "c.txt"))
    vm.LocalPane.SelectedItems.Add(it);
await vm.LocalPane.DeleteAsync();
await Task.Delay(800);
var localDelOk = !File.Exists(Path.Combine(delDir, "a.txt"))
    && !File.Exists(Path.Combine(delDir, "b.txt"))
    && !File.Exists(Path.Combine(delDir, "c.txt"));
await Check("批量删除(本地3项)", localDelOk);

await ssh.RunBashAsync(host, "touch /tmp/da.txt /tmp/db.txt /tmp/dc.txt");
await vm.RemotePane.NavigateAsync("/tmp");
await Task.Delay(1500);
foreach (var it in vm.RemotePane.Entries.Where(x => x.Name is "da.txt" or "db.txt" or "dc.txt"))
    vm.RemotePane.SelectedItems.Add(it);
await vm.RemotePane.DeleteAsync();
await Task.Delay(1500);
var remChk = await ssh.RunSimpleAsync(host, "ls /tmp/da.txt /tmp/db.txt /tmp/dc.txt 2>/dev/null | wc -l");
await Check("批量删除(远端3项)", remChk.Ok && remChk.StdOut.Trim() == "0", remChk.StdOut.Trim());
try { Directory.Delete(delDir, true); } catch { }

// 15. 面包屑分块 + 列表".."返回上一级（".."在文件列表顶部，不随隐藏过滤）
await Check("本地面包屑末段", vm.LocalPane.Breadcrumbs[^1].FullPath == vm.LocalPane.CurrentPath, $"{vm.LocalPane.CurrentPath}");
await Check("本地面包屑首段盘符", vm.LocalPane.Breadcrumbs[0].FullPath.Contains(":"), vm.LocalPane.Breadcrumbs[0].Text);
await Check("远端面包屑末段", vm.RemotePane.Breadcrumbs[^1].FullPath == vm.RemotePane.CurrentPath, $"{vm.RemotePane.CurrentPath}");
await Check("远端面包屑根段", vm.RemotePane.Breadcrumbs[0].FullPath == "/", vm.RemotePane.Breadcrumbs[0].Text);
// 列表顶部".."返回上一级：远端在"不显示隐藏"时也应有 IsParent 项（修复".."被隐藏过滤吞掉）
vm.LocalPane.ShowHidden = false;
vm.RemotePane.ShowHidden = false;
await vm.RemotePane.RefreshAsync();
await Task.Delay(800);
await Check("远端列表始终显示'..'", vm.RemotePane.Entries.Any(x => x.IsParent), $"entries={vm.RemotePane.Entries.Count}");

// 16. 多选拖拽上传（3 文件，不打包）→ 全部落远端（修复"只拖一个"）
var multiTmp = Path.Combine(Path.GetTempPath(), "ds_multi_" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(multiTmp);
for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(multiTmp, $"m{i}.txt"), $"multi-{i}");
dialogs.PackMode = PackMode.None;
await vm.LocalPane.NavigateAsync(multiTmp);
await Task.Delay(600);
var multiItems = vm.LocalPane.Entries.Where(x => x.Name is "m0.txt" or "m1.txt" or "m2.txt").ToList();
await vm.RemotePane.HandleDropAsync(new DragPayload { Format = DragFormats.LocalFileDrop, Items = multiItems }, "/tmp", false);
await Task.Delay(9000);
var multiOk = true;
for (int i = 0; i < 3; i++)
{
    var mcat = await ssh.RunSimpleAsync(host, $"cat /tmp/m{i}.txt");
    if (!(mcat.Ok && mcat.StdOut.Trim() == $"multi-{i}")) multiOk = false;
}
await Check("多选拖拽上传(3文件)", multiOk);
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["m0.txt", "m1.txt", "m2.txt"]));

// 16b. 上传确认框总字节：条目无 Size（模拟外部拖入重建）也应统计真实大小（修复"共0B"）
var sizeTmp = Path.Combine(Path.GetTempPath(), "ds_size_" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(sizeTmp);
for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(sizeTmp, $"s{i}.txt"), new string('x', 1024 * (i + 1)));
dialogs.PackMode = PackMode.None;
var sizeItems = Enumerable.Range(0, 3).Select(i => new FilePaneItem
{
    Name = $"s{i}.txt",
    FullPath = Path.Combine(sizeTmp, $"s{i}.txt"),
    IsDirectory = false, // 不设 Size=0，模拟 Explorer 拖入重建
}).ToList();
await vm.UploadAsync(sizeItems, "/tmp");
await Task.Delay(8000);
var sizeTotal = dialogs.LastPreview?.TotalBytes ?? 0;
await Check("上传确认总字节(Size=0回退统计)", sizeTotal >= 1024 * 6, $"total={sizeTotal}");
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["s0.txt", "s1.txt", "s2.txt"]));
try { Directory.Delete(sizeTmp, true); } catch { }

// 17. 打包上传（tar）：远端解压到目标目录 + 归档清理
dialogs.PackMode = PackMode.Tar;
await vm.LocalPane.NavigateAsync(multiTmp);
await Task.Delay(600);
var packItems = vm.LocalPane.Entries.Where(x => x.Name is "m0.txt" or "m1.txt" or "m2.txt").ToList();
await vm.RemotePane.HandleDropAsync(new DragPayload { Format = DragFormats.LocalFileDrop, Items = packItems }, "/tmp", false);
await Task.Delay(7000);
var packedOk = true;
for (int i = 0; i < 3; i++)
{
    var pcat = await ssh.RunSimpleAsync(host, $"cat /tmp/m{i}.txt");
    if (!(pcat.Ok && pcat.StdOut.Trim() == $"multi-{i}")) packedOk = false;
}
var leftovers = await ssh.RunSimpleAsync(host, "ls /tmp/dscp_upload_*.tar 2>/dev/null | wc -l");
await Check("打包上传解压+清理", packedOk && leftovers.StdOut.Trim() == "0", $"leftovers={leftovers.StdOut.Trim()}");
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["m0.txt", "m1.txt", "m2.txt"]));
try { Directory.Delete(multiTmp, true); } catch { }

// 17b. 打包上传（zip）：ZipArchive 打包 + 远端 unzip 解压 + 归档清理
var zipTmp = Path.Combine(Path.GetTempPath(), "ds_zip_" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(zipTmp);
File.WriteAllText(Path.Combine(zipTmp, "z1.txt"), "zip-1");
File.WriteAllText(Path.Combine(zipTmp, "z2.txt"), "zip-2");
dialogs.PackMode = PackMode.Zip;
await vm.LocalPane.NavigateAsync(zipTmp);
await Task.Delay(600);
var zipItems = vm.LocalPane.Entries.Where(x => x.Name is "z1.txt" or "z2.txt").ToList();
await vm.RemotePane.HandleDropAsync(new DragPayload { Format = DragFormats.LocalFileDrop, Items = zipItems }, "/tmp", false);
await Task.Delay(7000);
var zipOk = (await ssh.RunSimpleAsync(host, "cat /tmp/z1.txt")).StdOut.Trim() == "zip-1"
    && (await ssh.RunSimpleAsync(host, "cat /tmp/z2.txt")).StdOut.Trim() == "zip-2";
var zipLeftovers = await ssh.RunSimpleAsync(host, "ls /tmp/dscp_upload_*.zip 2>/dev/null | wc -l");
await Check("zip打包上传解压+清理", zipOk && zipLeftovers.StdOut.Trim() == "0", $"leftovers={zipLeftovers.StdOut.Trim()}");
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["z1.txt", "z2.txt"]));
try { Directory.Delete(zipTmp, true); } catch { }

// 18. 操作任务卡：文件操作在队列中显式显示（重命名）
await ssh.RunBashAsync(host, $"echo op > {ShellQuote.Quote("/tmp/ds_op.txt")}");
var opsBefore = vm.TransferQueue.Items.OfType<OperationTask>().Count();
var ren = await vm.TransferQueue.RunSshOperationAsync("重命名", "ds_op.txt → ds_op2.txt",
    ct => ssh.RunBashAsync(host, RemoteOps.Rename("/tmp", "ds_op.txt", "ds_op2.txt"), ct));
var opsAfter = vm.TransferQueue.Items.OfType<OperationTask>().Count();
var opDone = vm.TransferQueue.Items.OfType<OperationTask>()
    .Any(t => t.DisplayName == "重命名" && t.State == TransferState.Completed);
await Check("操作任务卡显示", ren is not null && opsAfter > opsBefore && opDone, $"ops={opsAfter}");
var opChk = await ssh.RunSimpleAsync(host, "test -f /tmp/ds_op2.txt && echo Y");
await Check("操作命令已执行", opChk.Ok && opChk.StdOut.Trim() == "Y");
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["ds_op2.txt"]));

// 19. 传输 ETA/乐观进度：总量已知后进度条从不确定 → 确定且进度 > 0
var etaTmp = Path.Combine(Path.GetTempPath(), "ds_eta.bin");
using (var fs = File.Create(etaTmp)) fs.SetLength(2 * 1024 * 1024); // 2MB
var etaItem = new TransferItem
{
    Direction = TransferDirection.Upload,
    LocalPath = etaTmp,
    RemotePath = "/tmp",
    DisplayName = "ds_eta.bin",
    IsDirectory = false,
    Size = new FileInfo(etaTmp).Length,
};
vm.TransferQueue.Enqueue([etaItem]);
var sawDeterminate = false;
for (int i = 0; i < 25; i++)
{
    await Task.Delay(300);
    if (!etaItem.IsIndeterminate && etaItem.Progress > 0) { sawDeterminate = true; break; }
    if (etaItem.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled) break;
}
await Check("传输ETA/乐观进度", sawDeterminate, $"indet={etaItem.IsIndeterminate} prog={etaItem.Progress:0.##} eta='{etaItem.EtaText}'");
vm.TransferQueue.CancelAllCommand.Execute(null); // 取消未完成的大传输，避免探针退出后残留 scp
await Task.Delay(800);
try { File.Delete(etaTmp); } catch { }

// 20. 密码登录模式：ProcessRunner 应注入 SSH_ASKPASS 环境（IP+密码认证的基础）
SshAuthContext.SetPassword("pw-test");
var envR = await runner.RunAsync("cmd.exe", ["/c", "echo %SSH_ASKPASS%"], CancellationToken.None, timeoutMs: 10_000);
SshAuthContext.Clear();
await Check("密码模式注入SSH_ASKPASS", envR.Ok && envR.StdOut.Trim().Length > 0, $"askpass='{envR.StdOut.Trim()}'");

// 21. 连接方式二选一：脚本/IP+密码 互斥，载入与切换正确（别名方式已移除）
var cfg2 = Path.Combine(Path.GetTempPath(), "ds_cfg2_" + Guid.NewGuid().ToString("N")[..6]);
var store2 = new ProfileStore(cfg2);
store2.Upsert(new ConnectionProfile { Name = "sc", ScriptPath = @"C:\x\login.ps1", HostAlias = "my-host", WorkDir = "/home" });
store2.Upsert(new ConnectionProfile { Name = "al", HostAlias = host, WorkDir = "/" });
store2.Upsert(new ConnectionProfile { Name = "pw", Host = "10.0.0.9", User = "user", Password = "secret", WorkDir = "/tmp" });
store2.Save();
var cv = new ConnectViewModel(store2, runner, ssh, dialogs);
cv.LoadProfileCommand.Execute("sc");
await Check("连接方式:脚本载入", cv.Method == ConnectionMethod.Script && cv.ScriptPath.Length > 0 && cv.Host == "");
cv.LoadProfileCommand.Execute("al");
await Check("连接方式:脚本兜底(纯别名)", cv.Method == ConnectionMethod.Script && cv.HostAlias == host);
cv.LoadProfileCommand.Execute("pw");
await Check("连接方式:密码载入", cv.Method == ConnectionMethod.Password && cv.User == "user" && cv.Host == "10.0.0.9" && cv.Password == "secret");
var p = cv.ToProfile();
await Check("连接方式:ToProfile密码", p.IsPasswordAuth && p.UserHost == "user@10.0.0.9" && p.ScriptPath is null && p.HostAlias == "");
cv.Method = ConnectionMethod.Password;
await Check("连接方式:切换清空脚本字段", cv.ScriptPath == "" && cv.HostAlias == "");
cv.Method = ConnectionMethod.Script;
await Check("连接方式:切换清空密码字段", cv.Host == "" && cv.User == "" && cv.Password == "");
try { Directory.Delete(cfg2, true); } catch { }

// 22. 只读文件删除：先清只读属性再删 → 不弹错、文件被删（修复"递归弹只读异常"）
var roTmp = Path.Combine(Path.GetTempPath(), "ds_ro_" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(roTmp);
var roFile = Path.Combine(roTmp, "ro.txt");
File.WriteAllText(roFile, "ro");
File.SetAttributes(roFile, FileAttributes.ReadOnly);
// 只读目录内嵌只读文件：递归删除也不该失败
var roSub = Path.Combine(roTmp, "sub");
Directory.CreateDirectory(roSub);
var roSubFile = Path.Combine(roSub, "inner.txt");
File.WriteAllText(roSubFile, "inner");
File.SetAttributes(roSubFile, FileAttributes.ReadOnly);
await vm.LocalPane.NavigateAsync(roTmp);
await Task.Delay(600);
vm.LocalPane.SelectedItems.Clear();
vm.LocalPane.SelectedItem = vm.LocalPane.Entries.FirstOrDefault(x => x.Name == "ro.txt");
var errBefore = dialogs.ErrorCount;
await vm.LocalPane.DeleteAsync();
await Task.Delay(800);
var errDelta = dialogs.ErrorCount - errBefore;
await Check("只读删除不弹错", errDelta == 0, $"errors={errDelta}");
vm.LocalPane.SelectedItems.Clear();
vm.LocalPane.SelectedItem = vm.LocalPane.Entries.FirstOrDefault(x => x.Name == "sub");
errBefore = dialogs.ErrorCount;
await vm.LocalPane.DeleteAsync();
await Task.Delay(800);
errDelta = dialogs.ErrorCount - errBefore;
var subGone = !Directory.Exists(roSub);
await Check("只读目录递归删除", errDelta == 0 && subGone, $"errors={errDelta} subGone={subGone}");
if (File.Exists(roFile)) File.SetAttributes(roFile, FileAttributes.Normal);
try { Directory.Delete(roTmp, true); } catch { }

// 23. 脏 HostAlias 自愈：脚本解析的目标覆盖垃圾别名（修复连接失败）
var healScript = Environment.GetEnvironmentVariable("WINLINSCP_LOGIN_SCRIPT") ?? "";
if (healScript.Length > 0 && File.Exists(healScript))
{
    var sshHeal = new SshService(runner);
    var scpHeal = new ScpService(runner);
    var vmHeal = new MainViewModel(
        new ProfileStore(Path.Combine(Path.GetTempPath(), "ds_heal_" + Guid.NewGuid().ToString("N")[..6])),
        runner, sshHeal, scpHeal, dialogs);
    vmHeal.ApplyConnection(new ConnectionProfile
    {
        Name = "heal",
        ScriptPath = healScript,
        HostAlias = "jump-host  (root@10.0.0.1)", // 历史脏值：别名下拉误存的显示串
        WorkDir = workDir,
    });
    await Task.Delay(4000);
    await Check("脏HostAlias自愈连接", vmHeal.RemotePane.IsConnected && vmHeal.RemotePane.CurrentPath == workDir,
        $"path={vmHeal.RemotePane.CurrentPath} host={vmHeal.RemotePane.Alias}");
    sshHeal.StopSession();
}

// 清理（注意：先做远端清理，最后再关会话——StopSession 后不能再跑 ssh 命令）
await ssh.RunBashAsync(host, RemoteOps.Delete("/tmp", ["vm_selftest.txt", "ds_eta.bin"]));
try { File.Delete(tmpFile); File.Delete(localDown); } catch { }
vm.SaveCurrentState();

ssh.StopSession(); // 最后关闭常驻会话，避免探针退出后残留 ssh 进程
try { Directory.Delete(cfgDir, true); } catch { }

Console.WriteLine(ok ? "VM-STACK: ALL PASS" : "VM-STACK: FAILED");
return ok ? 0 : 1;

sealed class FakeDialogs : IDialogService
{
    /// <summary>多选上传确认返回的打包方式（测试可配置）。</summary>
    public PackMode PackMode { get; set; } = PackMode.None;

    /// <summary>最近一次上传确认收到的预览（测试断言总字节用）。</summary>
    public UploadPreview? LastPreview { get; private set; }

    /// <summary>Error 弹窗累计次数（诊断递归弹窗用）。</summary>
    public int ErrorCount { get; private set; }

    public string? PromptText(string title, string prompt, string initial = "") => initial;
    public bool Confirm(string message, string title = "确认") => true;
    public void Info(string message, string title = "WinLinScp") => Console.WriteLine("[INFO] " + message);
    public void Error(string message, string title = "WinLinScp") { ErrorCount++; Console.WriteLine($"[ERROR#{ErrorCount}] {title}: {message}"); }
    public string? SaveFile(string title, string defaultName) => Path.Combine(Path.GetTempPath(), defaultName);
    public string? OpenFile(string title, string filter) => null;
    public UploadPlan? ConfirmUpload(UploadPreview preview)
    {
        LastPreview = preview;
        return new UploadPlan { Mode = PackMode };
    }

    /// <summary>本地 zip 压缩等级（测试可配置）。</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;
    public CompressionLevel? ChooseCompressionLevel() => CompressionLevel;
    public void ShowOutput(string title, string text) => Console.WriteLine($"[OUTPUT {title}] {text.Trim().Replace('\n', ' ')[..Math.Min(60, text.Trim().Length)]}");
}
