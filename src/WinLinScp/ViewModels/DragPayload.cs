namespace WinLinScp.ViewModels;

/// <summary>拖拽格式名。跨系统/系统内拖拽统一抽象，View 层负责与 WPF IDataObject 互转。</summary>
public static class DragFormats
{
    /// <summary>本地文件路径列表（与 WPF DataFormats.FileDrop 对应）。</summary>
    public const string LocalFileDrop = "WinLinScp.LocalFileDrop";

    /// <summary>远端条目列表（FilePaneItem[]，同一进程内直接传对象）。</summary>
    public const string RemoteItem = "WinLinScp.RemoteItem";
}

/// <summary>拖拽载荷：源面板生成，目标面板解释。</summary>
public sealed class DragPayload
{
    public string Format { get; init; } = "";
    public IReadOnlyList<FilePaneItem> Items { get; init; } = Array.Empty<FilePaneItem>();
}
