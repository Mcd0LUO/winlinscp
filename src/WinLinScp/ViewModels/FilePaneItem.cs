using WinLinScp.Services;

namespace WinLinScp.ViewModels;

/// <summary>双栏共用的显示行（本地条目与远端条目统一映射成这个）。</summary>
public sealed class FilePaneItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsParent { get; init; }
    public bool IsHidden { get; init; }
    public bool IsDrive { get; init; }
    public long Size { get; init; }
    public bool IsSymlink { get; init; }
    public string? Extension { get; init; }
    public DateTime Mtime { get; init; }

    public string KindText => IsParent ? "上级目录"
        : IsDirectory ? "文件夹"
        : IsSymlink ? "符号链接"
        : Extension == null ? "文件" : Extension.TrimStart('.') + " 文件";

    public string SizeText => IsDirectory || IsParent ? "" : SizeFormatter.Format(Size);

    public string ModifiedText => Mtime == default ? "" : Mtime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    // ---------------- 图标（Segoe MDL2 Assets 字形 + 配色） ----------------

    public string IconGlyph => IsParent ? ""          // 向上箭头
        : IsDirectory ? ""                            // 文件夹
        : IsSymlink ? ""                              // 链接
        : FileKindIcon;

    public string IconBrush => IsParent ? "#0078D4"
        : IsDirectory ? "#F1A50B"                           // 文件夹金黄
        : IsSymlink ? "#8A8A8A"
        : FileKindBrush;

    private string FileKindIcon => Ext switch
    {
        var e when ImageExts.Contains(e) => "",       // 图片
        var e when ArchiveExts.Contains(e) => "",     // 压缩包
        var e when CodeExts.Contains(e) => "",        // 代码
        var e when TextExts.Contains(e) => "",        // 文本文档
        var e when AudioExts.Contains(e) => "",       // 音频
        var e when VideoExts.Contains(e) => "",       // 视频
        _ => "",                                      // 通用文件
    };

    private string FileKindBrush => Ext switch
    {
        var e when ImageExts.Contains(e) => "#00A87C",
        var e when ArchiveExts.Contains(e) => "#9B59B6",
        var e when CodeExts.Contains(e) => "#00897B",
        var e when TextExts.Contains(e) => "#4A7DBF",
        var e when AudioExts.Contains(e) => "#E8873C",
        var e when VideoExts.Contains(e) => "#C94F4F",
        _ => "#808080",
    };

    private string Ext => (Extension ?? "").ToLowerInvariant();

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp", ".ico", ".tiff" };

    private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
    { ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar" };

    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".py", ".js", ".ts", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".sh", ".ps1",
      ".json", ".yaml", ".yml", ".xml", ".html", ".css", ".sql", ".csproj", ".slnx", ".xaml" };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".log", ".doc", ".docx", ".pdf", ".rtf", ".ini", ".conf", ".cfg" };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm" };
}
