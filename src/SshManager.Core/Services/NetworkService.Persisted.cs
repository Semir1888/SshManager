using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>读取持久化 IPv4 配置（netplan / NetworkManager），并分析 netplan 冲突文件。</summary>
public sealed partial class NetworkService
{
    public async Task<PersistentIpConfig> GetPersistedIpConfigAsync(
        NetInterface iface, NetworkEnvironment? env = null, CancellationToken ct = default)
    {
        env ??= await GetEnvironmentAsync();

        PersistentIpConfig? netplanEffective = null;
        if (env.NetplanInstalled)
        {
            var effective = await ReadNetplanEffectiveAsync(env, iface.Name, ct);
            netplanEffective = effective;
            if (effective.Source == IpSourceKind.Netplan)
            {
                var files = await AnalyzeNetplanFilesAsync(iface.Name, env, ct);
                return new PersistentIpConfig
                {
                    Source = IpSourceKind.Netplan,
                    Mode = effective.Mode,
                    Addresses = effective.Addresses,
                    Gateway = effective.Gateway,
                    Dns = effective.Dns,
                    Renderer = effective.Renderer.Length > 0
                        ? effective.Renderer
                        : (iface.NmManaged && env.NmRunning ? "NetworkManager" : "networkd"),
                    PrimaryFile = NetplanTargetPath(iface.Name),
                    SourceFiles = files.SourceFiles,
                    ConflictFiles = files.ConflictFiles,
                    ConflictNeedsTakeover = files.ConflictNeedsTakeover,
                    ConflictComplex = files.ConflictComplex,
                    IsAuthoritative = true,
                    ReadError = files.ReadError.Length > 0
                        ? files.ReadError
                        : (env.NetplanFiles.Count == 0
                            ? "无法列出 netplan 文件（可能需要 sudo）。请点击“读取持久化配置（需要 sudo）”后重试。"
                            : ""),
                };
            }
        }

        if (netplanEffective is { Source: IpSourceKind.Unknown })
        {
            // netplan 存在但读不到：不要改用 nmcli 或运行态冒充持久化
            return new PersistentIpConfig
            {
                Source = IpSourceKind.Unknown,
                PrimaryFile = NetplanTargetPath(iface.Name),
                ReadError = netplanEffective.ReadError.Length > 0
                    ? netplanEffective.ReadError
                    : "无法读取 netplan 持久化配置（可能需要 sudo）。",
                Renderer = iface.NmManaged && env.NmRunning ? "NetworkManager" : "networkd",
            };
        }

        if (env.NmRunning && iface.NmManaged && iface.NmConnection.Length > 0)
        {
            var nm = await ReadNmPersistentAsync(iface.NmConnection, ct);
            if (nm != null) return nm;
        }

        if (env.NetplanInstalled)
        {
            // netplan 存在但尚未定义该网卡：后续可创建 99 文件
            return new PersistentIpConfig
            {
                Source = IpSourceKind.Netplan,
                Mode = IpMode.Dhcp,
                Renderer = iface.NmManaged && env.NmRunning ? "NetworkManager" : "networkd",
                PrimaryFile = NetplanTargetPath(iface.Name),
                IsAuthoritative = false,
                ReadError = env.NetplanFiles.Count == 0
                    ? "无法列出 netplan 文件（可能需要 sudo）。请点击“读取持久化配置（需要 sudo）”后重试。"
                    : "",
            };
        }

        return new PersistentIpConfig
        {
            Source = IpSourceKind.RuntimeOnly,
            Mode = iface.Ipv4Addresses.Count > 0 ? IpMode.Static : IpMode.Dhcp,
            Addresses = iface.Ipv4Addresses,
            Gateway = iface.Gateway,
            Dns = iface.DnsServers,
            IsAuthoritative = false,
        };
    }

    public static string NetplanTargetPath(string iface)
        => $"/etc/netplan/99-sshmanager-{SanitizeIfaceForFile(iface)}.yaml";

    private static string SanitizeIfaceForFile(string iface)
    {
        var s = NetworkService.CleanIfaceName(iface);
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray();
        var result = new string(chars).Trim('_', '.');
        return result.Length == 0 ? "iface" : result;
    }

    /// <summary>
    /// 读取完整的 netplan 配置。
    /// 优先 sudo：非 root 直接读 /etc/netplan/*.yaml（600）时可能“退出码 0 但输出为空/不完整”，
    /// 若仅凭直读结果会误判为“没有 netplan 配置”，从而跳过冲突分析。
    /// 必须用 WrapSudoRaw（WrapSudo 的 2>&1 会污染 YAML）。
    /// </summary>
    internal async Task<string> ReadNetplanYamlAsync(CancellationToken ct = default)
    {
        if (_s.IsRoot || !string.IsNullOrEmpty(_s.SudoPassword))
        {
            try
            {
                var s = await _s.RunAsync(_s.WrapSudoRaw("LC_ALL=C netplan get 2>/dev/null"),
                    TimeSpan.FromSeconds(20), ct);
                if (s.ExitCode == 0 && !string.IsNullOrWhiteSpace(s.Output))
                    return s.Output;
            }
            catch (SudoPasswordRequiredException) { /* 回退直读 */ }
        }

        var r = await _s.RunAsync("LC_ALL=C netplan get 2>/dev/null",
            TimeSpan.FromSeconds(20), ct);
        return r.ExitCode == 0 ? r.Output : "";
    }

