using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using WinLinScp.Models;
using WinLinScp.Services;
using WinLinScp.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace WinLinScp.ViewModels;

/// <summary>本地面板：Windows 文件系统列举与增删改。</summary>
public sealed partial class LocalPaneViewModel : FilePaneViewModel
{
    private readonly ProcessRunner _runner;

    public LocalPaneViewModel(ProcessRunner runner, IDialogService dialogs) : base(dialogs)
    {
        _runner = runner;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            PathHistory.Add(d.RootDirectory.FullName);
    }

    protected override IComparer<FilePaneItem> SortComparer { get; } = Comparer<FilePaneItem>.Create((a, b) =>
    {
        int cmp = b.IsParent.CompareTo(a.IsParent);
        if (cmp != 0) return cmp;
        cmp = b.IsDirectory.CompareTo(a.IsDirectory);
        if (cmp != 0) return cmp;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    });

    public override bool SupportsUpload => true;
    public override bool SupportsArchive => true;
    public override bool SupportsShellTools => true;

    // ---------------- shell 工具：以终端/任务管理器打开 ----------------

    /// <summary>在所选文件夹（或当前目录）打开 Windows Terminal；不可用则回退 cmd。</summary>
    [RelayCommand]
    private void OpenTerminal()
    {
        var dir = SelectedItem is { IsDirectory: true } f ? f.FullPath : CurrentPath;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Process.Start(new ProcessStartInfo("wt") { Arguments = $"-d \"{dir}\"", UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/k cd /d \"{dir}\"",
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Dialogs.Error(ex.Message, "打开终端失败"); }
        }
    }

