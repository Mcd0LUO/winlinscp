using WinLinScp.Models;
using WinLinScp.Services;

namespace WinLinScp.ViewModels;

/// <summary>远端面板：经系统 ssh/scp 列举与增删改。带目录列表缓存（返回/后退秒开 + 后台刷新）。</summary>
public sealed partial class RemotePaneViewModel : FilePaneViewModel
{
    private readonly SshService _ssh;
    private readonly RemoteFileListing _listing;

    /// <summary>path → 最近一次列举结果。命中即秒开，随后后台刷新。</summary>
    private readonly Dictionary<string, List<RemoteFileInfo>> _cache = new();

    public RemotePaneViewModel(SshService ssh, RemoteFileListing listing, IDialogService dialogs)
        : base(dialogs)
    {
        _ssh = ssh;
        _listing = listing;
    }

    /// <summary>请求打开远端文件查看器（path, name）。由 MainWindow 订阅。</summary>
    public event Action<string, string>? ViewFileRequested;

    public string Alias { get; private set; } = "";

    public bool IsConnected => Alias.Length > 0;

    public override bool SupportsDownload => true;
    public override bool SupportsArchive => true;
    public override bool SupportsTarGz => true;

    protected override IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbs(string path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<BreadcrumbSegment>(); // 未连接

        var segments = new List<BreadcrumbSegment>();
        if (path.StartsWith('/'))
        {
            segments.Add(new BreadcrumbSegment { Text = "/", FullPath = "/" });
            var cur = "/";
            foreach (var part in path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                cur = RemotePath.Combine(cur, part);
                segments.Add(new BreadcrumbSegment { Text = part, FullPath = cur, ShowChevron = true });
            }
        }
        else
        {
            var cur = "";
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                cur = i == 0 ? parts[0] : RemotePath.Combine(cur, parts[i]);
                segments.Add(new BreadcrumbSegment { Text = parts[i], FullPath = cur, ShowChevron = i > 0 });
            }
        }
        if (segments.Count == 0) return segments;
        var last = segments[^1];
        segments[^1] = new BreadcrumbSegment { Text = last.Text, FullPath = last.FullPath, ShowChevron = last.ShowChevron, IsCurrent = true };
        return segments;
    }

    public void Connect(string alias, string workDir)
    {
        InvalidateNavigation(); // 作废旧连接的导航/后台刷新
        Alias = alias;
        _cache.Clear();
        _ = ConnectAndNavigateAsync(workDir);
    }

    /// <summary>连接后：先确保工作目录存在（与登录脚本一致），再列目录；首次连接失败重试一次。</summary>
    private async Task ConnectAndNavigateAsync(string workDir)
    {
        // 确保目录存在并顺手建立会话（mkdir -p 已存在则无副作用）
        try { await _ssh.RunBashAsync(Alias, RemoteOps.EnsureDir(workDir)); }
        catch { /* 首连失败不阻塞，下一步重试 */ }

        await NavigateAsync(workDir);
        if (CurrentPath != workDir && !string.IsNullOrEmpty(workDir))
        {
            // 冷启动首连可能偶发失败：重试一次
            await Task.Delay(500);
            await NavigateAsync(workDir);
        }
    }

    public void Disconnect()
    {
        InvalidateNavigation(); // 作废在途导航，避免断开后旧目录回填
        Alias = "";
        _cache.Clear();
        Entries.Clear();
        CurrentPath = "";
        StatusText = "";
        _ssh.StopSession();
    }

    public override async Task<IReadOnlyList<FilePaneItem>> LoadAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            // 缓存命中：立即返回（返回/后退秒开），后台刷新保持新鲜
            _ = RefreshInBackgroundAsync(path);
            return cached.Where(i => ShowHidden || !i.IsHidden).Select(i => Map(i, path)).ToList();
        }

        var infos = await _listing.ListAsync(Alias, path, ct);
        _cache[path] = infos.ToList();
        return infos.Where(i => ShowHidden || !i.IsHidden).Select(i => Map(i, path)).ToList();
    }

    /// <summary>F5 强制重新拉取当前目录。</summary>
    public override Task RefreshAsync()
    {
        _cache.Remove(CurrentPath);
        return base.RefreshAsync();
    }

    private async Task RefreshInBackgroundAsync(string path)
    {
        var gen = CurrentGeneration;
        var alias = Alias;
        try
        {
            var fresh = await _listing.ListAsync(alias, path, CancellationToken.None);
            if (gen != CurrentGeneration || alias != Alias) return; // 已切换连接/导航，丢弃过期结果
            _cache[path] = fresh.ToList();
            if (CurrentPath == path)                       // 仍是当前目录才就地更新
                RenderEntries(fresh.Where(i => ShowHidden || !i.IsHidden).Select(i => Map(i, path)).ToList());
        }
        catch (RemoteOperationException) { /* 后台刷新失败：保留旧缓存 */ }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 未知异常：保留旧缓存，避免 fire-and-forget 冒泡成未处理异常弹窗 */ }
    }

    public override string? GetParent(string path) => RemotePath.GetParent(path);

    public override Task OpenFileAsync(FilePaneItem item)
    {
        // 仅文件/指向文件的符号链接会走到这里（目录走导航）
        ViewFileRequested?.Invoke(item.FullPath, item.Name);
        return Task.CompletedTask;
    }

    public override async Task NewFolderAsync()
    {
        var name = Dialogs.PromptText("新建文件夹", "文件夹名称：", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!await RunAndRefresh(RemoteOps.MkDir(CurrentPath, name), "新建文件夹", name)) return;
    }

    public override async Task NewFileAsync()
    {
        var name = Dialogs.PromptText("新建文件", "文件名称：", "新建文件.txt");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!await RunAndRefresh(RemoteOps.Touch(CurrentPath, name), "新建文件", name)) return;
    }

    public override async Task RenameAsync()
    {
        if (SelectedItem is not { } item || item.IsParent) return;
        var newName = Dialogs.PromptText("重命名", "新名称：", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        if (!await RunAndRefresh(RemoteOps.Rename(CurrentPath, item.Name, newName), "重命名", $"{item.Name} → {newName}")) return;
    }

    public override async Task DeleteAsync()
    {
        var targets = GetDeletionTargets().Where(i => !i.IsParent).Select(i => i.Name).ToList();
        if (targets.Count == 0) return;

        var summary = targets.Count <= 5
            ? string.Join("、", targets)
            : string.Join("、", targets.Take(5)) + $" 等 {targets.Count} 项";
        if (!Dialogs.Confirm($"确定删除远端 {targets.Count} 项？\n{summary}\n此操作不可恢复。", "删除确认"))
            return;

        if (!await RunAndRefresh(RemoteOps.Delete(CurrentPath, targets), "删除", $"{targets.Count} 项")) return;
    }

    // ---------------- 归档：压缩/解压 ----------------

    protected override bool IsArchive(FilePaneItem item) => IsArchiveName(item.Name);

    protected override async Task CompressCoreAsync(string format)
    {
        if (SelectedItem is not { } item || item.IsParent) return;
        var script = format == ".zip"
            ? RemoteOps.CompressZip(CurrentPath, item.Name)
            : RemoteOps.CompressTarGz(CurrentPath, item.Name);
        await RunAndRefresh(script, "压缩", item.Name);
    }

    protected override async Task ExtractCoreAsync()
    {
        if (SelectedItem is not { } item || item.IsParent || !IsArchiveName(item.Name)) return;
        await RunAndRefresh(RemoteOps.Extract(CurrentPath, item.Name), "解压", item.Name);
    }

    private static bool IsArchiveName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".zip")
            || n.EndsWith(".tar") || n.EndsWith(".tar.gz") || n.EndsWith(".tgz")
            || n.EndsWith(".tar.bz2") || n.EndsWith(".tar.xz") || n.EndsWith(".gz");
    }

    // ---------------- 传输 ----------------

    /// <summary>远端操作统一骨架：队列显式任务卡 + 执行 + 刷新 + 错误弹窗。</summary>
    private async Task<bool> RunAndRefresh(string script, string opName, string? targetText = null)
    {
        if (Queue is null)
        {
            try
            {
                var r = await _ssh.RunBashAsync(Alias, script);
                if (!r.Ok)
                {
                    Dialogs.Error(SshErrorTranslator.Describe(r, opName));
                    return false;
                }
                await RefreshAsync();
                return true;
            }
            catch (Exception ex)
            {
                Dialogs.Error(ex.Message, opName + "失败");
                return false;
            }
        }

        var res = await Queue.RunSshOperationAsync(opName, targetText ?? CurrentPath,
            ct => _ssh.RunBashAsync(Alias, script, ct));
        if (res is null) return false; // 取消/异常：任务卡已显示
        if (!res.Ok)
        {
            Dialogs.Error(SshErrorTranslator.Describe(res, opName));
            return false;
        }
        await RefreshAsync();
        return true;
    }

    /// <summary>下载选中项（全部选中）到本地面板当前目录。</summary>
    protected override async Task DownloadCoreAsync()
    {
        if (Coordinator is null) return;
        var targets = GetDeletionTargets().Where(i => !i.IsParent).ToList();
        if (targets.Count == 0) return;
        await Coordinator.DownloadAsync(targets);
    }

    // ---------------- 拖拽：远端可拖出(RemoteItem)、可接收(本地文件上传) ----------------

    public override DragPayload? CreateDragPayload(IReadOnlyList<FilePaneItem> items) =>
        items.Count > 0 ? new DragPayload { Format = DragFormats.RemoteItem, Items = items } : null;

    public override bool CanAcceptDrop(DragPayload payload) =>
        (payload.Format is DragFormats.LocalFileDrop or DragFormats.RemoteItem) && IsConnected;

    public override async Task HandleDropAsync(DragPayload payload, string targetDir, bool copy)
    {
        if (payload.Format == DragFormats.RemoteItem)
        {
            // linux 内移动：拖到目录行 → ssh mv 进该目录（同目录则 no-op）
            var names = payload.Items.Select(i => RemotePath.Name(i.FullPath)).ToList();
            if (names.Count == 0) return;
            if (SameRemoteDir(targetDir, CurrentPath)) return;
            await RunAndRefresh(RemoteOps.Move(CurrentPath, names, targetDir), "移动", targetDir);
        }
        else if (payload.Format == DragFormats.LocalFileDrop)
        {
            // 本地文件拖入：上传到目标目录（目录行 or 当前目录）
            if (Coordinator is null) return;
            await Coordinator.UploadAsync(payload.Items, targetDir);
        }
    }

    private static string NormRemote(string p) => p.TrimEnd('/').Length > 0 ? p.TrimEnd('/') : "/";

    private static bool SameRemoteDir(string a, string b) =>
        string.Equals(NormRemote(a), NormRemote(b), StringComparison.Ordinal);

    // ---------------- 执行自定义脚本（远端默认 bash） ----------------

    protected override async Task RunCustomScriptCoreAsync()
    {
        var script = Dialogs.PromptText("执行自定义脚本", "输入要执行的 bash 命令/脚本（工作目录：当前远端目录）：", "");
        if (string.IsNullOrWhiteSpace(script)) return;
        try
        {
            var cmd = $"cd -- {ShellQuote.Quote(CurrentPath)} && {script}";
            var r = Queue is null
                ? await _ssh.RunBashAsync(Alias, cmd)
                : await Queue.RunSshOperationAsync("执行脚本", CurrentPath, ct => _ssh.RunBashAsync(Alias, cmd, ct));
            if (r is null) return; // 取消/异常：任务卡已显示
            var text = (r.StdOut + (r.StdErr.Length > 0 ? "\n[stderr]\n" + r.StdErr.Trim() : "")).Trim();
            if (text.Length == 0) text = "(无输出)";
            if (!r.Ok) text += $"\n\n[退出码 {r.ExitCode}]";
            Dialogs.ShowOutput($"bash 执行结果 · {CurrentPath}", text);
        }
        catch (Exception ex) { Dialogs.Error(ex.Message, "执行脚本失败"); }
    }

    private static FilePaneItem Map(RemoteFileInfo info, string dir)
    {
        var full = info.IsParent
            ? (RemotePath.GetParent(dir) ?? dir)
            : RemotePath.Combine(dir, info.Name);
        return new FilePaneItem
        {
            Name = info.Name,
            FullPath = full,
            IsDirectory = info.IsNavigable,
            IsParent = info.IsParent,
            IsHidden = info.IsHidden,
            Size = info.Size,
            Mtime = info.MtimeUtc,
            IsSymlink = info.Kind == RemoteFileKind.Symlink,
            Extension = info.Extension,
        };
    }
}
