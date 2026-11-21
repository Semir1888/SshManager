using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>网络环境探测：netplan / NetworkManager / cloud-init / ip -j / SSH 服务端地址。</summary>
public sealed partial class NetworkService
{
    private NetworkEnvironment? _envCache;
    private DateTime _envCacheAt;

    /// <summary>探测一次并缓存 60 秒，避免每次刷新重复执行多条命令。</summary>
    public async Task<NetworkEnvironment> GetEnvironmentAsync(bool force = false)
    {
        if (!force && _envCache != null && DateTime.UtcNow - _envCacheAt < TimeSpan.FromSeconds(60))
            return _envCache;

        var netplan = (await RunOutAsync("command -v netplan 2>/dev/null")).Trim().Length > 0;
        var netplanVersion = netplan
            ? (await RunOutAsync("LC_ALL=C netplan --version 2>/dev/null | head -n 1")).Trim()
            : "";
        var netplanGet = false;
        if (netplan)
        {
            var help = await RunOutAsync("LC_ALL=C netplan get --help 2>&1");
            netplanGet = help.Length > 0
                         && !help.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                         && !help.Contains("Invalid command", StringComparison.OrdinalIgnoreCase);
        }

        var nm = (await RunOutAsync("command -v nmcli 2>/dev/null")).Trim().Length > 0;
        var nmRunning = nm && (await RunOutAsync("LC_ALL=C nmcli -t -f RUNNING general 2>/dev/null"))
            .Trim().Equals("running", StringComparison.OrdinalIgnoreCase);

        var cloudInit = (await RunOutAsync("test -e /etc/cloud && echo yes")).Trim().Length > 0;
        var ipJson = (await RunOutAsync("LC_ALL=C ip -j addr show 2>/dev/null")).TrimStart().StartsWith("[");
        const string findCmd =
            "find /etc/netplan /lib/netplan /usr/lib/netplan /run/netplan " +
            "-maxdepth 1 -type f -name '*.yaml' 2>/dev/null | sort -u; true";

        var fileSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in (await RunOutAsync(findCmd))
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            fileSet.Add(f);

        // /etc/netplan 可能是 700/600 root：普通用户 find 会得到空列表，
        // 但 netplan get 经 sudo 能看到配置。文件列表必须同样用 sudo 补全，
        // 否则冲突分析会漏掉旧地址/DNS 的来源文件。
        if (_s.IsRoot || !string.IsNullOrEmpty(_s.SudoPassword))
        {
            try
            {
                var sudoFind = await _s.RunAsync(
                    _s.WrapSudoRaw(findCmd), TimeSpan.FromSeconds(20));
                if (sudoFind.ExitCode == 0)
                {
                    foreach (var f in sudoFind.Output
                                 .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        fileSet.Add(f);
                }
            }
            catch (SudoPasswordRequiredException) { /* 回退直接 find 的结果 */ }
        }

        var files = fileSet.OrderBy(f => f, StringComparer.Ordinal).ToList();

        // $SSH_CONNECTION: client_ip client_port server_ip server_port
        var sshConn = (await RunOutAsync("printf '%s' \"${SSH_CONNECTION:-}\"")).Trim();
        var sshServerIp = "";
        if (sshConn.Length > 0)
        {
            var parts = sshConn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && IpValidation.TryParseIPv4Strict(parts[2], out var ip))
                sshServerIp = ip;
        }

        _envCache = new NetworkEnvironment
        {
            NetplanInstalled = netplan,
            NetplanGetSupported = netplanGet,
            NetplanVersion = netplanVersion,
            NmInstalled = nm,
            NmRunning = nmRunning,
            CloudInitPresent = cloudInit,
            IpJsonSupported = ipJson,
            NetplanFiles = files,
            SshServerIp = sshServerIp,
        };
        _envCacheAt = DateTime.UtcNow;
        return _envCache;
    }
}
