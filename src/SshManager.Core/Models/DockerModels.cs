using System.Text.Json.Serialization;

namespace SshManager.Core.Models;

/// <summary>Docker 环境探测结果。</summary>
public enum DockerState
{
    /// <summary>尚未探测。</summary>
    Unknown,
    /// <summary>服务器上未安装 docker CLI。</summary>
    NotInstalled,
    /// <summary>可用（root 或用户在 docker 组，或 sudo 可用）。</summary>
    Ready,
    /// <summary>已安装但不可用（守护进程未运行 / 权限不可得等）。</summary>
    Failed,
}

public sealed record DockerProbe(DockerState State, string Version, bool Sudo, string? Error)
{
    public static DockerProbe NotInstalled() => new(DockerState.NotInstalled, "", false, null);
    public static DockerProbe Ready(string version, bool sudo) => new(DockerState.Ready, version, sudo, null);
    public static DockerProbe Fail(string error) => new(DockerState.Failed, "", false, error);
}

/// <summary>docker ps -a --format '{{json .}}' 的一项。</summary>
public sealed record DockerContainer
{
    [JsonPropertyName("ID")] public string Id { get; init; } = "";
    [JsonPropertyName("Names")] public string Names { get; init; } = "";
    [JsonPropertyName("Image")] public string Image { get; init; } = "";
    [JsonPropertyName("State")] public string State { get; init; } = "";
    [JsonPropertyName("Status")] public string Status { get; init; } = "";
    [JsonPropertyName("Ports")] public string Ports { get; init; } = "";
    [JsonPropertyName("RunningFor")] public string RunningFor { get; init; } = "";
}

/// <summary>docker images --format '{{json .}}' 的一项。</summary>
public sealed record DockerImage
{
    [JsonPropertyName("ID")] public string Id { get; init; } = "";
    [JsonPropertyName("Repository")] public string Repository { get; init; } = "";
    [JsonPropertyName("Tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("Size")] public string Size { get; init; } = "";
    [JsonPropertyName("CreatedSince")] public string CreatedSince { get; init; } = "";
}

/// <summary>docker stats --no-stream --format '{{json .}}' 的一项。</summary>
public sealed record DockerStats
{
    [JsonPropertyName("ID")] public string Id { get; init; } = "";
    [JsonPropertyName("Container")] public string Container { get; init; } = "";
    [JsonPropertyName("Name")] public string Name { get; init; } = "";
    [JsonPropertyName("CPUPerc")] public string CpuPercent { get; init; } = "";
    [JsonPropertyName("MemUsage")] public string MemUsage { get; init; } = "";
    [JsonPropertyName("MemPerc")] public string MemPercent { get; init; } = "";
}

/// <summary>docker system df --format '{{json .}}' 的一项（Images / Containers / Local Volumes / Build Cache）。</summary>
public sealed record DockerDiskUsage
{
    [JsonPropertyName("Type")] public string Type { get; init; } = "";
    [JsonPropertyName("Total")] public string Total { get; init; } = "";
    [JsonPropertyName("Active")] public string Active { get; init; } = "";
    [JsonPropertyName("Size")] public string Size { get; init; } = "";
    [JsonPropertyName("Reclaimable")] public string Reclaimable { get; init; } = "";
}
