using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinLinScp.Models;
using WinLinScp.Services;

namespace WinLinScp.ViewModels;

/// <summary>
/// 传输队列：无界 Channel + 单消费者顺序执行。一个条目 = 一个顶层传输（文件夹 = 一次 scp -r）。
/// 卡片既含字节传输（TransferItem，实时进度/ETA），也含文件操作任务（OperationTask，不确定进度）。
/// </summary>
public sealed partial class TransferQueueViewModel : ObservableObject
{
    private readonly SshService _ssh;
    private readonly ScpService _scp;
    private readonly Func<string> _aliasProvider;
    private readonly Channel<TransferItem> _channel = Channel.CreateUnbounded<TransferItem>();
    private bool _pumpStarted;
    private bool _wasActive;

    /// <summary>一批传输/操作全部结束时触发（用于刷新双栏）。</summary>
    public event Action? AllCompleted;

    public TransferQueueViewModel(SshService ssh, ScpService scp, Func<string> aliasProvider)
    {
        _ssh = ssh;
        _scp = scp;
        _aliasProvider = aliasProvider;
    }

    /// <summary>全部任务卡片（传输 + 文件操作）。</summary>
    public ObservableCollection<ITransferTask> Items { get; } = new();

    [ObservableProperty]
    private int _doneCount;

    partial void OnDoneCountChanged(int value) => OnPropertyChanged(nameof(TotalText));

