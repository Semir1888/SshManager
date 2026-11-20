using System.Globalization;
using System.Text.RegularExpressions;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>系统概览信息（一次命令取回全部指标）。</summary>
public partial class SystemInfoService(SshSession session)
{
    public async Task<SystemSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var cmd = string.Join("; ", new[]
        {
            "echo @M@OS",
            "grep '^PRETTY_NAME' /etc/os-release 2>/dev/null | cut -d'\"' -f2",
            "echo @M@KERNEL",
            "uname -r",
            "echo @M@ARCH",
            "uname -m",
            "echo @M@HOST",
            "hostname",
            "echo @M@IP",
            "hostname -I 2>/dev/null | awk '{print $1}'",
            "echo @M@UPTIME",
            "LC_ALL=C uptime -p",
            "echo @M@BOOT",
            "uptime -s 2>/dev/null",
            "echo @M@CPU",
            "LC_ALL=C lscpu 2>/dev/null | grep -im1 'model name' | cut -d: -f2-",
            "echo @M@MEM",
            "free -b | awk 'NR==2{print $2\" \"$7}'",
            "echo @M@DISK",
            "LC_ALL=C df -B1 --output=size,used,pcent / 2>/dev/null | tail -n1",
            "echo @M@CPUUSE",
            "LC_ALL=C top -bn1 | sed -n '3p' | grep -oE '[0-9.]+[[:space:]]*id' | head -n1",
            "echo @M@END",
        });

        var res = await session.RunAsync(cmd, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        var snap = new SystemSnapshot();
        var section = "";
        foreach (var raw in res.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("@M@"))
            {
                section = line[3..];
                continue;
            }
            if (line.Length == 0) continue;
            switch (section)
            {
                case "OS": if (snap.Os.Length == 0) snap.Os = line; break;
                case "KERNEL": if (snap.Kernel.Length == 0) snap.Kernel = line; break;
                case "ARCH": if (snap.Arch.Length == 0) snap.Arch = line; break;
                case "HOST": if (snap.Hostname.Length == 0) snap.Hostname = line; break;
                case "IP": if (snap.LanIp.Length == 0) snap.LanIp = line; break;
                case "UPTIME": if (snap.Uptime.Length == 0) snap.Uptime = line; break;
                case "BOOT": if (snap.BootTime.Length == 0) snap.BootTime = line; break;
                case "CPU": if (snap.CpuModel.Length == 0) snap.CpuModel = line; break;
                case "MEM":
                    var m = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (m.Length >= 1 && TryD(m[0], out var total)) snap.MemTotalBytes = total;
                    if (m.Length >= 2 && TryD(m[1], out var avail)) snap.MemAvailableBytes = avail;
                    break;
                case "DISK":
                    var d = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (d.Length >= 1 && TryD(d[0], out var dt)) snap.DiskTotalBytes = dt;
                    if (d.Length >= 2 && TryD(d[1], out var du)) snap.DiskUsedBytes = du;
                    break;
                case "CPUUSE":
                    var match = CpuIdleRegex().Match(line);
                    if (match.Success && TryD(match.Groups[1].Value, out var idle))
                        snap.CpuUsagePercent = Math.Clamp(100.0 - idle, 0, 100);
                    break;
            }
        }
        snap.RetrievedAtUtc = DateTime.UtcNow;
        return snap;
    }

    private static bool TryD(string s, out double v) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v);

    [GeneratedRegex(@"^([0-9.]+)\s*id")]
    private static partial Regex CpuIdleRegex();
}
