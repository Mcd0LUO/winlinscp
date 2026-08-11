using WinLinScp.Models;

namespace WinLinScp.ViewModels;

/// <summary>
/// 底部传输/任务栏中一个卡片的统一契约：既包括字节传输（TransferItem），
/// 也包括文件操作任务（OperationTask，如压缩/删除/打包）。底部 ListBox 与
/// 取消/计数逻辑只依赖该接口。
/// </summary>
public interface ITransferTask
{
    TransferState State { get; }
    bool IsRunning { get; }
    string StatusText { get; set; }
    string Error { get; }
    string DisplayName { get; }
    string SpeedText { get; }
    string TargetText { get; }
    string EtaText { get; }
    double Progress { get; }
    bool IsIndeterminate { get; }
    CancellationTokenSource Cts { get; }
}