    [RelayCommand]
    private void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error(ex.Message, "打开任务管理器失败"); }
    }

    protected override IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbs(string path)
    {
        // 空路径 = “我的电脑”盘符根
        if (string.IsNullOrEmpty(path))
            return [new BreadcrumbSegment { Text = "此电脑", FullPath = "", IsCurrent = true }];

        var segments = new List<BreadcrumbSegment>();
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return [new BreadcrumbSegment { Text = "此电脑", FullPath = "", IsCurrent = true }];

        string cur;
        if (parts[0].Contains(':'))
        {
            // 首段盘符：D: → D:\
            cur = parts[0] + Path.DirectorySeparatorChar;
            segments.Add(new BreadcrumbSegment { Text = parts[0], FullPath = cur });
        }
        else
        {
            cur = parts[0];
            segments.Add(new BreadcrumbSegment { Text = parts[0], FullPath = cur });
        }
        for (int i = 1; i < parts.Length; i++)
        {
            cur = Path.Combine(cur, parts[i]);
            segments.Add(new BreadcrumbSegment { Text = parts[i], FullPath = cur, ShowChevron = true });
        }
        var last = segments[^1];
        segments[^1] = new BreadcrumbSegment { Text = last.Text, FullPath = last.FullPath, ShowChevron = last.ShowChevron, IsCurrent = true };
        return segments;
    }

    // ---------------- 本地归档：zip 压缩/解压（.NET 内置，正确处理中文文件名） ----------------

    protected override bool IsArchive(FilePaneItem item) =>
        item.Extension?.Equals(".zip", StringComparison.OrdinalIgnoreCase) == true;

    protected override async Task CompressCoreAsync(string format)
    {
        if (format != ".zip") return; // 本地默认 zip
        if (SelectedItem is not { } item || item.IsParent) return;

        var level = Dialogs.ChooseCompressionLevel();
        if (level is null) return; // 取消压缩

        var zipPath = item.FullPath + ".zip";
        if (File.Exists(zipPath) || Directory.Exists(zipPath))
        {
            if (!Dialogs.Confirm($"{Path.GetFileName(zipPath)} 已存在，覆盖？", "压缩确认")) return;
        }

        var lv = level.Value;
        await RunLocalAsync(() =>
        {
            if (item.IsDirectory)
            {
                ZipFile.CreateFromDirectory(item.FullPath, zipPath, lv, includeBaseDirectory: true);
            }
            else
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(item.FullPath, item.Name, lv);
            }
            return Task.CompletedTask;
        }, "压缩 zip", $"{item.Name}（{CompressionLevelName(lv)}）");
    }

    /// <summary>压缩等级 → 中文名（任务卡上显示，让用户知道用了哪个等级）。</summary>
    private static string CompressionLevelName(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => "不压缩",
        CompressionLevel.Fastest => "最快",
        CompressionLevel.SmallestSize => "最小体积",
        _ => "标准",
    };

    protected override async Task ExtractCoreAsync()
    {
        if (SelectedItem is not { } item || item.IsParent || !IsArchive(item)) return;
        if (!Dialogs.Confirm($"确定将「{item.Name}」解压到当前目录？若文件已存在将被覆盖。", "解压确认")) return;

        await RunLocalAsync(() =>
        {
            ZipFile.ExtractToDirectory(item.FullPath, CurrentPath, overwriteFiles: true);
            return Task.CompletedTask;
        }, "解压", item.Name);
    }

    public override Task<IReadOnlyList<FilePaneItem>> LoadAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
            return Task.FromResult((IReadOnlyList<FilePaneItem>)LoadDrives());
        return Task.Run(() => (IReadOnlyList<FilePaneItem>)LoadDirectory(path, ct), ct);
    }

    public override string? GetParent(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parent = Directory.GetParent(path);
        return parent?.FullName ?? ""; // 盘符根之上 = “我的电脑”
    }

    public override Task OpenFileAsync(FilePaneItem item)
    {
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error(ex.Message, "打开失败"); }
        return Task.CompletedTask;
    }

    public override async Task NewFolderAsync()
    {
        var name = Dialogs.PromptText("新建文件夹", "文件夹名称：", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunLocalAsync(() => { Directory.CreateDirectory(Path.Combine(CurrentPath, name)); return Task.CompletedTask; }, "新建文件夹", name);
    }

    public override async Task NewFileAsync()
    {
        var name = Dialogs.PromptText("新建文件", "文件名称：", "新建文件.txt");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunLocalAsync(() =>
        {
            using (File.Create(Path.Combine(CurrentPath, name))) { }
            return Task.CompletedTask;
        }, "新建文件", name);
    }

    public override async Task RenameAsync()
    {
        if (SelectedItem is not { } item || item.IsParent) return;
        var newName = Dialogs.PromptText("重命名", "新名称：", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        await RunLocalAsync(() =>
        {
            var dest = Path.Combine(Path.GetDirectoryName(item.FullPath) ?? "", newName);
            if (item.IsDirectory) Directory.Move(item.FullPath, dest);
            else File.Move(item.FullPath, dest);
            return Task.CompletedTask;
        }, "重命名", $"{item.Name} → {newName}");
    }

    public override async Task DeleteAsync()
    {
        var targets = GetDeletionTargets().Where(i => !i.IsParent).ToList();
        if (targets.Count == 0) return;

        var summary = targets.Count <= 5
            ? string.Join("、", targets.Select(t => t.Name))
            : string.Join("、", targets.Take(5).Select(t => t.Name)) + $" 等 {targets.Count} 项";
        if (!Dialogs.Confirm($"确定删除 {targets.Count} 项？\n{summary}\n此操作不可恢复。", "删除确认"))
            return;

        await RunLocalAsync(() =>
        {
            foreach (var item in targets)
            {
                if (item.IsDirectory) DeleteDirClearingReadOnly(item.FullPath);
                else DeleteFileClearingReadOnly(item.FullPath);
            }
            return Task.CompletedTask;
        }, "删除", $"{targets.Count} 项");
    }

    /// <summary>删除单个文件：先清只读属性再删（只读文件 File.Delete 会抛拒绝访问）。</summary>
    private static void DeleteFileClearingReadOnly(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Exists && (fi.Attributes & FileAttributes.ReadOnly) != 0)
            fi.Attributes &= ~FileAttributes.ReadOnly;
        File.Delete(path);
    }

    /// <summary>递归删除目录：先清目录内所有文件/子目录的只读属性（递归删除遇只读子文件会失败）。</summary>
    private static void DeleteDirClearingReadOnly(string path)
    {
        var dir = new DirectoryInfo(path);
        foreach (var f in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((f.Attributes & FileAttributes.ReadOnly) != 0)
                f.Attributes &= ~FileAttributes.ReadOnly;
        }
        Directory.Delete(path, recursive: true);
    }

    /// <summary>本地操作统一骨架：队列显式任务卡 + 线程池执行（不卡 UI）+ 刷新 + 错误弹窗。</summary>
    private async Task RunLocalAsync(Func<Task> action, string opName, string? targetText = null)
    {
        if (Queue is null)
        {
            try { await Task.Run(action); await RefreshAsync(); }
            catch (Exception ex) { Dialogs.Error(ex.Message, opName + "失败"); }
            return;
        }
        var (ok, err) = await Queue.RunOperationAsync(opName, targetText ?? "...", _ => Task.Run(action));
        if (!ok) { Dialogs.Error(err ?? "操作失败", opName + "失败"); return; }
        await RefreshAsync();
    }

    /// <summary>上传选中项（全部选中）到远端面板当前目录。</summary>
    protected override async Task UploadCoreAsync()
    {
        if (Coordinator is null) return;
        var targets = GetDeletionTargets().Where(i => !i.IsParent).ToList();
        if (targets.Count == 0) return;
        await Coordinator.UploadAsync(targets);
    }

    // ---------------- 拖拽：本地可拖出(FileDrop)、可接收(本地移动/复制 或 远端下载) ----------------

    public override DragPayload? CreateDragPayload(IReadOnlyList<FilePaneItem> items) =>
        items.Count > 0 ? new DragPayload { Format = DragFormats.LocalFileDrop, Items = items } : null;

    public override bool CanAcceptDrop(DragPayload payload) =>
        payload.Format is DragFormats.LocalFileDrop or DragFormats.RemoteItem;

    public override async Task HandleDropAsync(DragPayload payload, string targetDir, bool copy)
    {
        if (payload.Format == DragFormats.RemoteItem)
        {
            // 远端 → 本地：下载到目标目录（目录行 or 当前目录）
            if (Coordinator is null) return;
            await Coordinator.DownloadAsync(payload.Items, targetDir);
        }
        else if (payload.Format == DragFormats.LocalFileDrop)
        {
            // 本地文件拖入目标目录：移动（默认）或复制（Ctrl）
            await MoveOrCopyIntoAsync(payload.Items, targetDir, copy);
        }
    }

    private async Task MoveOrCopyIntoAsync(IReadOnlyList<FilePaneItem> items, string targetDir, bool copy)
    {
        if (string.IsNullOrEmpty(targetDir)) return;
        int moved = 0;
        foreach (var item in items)
        {
            var dest = Path.Combine(targetDir, item.Name);
            if (string.Equals(Path.GetFullPath(item.FullPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                continue; // 已在目标目录

            bool destExists = File.Exists(dest) || Directory.Exists(dest);
            if (destExists && !Dialogs.Confirm($"{item.Name} 已存在，覆盖？", "移动确认")) continue;

            try
            {
                if (copy) CopyItem(item.FullPath, dest, item.IsDirectory);
                else MoveItem(item.FullPath, dest, item.IsDirectory);
                moved++;
            }
            catch (Exception ex) { Dialogs.Error(ex.Message, item.IsDirectory ? "移动目录失败" : "移动文件失败"); }
        }
        if (moved > 0) await RefreshAsync();
    }

    /// <summary>移动文件/目录，覆盖时先备份目标、成功后再删备份——避免"先删后移失败"导致数据丢失。</summary>
    private static void MoveItem(string src, string dest, bool isDir)
    {
        if (isDir)
        {
            string? backup = null;
            if (Directory.Exists(dest))
            {
                backup = dest + ".dsbak" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(dest, backup);
            }
            try
            {
                Directory.Move(src, dest);
            }
            catch
            {
                if (backup is not null) { try { Directory.Move(backup, dest); } catch { } } // 回滚
                throw;
            }
            if (backup is not null) { try { Directory.Delete(backup, true); } catch { } }
        }
        else
        {
            try
            {
                File.Move(src, dest, overwrite: true); // 原生覆盖移动，无先删后移窗口
            }
            catch (IOException)
            {
                // 跨卷不支持 Move：复制 + 删除源（源删除失败也留有副本，不丢数据）
                File.Copy(src, dest, overwrite: true);
                File.Delete(src);
            }
        }
    }

    private static void CopyItem(string src, string dest, bool isDir)
    {
        if (isDir) CopyDirectory(src, dest);
        else File.Copy(src, dest, overwrite: true);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(source))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(source))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    // ---------------- 执行自定义脚本（Windows 默认 PowerShell） ----------------

    protected override async Task RunCustomScriptCoreAsync()
    {
        var script = Dialogs.PromptText("执行自定义脚本", "输入要执行的 PowerShell 命令/脚本（工作目录：当前文件夹）：", "");
        if (string.IsNullOrWhiteSpace(script)) return;
        try
        {
            var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script };
            SshResult? r = Queue is null
                ? await _runner.RunAsync("powershell", args, CancellationToken.None, timeoutMs: 120_000, workingDirectory: CurrentPath)
                : await Queue.RunSshOperationAsync("执行脚本", CurrentPath,
                    ct => _runner.RunAsync("powershell", args, ct, timeoutMs: 120_000, workingDirectory: CurrentPath));
            if (r is null) return; // 取消/异常：任务卡已显示

            var text = (r.StdOut + (r.StdErr.Length > 0 ? "\n[stderr]\n" + r.StdErr.Trim() : "")).Trim();
            if (text.Length == 0) text = "(无输出)";
            if (!r.Ok) text += $"\n\n[退出码 {r.ExitCode}]";
            Dialogs.ShowOutput($"PowerShell 执行结果 · {Path.GetFileName(CurrentPath)}\\", text);
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "执行脚本失败"); }
    }

    private IReadOnlyList<FilePaneItem> LoadDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new FilePaneItem
            {
                Name = d.Name.TrimEnd('\\') + "\\",
                FullPath = d.RootDirectory.FullName,
                IsDirectory = true,
                IsDrive = true,
                Size = d.TotalSize,
                Mtime = default,
            })
            .ToList();
    }

    private IReadOnlyList<FilePaneItem> LoadDirectory(string path, CancellationToken ct)
    {
        var result = new List<FilePaneItem>();
        var parent = Directory.GetParent(path);
        if (parent is not null)
            result.Add(new FilePaneItem { Name = "..", FullPath = parent.FullName, IsDirectory = true, IsParent = true });

        // EnumerateFileSystemInfos 一次返回带元数据的 FileSystemInfo，避免每文件多次系统调用
        foreach (var fsi in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool isDir = fsi is DirectoryInfo;
                bool hidden = (fsi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (hidden && !ShowHidden) continue;

                long size = isDir ? 0 : ((FileInfo)fsi).Length;
                result.Add(new FilePaneItem
                {
                    Name = fsi.Name,
                    FullPath = fsi.FullName,
                    IsDirectory = isDir,
                    IsHidden = hidden,
                    Size = size,
                    Mtime = fsi.LastWriteTime,
                    Extension = isDir ? null : Path.GetExtension(fsi.Name),
                });
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return result;
    }
}
