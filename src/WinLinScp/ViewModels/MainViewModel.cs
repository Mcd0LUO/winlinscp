using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinLinScp.Models;
using WinLinScp.Services;

namespace WinLinScp.ViewModels;

/// <summary>主视图模型：持有双栏、传输队列与连接状态，实现跨栏传输协调。</summary>
public sealed partial class MainViewModel : ObservableObject, ITransferCoordinator
{
    private readonly ProfileStore _store;
    private readonly ProcessRunner _runner;
    private readonly SshService _ssh;
    private readonly ScpService _scp;
    private readonly IDialogService _dialogs;
    private string? _connectedProfileName;

    public MainViewModel(ProfileStore store, ProcessRunner runner, SshService ssh, ScpService scp, IDialogService dialogs)
    {
        _store = store;
        _runner = runner;
        _ssh = ssh;
        _scp = scp;
        _dialogs = dialogs;
        _store.Load();

        LocalPane = new LocalPaneViewModel(runner, dialogs);
        RemotePane = new RemotePaneViewModel(ssh, new RemoteFileListing(ssh), dialogs);
        TransferQueue = new TransferQueueViewModel(ssh, scp, () => RemotePane.Alias);

        LocalPane.Coordinator = this;
        RemotePane.Coordinator = this;
        LocalPane.Queue = TransferQueue;
        RemotePane.Queue = TransferQueue;

        // 传输全部完成后刷新双栏（上传/下载可能改变了目录内容）
        TransferQueue.AllCompleted += () =>
        {
            _ = RemotePane.RefreshAsync();
            _ = LocalPane.RefreshAsync();
        };

        ShowHidden = _store.Settings.ShowHidden;
        LocalPane.ShowHidden = ShowHidden;
        RemotePane.ShowHidden = ShowHidden;

        _ = InitializeAsync();
    }

    public LocalPaneViewModel LocalPane { get; }
    public RemotePaneViewModel RemotePane { get; }
    public TransferQueueViewModel TransferQueue { get; }

    public SshService Ssh => _ssh;
    public ScpService Scp => _scp;
    public ProfileStore Store => _store;
    public ProcessRunner Runner => _runner;

    /// <summary>主窗口订阅：打开连接对话框。</summary>
    public event Action? ConnectRequested;

    /// <summary>主窗口订阅：打开"关于"对话框。</summary>
    public event Action? AboutRequested;

    [RelayCommand]
    private void About() => AboutRequested?.Invoke();

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    private string _connectedText = "未连接";

    private async Task InitializeAsync()
    {
        // 先让出 UI 线程：窗口显示之后再启动预连接与本地面板，
        // 避免 SSH 进程 spawn 等启动工作在 win.Show() 之前阻塞窗口
        await Task.Yield();

        // 预连接：与本地面板并行启动自动连接（内部 fire-and-forget），
        // 让 SSH 建连(~1s)与本地面板枚举重叠，右侧不再等左侧完成才开始
        if (_store.Settings.AutoConnect)
            StartAutoConnect();

        var last = _store.Settings.LastLocalDir;
        if (!Directory.Exists(last))
            last = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await LocalPane.NavigateAsync(last);
    }

    private void StartAutoConnect()
    {
        if (_store.Settings.LastProfileName is { } name && _store.Find(name) is { } p)
        {
            ApplyConnection(p);
        }
        else
        {
            StatusText = $"自动连接：找不到配置「{_store.Settings.LastProfileName}」，请手动连接";
        }
    }

    partial void OnShowHiddenChanged(bool value)
    {
        LocalPane.ShowHidden = value;
        RemotePane.ShowHidden = value;
        _ = LocalPane.RefreshAsync();
        _ = RemotePane.RefreshAsync();
    }

    [RelayCommand]
    private void Connect() => ConnectRequested?.Invoke();

