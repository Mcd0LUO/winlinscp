using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinLinScp.Services;

/// <summary>
/// 复用 Windows shell 的真实图标（文件夹/盘符/各文件类型）。
/// SHGetFileInfo + USEFILEATTRIBUTES：不访问磁盘、对远端路径也按扩展名取关联图标，按类型缓存。
/// GetIconAsync 在后台线程提取 HICON，BitmapSource 在调用方上下文（UI 线程）创建。
/// </summary>
public static class ShellIcon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi,
        uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>图标源分辨率：取 32px 大图标，显示时下采样到 16px，高 DPI 下更清晰。</summary>
    private const int IconSourceSize = 32;
    private const int IconDisplaySize = 16;

    private static readonly Dictionary<string, BitmapSource?> Cache = new();
    private static readonly object Gate = new();

    /// <summary>图标磁盘缓存目录（exe 所在目录\icons）：重启后免再走 shell 提取。</summary>
    private static string IconsDir => AppPaths.IconsDir;

    /// <summary>同步取图标（探针等非 UI 场景用）。</summary>
    public static BitmapSource? GetIcon(string fullPath, bool isDirectory, bool isDrive = false)
    {
        var key = MakeKey(fullPath, isDirectory, isDrive);
        lock (Gate) if (Cache.TryGetValue(key, out var hit)) return hit;
        var fromDisk = LoadFromDisk(key);
        if (fromDisk is not null) { lock (Gate) Cache[key] = fromDisk; return fromDisk; }
        return FinishIcon(key, ExtractIconHandle(fullPath, isDirectory, isDrive));
    }

    /// <summary>异步取图标：内存/磁盘缓存优先，否则后台提取 HICON（SHGetFileInfo 可在后台线程），BitmapSource 在调用方上下文创建。</summary>
    public static async Task<BitmapSource?> GetIconAsync(string fullPath, bool isDirectory, bool isDrive = false)
    {
        var key = MakeKey(fullPath, isDirectory, isDrive);
        lock (Gate) if (Cache.TryGetValue(key, out var hit)) return hit; // 内存缓存命中：无开销
        var fromDisk = LoadFromDisk(key);
        if (fromDisk is not null) { lock (Gate) Cache[key] = fromDisk; return fromDisk; }
        var hic = await Task.Run(() => ExtractIconHandle(fullPath, isDirectory, isDrive));
        return FinishIcon(key, hic);
    }

    private static string MakeKey(string fullPath, bool isDirectory, bool isDrive) =>
        isDrive ? "drive"
        : isDirectory ? "dir"
        : "ext:" + (Path.GetExtension(fullPath) ?? "").ToLowerInvariant();

    /// <summary>键 → 合法文件名（Windows 不允许 ':' 等字符）。</summary>
    private static string KeyFile(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (var c in key) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static BitmapSource? LoadFromDisk(string key)
    {
        try
        {
            var path = Path.Combine(IconsDir, KeyFile(key) + "_" + IconSourceSize + ".png");
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private static void SaveToDisk(string key, BitmapSource img)
    {
        try
        {
            Directory.CreateDirectory(IconsDir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(Path.Combine(IconsDir, KeyFile(key) + "_" + IconSourceSize + ".png"));
            encoder.Save(fs);
        }
        catch { /* 缓存写入失败不影响使用 */ }
    }

    private static IntPtr ExtractIconHandle(string fullPath, bool isDirectory, bool isDrive)
    {
        var info = new SHFILEINFO();
        uint attrs = (isDirectory || isDrive) ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var probe = (isDirectory || isDrive) ? fullPath.TrimEnd('\\') + "\\" : fullPath;
        // 不加 SMALLICON = 取 32px 大图标（清晰度更高）
        uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES;
        return SHGetFileInfo(probe, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero
               || info.hIcon == IntPtr.Zero
            ? IntPtr.Zero
            : info.hIcon;
    }

    private static BitmapSource? FinishIcon(string key, IntPtr hic)
    {
        if (hic == IntPtr.Zero) return null;
        BitmapSource? img;
        try
        {
            img = Imaging.CreateBitmapSourceFromHIcon(hic, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(IconSourceSize, IconSourceSize));
            img.Freeze(); // 冻结以跨线程共享
        }
        finally
        {
            DestroyIcon(hic);
        }
        lock (Gate) Cache[key] = img;
        _ = Task.Run(() => SaveToDisk(key, img)); // 异步写磁盘缓存，不阻塞
        return img;
    }
}
