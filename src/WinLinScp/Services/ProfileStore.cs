using System.IO;
using System.Text.Json;
using WinLinScp.Models;

namespace WinLinScp.Services;

/// <summary>exe 所在目录\profiles.json 的读写。原子写；损坏时备份 .bad 后重建。</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _saveLock = new();

    private readonly string _dir;

    /// <param name="customDir">自定义配置目录（测试隔离用）；默认 exe 所在目录（便携）。</param>
    public ProfileStore(string? customDir = null)
    {
        _dir = customDir ?? AppPaths.DataDir;
    }

    private string DirectoryPath => _dir;

    private string FilePath => Path.Combine(_dir, "profiles.json");

    public List<ConnectionProfile> Profiles { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var doc = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath), JsonOptions);
            if (doc is not null)
            {
                Profiles = doc.Profiles ?? new List<ConnectionProfile>();
                Settings = doc.Settings ?? new AppSettings();
            }
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { /* 忽略 */ }
            Profiles = new List<ConnectionProfile>();
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        lock (_saveLock) // 串行化，避免并发 Save 的 File.Exists 竞态
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(new ProfileDocument { Profiles = Profiles, Settings = Settings }, JsonOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            try
            {
                if (File.Exists(FilePath))
                    File.Replace(tmp, FilePath, null); // 原子替换
                else
                    File.Move(tmp, FilePath);           // 首次保存：目标不存在
            }
            catch
            {
                // Replace 竞态/占用等：回退为删旧 + 移动
                try { File.Delete(FilePath); } catch { }
                File.Move(tmp, FilePath);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } // 清理 .tmp 残留
            }
        }
    }

    public ConnectionProfile? Find(string name) => Profiles.FirstOrDefault(p => p.Name == name);

    public void Upsert(ConnectionProfile profile)
    {
        var existing = Find(profile.Name);
        if (existing is null) Profiles.Add(profile);
        else
        {
            existing.ScriptPath = profile.ScriptPath;
            existing.HostAlias = profile.HostAlias;
            existing.WorkDir = profile.WorkDir;
        }
    }

    public void Delete(string name)
    {
        var existing = Find(name);
        if (existing is not null) Profiles.Remove(existing);
    }

    private sealed class ProfileDocument
    {
        public List<ConnectionProfile>? Profiles { get; set; }
        public AppSettings? Settings { get; set; }
    }
}
