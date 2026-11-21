using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>一份待下发的 IPv4 配置（支持多个地址、多 DNS、单网关）。</summary>
public sealed record IpConfig(
    string Iface,
    IpMode Mode,
    IReadOnlyList<Ipv4Address> Addresses,
    string Gateway,
    IReadOnlyList<string> Dns)
{
    public bool UseDhcp => Mode == IpMode.Dhcp;
    public IReadOnlyList<string> Cidrs => Addresses.Select(a => a.Cidr).ToList();

    /// <summary>旧版单地址构造器（兼容现有调用，后续 UI 改造完成后移除）。</summary>
    public IpConfig(string iface, bool useDhcp, string ip, int prefix, string gateway, List<string> dns)
        : this(
            iface,
            useDhcp ? IpMode.Dhcp : IpMode.Static,
            useDhcp || string.IsNullOrWhiteSpace(ip)
                ? Array.Empty<Ipv4Address>()
                : new[] { new Ipv4Address(ip, prefix) },
            gateway,
            dns)
    {
    }
}

/// <summary>
/// 服务器网络参数读取/修改（工程配置用）。
/// 网卡 IP：优先 nmcli（NetworkManager 场景，含 WiFi 网卡），否则生成 netplan 配置（networkd 场景）。
/// WiFi 扫描/连接依赖 nmcli；防火墙依赖 ufw；未安装时界面给出提示。
/// </summary>
public sealed partial class NetworkService(SshSession session)
{
    private readonly SshSession _s = session;

    public SshSession Session => _s;

    // ------------------------------------------------------------------ 基础

    private Task<CommandResult> RunAsync(string cmd, TimeSpan? timeout = null)
        => _s.RunAsync(cmd, timeout ?? TimeSpan.FromSeconds(20));

    private async Task<string> RunOutAsync(string cmd, TimeSpan? timeout = null)
    {
        try
        {
            var r = await RunAsync(cmd, timeout);
            return r.ExitCode == 0 ? (r.Output ?? "") : "";
        }
        catch { return ""; }
    }

    /// <summary>以 sudo 执行命令（调用方负责捕获 SudoPasswordRequiredException 弹窗补密码）。</summary>
    public Task<CommandResult> RunSudoRawAsync(string command, TimeSpan? timeout = null)
        => RunAsync(_s.WrapSudo(command), timeout ?? TimeSpan.FromSeconds(90));

    /// <summary>
    /// 以 sudo 执行**整条 shell 命令**（含 &amp;&amp; / ; / 管道时必须用这个）。
    /// 普通 WrapSudo 只会给第一段加 sudo，后续段仍以普通用户运行。
    /// </summary>
    public Task<CommandResult> RunSudoShellAsync(string command, TimeSpan? timeout = null)
        => RunAsync(_s.WrapSudoShell(command), timeout ?? TimeSpan.FromSeconds(90));

    /// <summary>nmcli 是否可用（NetworkManager 是否安装）。</summary>
    public async Task<bool> HasNmAsync()
    {
        var r = await RunAsync("command -v nmcli");
        return r.ExitCode == 0;
    }

    /// <summary>ufw 是否安装。</summary>
    public async Task<bool> HasUfwAsync()
    {
        var r = await RunAsync("command -v ufw");
        return r.ExitCode == 0;
    }

    // ------------------------------------------------------------------ 网卡

