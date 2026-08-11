namespace WinLinScp.ViewModels;

/// <summary>多选上传的打包方式。</summary>
public enum PackMode { None, Tar, Zip }

/// <summary>上传确认弹窗的展示信息（项数 / 总字节 / 目标目录）。</summary>
public sealed class UploadPreview
{
    public int Count { get; init; }
    public long TotalBytes { get; init; }
    public string Destination { get; init; } = "";
}

/// <summary>上传确认弹窗的结果；null = 取消。</summary>
public sealed class UploadPlan
{
    public PackMode Mode { get; init; } = PackMode.Tar;
}
