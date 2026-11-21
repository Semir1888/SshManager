using SshManager.Core.Models;
using SshManager.Core.Services;
using Xunit;

namespace SshManager.Core.Tests;

public class NetworkIpTests
{
    private static IpConfig Static(params Ipv4Address[] addresses)
        => new("eth0", IpMode.Static, addresses, "192.168.1.1", new[] { "1.1.1.1", "8.8.8.8" });

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("192.168.1", false)]
    [InlineData("192.168.01.1", false)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("", false)]
    public void StrictIpv4(string text, bool expected)
        => Assert.Equal(expected, IpValidation.TryParseIPv4Strict(text, out _));

    [Theory]
    [InlineData("24", 24)]
    [InlineData("0", 0)]
    [InlineData("32", 32)]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.0.0", 16)]
    public void PrefixOrMask(string text, int expected)
    {
        Assert.True(IpValidation.TryParsePrefixOrMaskStrict(text, out var prefix));
        Assert.Equal(expected, prefix);
    }

    [Theory]
    [InlineData("33")]
    [InlineData("255.0.255.0")]
    [InlineData("255.255.255.1")]
    [InlineData("abc")]
    public void InvalidMask(string text)
        => Assert.False(IpValidation.TryParsePrefixOrMaskStrict(text, out _));

    [Theory]
    [InlineData("192.168.1.0", 24, true)]
    [InlineData("192.168.1.255", 24, true)]
    [InlineData("192.168.1.10", 24, false)]
    [InlineData("10.0.0.0", 31, false)]
    [InlineData("10.0.0.1", 31, false)]
    public void NetworkOrBroadcast(string ip, int prefix, bool expected)
        => Assert.Equal(expected, IpValidation.IsNetworkOrBroadcast(ip, prefix));

    [Fact]
    public void MultipleAddressesAreValid()
    {
        var errors = IpValidation.Validate(
            Static(new Ipv4Address("192.168.1.10", 24), new Ipv4Address("192.168.1.11", 24)),
            Array.Empty<Ipv4Address>(), "", out var removed, out var unknown);

        Assert.Empty(errors);
        Assert.False(removed);
        Assert.True(unknown);
    }

    [Fact]
    public void GatewayMustBeInsideSubnet()
    {
        var cfg = new IpConfig("eth0", IpMode.Static,
            new[] { new Ipv4Address("192.168.1.10", 24) },
            "10.1.1.1", Array.Empty<string>());
        var errors = IpValidation.Validate(cfg, Array.Empty<Ipv4Address>(), "", out _, out _);
        Assert.Contains(errors, e => e.Contains("网关"));
    }

    [Fact]
    public void DuplicateAddressIsRejected()
    {
        var errors = IpValidation.Validate(
            Static(new Ipv4Address("192.168.1.10", 24), new Ipv4Address("192.168.1.10", 25)),
            Array.Empty<Ipv4Address>(), "", out _, out _);
        Assert.Contains(errors, e => e.Contains("重复"));
    }

    [Fact]
    public void InvalidDnsIsReported()
    {
        var cfg = new IpConfig("eth0", IpMode.Static,
            new[] { new Ipv4Address("192.168.1.10", 24) }, "", new[] { "not-a-dns" });
        var errors = IpValidation.Validate(cfg, Array.Empty<Ipv4Address>(), "", out _, out _);
        Assert.Contains(errors, e => e.Contains("DNS"));
    }

    [Fact]
    public void SshAddressRemovalIsWarningNotBlocking()
    {
        var cfg = new IpConfig("eth0", IpMode.Static,
            new[] { new Ipv4Address("10.0.0.6", 24) }, "", Array.Empty<string>());
        var errors = IpValidation.Validate(
            cfg, new[] { new Ipv4Address("10.0.0.5", 24) }, "10.0.0.5",
            out var removed, out _);
        Assert.True(removed);
        Assert.Empty(errors);
    }

    [Fact]
    public void NetplanRendersMultipleAddressesAndDns()
    {
        var yaml = NetworkService.BuildNetplanYaml(
            Static(new Ipv4Address("192.168.1.10", 24), new Ipv4Address("192.168.1.11", 24)),
            "NetworkManager");

        Assert.Contains("renderer: NetworkManager", yaml);
        Assert.Contains("- 192.168.1.10/24", yaml);
        Assert.Contains("- 192.168.1.11/24", yaml);
        Assert.Contains("via: 192.168.1.1", yaml);
        Assert.Contains("- 1.1.1.1", yaml);
        Assert.Contains("- 8.8.8.8", yaml);
    }

    [Fact]
    public void NetplanDhcpHasNoAddresses()
    {
        var cfg = new IpConfig("eth0", IpMode.Dhcp,
            Array.Empty<Ipv4Address>(), "", Array.Empty<string>());
        var yaml = NetworkService.BuildNetplanYaml(cfg, "networkd");
        Assert.Contains("dhcp4: true", yaml);
        Assert.DoesNotContain("addresses:", yaml);
    }

    private const string SampleNetplan = """
network:
  version: 2
  renderer: NetworkManager
  ethernets:
    eth0:
      dhcp4: false
      addresses:
        - 192.168.1.10/24
        - 192.168.1.11/24
      routes:
        - to: default
          via: 192.168.1.1
      nameservers:
        addresses:
          - 1.1.1.1
          - 8.8.8.8
""";

    [Fact]
    public void ParseEffectiveNetplan()
    {
        var parsed = NetplanConfig.ParseEffective(SampleNetplan, "eth0");
        Assert.Equal(IpSourceKind.Netplan, parsed.Source);
        Assert.Equal(IpMode.Static, parsed.Mode);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Equal("192.168.1.1", parsed.Gateway);
        Assert.Equal(2, parsed.Dns.Count);
        Assert.Equal("NetworkManager", parsed.Renderer);
    }

    [Fact]
    public void EffectiveConfigMatchesTarget()
    {
        var cfg = Static(new Ipv4Address("192.168.1.10", 24), new Ipv4Address("192.168.1.11", 24));
        var match = NetplanConfig.EffectiveMatches(SampleNetplan, "eth0", cfg);
        Assert.True(match.Ok, match.Error);
    }

    [Fact]
    public void ParseEffectiveIgnoresLeadingWarningLines()
    {
        var noisy = "WARNING: something from netplan\n" + SampleNetplan;
        var parsed = NetplanConfig.ParseEffective(noisy, "eth0");
        Assert.Equal(IpSourceKind.Netplan, parsed.Source);
        Assert.Equal(2, parsed.Addresses.Count);

        var cfg = Static(new Ipv4Address("192.168.1.10", 24), new Ipv4Address("192.168.1.11", 24));
        var match = NetplanConfig.EffectiveMatches(noisy, "eth0", cfg);
        Assert.True(match.Ok, match.Error);
    }

    [Fact]
    public void ResidualOldAddressIsReported()
    {
        var residual = SampleNetplan.Replace(
            "        - 192.168.1.11/24",
            "        - 192.168.1.11/24\n        - 192.168.1.12/24");
        var cfg = Static(new Ipv4Address("192.168.1.10", 24), new Ipv4Address("192.168.1.11", 24));
        var match = NetplanConfig.EffectiveMatches(residual, "eth0", cfg);
        Assert.False(match.Ok);
        Assert.Contains("192.168.1.12", match.Error);
    }

    [Fact]
    public void DhcpEffectiveWithStaticAddressesDoesNotMatch()
    {
        const string yaml = """
network:
  version: 2
  ethernets:
    eth0:
      dhcp4: true
      addresses:
        - 10.0.0.2/24
""";
        var cfg = new IpConfig("eth0", IpMode.Dhcp,
            Array.Empty<Ipv4Address>(), "", Array.Empty<string>());
        var match = NetplanConfig.EffectiveMatches(yaml, "eth0", cfg);
        Assert.False(match.Ok);
        Assert.Contains("静态地址", match.Error);
    }

    [Fact]
    public void AnalyzeSimpleNetplanFile()
    {
        var analysis = NetplanConfig.AnalyzeFile("/etc/netplan/50-cloud-init.yaml", SampleNetplan, "eth0");
        Assert.True(analysis.DefinesInterface);
        Assert.True(analysis.OnlyThisInterface);
        Assert.False(analysis.Complex);
    }

    [Fact]
    public void ParseNmcliAddresses()
    {
        var addrs = NmcliConfig.ParseAddresses("192.168.1.10/24, 192.168.1.11/24");
        Assert.Equal(2, addrs.Count);
    }

    [Fact]
    public void NmcliPersistentConfigParses()
    {
        var cfg = NmcliConfig.FromNmcli(
            "Wired connection 1", "uuid-1", "/etc/NetworkManager/system-connections/x.nmconnection",
            "manual", "192.168.1.10/24,192.168.1.11/24", "192.168.1.1", "1.1.1.1 8.8.8.8");
        Assert.Equal(IpMode.Static, cfg.Mode);
        Assert.Equal(2, cfg.Addresses.Count);
        Assert.Equal(2, cfg.Dns.Count);
        Assert.False(cfg.NmEphemeral);
    }

    [Fact]
    public void NmcliEphemeralDetection()
    {
        Assert.True(NmcliConfig.IsEphemeralFile("/run/NetworkManager/system-connections/x.nmconnection"));
        Assert.False(NmcliConfig.IsEphemeralFile("/etc/NetworkManager/system-connections/x.nmconnection"));
    }

    [Fact]
    public void WatchdogScriptRestoresBackupAndSleeps()
    {
        var script = RollbackProtocol.BuildWatchdogScript();
        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("sleep 120", script);
        Assert.Contains("netplan apply", script);
        Assert.Contains("BACKUP_DIR/target.yaml", script);
        Assert.Contains("disabled.map", script);
        Assert.DoesNotContain("TARGET_EXISTED", script);
        Assert.DoesNotContain("sudo", script); // 脚本本身以 root 运行，不应包含密码/提权
    }

    [Fact]
    public void RollbackCommandsRejectInvalidToken()
    {
        Assert.Throws<ArgumentException>(() => RollbackProtocol.BuildCommitCommand("bad"));
        Assert.Throws<ArgumentException>(() => RollbackProtocol.BuildRollbackNowCommand(""));
        Assert.Throws<ArgumentException>(() => RollbackProtocol.BuildStartCommand("../x"));
    }

    [Fact]
    public void RollbackTokenAndPending()
    {
        var token = RollbackProtocol.NewToken();
        Assert.True(RollbackProtocol.IsValidToken(token));
        Assert.False(RollbackProtocol.IsValidToken("not-a-token"));
        Assert.False(RollbackProtocol.IsValidToken(new string('a', 31)));

        var meta = new RollbackProtocol.PendingMeta(
            token, "eth0", "netplan", "/etc/netplan/99-sshmanager-eth0.yaml",
            1_000_120, "2 个静态地址");
        var json = RollbackProtocol.BuildPendingJson(meta);
        var pending = RollbackProtocol.ParsePending(json, 1_000_060);
        Assert.NotNull(pending);
        Assert.Equal(60, pending!.RemainingSeconds);
        Assert.Equal("eth0", pending.Iface);
    }
}
