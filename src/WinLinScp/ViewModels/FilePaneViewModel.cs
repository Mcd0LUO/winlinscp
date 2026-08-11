using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinLinScp.ViewModels;

/// <summary>
/// 本地/远端面板的公共基类：条目集合、路径栏、后退/前进/上级、刷新、增删改查骨架。
/// 具体列举与操作由派生类实现。
/// </summary>
public abstract partial class FilePaneViewModel : ObservableObject
{
    protected readonly IDialogService Dialogs;

    public ObservableCollection<FilePaneItem> Entries { get; } = new();

    /// <summary>空目录提示（未加载且有内容时为 false）。</summary>
    public bool ShowEmptyHint => !IsLoading && Entries.Count == 0;

    protected FilePaneViewModel(IDialogService dialogs)
    {
        Dialogs = dialogs;
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyHint));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyHint));

    /// <summary>路径下拉（本地=盘符列表，远端=访问历史）。</summary>
    public ObservableCollection<string> PathHistory { get; } = new();

    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private int _generation;
    private CancellationTokenSource? _navCts;

    [ObservableProperty]
    private string _currentPath = "";

    /// <summary>面包屑段（点击跳转 / 拖放目标）。随 CurrentPath 重建，须可通知以便绑定刷新。</summary>
    [ObservableProperty]
    private IReadOnlyList<BreadcrumbSegment> _breadcrumbs = Array.Empty<BreadcrumbSegment>();

    partial void OnCurrentPathChanged(string value) => Breadcrumbs = BuildBreadcrumbs(value);

    /// <summary>把路径拆成可交互面包屑段（本地=盘符分段，远端=/ 分段）。空路径 → 根块。</summary>
    protected abstract IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbs(string path);

    [ObservableProperty]
    private FilePaneItem? _selectedItem;

    /// <summary>当前选中的条目集合（多选，由 View 的 SelectionChanged 同步）。</summary>
    public ObservableCollection<FilePaneItem> SelectedItems { get; } = new();

    /// <summary>删除目标：多选集合优先，否则单选。</summary>
    protected IReadOnlyList<FilePaneItem> GetDeletionTargets() =>
        SelectedItems.Count > 0
            ? SelectedItems.ToList()
            : (SelectedItem is { } s ? new[] { s } : Array.Empty<FilePaneItem>());

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    /// <summary>是否显示隐藏文件（由主界面开关同步）。</summary>
    public bool ShowHidden { get; set; }

    /// <summary>跨栏传输协调器（由 MainViewModel 注入）。</summary>
    public ITransferCoordinator? Coordinator { get; set; }

    /// <summary>底部任务队列（由 MainViewModel 注入）。文件操作经它展示为显式任务卡。</summary>
    public TransferQueueViewModel? Queue { get; set; }

    /// <summary>本面板是否支持上传/下载（用于右键菜单显示）。</summary>
    public virtual bool SupportsDownload => false;
    public virtual bool SupportsUpload => false;

    /// <summary>是否支持 shell 工具（以终端/任务管理器打开）——仅本地面板。</summary>
    public virtual bool SupportsShellTools => false;

    /// <summary>排序比较器（本地忽略大小写，远端区分）。</summary>
    protected virtual IComparer<FilePaneItem> SortComparer => DefaultComparer;

    private static readonly IComparer<FilePaneItem> DefaultComparer = Comparer<FilePaneItem>.Create((a, b) =>
    {
        int cmp = b.IsParent.CompareTo(a.IsParent);          // .. 最前
        if (cmp != 0) return cmp;
        cmp = b.IsDirectory.CompareTo(a.IsDirectory);        // 目录优先
        if (cmp != 0) return cmp;
        return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
    });

    // ---------------- 抽象行为 ----------------

    /// <summary>列举 path 下的条目（含合成的 .. 项）。path 为空表示“我的电脑”盘符根。</summary>
    public abstract Task<IReadOnlyList<FilePaneItem>> LoadAsync(string path, CancellationToken ct);

    /// <summary>父目录；null 表示已到根（本地根之上是“我的电脑”，返回空串）。</summary>
    public abstract string? GetParent(string path);

    /// <summary>打开文件（本地=默认程序；远端=查看器）。</summary>
    public abstract Task OpenFileAsync(FilePaneItem item);

    public abstract Task NewFolderAsync();

    public abstract Task NewFileAsync();

    public abstract Task RenameAsync();

    public abstract Task DeleteAsync();

    // ---------------- 导航 ----------------

    public Task NavigateAsync(string path) => NavigateCoreAsync(path, recordHistory: true);

    /// <summary>后退/前进不写历史栈、不更新状态文案。</summary>
    private Task NavigateToNoHistoryAsync(string path) => NavigateCoreAsync(path, recordHistory: false);

    private async Task NavigateCoreAsync(string path, bool recordHistory)
    {
        var old = CurrentPath;
        var oldCts = _navCts;
        oldCts?.Cancel();
        oldCts?.Dispose();
        var gen = ++_generation;
        var cts = _navCts = new CancellationTokenSource();

        IsLoading = true;
        StatusText = "";
        try
        {
            var items = await LoadAsync(path, cts.Token);
            if (gen != _generation) return; // 已有更新的导航，丢弃旧结果

            RenderEntries(items);

            if (recordHistory)
            {
                if (old != path && old.Length > 0) _back.Push(old);
                _forward.Clear();
                StatusText = $"共 {items.Count - 1} 项"; // 减去 .. 项
            }
            CurrentPath = path;
            if (recordHistory && path.Length > 0) TrackHistory(path);
            UpdateNavFlags();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (gen == _generation) Dialogs.Error(ex.Message, "导航失败");
        }
        finally
        {
            if (gen == _generation) IsLoading = false;
        }
    }

    public virtual Task RefreshAsync() => CurrentPath is { } p ? NavigateAsync(p) : Task.CompletedTask;

    /// <summary>把成功导航过的目录记入历史（本地=盘符 + 访问过的目录；远端=访问过的目录），MRU 去重、上限 30。</summary>
    private void TrackHistory(string path)
    {
        PathHistory.Remove(path);
        PathHistory.Insert(0, path);
        while (PathHistory.Count > 30) PathHistory.RemoveAt(PathHistory.Count - 1);
    }

    [RelayCommand]
    private async Task GoUp()
    {
        var parent = GetParent(CurrentPath);
        if (parent is null) return;
        await NavigateAsync(parent);
    }

    [RelayCommand]
    private async Task GoBack()
    {
        if (_back.Count == 0) return;
        var target = _back.Pop();
        _forward.Push(CurrentPath);
        await NavigateToNoHistoryAsync(target);
    }

    [RelayCommand]
    private async Task GoForward()
    {
        if (_forward.Count == 0) return;
        var target = _forward.Pop();
        _back.Push(CurrentPath);
        await NavigateToNoHistoryAsync(target);
    }

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync();

    [RelayCommand]
    private async Task Open()
    {
        if (SelectedItem is not { } item) return;
        if (item.IsParent) { await GoUpAsync(); return; }
        if (item.IsDirectory) await NavigateAsync(item.FullPath);
        else await OpenFileAsync(item);
    }

    private async Task GoUpAsync() => await GoUpCommand.ExecuteAsync(null);

    protected void UpdateNavFlags()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }

    /// <summary>作废所有进行中的导航/后台刷新（断开连接、切换连接时调用）。</summary>
    public void InvalidateNavigation()
    {
        var old = _navCts;
        _navCts = null;
        old?.Cancel();
        old?.Dispose();
        _generation++;
    }

    /// <summary>当前导航代数（后台刷新用，判断结果是否已过期）。</summary>
    protected int CurrentGeneration => _generation;

    /// <summary>按排序规则渲染条目集合（本地/远端共用）。</summary>
    protected void RenderEntries(IReadOnlyCollection<FilePaneItem> items)
    {
        Entries.Clear();
        foreach (var i in items.OrderBy(x => x, SortComparer)) Entries.Add(i);
    }

    // ---------------- 归档（tar/zip）骨架：默认空操作，远端面板实现 ----------------

    public virtual bool SupportsArchive => false;

    /// <summary>是否支持 tar.gz 压缩（远端支持；本地走系统默认 zip）。</summary>
    public virtual bool SupportsTarGz => false;

    public bool CanExtractSelected => SelectedItem is not null && IsArchive(SelectedItem);

    protected virtual bool IsArchive(FilePaneItem item) => false;

    partial void OnSelectedItemChanged(FilePaneItem? value) => OnPropertyChanged(nameof(CanExtractSelected));

    [RelayCommand]
    private Task CompressTarGz() => CompressCoreAsync(".tar.gz");

    [RelayCommand]
    private Task CompressZip() => CompressCoreAsync(".zip");

    [RelayCommand]
    private Task Extract() => ExtractCoreAsync();

    protected virtual Task CompressCoreAsync(string format) => Task.CompletedTask;

    protected virtual Task ExtractCoreAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task Rename() => RenameAsync();

    [RelayCommand]
    private Task Delete() => DeleteAsync();

    [RelayCommand]
    private Task NewFolder() => NewFolderAsync();

    [RelayCommand]
    private Task NewFile() => NewFileAsync();

    // 下载/上传：两栏都有这两个命令（键盘快捷键/Ctrl+D/U 通用），
    // 与面板无关的那一方是空操作，右键菜单用 Supports* 隐藏掉。
    [RelayCommand]
    private Task Download() => DownloadCoreAsync();

    [RelayCommand]
    private Task Upload() => UploadCoreAsync();

    protected virtual Task DownloadCoreAsync() => Task.CompletedTask;

    protected virtual Task UploadCoreAsync() => Task.CompletedTask;

    // ---------------- 拖拽（跨系统/系统内） ----------------

    /// <summary>生成拖拽载荷（源面板）。返回 null 表示不支持拖出。</summary>
    public virtual DragPayload? CreateDragPayload(IReadOnlyList<FilePaneItem> items) => null;

    /// <summary>是否接受该载荷（目标面板）。</summary>
    public virtual bool CanAcceptDrop(DragPayload payload) => false;

    /// <summary>处理拖入（目标面板）。targetDir 为落点目录（拖到目录行时是那个目录，否则当前目录）。
    /// copy=true 表示 Ctrl 复制语义。</summary>
    public virtual Task HandleDropAsync(DragPayload payload, string targetDir, bool copy) => Task.CompletedTask;

    // ---------------- 执行自定义脚本（本地=PowerShell，远端=bash） ----------------

    public virtual bool SupportsCustomScript => true;

    [RelayCommand]
    private Task RunCustomScript() => RunCustomScriptCoreAsync();

    protected virtual Task RunCustomScriptCoreAsync() => Task.CompletedTask;
}
