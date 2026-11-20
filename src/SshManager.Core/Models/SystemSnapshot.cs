namespace SshManager.Core.Models;

/// <summary>概览页的一次系统快照。</summary>
public sealed class SystemSnapshot
{
    public string Os { get; set; } = "";
    public string Kernel { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string LanIp { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string BootTime { get; set; } = "";
    public string CpuModel { get; set; } = "";

    /// <summary>CPU 使用率（%），null 表示未知。</summary>
    public double? CpuUsagePercent { get; set; }

    public double MemTotalBytes { get; set; }
    public double MemAvailableBytes { get; set; }
    public double DiskTotalBytes { get; set; }
    public double DiskUsedBytes { get; set; }

    public double? MemUsedPercent => MemTotalBytes > 0 ? (MemTotalBytes - MemAvailableBytes) / MemTotalBytes * 100.0 : null;
    public double? DiskUsedPercent => DiskTotalBytes > 0 ? DiskUsedBytes / DiskTotalBytes * 100.0 : null;

    public DateTime RetrievedAtUtc { get; set; } = DateTime.UtcNow;
}
