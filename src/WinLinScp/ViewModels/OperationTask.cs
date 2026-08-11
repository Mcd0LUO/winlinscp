using CommunityToolkit.Mvvm.ComponentModel;
using WinLinScp.Models;

namespace WinLinScp.ViewModels;

/// <summary>
/// 文件操作任务（压缩/解压/删除/重命名/移动/新建/打包/执行脚本等）在底部队列中的卡片。
/// 与 TransferItem 共用 ITransferTask 契约；无字节进度，进度条恒为不确定。
/// </summary>
public sealed partial class OperationTask : ObservableObject, ITransferTask
{
    public string DisplayName { get; init; } = "";
    public string TargetText { get; init; } = "";

    public CancellationTokenSource Cts { get; } = new();

    [ObservableProperty]
    private TransferState _state = TransferState.Pending;

    partial void OnStateChanged(TransferState value) => OnPropertyChanged(nameof(IsRunning));

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _error = "";

    public string SpeedText => "";
    public string EtaText => "";
    public double Progress => 0;
    public bool IsIndeterminate => true;
    public bool IsRunning => State == TransferState.Running;
}