    /// <summary>旧版运行时解析（保留作极老 iproute2 的回退）。</summary>
    private async Task<List<NetInterface>> ListInterfacesLegacyAsync()
    {
        var result = new List<NetInterface>();

        var linkOut = await RunOutAsync("ip -br link show");
        if (linkOut.Length == 0) return result;

        var states = new Dictionary<string, (string state, string mac)>();
        foreach (var raw in linkOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = Regex.Split(raw.Trim(), @"\s+");
            if (parts.Length < 2) continue;
            var name = parts[0];
            var state = parts[1];
            var mac = parts.Length >= 3 ? parts[2] : "";
            if (IsVirtual(name)) continue;
            states[name] = (state, mac);
        }

        // 地址
        var addrs = new Dictionary<string, List<string>>();
        var addrOut = await RunOutAsync("ip -br addr show");
        foreach (var raw in addrOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = Regex.Split(raw.Trim(), @"\s+");
            if (parts.Length < 2) continue;
            var name = parts[0];
            if (!states.ContainsKey(name)) continue;
            var list = parts.Skip(2).Where(a => a.Contains('/')).ToList();
            addrs[name] = list;
        }

        // 默认网关（按网卡）
        var gws = new Dictionary<string, string>();
        var routeOut = await RunOutAsync("ip route show default");
        foreach (var raw in routeOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = DefaultRouteRegex().Match(raw);
            if (m.Success) gws[m.Groups["dev"].Value] = m.Groups["via"].Value;
        }

        // 全局 DNS（systemd-resolved 优先，回退 resolv.conf）
        var dnsText = await RunOutAsync(
            "resolvectl --no-pager 2>/dev/null | sed -n 's/^ *DNS Servers: //p' | sort -u | tr '\\n' ' '");
        if (dnsText.Trim().Length == 0)
            dnsText = await RunOutAsync("awk '/^nameserver/ && $2 != \"127.0.0.53\" {printf \"%s \", $2}' /etc/resolv.conf 2>/dev/null");
        var globalDns = string.Join(", ", dnsText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());

        // NetworkManager 托管情况
        var nmByDev = new Dictionary<string, (bool managed, string con)>();
        var nmOut = await RunOutAsync("nmcli -t -f DEVICE,STATE,CONNECTION device status");
        foreach (var raw in nmOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = SplitTerse(raw);
            if (f.Length < 3) continue;
            var managed = f[1].StartsWith("connected") && f[2].Length > 0;
            nmByDev[f[0]] = (managed, f[2]);
        }

        foreach (var (name, (state, mac)) in states)
        {
            var list = addrs.TryGetValue(name, out var l) ? l : new List<string>();
            var nmManaged = nmByDev.TryGetValue(name, out var nm) && nm.managed;
            var conName = nmManaged ? nm.con : "";
            var method = "";
            var dns = globalDns;
            if (nmManaged && conName.Length > 0)
            {
                var g = await RunOutAsync($"nmcli -g ipv4.method,ipv4.dns connection show {Q(conName)}");
                var lines = g.Split('\n');
                if (lines.Length >= 1) method = lines[0].Trim();
                if (lines.Length >= 2 && lines[1].Trim().Length > 0)
                    dns = string.Join(", ", lines[1].Trim().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }
            result.Add(new NetInterface
            {
                Name = name,
                IsWifi = name.StartsWith("wl", StringComparison.Ordinal),
                State = state,
                Mac = mac,
                Addresses = list,
                Gateway = gws.TryGetValue(name, out var gw) ? gw : "",
                Dns = dns,
                NmManaged = nmManaged,
                NmConnection = conName,
                NmMethod = method,
            });
        }
        return result.OrderBy(i => i.IsWifi).ThenBy(i => i.Name).ToList();
    }

    private static bool IsVirtual(string name) =>
        name == "lo" || name.StartsWith("docker") || name.StartsWith("veth") ||
        name.StartsWith("br-") || name.StartsWith("virbr") || name.StartsWith("vmnet") ||
        name.StartsWith("tap") || name.StartsWith("tun") || name.StartsWith("wg") ||
        name.StartsWith("zt") || name.StartsWith("ovs") || name.StartsWith("sit") ||
        name.StartsWith("dummy");

    /// <summary>解析“24”或“255.255.255.0”为前缀长度；不合法返回 null。</summary>
    public static int? ParsePrefixOrMask(string s)
    {
        s = (s ?? "").Trim();
        if (int.TryParse(s, out var p)) return p >= 0 && p <= 32 ? p : null;
        if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var b = ip.GetAddressBytes();
        var mask = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var inv = ~mask;
        if ((inv & (inv + 1)) != 0) return null; // 掩码位不连续
        return BitOperations.PopCount(mask);
    }

    public static bool IsIPv4(string s) =>
        IPAddress.TryParse((s ?? "").Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    // ------------------------------------------------------------------ 应用 IP

    /// <summary>经 NetworkManager 应用 IP 配置（修改连接并 up）。sudoRun 由调用方注入（负责密码弹窗）。</summary>
    public async Task<string?> ApplyViaNmAsync(string conName, IpConfig cfg, Func<string, Task<string?>> sudoRun)
    {
        if (cfg.UseDhcp)
        {
            var err = await sudoRun(
                $"nmcli connection modify {Q(conName)} ipv4.method auto ipv4.addresses \"\" " +
                "ipv4.gateway \"\" ipv4.dns \"\" ipv4.ignore-auto-dns no ipv4.routes \"\" connection.autoconnect yes");
            if (err != null) return err;
        }
        else
        {
            if (cfg.Addresses.Count == 0) return "静态模式至少需要一个 IPv4 地址。";
            var dnsPart = cfg.Dns.Count > 0
                ? $" ipv4.dns {Q(string.Join(",", cfg.Dns))} ipv4.ignore-auto-dns yes"
                : " ipv4.dns \"\" ipv4.ignore-auto-dns no";
            var gwPart = cfg.Gateway.Length > 0 ? $" ipv4.gateway {Q(cfg.Gateway)}" : " ipv4.gateway \"\"";
            var addrs = string.Join(",", cfg.Cidrs);
            var err = await sudoRun(
                $"nmcli connection modify {Q(conName)} ipv4.method manual " +
                $"ipv4.addresses {Q(addrs)}{gwPart}{dnsPart} ipv4.routes \"\" connection.autoconnect yes");
            if (err != null) return err;
        }
        // nmcli up 会短暂断链再恢复
        return await sudoRun($"nmcli --wait 60 connection up {Q(conName)}");
    }

    /// <summary>生成 netplan YAML（支持多个 IPv4、多 DNS、单默认网关）。</summary>
    public static string BuildNetplanYaml(IpConfig cfg, string renderer = "networkd")
    {
        renderer = renderer is "NetworkManager" or "networkd" ? renderer : "networkd";
        var sb = new StringBuilder();
        sb.AppendLine("network:");
        sb.AppendLine("  version: 2");
        sb.AppendLine($"  renderer: {renderer}");
        sb.AppendLine("  ethernets:");
        sb.AppendLine($"    {cfg.Iface}:");
        if (cfg.UseDhcp)
        {
            sb.AppendLine("      dhcp4: true");
        }
        else
        {
            sb.AppendLine("      dhcp4: false");
            sb.AppendLine("      addresses:");
            foreach (var a in cfg.Addresses)
                sb.AppendLine($"        - {a.Cidr}");
            if (cfg.Gateway.Length > 0)
            {
                sb.AppendLine("      routes:");
                sb.AppendLine("        - to: default");
                sb.AppendLine($"          via: {cfg.Gateway}");
            }
            if (cfg.Dns.Count > 0)
            {
                sb.AppendLine("      nameservers:");
                sb.AppendLine("        addresses:");
                foreach (var d in cfg.Dns)
                    sb.AppendLine($"          - {d}");
            }
        }
        return sb.ToString();
    }

    /// <summary>写 netplan 配置并应用（临时文件 → 备份旧文件 → sudo 安装到 /etc/netplan → netplan apply）。</summary>
    public async Task<string?> ApplyViaNetplanAsync(IpConfig cfg, string yaml, Func<string, Task<string?>> sudoRun)
    {
        var tmp = "/tmp/sshmanager-netplan-" + cfg.Iface.Replace("/", "_") + ".yaml";
        var target = $"/etc/netplan/99-sshmanager-{cfg.Iface}.yaml";
        try
        {
            var w = await _s.WriteFileBytesAsync(tmp, Encoding.UTF8.GetBytes(yaml));
            if (w.Status != WriteStatus.Ok)
                return "写入服务器临时文件失败：" + w.Describe();
            // 已有同目标文件先备份；install 原子替换并限定权限，最后应用
            return await sudoRun(
                $"cp -f -- {Q(target)} {Q(target + ".bak")} 2>/dev/null; " +
                $"install -m 600 -- {Q(tmp)} {Q(target)} && netplan apply");
        }
        finally
        {
            try { await _s.RunAsync("rm -f -- " + Q(tmp), TimeSpan.FromSeconds(10)); }
            catch { /* 临时文件清理失败可忽略 */ }
        }
    }

    // ------------------------------------------------------------------ WiFi（依赖 NetworkManager）

    /// <summary>扫描可见 WiFi。依赖 nmcli。</summary>
    public async Task<List<WifiNetwork>> ScanWifiAsync()
    {
        var result = new List<WifiNetwork>();
        var outp = await RunOutAsync(
            "nmcli -t -f IN-USE,SSID,SIGNAL,SECURITY,CHANNEL,DEVICE device wifi list",
            TimeSpan.FromSeconds(40));
        foreach (var raw in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = SplitTerse(raw);
            if (f.Length < 6) continue;
            var ssid = f[1].Length == 0 ? "(隐藏网络)" : f[1];
            result.Add(new WifiNetwork
            {
                InUse = f[0] == "*",
                Ssid = ssid,
                Signal = f[2],
                Security = f[3],
                Channel = f[4],
                Dev = f[5],
            });
        }
        // 同名 SSID 只保留信号最强的
        return result
            .GroupBy(w => w.Ssid)
            .Select(g => g.FirstOrDefault(w => w.InUse)
                         ?? g.OrderByDescending(w => int.TryParse(w.Signal, out var s) ? s : 0).First())
            .OrderByDescending(w => w.InUse)
            .ThenByDescending(w => int.TryParse(w.Signal, out var s2) ? s2 : 0)
            .ToList();
    }

    /// <summary>连接 WiFi（开放网络 password 传 null/空）。</summary>
    public async Task<string?> ConnectWifiAsync(string dev, string ssid, string? password, Func<string, Task<string?>> sudoRun)
    {
        var pwPart = string.IsNullOrEmpty(password) ? "" : $" password {Q(password)}";
        var cmd = $"nmcli device wifi connect {Q(ssid)} ifname {Q(dev)}{pwPart}";
        var err = await sudoRun(cmd);
        if (err != null && (err.Contains("already exists") || err.Contains("已存在")))
        {
            // 已有同名连接：直接激活
            err = await sudoRun($"nmcli connection up {Q(ssid)}");
        }
        return err;
    }

    /// <summary>已保存的 WiFi 连接列表。</summary>
    public async Task<List<WifiConnection>> ListWifiConnectionsAsync()
    {
        var result = new List<WifiConnection>();
        var outp = await RunOutAsync("nmcli -t -f NAME,UUID,DEVICE,TYPE connection show");
        foreach (var raw in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = SplitTerse(raw);
            if (f.Length < 4 || f[3] != "802-11-wireless") continue;
            result.Add(new WifiConnection { Name = f[0], Uuid = f[1], Device = f[2] });
        }
        return result;
    }

    public Task<string?> DisconnectWifiAsync(string dev, Func<string, Task<string?>> sudoRun)
        => sudoRun($"nmcli device disconnect {Q(dev)}");

    public Task<string?> ForgetWifiAsync(string nameOrUuid, Func<string, Task<string?>> sudoRun)
        => sudoRun($"nmcli connection delete {Q(nameOrUuid)}");

    // ------------------------------------------------------------------ 防火墙 UFW

    /// <summary>统一加 LC_ALL=C：中文系统下 ufw 会本地化输出（“允许 入站”），
    /// 解析正则只认英文；强制 C locale 保证任何服务器上输出恒为英文。</summary>
    private const string UfwPrefix = "LC_ALL=C";

    public async Task<UfwStatus> GetUfwAsync()
    {
        var installed = await HasUfwAsync();
        var status = new UfwStatus { Installed = installed };
        if (!installed) return status;

        var verbose = await RunOutAsync($"{UfwPrefix} ufw status verbose");
        if (verbose.Length == 0)
        {
            // 无 sudo 权限时 ufw status 也可能输出为空；尝试只读 status
            verbose = await RunOutAsync($"{UfwPrefix} ufw status");
        }
        foreach (var line in verbose.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("Status:")) status.Active = l.Contains("active", StringComparison.OrdinalIgnoreCase) && !l.Contains("inactive");
            else if (l.StartsWith("Default:"))
            {
                // ufw verbose 实际格式：Default: deny (incoming), allow (outgoing)
                var m = Regex.Match(l, @"Default:\s*(?<in>\w+)\s*\(incoming\),\s*(?<out>\w+)\s*\(outgoing\)");
                if (m.Success)
                {
                    status.DefaultIncoming = m.Groups["in"].Value;
                    status.DefaultOutgoing = m.Groups["out"].Value;
                }
            }
        }

        if (!status.Active) return status;

        var numbered = await RunOutAsync($"{UfwPrefix} ufw status numbered");
        foreach (var raw in numbered.Split('\n'))
        {
            var m = UfwRuleRegex().Match(raw);
            if (!m.Success) continue;
            status.Rules.Add(new UfwRule
            {
                Num = int.Parse(m.Groups["num"].Value),
                To = m.Groups["to"].Value.Trim(),
                Action = m.Groups["act"].Value.Trim(),
                From = m.Groups["from"].Value.Trim(),
            });
        }
        return status;
    }

    public Task<string?> SetUfwEnabledAsync(bool enable, Func<string, Task<string?>> sudoRun)
        => sudoRun(enable ? "ufw --force enable" : "ufw --force disable");

    /// <summary>设置默认入站策略（allow / deny / reject）。</summary>
    public Task<string?> SetUfwDefaultAsync(string incoming, Func<string, Task<string?>> sudoRun)
        => sudoRun($"ufw default {incoming} incoming");

    /// <summary>添加规则。port 为空 = 任意端口；proto: ""(任意)/tcp/udp；from 为空 = 任意来源；
    /// outDirection = 出站规则（ufw allow out …）。</summary>
    public static string? BuildAddRuleCommand(string action, string port, string proto, string from, bool outDirection = false)
    {
        action = action switch { "允许" => "allow", "拒绝" => "deny", "限制" => "limit", _ => action };
        proto = proto switch { "TCP" => "tcp", "UDP" => "udp", _ => "" };
        var dir = outDirection ? " out" : "";
        var sb = new StringBuilder($"ufw {action}{dir}");
        if (!string.IsNullOrWhiteSpace(from))
        {
            sb.Append(" from ").Append(from.Trim());
            if (!string.IsNullOrWhiteSpace(port))
                sb.Append(" to any port ").Append(port.Trim());
            if (proto.Length > 0)
                sb.Append(" proto ").Append(proto);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(port)) return null; // 无来源限制时必须有端口
            sb.Append(' ').Append(port.Trim());
            if (proto.Length > 0)
                sb.Append('/').Append(proto);
        }
        return sb.ToString();
    }

