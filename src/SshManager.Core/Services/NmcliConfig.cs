using System.Text;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>nmcli 持久化配置解析与命令渲染（纯逻辑）。</summary>
public static class NmcliConfig
{
    public static List<Ipv4Address> ParseAddresses(string? text)
    {
        var list = new List<Ipv4Address>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split(new[] { ',', ' ', '\t', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var s = raw;
            var slash = s.IndexOf('/');
            var ipText = slash > 0 ? s[..slash] : s;
            var prefixText = slash > 0 ? s[(slash + 1)..] : "32";
            if (!IpValidation.TryParseIPv4Strict(ipText, out var ip)) continue;
            if (!IpValidation.TryParsePrefixOrMaskStrict(prefixText, out var prefix)) continue;
            var addr = new Ipv4Address(ip, prefix);
            if (!list.Contains(addr)) list.Add(addr);
        }
        return list;
    }

    public static List<string> ParseDns(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split(new[] { ',', ' ', '\t', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IpValidation.TryParseIPv4Strict(raw, out var d) && !list.Contains(d))
                list.Add(d);
        }
        return list;
    }

    /// <summary>NM 连接文件在 /run 或为空时，通常由 netplan/临时状态生成，重启不可靠。</summary>
    public static bool IsEphemeralFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return true;
        return file.StartsWith("/run/", StringComparison.Ordinal);
    }

    public static PersistentIpConfig FromNmcli(
        string connectionName,
        string uuid,
        string file,
        string method,
        string addresses,
        string gateway,
        string dns)
    {
        var mode = method.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? IpMode.Static
            : IpMode.Dhcp;
        var addrList = ParseAddresses(addresses);
        var dnsList = ParseDns(dns);
        if (mode == IpMode.Dhcp)
        {
            // DHCP 连接也可能保存了旧静态字段；不要把它们当作持久化地址
            addrList.Clear();
        }
        return new PersistentIpConfig
        {
            Source = IpSourceKind.NetworkManager,
            Mode = mode,
            Addresses = addrList,
            Gateway = IpValidation.TryParseIPv4Strict(gateway, out var gw) ? gw : "",
            Dns = dnsList,
            NmConnectionName = connectionName,
            NmUuid = uuid,
            NmFile = file,
            NmEphemeral = IsEphemeralFile(file),
            IsAuthoritative = true,
        };
    }

    public static string BuildModifyCommand(string connectionId, IpConfig cfg)
    {
        var id = UnixPath.ShellQuote(connectionId);
        if (cfg.UseDhcp)
        {
            return $"nmcli connection modify {id} ipv4.method auto ipv4.addresses \"\" " +
                   "ipv4.gateway \"\" ipv4.dns \"\" ipv4.ignore-auto-dns no ipv4.routes \"\" " +
                   "connection.autoconnect yes";
        }

        var sb = new StringBuilder();
        sb.Append($"nmcli connection modify {id} ipv4.method manual ");
        sb.Append($"ipv4.addresses {UnixPath.ShellQuote(string.Join(",", cfg.Cidrs))} ");
        sb.Append(cfg.Gateway.Length > 0
            ? $"ipv4.gateway {UnixPath.ShellQuote(cfg.Gateway)} "
            : "ipv4.gateway \"\" ");
        if (cfg.Dns.Count > 0)
            sb.Append($"ipv4.dns {UnixPath.ShellQuote(string.Join(",", cfg.Dns))} ipv4.ignore-auto-dns yes ");
        else
            sb.Append("ipv4.dns \"\" ipv4.ignore-auto-dns no ");
        sb.Append("ipv4.routes \"\" connection.autoconnect yes");
        return sb.ToString();
    }

    public static string BuildUpCommand(string connectionId)
        => $"nmcli --wait 60 connection up {UnixPath.ShellQuote(connectionId)}";
}