    /// <summary>连接对话框成功后调用：给远端面板接线并持久化。</summary>
    public void ApplyConnection(ConnectionProfile profile)
    {
        _connectedProfileName = profile.Name;

        var host = profile.UserHost;
        var workDirOverride = profile.WorkDir;
        if (profile.IsPasswordAuth)
        {
            // IP+密码 → 密码模式（ssh 经 SSH_ASKPASS 免交互取密码）
            SshAuthContext.SetPassword(profile.Password);
        }
        else
        {
            SshAuthContext.Clear();
            // 脚本方式是权威目标来源：脚本解析的 Target/WorkDir 覆盖历史脏值（修复别名下拉误存显示串导致的连接失败）
            if (!string.IsNullOrEmpty(profile.ScriptPath))
            {
                var (t, w) = LoginScriptParser.ParseFile(profile.ScriptPath);
                if (!string.IsNullOrEmpty(t)) host = t;
                if (!string.IsNullOrEmpty(w)) workDirOverride = w;
            }
            else
            {
                host = profile.HostAlias;
            }
        }

        // 恢复到上次退出的远端目录（仅当同一配置且已有记录），否则用配置工作目录
        var workDir = _store.Settings.LastRemoteProfile == profile.Name && !string.IsNullOrEmpty(_store.Settings.LastRemoteDir)
            ? _store.Settings.LastRemoteDir
            : (workDirOverride.Length > 0 ? workDirOverride : "/");

        RemotePane.Connect(host, workDir);
        ConnectedText = $"已连接 {host}";
        StatusText = $"已连接 {host} · {workDir}";
        _store.Settings.LastProfileName = profile.Name;
        _store.Settings.LastRemoteDir = workDir;
        _store.Settings.LastRemoteProfile = profile.Name;
        _store.Save();
    }

    [RelayCommand]
    private void Disconnect() => DisconnectInternal();

    private void DisconnectInternal()
    {
        SshAuthContext.Clear();
        RemotePane.Disconnect();
        ConnectedText = "未连接";
        StatusText = "未连接";
    }

    /// <summary>退出前保存面板位置与设置。</summary>
    public void SaveCurrentState()
    {
        _store.Settings.LastLocalDir = LocalPane.CurrentPath;
        _store.Settings.LastRemoteDir = RemotePane.CurrentPath;
        _store.Settings.LastRemoteProfile = _connectedProfileName;
        _store.Settings.ShowHidden = ShowHidden;
        _store.Save();
    }

    // ---------------- ITransferCoordinator ----------------

    public Task DownloadAsync(IReadOnlyList<FilePaneItem> remoteItems, string? targetDir = null)
    {
        if (!RemotePane.IsConnected) { _dialogs.Error("尚未连接远端。"); return Task.CompletedTask; }
        targetDir ??= LocalPane.CurrentPath;
        if (string.IsNullOrEmpty(targetDir)) { _dialogs.Error("本地面板未在有效目录。"); return Task.CompletedTask; }

        var items = remoteItems.Select(item => new TransferItem
        {
            Direction = TransferDirection.Download,
            RemotePath = item.FullPath,
            LocalPath = targetDir,
            DisplayName = item.Name,
            IsDirectory = item.IsDirectory,
            Size = item.Size,
        });
        TransferQueue.Enqueue(items);
        return Task.CompletedTask;
    }

