namespace SshManager.Core.Models;

/// <summary>一个 IPv4 地址及其前缀长度。</summary>
public readonly record struct Ipv4Address(string Address, int Prefix)
{
    public string Cidr => $"{Address}/{Prefix}";
    public override string ToString() => Cidr;
}

public enum IpMode
{
    Dhcp,
    Static,
}

/// <summary>持久化 IPv4 配置的来源。</summary>
public enum IpSourceKind
{
    None,
    Netplan,
    NetworkManager,
    RuntimeOnly,
    Unknown,
}

/// <summary>从 netplan / NetworkManager 读到的持久化配置（不是运行时租约）。</summary>
public sealed class PersistentIpConfig
{
    public IpSourceKind Source { get; init; } = IpSourceKind.None;
    public IpMode Mode { get; init; } = IpMode.Dhcp;
    public List<Ipv4Address> Addresses { get; init; } = new();
    public string Gateway { get; init; } = "";
    public List<string> Dns { get; init; } = new();
    public string Renderer { get; init; } = "";          // NetworkManager / networkd
    public string PrimaryFile { get; init; } = "";
    public List<string> SourceFiles { get; init; } = new();
    public List<string> ConflictFiles { get; init; } = new();
    public bool ConflictNeedsTakeover { get; init; }
    public bool ConflictComplex { get; init; }           // 存在无法安全接管的结构
    public string NmConnectionName { get; init; } = "";
    public string NmUuid { get; init; } = "";
    public string NmFile { get; init; } = "";
    public bool NmEphemeral { get; init; }               // /run 或只读/未保存连接
    public bool IsAuthoritative { get; init; }
    public string ReadError { get; init; } = "";

    public bool IsDhcp => Mode == IpMode.Dhcp;
}

/// <summary>一次网络环境探测结果。</summary>
public sealed class NetworkEnvironment
{
    public bool NetplanInstalled { get; init; }
    public bool NetplanGetSupported { get; init; }
    public string NetplanVersion { get; init; } = "";
    public bool NmInstalled { get; init; }
    public bool NmRunning { get; init; }
    public bool CloudInitPresent { get; init; }
    public bool IpJsonSupported { get; init; }
    public List<string> NetplanFiles { get; init; } = new();
    /// <summary>$SSH_CONNECTION 第 3 段（服务端 IP），用于判断改 IP 是否会断线。</summary>
    public string SshServerIp { get; init; } = "";
    public string ReadError { get; init; } = "";
}

/// <summary>一次 IP 下发的完整计划（确认框展示 + 实际执行）。</summary>
public sealed class IpApplyPlan
{
    public string Backend { get; init; } = "";           // netplan / nm
    public string Target { get; init; } = "";            // netplan 文件或 NM UUID
    public string Renderer { get; init; } = "";
    public string Preview { get; init; } = "";           // 不含 sudo 密码
    public string BackupRoot { get; init; } = "";        // /var/lib/sshmanager-net
    public bool NeedTakeover { get; init; }
    public List<string> TakeoverFiles { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public bool SshIpRemoved { get; init; }
    public bool SshIpRemovedUnknown { get; init; }
    public string SshServerIp { get; init; } = "";
    /// <summary>网卡名（已剥离 @peer）。</summary>
    public string Iface { get; init; } = "";
    public IpConfigSnapshot? TargetConfig { get; init; }
}

/// <summary>计划中要写入的目标配置（与 IpConfig 解耦，便于纯逻辑测试）。</summary>
public sealed class IpConfigSnapshot
{
    public IpMode Mode { get; init; }
    public List<Ipv4Address> Addresses { get; init; } = new();
    public string Gateway { get; init; } = "";
    public List<string> Dns { get; init; } = new();
}

/// <summary>应用结果。</summary>
public sealed class IpApplyResult
{
    public bool Success { get; init; }
    public bool Committed { get; init; }
    public bool RolledBack { get; init; }
    public bool Pending { get; init; }
    public string Message { get; init; } = "";
    public string Error { get; init; } = "";
    public string Token { get; init; } = "";
    public string Target { get; init; } = "";
    public bool SessionLost { get; init; }
}

/// <summary>服务器上等待确认（120 秒看门狗）的变更。</summary>
public sealed class PendingIpChange
{
    public string Token { get; init; } = "";
    public string Iface { get; init; } = "";
    public string Backend { get; init; } = "";
    public string Target { get; init; } = "";
    public long DeadlineEpoch { get; init; }
    public long ServerNowEpoch { get; init; }
    public int RemainingSeconds { get; init; }
    public bool Committed { get; init; }
    public bool RolledBack { get; init; }
    public string Summary { get; init; } = "";
    public string Result { get; init; } = "";
}
