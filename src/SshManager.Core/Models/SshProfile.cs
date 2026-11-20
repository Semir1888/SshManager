using System.Text.Json.Serialization;

namespace SshManager.Core.Models;

public enum AuthMode
{
    Password,
    PrivateKey,
}

/// <summary>一个服务器连接配置。密码/口令只存在于内存，序列化时会被忽略。</summary>
public sealed class SshProfile
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public AuthMode AuthMode { get; set; } = AuthMode.Password;

    [JsonIgnore]
    public string? Password { get; set; }

    public string? KeyPath { get; set; }

    [JsonIgnore]
    public string? KeyPassphrase { get; set; }

    /// <summary>已确认过的服务器主机密钥指纹（md5 十六进制），保存后下次连接不再询问。</summary>
    public string? AcceptedFingerprint { get; set; }

    /// <summary>
    /// 文件管理遇到无权限目录/文件时是否自动用 sudo 提权访问（默认 true）。
    /// 提权读取仅在 SFTP 报“权限不足”时降级执行，正常路径不受影响。
    /// </summary>
    public bool AutoSudo { get; set; } = true;

    public DateTime? LastConnectAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name;

    [JsonIgnore]
    public string HostSummary => $"{Username}@{Host}:{Port}";

    public SshProfile Clone() => new()
    {
        Name = Name,
        Host = Host,
        Port = Port,
        Username = Username,
        AuthMode = AuthMode,
        KeyPath = KeyPath,
        AcceptedFingerprint = AcceptedFingerprint,
        AutoSudo = AutoSudo,
        LastConnectAt = LastConnectAt,
    };
}
