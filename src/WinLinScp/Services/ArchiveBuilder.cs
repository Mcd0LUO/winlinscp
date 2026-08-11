using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace WinLinScp.Services;

/// <summary>多选上传打包：把同一基准目录下的多个文件/文件夹打成单个 tar/zip 归档（纯 .NET，免外部 tar.exe）。</summary>
public static class ArchiveBuilder
{
    public sealed record ArchiveItem(string FullPath, bool IsDirectory);

    /// <summary>把 baseDir 下的条目打成一个归档（zip=true 走 zip，否则 tar），返回归档路径。</summary>
    public static string Build(string archivePath, bool zip, string baseDir, IReadOnlyList<ArchiveItem> items)
    {
        if (zip) BuildZip(archivePath, baseDir, items);
        else BuildTar(archivePath, baseDir, items);
        return archivePath;
    }

    private static void BuildTar(string archivePath, string baseDir, IReadOnlyList<ArchiveItem> items)
    {
        using var fs = File.Create(archivePath);
        using var writer = new TarWriter(fs, TarEntryFormat.Pax);
        foreach (var it in items)
            AddTarPath(writer, it.FullPath, Rel(baseDir, it.FullPath));
    }

    private static void AddTarPath(TarWriter writer, string full, string rel)
    {
        if (Directory.Exists(full))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, rel));
            foreach (var f in Directory.EnumerateFiles(full))
                writer.WriteEntry(f, rel + "/" + Path.GetFileName(f));
            foreach (var d in Directory.EnumerateDirectories(full))
                AddTarPath(writer, d, rel + "/" + Path.GetFileName(d));
        }
        else
        {
            writer.WriteEntry(full, rel);
        }
    }

    private static void BuildZip(string archivePath, string baseDir, IReadOnlyList<ArchiveItem> items)
    {
        using var fs = File.Create(archivePath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var it in items)
            AddZipPath(zip, it.FullPath, Rel(baseDir, it.FullPath));
    }

    private static void AddZipPath(ZipArchive zip, string full, string rel)
    {
        if (Directory.Exists(full))
        {
            if (rel.Length > 0 && !rel.EndsWith('/')) rel += "/";
            if (rel.Length > 0) zip.CreateEntry(rel); // 目录条目
            foreach (var f in Directory.EnumerateFiles(full))
                zip.CreateEntryFromFile(f, rel + Path.GetFileName(f), CompressionLevel.Optimal);
            foreach (var d in Directory.EnumerateDirectories(full))
                AddZipPath(zip, d, rel + Path.GetFileName(d));
        }
        else
        {
            zip.CreateEntryFromFile(full, rel, CompressionLevel.Optimal);
        }
    }

    private static string Rel(string baseDir, string full) =>
        Path.GetRelativePath(baseDir, full).Replace('\\', '/');
}
