namespace WinLinScp.ViewModels;

/// <summary>跨栏传输协调（本地面板上传、远端面板下载都经它入队）。由 MainViewModel 实现。</summary>
public interface ITransferCoordinator
{
    /// <summary>下载远端条目到 targetDir（null=本地面板当前目录）。</summary>
    Task DownloadAsync(IReadOnlyList<FilePaneItem> remoteItems, string? targetDir = null);

    /// <summary>上传本地条目到 targetDir（null=远端面板当前目录）。</summary>
    Task UploadAsync(IReadOnlyList<FilePaneItem> localItems, string? targetDir = null);
}