    private async Task<PersistentIpConfig> ReadNetplanEffectiveAsync(
        NetworkEnvironment env, string iface, CancellationToken ct)
    {
        if (!env.NetplanInstalled)
            return new PersistentIpConfig { Source = IpSourceKind.None };

        var yaml = await ReadNetplanYamlAsync(ct);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new PersistentIpConfig
            {
                Source = IpSourceKind.Unknown,
                ReadError = "无法读取 netplan 持久化配置（可能需要 sudo）。",
            };
        }

        return NetplanConfig.ParseEffective(yaml, iface);
    }

    private async Task<PersistentIpConfig?> ReadNmPersistentAsync(string connection, CancellationToken ct)
    {
        var method = (await _s.RunAsync(
            $"LC_ALL=C nmcli -g ipv4.method connection show {Q(connection)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var addrs = (await _s.RunAsync(
            $"LC_ALL=C nmcli -g ipv4.addresses connection show {Q(connection)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var gw = (await _s.RunAsync(
            $"LC_ALL=C nmcli -g ipv4.gateway connection show {Q(connection)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var dns = (await _s.RunAsync(
            $"LC_ALL=C nmcli -g ipv4.dns connection show {Q(connection)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var ids = (await _s.RunAsync(
                $"LC_ALL=C nmcli -g connection.uuid,connection.filename connection show {Q(connection)} 2>/dev/null",
                TimeSpan.FromSeconds(15), ct))
            .Output.Split('\n', StringSplitOptions.TrimEntries);
        var uuid = ids.Length >= 1 ? ids[0] : "";
        var file = ids.Length >= 2 ? ids[1] : "";

        if (method.Length == 0 && addrs.Length == 0 && gw.Length == 0 && dns.Length == 0)
            return null;

        return NmcliConfig.FromNmcli(connection, uuid, file, method, addrs, gw, dns);
    }

    private sealed class NetplanAnalysis
    {
        public List<string> SourceFiles { get; } = new();
        public List<string> ConflictFiles { get; } = new();
        public bool ConflictNeedsTakeover { get; set; }
        public bool ConflictComplex { get; set; }
        public string ReadError { get; set; } = "";
    }

    private async Task<NetplanAnalysis> AnalyzeNetplanFilesAsync(
        string iface, NetworkEnvironment env, CancellationToken ct)
    {
        var result = new NetplanAnalysis();
        var targetPath = NetplanTargetPath(iface);
        foreach (var file in env.NetplanFiles)
        {
            // 自己的 99 文件不参与冲突分析，也不需要读取（可能 600 root）
            if (file.Equals(targetPath, StringComparison.Ordinal))
            {
                result.SourceFiles.Add(file);
                continue;
            }

            var content = await ReadTextForAnalysisAsync(file, ct);
            if (content == null)
            {
                if (file.StartsWith("/etc/netplan/", StringComparison.Ordinal))
                    result.ReadError = "部分 netplan 文件无法读取（可能需要 sudo）。";
                else
                    result.ConflictComplex = true; // /lib、/run 下的文件无法读取，保守拒绝
                continue;
            }

            var analysis = NetplanConfig.AnalyzeFile(file, content, iface);
            if (analysis.Error.Length > 0) continue;
            if (!analysis.DefinesInterface) continue;
            if (!result.SourceFiles.Contains(file)) result.SourceFiles.Add(file);

            if (!file.StartsWith("/etc/netplan/", StringComparison.Ordinal))
            {
                result.ConflictComplex = true;
                continue;
            }

            if (analysis.Complex || !analysis.OnlyThisInterface)
                result.ConflictComplex = true;
            else if (!result.ConflictFiles.Contains(file))
                result.ConflictFiles.Add(file);
        }

        result.ConflictNeedsTakeover = result.ConflictFiles.Count > 0 && !result.ConflictComplex;
        return result;
    }

    private async Task<string?> ReadTextForAnalysisAsync(string path, CancellationToken ct)
    {
        var r = await _s.RunAsync($"cat {Q(path)} 2>/dev/null", TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode == 0) return r.Output;
        if (_s.IsRoot || !string.IsNullOrEmpty(_s.SudoPassword))
        {
            try
            {
                // Raw：避免 sudo/netplan 的 stderr 混入 YAML
                var w = await _s.RunAsync(_s.WrapSudoRaw($"cat {Q(path)} 2>/dev/null"),
                    TimeSpan.FromSeconds(15), ct);
                if (w.ExitCode == 0) return w.Output;
            }
            catch (SudoPasswordRequiredException) { /* 只读探测不主动弹窗 */ }
        }
        return null;
    }
}
