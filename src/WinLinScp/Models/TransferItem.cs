using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WinLinScp.Services;
using WinLinScp.ViewModels;

namespace WinLinScp.Models;

public enum TransferDirection { Upload, Download }

public enum TransferState { Pending, Running, Completed, Failed, Cancelled }

/// <summary>传输队列中的一个条目。一个条目 = 一个顶层传输（文件夹即一次 scp -r）。</summary>
public sealed partial class TransferItem : ObservableObject, ITransferTask
{
    public Guid Id { get; } = Guid.NewGuid();
    public TransferDirection Direction { get; init; }
    public string LocalPath { get; init; } = "";
    public string RemotePath { get; init; } = "";
    public long Size { get; init; }
    public string DisplayName { get; init; } = "";
    public bool IsDirectory { get; init; }

    /// <summary>每项独立的取消源。</summary>
    public CancellationTokenSource Cts { get; } = new();

    [ObservableProperty]
    private TransferState _state = TransferState.Pending;

    partial void OnStateChanged(TransferState value) => OnPropertyChanged(nameof(IsRunning));

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _error = "";

    /// <summary>实时/平均速度（如 "12.3 MB/s"）。</summary>
    [ObservableProperty]
    private string _speedText = "";

    /// <summary>乐观进度 0..1（完成前封顶 0.95），无总量时为 0。</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>总量未知（文件夹估算失败等）时为 true，进度条走不确定动画。</summary>
    [ObservableProperty]
    private bool _isIndeterminate = true;

    /// <summary>预计剩余时间文案（如 "剩余 2 分 5 秒"）。</summary>
    [ObservableProperty]
    private string _etaText = "";

    /// <summary>scp 完成后执行的后续动作（打包上传=远端解压+清理）。失败会把条目置为失败。</summary>
    public Func<CancellationToken, Task>? PostAction { get; init; }

    public bool IsRunning => State == TransferState.Running;

    public string DirectionText => Direction == TransferDirection.Upload ? "↑ 上传" : "↓ 下载";

    /// <summary>目标所在目录（上传=远端当前目录，下载=本地当前目录）。</summary>
    public string Destination => Direction == TransferDirection.Upload ? RemotePath : LocalPath;

    /// <summary>完整目标路径（含文件名），用于清晰显示"传到哪"。打包上传等可用显式覆盖。</summary>
    public string TargetText => TargetTextOverride ?? (Direction == TransferDirection.Upload
        ? RemotePath.TrimEnd('/') + "/" + DisplayName
        : Path.Combine(LocalPath, DisplayName));

    /// <summary>目标路径覆盖（如打包上传的远端归档完整路径）。</summary>
    public string? TargetTextOverride { get; init; }

    public string SizeText => Size > 0 ? SizeFormatter.Format(Size) : "";
}
