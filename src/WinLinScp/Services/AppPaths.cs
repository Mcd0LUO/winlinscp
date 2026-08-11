using System.IO;

namespace WinLinScp.Services;

/// <summary>
/// 应用数据目录 = exe 所在目录（便携：配置+图标缓存随文件夹走）。
/// 不做可写性回退、不依赖 AppData。
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; }

    public static string ProfilesFile => Path.Combine(DataDir, "profiles.json");
    public static string IconsDir => Path.Combine(DataDir, "icons");

    static AppPaths()
    {
        DataDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
    }
}