    public Task<string?> AddUfwRuleAsync(string command, Func<string, Task<string?>> sudoRun)
        => sudoRun(command);

    public Task<string?> DeleteUfwRuleAsync(int num, Func<string, Task<string?>> sudoRun)
        => sudoRun($"{UfwPrefix} ufw --force delete {num}");

    /// <summary>修改规则：同一条 shell 命令内先按编号删除旧规则、再添加新规则（避免两次 sudo 间编号漂移）。</summary>
    public Task<string?> ModifyUfwRuleAsync(int num, string addCommand, Func<string, Task<string?>> sudoRun)
        => sudoRun($"{UfwPrefix} ufw --force delete {num} && {addCommand}");

    // ------------------------------------------------------------------ 主机名

    public async Task<string> GetHostnameAsync()
    {
        var h = await RunOutAsync("hostname");
        return h.Trim();
    }

    /// <summary>校验主机名字符集。</summary>
    public static bool IsValidHostname(string name) =>
        Regex.IsMatch(name ?? "", @"^[a-zA-Z0-9][a-zA-Z0-9.-]{0,62}$");

    public Task<string?> SetHostnameAsync(string name, Func<string, Task<string?>> sudoRun)
        => sudoRun($"hostnamectl set-hostname {Q(name)}");

    // ------------------------------------------------------------------ 工具

    /// <summary>Shell 引用别名（UnixPath.ShellQuote）。</summary>
    private static string Q(string s) => UnixPath.ShellQuote(s);

    /// <summary>nmcli -t 输出按未转义的冒号切分，并还原 '\:'。</summary>
    private static string[] SplitTerse(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && (line[i + 1] == ':' || line[i + 1] == '\\'))
            {
                sb.Append(line[i + 1]);
                i++;
            }
            else if (c == ':')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    [GeneratedRegex(@"via\s+(?<via>\S+)\s+.*dev\s+(?<dev>\S+)")]
    private static partial Regex DefaultRouteRegex();

    [GeneratedRegex(@"^\[\s*(?<num>\d+)\]\s+(?<to>.+?)\s{2,}(?<act>ALLOW IN|ALLOW OUT|ALLOW FWD|DENY IN|DENY OUT|DENY FWD|REJECT IN|REJECT OUT|LIMIT IN|LIMIT OUT|ALLOW|DENY|REJECT|LIMIT)\s+(?<from>.+)$")]
    private static partial Regex UfwRuleRegex();
}
