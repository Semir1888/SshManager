using System.Text.Json;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>服务器配置的本地持久化（%APPDATA%/SshManager/profiles.json，密码不落盘）。</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    public ProfileStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SshManager");
        _path = Path.Combine(dir, "profiles.json");
    }

    public List<SshProfile> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<SshProfile>>(File.ReadAllText(_path));
                if (list != null) return list;
            }
        }
        catch { /* 损坏则视为空 */ }
        return new List<SshProfile>();
    }

    public void Save(List<SshProfile> profiles)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(profiles, JsonOpts));
    }
}
