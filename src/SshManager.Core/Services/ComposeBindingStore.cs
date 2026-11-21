using System.Text.Json;
using System.Text.Json.Serialization;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>compose 项目绑定的本地持久化（%APPDATA%/SshManager/compose.json，按服务器隔离）。</summary>
public sealed class ComposeBindingStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ComposeBindingStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SshManager");
        _path = Path.Combine(dir, "compose.json");
    }

    /// <summary>服务器标识：用户名 + 主机 + 端口（同一主机不同用户相互隔离）。</summary>
    public static string ServerKey(SshProfile profile)
        => $"{profile.Username}@{profile.Host.ToLowerInvariant()}:{profile.Port}";

    public ComposeStoreData Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<ComposeStoreData>(File.ReadAllText(_path), JsonOpts);
                if (data != null) return data;
            }
        }
        catch { /* 损坏则视为空 */ }
        return new ComposeStoreData();
    }

    public void Save(ComposeStoreData data)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    public IReadOnlyList<ComposeProjectConfig> LoadForServer(string serverKey)
    {
        var data = Load();
        return data.Servers.TryGetValue(serverKey, out var s)
            ? s.Projects.ToList()
            : Array.Empty<ComposeProjectConfig>();
    }

    public void Upsert(string serverKey, ComposeProjectConfig config)
    {
        var data = Load();
        if (!data.Servers.TryGetValue(serverKey, out var server))
        {
            server = new ComposeServerProjects();
            data.Servers[serverKey] = server;
        }
        var old = server.Projects.FirstOrDefault(p =>
            p.ProjectName.Equals(config.ProjectName, StringComparison.Ordinal));
        if (old != null) server.Projects.Remove(old);
        config.UpdatedAtUtc = DateTime.UtcNow;
        server.Projects.Add(config);
        Save(data);
    }

    public void Remove(string serverKey, string projectName)
    {
        var data = Load();
        if (!data.Servers.TryGetValue(serverKey, out var server)) return;
        server.Projects.RemoveAll(p => p.ProjectName.Equals(projectName, StringComparison.Ordinal));
        Save(data);
    }
}

public sealed class ComposeStoreData
{
    public int Version { get; set; } = 1;
    public Dictionary<string, ComposeServerProjects> Servers { get; set; } = new();
}

public sealed class ComposeServerProjects
{
    public List<ComposeProjectConfig> Projects { get; set; } = new();
}