    public async Task UploadAsync(IReadOnlyList<FilePaneItem> localItems, string? targetDir = null)
    {
        if (!RemotePane.IsConnected) { _dialogs.Error("尚未连接远端。"); return; }
        targetDir ??= RemotePane.CurrentPath;
        if (string.IsNullOrEmpty(targetDir)) { _dialogs.Error("远端面板未在有效目录。"); return; }

        // 多选上传：确认 + 打包选项（不打包 | tar | zip）
        if (localItems.Count > 1)
        {
            var preview = new UploadPreview
            {
                Count = localItems.Count,
                TotalBytes = localItems.Sum(EstimateItemSize),
                Destination = targetDir,
            };
            var plan = _dialogs.ConfirmUpload(preview);
            if (plan is null) return; // 用户取消
            if (plan.Mode != PackMode.None)
            {
                await UploadPackedAsync(localItems, targetDir, plan.Mode);
                return;
            }
        }

        // 逐个上传（含单个文件夹 = 一次 scp -r）
        var items = localItems.Select(item => new TransferItem
        {
            Direction = TransferDirection.Upload,
            LocalPath = item.FullPath,
            RemotePath = targetDir,
            DisplayName = item.Name,
            IsDirectory = item.IsDirectory,
            Size = item.Size,
        });
        TransferQueue.Enqueue(items);
    }

    private static long EstimateItemSize(FilePaneItem item)
    {
        if (item.IsDirectory) return 0; // 文件夹体积未知，不扫描
        if (item.Size > 0) return item.Size;
        // 外部拖入（Explorer）重建的条目无 Size：回退统计本地文件真实大小
        try { return File.Exists(item.FullPath) ? new FileInfo(item.FullPath).Length : 0; }
        catch { return 0; }
    }

    /// <summary>打包上传：本地打单个归档 → 上传单文件（有真实进度）→ 远端解压到目标目录并清理。</summary>
    private async Task UploadPackedAsync(IReadOnlyList<FilePaneItem> items, string targetDir, PackMode mode)
    {
        // 公共基准目录（同面板多选 = 同目录）
        string? baseDir = null;
        foreach (var it in items)
        {
            var parent = Path.GetDirectoryName(it.FullPath);
            if (baseDir is null) baseDir = parent;
            else if (parent is not null && !string.Equals(Path.GetFullPath(parent), Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase))
            {
                baseDir = null;
                break;
            }
        }
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            _dialogs.Error("无法确定打包基准目录，请逐个上传。");
            return;
        }

        var isZip = mode == PackMode.Zip;
        var archiveName = $"dscp_upload_{Guid.NewGuid().ToString("N")[..8]}.{(isZip ? "zip" : "tar")}";
        var archivePath = Path.Combine(Path.GetTempPath(), "WinLinScp", archiveName);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        TempFileTracker.Register(archivePath);

        // 打包（显式任务卡；打包是 CPU/IO 活，走线程池）
        var sources = items.Select(i => new ArchiveBuilder.ArchiveItem(i.FullPath, i.IsDirectory)).ToList();
        var (ok, err) = await TransferQueue.RunOperationAsync(
            isZip ? "打包 zip" : "打包 tar", $"{items.Count} 项",
            ct => Task.Run(() => ArchiveBuilder.Build(archivePath, isZip, baseDir, sources), ct));
        if (!ok)
        {
            try { File.Delete(archivePath); } catch { }
            if (!string.IsNullOrEmpty(err)) _dialogs.Error(err, "打包失败");
            return;
        }

        var remoteArchive = RemotePath.Combine(targetDir, archiveName);
        var transferItem = new TransferItem
        {
            Direction = TransferDirection.Upload,
            LocalPath = archivePath,
            RemotePath = targetDir,
            DisplayName = $"打包上传（{items.Count} 项）",
            TargetTextOverride = remoteArchive,
            IsDirectory = false,
            Size = new FileInfo(archivePath).Length,
            PostAction = async ct =>
            {
                try
                {
                    // 远端解压 + 清理归档；失败抛异常让条目置失败（远端归档保留便于重试）
                    var r = await _ssh.RunBashAsync(RemotePane.Alias, RemoteOps.ExtractAndCleanup(targetDir, archiveName, isZip), ct);
                    if (!r.Ok) throw new RemoteOperationException(SshErrorTranslator.Describe(r, "远端解压"));
                }
                finally
                {
                    try { File.Delete(archivePath); } catch { } // 本地临时归档用完即删
                }
            },
        };
        TransferQueue.Enqueue([transferItem]);
    }
}
