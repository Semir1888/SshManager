using System.Text.Json;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>网卡运行时状态解析（ip -j 优先，旧 iproute2 回退）。</summary>
public sealed partial class NetworkService
{
    private sealed class LinkInfo
    {
        public string Name { get; init; } = "";
        public string State { get; init; } = "";
        public string Mac { get; init; } = "";
        public string LinkType { get; init; } = "";
        public string Kind { get; init; } = "";
        public bool IsWifi { get; init; }
        public bool Unsupported { get; init; }
    }

    /// <summary>列出物理/无线网卡（过滤 lo、docker、veth 等虚拟网卡）。</summary>
    public async Task<List<NetInterface>> ListInterfacesAsync()
    {
        var env = await GetEnvironmentAsync();
        if (env.IpJsonSupported)
        {
            try
            {
                var list = await BuildFromIpJsonAsync(env);
                if (list.Count > 0 || env.NetplanInstalled || env.NmRunning)
                    return list;
            }
            catch
            {
                // 回退旧解析
            }
        }

        var legacy = await ListInterfacesLegacyAsync();
        return legacy.Select(i => EnrichLegacy(i, env)).ToList();
    }

    private async Task<List<NetInterface>> BuildFromIpJsonAsync(NetworkEnvironment env)
    {
        var links = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        var linkJson = await RunOutAsync("LC_ALL=C ip -j link show");
        if (linkJson.TrimStart().StartsWith("["))
        {
            using var doc = JsonDocument.Parse(linkJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = GetStr(el, "ifname");
                if (name.Length == 0 || IsVirtual(name)) continue;
                var linkType = GetStr(el, "link_type");
                if (linkType == "loopback") continue;

                var kind = "";
                if (el.TryGetProperty("linkinfo", out var li) && li.ValueKind == JsonValueKind.Object)
                    kind = GetStr(li, "info_kind");

                var unsupported = kind is "bridge" or "bond" or "vlan" or "vrf" or "macvlan"
                    or "ipvlan" or "wireguard" or "dummy" or "tun" or "tap"
                    || (linkType.Length > 0 && linkType != "ether")
                    || el.TryGetProperty("master", out _);

                links[name] = new LinkInfo
                {
                    Name = name,
                    State = GetStr(el, "operstate"),
                    Mac = GetStr(el, "address"),
                    LinkType = linkType,
                    Kind = kind,
                    IsWifi = name.StartsWith("wl", StringComparison.Ordinal),
                    Unsupported = unsupported,
                };
            }
        }

        if (links.Count == 0)
        {
            // ip -j link 不支持的极老环境
            return (await ListInterfacesLegacyAsync())
                .Select(i => EnrichLegacy(i, env))
                .ToList();
        }

        var ipv4 = new Dictionary<string, List<Ipv4Address>>(StringComparer.Ordinal);
        var ipv6 = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var addrJson = await RunOutAsync("LC_ALL=C ip -j addr show");
        if (addrJson.TrimStart().StartsWith("["))
        {
            using var doc = JsonDocument.Parse(addrJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = GetStr(el, "ifname");
                if (!links.ContainsKey(name)) continue;
                if (!el.TryGetProperty("addr_info", out var infos) || infos.ValueKind != JsonValueKind.Array)
                    continue;

                var v4 = new List<Ipv4Address>();
                var v6 = new List<string>();
                foreach (var info in infos.EnumerateArray())
                {
                    if (info.ValueKind != JsonValueKind.Object) continue;
                    var family = GetStr(info, "family");
                    var local = GetStr(info, "local");
                    var prefix = GetInt(info, "prefixlen");
                    if (local.Length == 0) continue;
                    if (family == "inet")
                    {
                        if (IpValidation.TryParseIPv4Strict(local, out var ip))
                        {
                            var a = new Ipv4Address(ip, prefix);
                            if (!v4.Contains(a)) v4.Add(a);
                        }
                    }
                    else if (family == "inet6")
                    {
                        var cidr = $"{local}/{prefix}";
                        if (!v6.Contains(cidr)) v6.Add(cidr);
                    }
                }
                ipv4[name] = v4;
                ipv6[name] = v6;
            }
        }

        var gateways = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var routeJson = await RunOutAsync("LC_ALL=C ip -j route show default");
        if (routeJson.TrimStart().StartsWith("["))
        {
            using var doc = JsonDocument.Parse(routeJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var dev = GetStr(el, "dev");
                var gw = GetStr(el, "gateway");
                if (dev.Length == 0 || !IpValidation.TryParseIPv4Strict(gw, out var g)) continue;
                if (!gateways.TryGetValue(dev, out var list))
                {
                    list = new List<string>();
                    gateways[dev] = list;
                }
                if (!list.Contains(g)) list.Add(g);
            }
        }

        var gateways6 = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var route6Json = await RunOutAsync("LC_ALL=C ip -j -6 route show default");
        if (route6Json.TrimStart().StartsWith("["))
        {
            using var doc = JsonDocument.Parse(route6Json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var dev = GetStr(el, "dev");
                var gw = GetStr(el, "gateway");
                if (dev.Length == 0 || gw.Length == 0) continue;
                if (!gateways6.TryGetValue(dev, out var list))
                {
                    list = new List<string>();
                    gateways6[dev] = list;
                }
                if (!list.Contains(gw)) list.Add(gw);
            }
        }

        var globalDns = ParseDnsList(await RunOutAsync(
            "resolvectl --no-pager 2>/dev/null | sed -n 's/^ *DNS Servers: //p' | sort -u | tr '\\n' ' '"));
        if (globalDns.Count == 0)
            globalDns = ParseDnsList(await RunOutAsync(
                "awk '/^nameserver/ && $2 != \"127.0.0.53\" {printf \"%s \", $2}' /etc/resolv.conf 2>/dev/null"));

        var nmRunning = env.NmRunning;
        var nmByDev = new Dictionary<string, (string state, string con)>(StringComparer.Ordinal);
        if (nmRunning)
        {
            var nmOut = await RunOutAsync("LC_ALL=C nmcli -t -f DEVICE,STATE,CONNECTION device status 2>/dev/null");
            foreach (var raw in nmOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = SplitTerse(raw);
                if (f.Length < 3) continue;
                nmByDev[f[0]] = (f[1], f[2]);
            }
        }

        var result = new List<NetInterface>();
        foreach (var link in links.Values)
        {
            var v4 = ipv4.TryGetValue(link.Name, out var v4List) ? v4List : new List<Ipv4Address>();
            var v6 = ipv6.TryGetValue(link.Name, out var v6List) ? v6List : new List<string>();
            var gws = gateways.TryGetValue(link.Name, out var gList) ? gList : new List<string>();
            var gws6 = gateways6.TryGetValue(link.Name, out var g6List) ? g6List : new List<string>();
            var ifDns = ParseDnsList(await RunOutAsync($"resolvectl dns {Q(link.Name)} 2>/dev/null"));
            if (ifDns.Count == 0) ifDns = globalDns;

            var nmManaged = false;
            var nmState = "";
            var nmCon = "";
            if (nmByDev.TryGetValue(link.Name, out var nm))
            {
                nmState = nm.state;
                nmCon = nm.con;
                nmManaged = !nm.state.Equals("unmanaged", StringComparison.OrdinalIgnoreCase);
            }

            var nmMethod = "";
            var nmUuid = "";
            var nmFile = "";
            if (nmManaged && nmCon.Length > 0)
            {
                nmMethod = (await RunOutAsync(
                    $"LC_ALL=C nmcli -g ipv4.method connection show {Q(nmCon)} 2>/dev/null")).Trim();
                var ids = (await RunOutAsync(
                        $"LC_ALL=C nmcli -g connection.uuid,connection.filename connection show {Q(nmCon)} 2>/dev/null"))
                    .Split('\n', StringSplitOptions.TrimEntries);
                if (ids.Length >= 1) nmUuid = ids[0];
                if (ids.Length >= 2) nmFile = ids[1];
            }

            var allAddrStrings = v4.Select(a => a.Cidr)
                .Concat(v6)
                .ToList();

            result.Add(new NetInterface
            {
                Name = link.Name,
                IsWifi = link.IsWifi,
                State = link.State,
                Mac = link.Mac,
                Addresses = allAddrStrings,
                Gateway = gws.FirstOrDefault() ?? "",
                Dns = string.Join(", ", ifDns),
                NmManaged = nmManaged,
                NmConnection = nmCon,
                NmMethod = nmMethod,
                Ipv4Addresses = v4,
                Ipv6Addresses = v6,
                DnsServers = ifDns,
                Gateways = gws,
                Ipv6Gateways = gws6,
                NmRunning = nmRunning,
                NmUuid = nmUuid,
                NmFile = nmFile,
                NmEphemeral = NmcliConfig.IsEphemeralFile(nmFile),
                NetplanInstalled = env.NetplanInstalled,
                Kind = link.Kind.Length > 0 ? link.Kind : link.LinkType,
                UnsupportedKind = link.Unsupported,
                HasMultipleDefaultRoutes = gws.Count > 1,
            });
        }

        return result.OrderBy(i => i.IsWifi).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    private static List<string> ParseDnsList(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split(new[] { ',', ' ', ';', '\t', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IpValidation.TryParseIPv4Strict(raw, out var d) && !list.Contains(d))
                list.Add(d);
        }
        return list;
    }

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;

    private static NetInterface EnrichLegacy(NetInterface legacy, NetworkEnvironment env)
    {
        var v4 = new List<Ipv4Address>();
        var v6 = new List<string>();
        foreach (var s in legacy.Addresses)
        {
            var slash = s.IndexOf('/');
            var ipText = slash > 0 ? s[..slash] : s;
            var prefixText = slash > 0 ? s[(slash + 1)..] : "";
            if (ipText.Contains(':'))
            {
                v6.Add(s);
                continue;
            }
            if (IpValidation.TryParseIPv4Strict(ipText, out var ip)
                && IpValidation.TryParsePrefixOrMaskStrict(prefixText, out var prefix))
                v4.Add(new Ipv4Address(ip, prefix));
        }

        var gws = string.IsNullOrWhiteSpace(legacy.Gateway)
            ? new List<string>()
            : new List<string> { legacy.Gateway };
        return new NetInterface
        {
            Name = CleanIfaceName(legacy.Name),
            IsWifi = legacy.IsWifi,
            State = legacy.State,
            Mac = legacy.Mac,
            Addresses = legacy.Addresses,
            Gateway = legacy.Gateway,
            Dns = legacy.Dns,
            NmManaged = legacy.NmManaged,
            NmConnection = legacy.NmConnection,
            NmMethod = legacy.NmMethod,
            Ipv4Addresses = v4,
            Ipv6Addresses = v6,
            DnsServers = ParseDnsList(legacy.Dns),
            Gateways = gws,
            NmRunning = env.NmRunning,
            NetplanInstalled = env.NetplanInstalled,
            Kind = "",
            UnsupportedKind = false,
        };
    }
}
