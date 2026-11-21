using System.Globalization;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>IPv4 / 掩码 / DNS / 网关 / SSH 风险校验（纯逻辑，可单测）。</summary>
public static class IpValidation
{
    /// <summary>严格点分四段 IPv4：拒绝 192.168.1、前导零、越界。</summary>
    public static bool TryParseIPv4Strict(string? text, out string normalized, out uint value)
    {
        normalized = "";
        value = 0;
        var s = (text ?? "").Trim();
        var parts = s.Split('.');
        if (parts.Length != 4) return false;

        uint v = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            if (part.Length > 1 && part[0] == '0') return false;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                || n < 0 || n > 255)
                return false;
            v = (v << 8) | (uint)n;
        }

        normalized = s;
        value = v;
        return true;
    }

    public static bool TryParseIPv4Strict(string? text, out string normalized)
        => TryParseIPv4Strict(text, out normalized, out _);

    /// <summary>解析 "24" 或 "255.255.255.0"；拒绝非连续掩码。</summary>
    public static bool TryParsePrefixOrMaskStrict(string? text, out int prefix)
    {
        prefix = 0;
        var s = (text ?? "").Trim();
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
        {
            if (p is < 0 or > 32) return false;
            prefix = p;
            return true;
        }

        if (!TryParseIPv4Strict(s, out _, out var mask)) return false;
        var inv = ~mask;
        if ((inv & (inv + 1)) != 0) return false; // 掩码位不连续
        prefix = System.Numerics.BitOperations.PopCount(mask);
        return true;
    }

    public static string MaskFromPrefix(int prefix)
    {
        prefix = Math.Clamp(prefix, 0, 32);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return $"{(mask >> 24) & 255}.{(mask >> 16) & 255}.{(mask >> 8) & 255}.{mask & 255}";
    }

    public static bool IsInSubnet(string ip, string networkIp, int prefix)
    {
        if (!TryParseIPv4Strict(ip, out _, out var a)) return false;
        if (!TryParseIPv4Strict(networkIp, out _, out var n)) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (a & mask) == (n & mask);
    }

    /// <summary>是否为 /0-/30 网段的网络地址或广播地址。</summary>
    public static bool IsNetworkOrBroadcast(string ip, int prefix)
    {
        if (prefix is >= 31 or < 0) return false;
        if (!TryParseIPv4Strict(ip, out _, out var value)) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = value & mask;
        var broadcast = network | ~mask;
        return value == network || value == broadcast;
    }

    /// <summary>单行地址校验；成功返回 null，失败返回错误文本。</summary>
    public static string? ValidateAddressRow(string addressText, string maskText, out Ipv4Address parsed)
    {
        parsed = default;
        if (!TryParseIPv4Strict(addressText, out var ip))
            return "IP 地址格式不正确（需点分四段，如 192.168.1.10）。";
        if (!TryParsePrefixOrMaskStrict(maskText, out var prefix))
            return "子网掩码/前缀不正确（如 24 或 255.255.255.0）。";
        if (IsNetworkOrBroadcast(ip, prefix))
            return $"{(prefix < 31 ? "网络地址/广播地址不能作为主机 IP" : "地址无效")}（{ip}/{prefix}）。";
        parsed = new Ipv4Address(ip, prefix);
        return null;
    }

    /// <summary>
    /// 完整配置校验。
    /// </summary>
    /// <param name="cfg">待下发配置。</param>
    /// <param name="oldAddresses">该网卡当前 IPv4（用于判断 SSH 地址是否被移除）。</param>
    /// <param name="sshServerIp">$SSH_CONNECTION 第 3 段（服务端 IP）。</param>
    public static List<string> Validate(
        IpConfig cfg,
        IReadOnlyList<Ipv4Address> oldAddresses,
        string sshServerIp,
        out bool sshIpRemoved,
        out bool sshIpUnknown)
    {
        var errors = new List<string>();
        sshIpRemoved = false;
        sshIpUnknown = string.IsNullOrWhiteSpace(sshServerIp);

        if (cfg.UseDhcp) return errors;

        if (cfg.Addresses.Count == 0)
            errors.Add("静态模式至少需要一个 IPv4 地址。");

        var seenIps = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < cfg.Addresses.Count; i++)
        {
            var a = cfg.Addresses[i];
            if (!TryParseIPv4Strict(a.Address, out var ip))
            {
                errors.Add($"第 {i + 1} 个 IP 地址格式不正确：{a.Address}");
                continue;
            }
            if (a.Prefix is < 0 or > 32)
            {
                errors.Add($"第 {i + 1} 个地址的前缀长度不正确：{a.Prefix}");
                continue;
            }
            if (IsNetworkOrBroadcast(ip, a.Prefix))
                errors.Add($"第 {i + 1} 个地址是网络地址/广播地址：{ip}/{a.Prefix}");
            if (!seenIps.Add(ip))
                errors.Add($"IP 地址重复：{ip}");
        }

        if (!string.IsNullOrWhiteSpace(cfg.Gateway))
        {
            if (!TryParseIPv4Strict(cfg.Gateway, out var gw))
            {
                errors.Add("默认网关格式不正确。");
            }
            else if (cfg.Addresses.Count > 0
                     && !cfg.Addresses.Any(a => IsInSubnet(gw, a.Address, a.Prefix)))
            {
                errors.Add($"默认网关 {gw} 不在任何已配置地址的子网内。");
            }
        }

        var seenDns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dns in cfg.Dns)
        {
            if (!TryParseIPv4Strict(dns, out var d))
                errors.Add($"DNS 地址格式不正确：{dns}");
            else if (!seenDns.Add(d))
                errors.Add($"DNS 地址重复：{d}");
        }

        if (!string.IsNullOrWhiteSpace(sshServerIp)
            && TryParseIPv4Strict(sshServerIp, out var sshIp)
            && oldAddresses.Any(a => a.Address.Equals(sshIp, StringComparison.Ordinal))
            && !cfg.Addresses.Any(a => a.Address.Equals(sshIp, StringComparison.Ordinal)))
        {
            // 不阻断应用：由确认框强提醒 + 120 秒自动回滚兜底
            sshIpRemoved = true;
        }

        return errors;
    }
}
