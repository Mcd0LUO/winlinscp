using System.IO;

namespace WinLinScp.Models;

public enum RemoteFileKind
{
    Directory,
    RegularFile,
    Symlink,
    Other,
}

/// <summary>远端文件系统中的一个条目（由 RemoteFileListing 解析产生）。</summary>
public sealed record RemoteFileInfo(
    string Name,
    RemoteFileKind Kind,
    long Size,
    DateTime MtimeUtc,
    bool LinkTargetIsDirectory,
    bool IsParent)
{
    public bool IsDirectory => Kind == RemoteFileKind.Directory;

    /// <summary>目录或指向目录的符号链接都可通过双击进入。</summary>
    public bool IsNavigable => Kind == RemoteFileKind.Directory
                               || (Kind == RemoteFileKind.Symlink && LinkTargetIsDirectory);

    public string? Extension => Kind == RemoteFileKind.RegularFile ? Path.GetExtension(Name) : null;

    public bool IsHidden => !IsParent && Name.StartsWith('.'); // ".." 返回上一级不随隐藏过滤
}
