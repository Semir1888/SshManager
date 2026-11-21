using SshManager.Core.Models;
using YamlDotNet.RepresentationModel;

namespace SshManager.Core.Services;

/// <summary>单个 netplan 文件的冲突分析结果。</summary>
public sealed class NetplanFileAnalysis
{
    public string Path { get; init; } = "";
    public bool DefinesInterface { get; init; }
    public bool OnlyThisInterface { get; init; }
    public bool Complex { get; init; }
    public bool IsCloudInit { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>netplan YAML 的解析/渲染/有效配置校验（纯逻辑，可单测）。</summary>
public static class NetplanConfig
{
    private static YamlNode? Get(YamlMappingNode map, string key)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode s && string.Equals(s.Value, key, StringComparison.Ordinal))
                return pair.Value;
        }
        return null;
    }

    private static string Scalar(YamlNode? node)
        => node is YamlScalarNode s ? (s.Value ?? "") : "";

    private static bool BoolValue(YamlNode? node)
        => string.Equals(Scalar(node), "true", StringComparison.OrdinalIgnoreCase);

    private static YamlMappingNode? Map(YamlNode? node) => node as YamlMappingNode;

    private static YamlSequenceNode? Seq(YamlNode? node) => node as YamlSequenceNode;

    /// <summary>
    /// 容错：sudo/stderr 泄漏时 YAML 前部可能出现 WARNING/日志行。
    /// 从第一行 network: 开始截取，避免整份配置解析失败。
    /// </summary>
    private static string SanitizeYaml(string text)
    {
        var t = (text ?? "").Replace("\r\n", "\n");
        var lines = t.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("network:", StringComparison.Ordinal))
                return i == 0 ? t : string.Join('\n', lines.Skip(i));
        }
        return t;
    }

    /// <summary>解析 netplan get 的有效合并配置。若未定义接口，返回 Source=None。</summary>
    public static PersistentIpConfig ParseEffective(string? yamlText, string iface)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
            return new PersistentIpConfig { Source = IpSourceKind.None };

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(SanitizeYaml(yamlText)));
            if (stream.Documents.Count == 0) return new PersistentIpConfig { Source = IpSourceKind.None };
            var root = Map(stream.Documents[0].RootNode);
            var network = root != null ? Map(Get(root, "network")) : null;
            if (network == null) return new PersistentIpConfig { Source = IpSourceKind.None };

            var renderer = Scalar(Get(network, "renderer"));
            var node = FindDevice(network, iface);
            if (node == null) return new PersistentIpConfig { Source = IpSourceKind.None, Renderer = renderer };

            var devRenderer = Scalar(Get(node, "renderer"));
            if (devRenderer.Length > 0) renderer = devRenderer;

            var dhcp = BoolValue(Get(node, "dhcp4"));
            var addresses = new List<Ipv4Address>();
            if (Seq(Get(node, "addresses")) is { } seq)
            {
                foreach (var item in seq.Children)
                {
                    var cidr = Scalar(item).Trim();
                    var addr = ParseCidr(cidr);
                    if (addr != null && !addresses.Contains(addr.Value)) addresses.Add(addr.Value);
                }
            }

            var gateway = "";
            if (Seq(Get(node, "routes")) is { } routes)
            {
                foreach (var item in routes.Children)
                {
                    var route = Map(item);
                    if (route == null) continue;
                    var to = Scalar(Get(route, "to")).Trim();
                    if (!(to.Equals("default", StringComparison.OrdinalIgnoreCase)
                          || to.Equals("0.0.0.0/0", StringComparison.Ordinal)))
                        continue;
                    var via = Scalar(Get(route, "via")).Trim();
                    if (IpValidation.TryParseIPv4Strict(via, out var gw))
                    {
                        gateway = gw;
                        break;
                    }
                }
            }
            if (gateway.Length == 0)
            {
                // 旧版 netplan 的 gateway4
                if (IpValidation.TryParseIPv4Strict(Scalar(Get(node, "gateway4")), out var gw4))
                    gateway = gw4;
            }

            var dns = new List<string>();
            var ns = Map(Get(node, "nameservers"));
            if (ns != null && Seq(Get(ns, "addresses")) is { } dnsSeq)
            {
                foreach (var item in dnsSeq.Children)
                {
                    if (IpValidation.TryParseIPv4Strict(Scalar(item), out var d) && !dns.Contains(d))
                        dns.Add(d);
                }
            }

            return new PersistentIpConfig
            {
                Source = IpSourceKind.Netplan,
                Mode = dhcp ? IpMode.Dhcp : IpMode.Static,
                Addresses = addresses,
                Gateway = gateway,
                Dns = dns,
                Renderer = renderer,
                IsAuthoritative = true,
            };
        }
        catch (Exception ex)
        {
            return new PersistentIpConfig
            {
                Source = IpSourceKind.Unknown,
                ReadError = "解析 netplan 配置失败：" + ex.Message,
            };
        }
    }

    private static Ipv4Address? ParseCidr(string s)
    {
        var slash = s.IndexOf('/');
        var ipText = slash > 0 ? s[..slash] : s;
        var prefixText = slash > 0 ? s[(slash + 1)..] : "32";
        if (!IpValidation.TryParseIPv4Strict(ipText, out var ip)) return null;
        if (!IpValidation.TryParsePrefixOrMaskStrict(prefixText, out var prefix)) return null;
        return new Ipv4Address(ip, prefix);
    }

    private static YamlMappingNode? FindDevice(YamlMappingNode network, string iface)
    {
        foreach (var section in new[] { "ethernets", "wifis" })
        {
            var sec = Map(Get(network, section));
            if (sec == null) continue;
            var dev = Map(Get(sec, iface));
            if (dev != null) return dev;
        }
        return null;
    }

    /// <summary>分析一个 netplan 文件是否定义该接口、是否可安全接管。</summary>
    public static NetplanFileAnalysis AnalyzeFile(string path, string? yamlText, string iface)
    {
        var result = new NetplanFileAnalysis { Path = path };
        if (string.IsNullOrWhiteSpace(yamlText))
            return new NetplanFileAnalysis { Path = path, Error = "空文件" };

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(SanitizeYaml(yamlText)));
            if (stream.Documents.Count == 0)
                return new NetplanFileAnalysis { Path = path, Error = "无 YAML 文档" };

            var root = Map(stream.Documents[0].RootNode);
            var network = root != null ? Map(Get(root, "network")) : null;
            if (network == null) return result;

            var sections = new[] { "ethernets", "wifis", "bonds", "bridges", "vlans" };
            var totalDevices = 0;
            var defines = false;
            var complex = false;
            foreach (var section in sections)
            {
                var sec = Map(Get(network, section));
                if (sec == null) continue;
                foreach (var pair in sec.Children)
                {
                    totalDevices++;
                    if (pair.Key is not YamlScalarNode name) continue;
                    if (!string.Equals(name.Value, iface, StringComparison.Ordinal)) continue;
                    defines = true;
                    if (section is "wifis" or "bonds" or "bridges" or "vlans")
                        complex = true;
                    var dev = Map(pair.Value);
                    if (dev != null && (Get(dev, "match") != null || Get(dev, "set-name") != null))
                        complex = true;
                }
            }

            return new NetplanFileAnalysis
            {
                Path = path,
                DefinesInterface = defines,
                OnlyThisInterface = defines && totalDevices == 1,
                Complex = complex,
                IsCloudInit = path.Contains("cloud-init", StringComparison.OrdinalIgnoreCase),
            };
        }
        catch (Exception ex)
        {
            return new NetplanFileAnalysis { Path = path, Error = ex.Message };
        }
    }

    /// <summary>校验 netplan get 的有效配置是否与目标完全一致（地址集合/网关/DNS/模式）。</summary>
    public static (bool Ok, string Error) EffectiveMatches(string? effectiveYaml, string iface, IpConfig cfg)
    {
        var effective = ParseEffective(effectiveYaml, iface);
        if (effective.Source == IpSourceKind.Unknown)
            return (false, effective.ReadError);
        if (effective.Source == IpSourceKind.None)
            return (false, "合并后的 netplan 配置中未找到该网卡（可能被其他文件覆盖）。");

        if (cfg.UseDhcp)
        {
            if (effective.Mode != IpMode.Dhcp)
                return (false, "目标为 DHCP，但有效配置不是 dhcp4: true。");
            if (effective.Addresses.Count > 0)
                return (false, $"目标为 DHCP，但有效配置仍残留静态地址：[{string.Join(", ", effective.Addresses)}]。");
            return (true, "");
        }

        if (effective.Mode != IpMode.Static)
            return (false, "目标为静态，但有效配置仍为 DHCP。");

        var want = cfg.Addresses.Select(a => a.Cidr).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var got = effective.Addresses.Select(a => a.Cidr).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!want.SequenceEqual(got))
            return (false, $"有效配置的地址集合不一致：期望 [{string.Join(", ", want)}]，实际 [{string.Join(", ", got)}]。");

        if (!string.Equals(cfg.Gateway, effective.Gateway, StringComparison.Ordinal))
            return (false, $"有效配置的默认网关不一致：期望 {cfg.Gateway}，实际 {effective.Gateway}。");

        var wantDns = cfg.Dns.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var gotDns = effective.Dns.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!wantDns.SequenceEqual(gotDns))
            return (false, $"有效配置的 DNS 不一致：期望 [{string.Join(", ", wantDns)}]，实际 [{string.Join(", ", gotDns)}]。");

        return (true, "");
    }
}
