namespace SshManager.Core.Models;

/// <summary>一张物理/无线网卡当前状态（用于工程配置界面）。</summary>
public sealed class NetInterface
{
    public string Name { get; init; } = "";
    public bool IsWifi { get; init; }
    public string State { get; init; } = "";          // UP / DOWN
    public string Mac { get; init; } = "";
    public List<string> Addresses { get; init; } = new(); // ["192.168.1.5/24", ...]
    public string Gateway { get; init; } = "";        // 默认网关（该网卡）
    public string Dns { get; init; } = "";            // 逗号分隔
    public bool NmManaged { get; init; }              // 由 NetworkManager 管理
    public string NmConnection { get; init; } = "";   // NM 连接名
    public string NmMethod { get; init; } = "";       // auto / manual
}

/// <summary>一次 WiFi 扫描结果。</summary>
public sealed class WifiNetwork
{
    public bool InUse { get; init; }
    public string Ssid { get; init; } = "";
    public string Signal { get; init; } = "";   // 0-100
    public string Security { get; init; } = ""; // "--" 表示开放网络
    public string Channel { get; init; } = "";
    public string Dev { get; init; } = "";
    public bool IsOpen => Security is "" or "--";
}

/// <summary>一条已保存的 WiFi 连接配置。</summary>
public sealed class WifiConnection
{
    public string Name { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string Device { get; init; } = "";  // 为空 = 未激活
}

/// <summary>UFW 防火墙状态与规则。</summary>
public sealed class UfwRule
{
    public int Num { get; init; }
    public string Action { get; init; } = "";  // ALLOW / DENY / REJECT / LIMIT
    public string To { get; init; } = "";      // "22/tcp"
    public string From { get; init; } = "";    // "Anywhere" / "192.168.1.0/24"
}

public sealed class UfwStatus
{
    public bool Installed { get; set; }
    public bool Active { get; set; }
    public string DefaultIncoming { get; set; } = "";
    public string DefaultOutgoing { get; set; } = "";
    public List<UfwRule> Rules { get; set; } = new();
}