    [ObservableProperty]
    private int _totalCount;

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(TotalText));

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _activityText = "";

    /// <summary>底部任务栏是否展开（传输开始时自动展开，可手动收起）。</summary>
    [ObservableProperty]
    private bool _panelVisible = true;

    /// <summary>任务卡片区高度（像素），可拖拽调整。收起时随 PanelVisible 隐藏。</summary>
    [ObservableProperty]
    private double _taskPanelHeight = 84;

    public string TotalText => $"{DoneCount}/{TotalCount}";

    public void Enqueue(IEnumerable<TransferItem> items)
    {
        PanelVisible = true; // 有传输时自动展开底部栏
        foreach (var item in items)
        {
            Items.Add(item);
            _channel.Writer.TryWrite(item);
        }
        TotalCount = Items.Count;
        if (!_pumpStarted)
        {
            _pumpStarted = true;
            _ = PumpAsync();
        }
    }

    // ---------------- 文件操作任务（压缩/删除/打包/脚本等） ----------------

    /// <summary>把一段工作包成显式任务卡在队列中展示（本地操作、打包等）。返回是否成功及错误信息。</summary>
    public async Task<(bool Ok, string? Error)> RunOperationAsync(
        string displayName, string targetText, Func<CancellationToken, Task> work)
    {
        var (ok, err, _) = await RunOperationTaskAsync(
            new OperationTask { DisplayName = displayName, TargetText = targetText },
            async ct => { await work(ct); return (true, (string?)null, (object?)null); });
        return (ok, err);
    }

    /// <summary>把一条 ssh 命令包成任务卡；返回 SshResult（命令完成即返回，含失败），null=取消或异常。</summary>
    public async Task<SshResult?> RunSshOperationAsync(
        string displayName, string targetText, Func<CancellationToken, Task<SshResult>> work)
    {
        var (ok, _, value) = await RunOperationTaskAsync(
            new OperationTask { DisplayName = displayName, TargetText = targetText },
            async ct =>
            {
                var r = await work(ct);
                if (ct.IsCancellationRequested) return (false, "已取消", (object?)null);
                return (r.Ok, SshErrorTranslator.Describe(r, displayName), (object?)r);
            });
        return value as SshResult;
    }

    private async Task<(bool Ok, string? Error, object? Value)> RunOperationTaskAsync(
        OperationTask task, Func<CancellationToken, Task<(bool Ok, string? Error, object? Value)>> body)
    {
        PanelVisible = true;
        Items.Add(task);
        TotalCount = Items.Count;
        task.State = TransferState.Running;
        task.StatusText = "执行中…";
        ActivityText = $"{task.DisplayName}「{task.TargetText}」";
        try
        {
            var (ok, err, val) = await body(task.Cts.Token);
            task.State = ok ? TransferState.Completed : TransferState.Failed;
            task.StatusText = ok ? "完成" : "失败";
            if (!ok && !string.IsNullOrEmpty(err)) task.Error = err;
            return (ok, err, val);
        }
        catch (OperationCanceledException)
        {
            task.State = TransferState.Cancelled;
            task.StatusText = "已取消";
            return (false, "已取消", null);
        }
        catch (Exception ex)
        {
            task.State = TransferState.Failed;
            task.StatusText = "失败";
            task.Error = ex.Message;
            return (false, ex.Message, null);
        }
        finally
        {
            UpdateCounts();
        }
    }

    // ---------------- 取消 / 清理 ----------------

    [RelayCommand]
    private void CancelItem(ITransferTask item)
    {
        if (item.State is TransferState.Running or TransferState.Pending)
        {
            item.Cts.Cancel();
            item.StatusText = "正在取消…";
        }
    }

    [RelayCommand]
    private void CancelAll()
    {
        foreach (var item in Items.Where(i => i.State is TransferState.Running or TransferState.Pending))
            item.Cts.Cancel();
    }

    /// <summary>传输/操作结束后清理已完成/取消/失败的条目。</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        var done = Items.Where(i => i.State is not (TransferState.Pending or TransferState.Running)).ToList();
        foreach (var item in done)
        {
            Items.Remove(item);
            item.Cts.Dispose();
        }
        UpdateCounts();
    }

    // ---------------- 传输泵 ----------------

    private async Task PumpAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            await ProcessAsync(item);
        }
    }

    private async Task ProcessAsync(TransferItem item)
    {
        var ct = item.Cts.Token;
        // 已取消的项直接跳过，不进入传输/估算
        if (ct.IsCancellationRequested)
        {
            item.State = TransferState.Cancelled;
            item.StatusText = "已取消";
            UpdateCounts();
            return;
        }

        item.State = TransferState.Running;
        item.StatusText = $"正在{(item.Direction == TransferDirection.Upload ? "上传" : "下载")}「{item.DisplayName}」";
        ActivityText = $"{item.DirectionText}「{item.DisplayName}」 → {item.TargetText}";

        // 总量并发预估 + 实时进度轮询（文件/文件夹均轮询目标已写字节）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var estimateTask = EstimateTotalBytesAsync(item, ct);
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poller = Task.Run(() => PollProgressAsync(item, sw, estimateTask, pollCts.Token), CancellationToken.None);

        try
        {
            SshResult r;
            if (item.Direction == TransferDirection.Upload)
                r = await _scp.UploadAsync(_aliasProvider(), item.LocalPath, item.RemotePath, ct);
            else
                r = await _scp.DownloadAsync(_aliasProvider(), item.RemotePath, item.LocalPath, item.IsDirectory, ct);

            if (ct.IsCancellationRequested)
            {
                item.State = TransferState.Cancelled;
                item.StatusText = "已取消";
            }
            else if (!r.Ok)
            {
                item.State = TransferState.Failed;
                item.Error = SshErrorTranslator.Describe(r, item.DirectionText);
                item.StatusText = "失败";
            }
            else if (item.PostAction is { } post)
            {
                // scp 成功后的后续动作（如打包上传的远端解压）；失败会把条目置为失败
                try
                {
                    await post(ct);
                    if (ct.IsCancellationRequested)
                    {
                        item.State = TransferState.Cancelled;
                        item.StatusText = "已取消";
                    }
                    else
                    {
                        item.State = TransferState.Completed;
                        item.StatusText = "完成";
                    }
                }
                catch (OperationCanceledException)
                {
                    item.State = TransferState.Cancelled;
                    item.StatusText = "已取消";
                }
                catch (Exception ex)
                {
                    item.State = TransferState.Failed;
                    item.Error = ex.Message;
                    item.StatusText = "失败";
                }
            }
            else
            {
                item.State = TransferState.Completed;
                item.StatusText = "完成";
            }
        }
        catch (OperationCanceledException)
        {
            item.State = TransferState.Cancelled;
            item.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            item.State = TransferState.Failed;
            item.Error = ex.Message;
            item.StatusText = "失败";
        }
        finally
        {
            pollCts.Cancel();
            try { await poller; } catch { }
            sw.Stop();

            // 完成：进度拉满；显示平均速度
            if (item.State == TransferState.Completed)
            {
                item.IsIndeterminate = false;
                item.Progress = 1;
                item.EtaText = "";
                long totalBytes = 0;
                try { totalBytes = await estimateTask; } catch { /* 取消/失败则跳过 */ }
                if (totalBytes > 0)
                {
                    var avg = totalBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                    item.SpeedText = $"{FormatSpeed(avg)} · 平均";
                }
            }
        }

        UpdateCounts();
    }

    /// <summary>
    /// 实时轮询目标已写字节：算速度 + 乐观进度（总量已知时封顶 0.95）+ 预计剩余时间。
    /// 总量未知则保持不确定条。
    /// </summary>
    private async Task PollProgressAsync(TransferItem item, System.Diagnostics.Stopwatch sw, Task<long> totalTask, CancellationToken ct)
    {
        long total = 0;
        try
        {
            var t = await totalTask;
            if (t > 0) { total = t; item.IsIndeterminate = false; }
        }
        catch { /* 估算失败 → 保持不确定条 */ }

        long prevBytes = 0;
        long prevMs = 0;
        double speed = 0;
        var interval = item.IsDirectory ? 2000 : 1200;
        while (true)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // 用 None 而非 ct：不取消进行中的 stat/du，避免中断共享会话被误杀
                var done = await GetTransferredBytesAsync(item, CancellationToken.None);
                var nowMs = sw.ElapsedMilliseconds;
                if (nowMs - prevMs >= interval)
                {
                    speed = (done - prevBytes) * 1000.0 / (nowMs - prevMs);
                    if (speed >= 0) item.SpeedText = FormatSpeed(speed);
                    prevBytes = done;
                    prevMs = nowMs;
                }
                if (total > 0)
                {
                    item.Progress = Math.Clamp((double)done / total, 0, 0.95); // 乐观：完成前不显 100%
                    item.EtaText = done >= total || speed <= 0
                        ? (done >= total ? "即将完成" : "")
                        : SizeFormatter.FormatEta(TimeSpan.FromSeconds((total - done) / speed));
                }
            }
            catch { /* 目标文件尚未出现等 */ }
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>已传输字节数：上传=远端目标大小（文件 stat / 目录 du），下载=本地目标大小。-1 表示不可实时测。</summary>
    private async Task<long> GetTransferredBytesAsync(TransferItem item, CancellationToken ct)
    {
        if (item.Direction == TransferDirection.Upload)
        {
            var remoteTarget = RemotePath.Combine(item.RemotePath, item.DisplayName);
            if (item.IsDirectory)
            {
                var r = await _ssh.RunSimpleAsync(_aliasProvider(), $"du -sb {ShellQuote.Quote(remoteTarget)}", ct);
                return r.Ok && long.TryParse(r.StdOut.Split(' ', 2)[0], out var size) ? size : 0;
            }
            var fr = await _ssh.RunSimpleAsync(_aliasProvider(), $"stat -c %s {ShellQuote.Quote(remoteTarget)}", ct);
            return fr.Ok && long.TryParse(fr.StdOut.Trim(), out var fsize) ? fsize : 0;
        }
        else
        {
            var localTarget = Path.Combine(item.LocalPath, item.DisplayName);
            if (item.IsDirectory)
                return Directory.Exists(localTarget) ? SumLocalDir(localTarget) : 0;
            return File.Exists(localTarget) ? new FileInfo(localTarget).Length : 0;
        }
    }

    /// <summary>预估本次传输总字节（平均速度/进度分母）。</summary>
    private async Task<long> EstimateTotalBytesAsync(TransferItem item, CancellationToken ct)
    {
        try
        {
            if (item.Direction == TransferDirection.Upload)
            {
                if (File.Exists(item.LocalPath)) return new FileInfo(item.LocalPath).Length;
                if (Directory.Exists(item.LocalPath))
                    return await Task.Run(() => SumLocalDir(item.LocalPath), ct);
            }
            else
            {
                if (item.Size > 0) return item.Size; // 远端文件大小（列目录已得）
                if (item.IsDirectory)
                {
                    var r = await _ssh.RunSimpleAsync(_aliasProvider(),
                        $"du -sb {ShellQuote.Quote(item.RemotePath)}", ct);
                    if (r.Ok && long.TryParse(r.StdOut.Split(' ', 2)[0], out var size)) return size;
                }
            }
        }
        catch { /* 忽略，返回 0 则只显示实时速度 */ }
        return 0;
    }

    private static long SumLocalDir(string dir)
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { }
        }
        return total;
    }

    private static string FormatSpeed(double bytesPerSec) => SizeFormatter.FormatSpeed(bytesPerSec);

    private void UpdateCounts()
    {
        TotalCount = Items.Count;
        DoneCount = Items.Count(i => i.State is not (TransferState.Pending or TransferState.Running));
        var active = Items.Any(i => i.State is TransferState.Pending or TransferState.Running);
        if (_wasActive && !active)
        {
            ActivityText = "传输完成";
            AllCompleted?.Invoke();
        }
        _wasActive = active;
        IsActive = active;
    }
}
