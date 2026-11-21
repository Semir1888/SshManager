namespace SshManager.Core.Models;

/// <summary>Docker Compose 探测状态。</summary>
public enum ComposeState
{
    Unknown,
    Probing,
    Ready,
    NotInstalled,
    Failed,
}

/// <summary>Compose 命令形态：v2 插件（docker compose）/ v1 独立二进制（docker-compose）。</summary>
public enum ComposeKind
{
    None,
    V1,
    V2,
}

/// <summary>项目整体运行状态。</summary>
public enum ComposeProjectState
{
    Unknown,
    Running,
    Partial,
    Stopped,
    ConfigMissing,
    Missing,
}

/// <summary>一键升级时使用的升级方式。</summary>
public enum ComposeUpgradeMode
{
    /// <summary>有 build 配置则 --build，否则文件有变化时强制重建。</summary>
    Auto,
    Build,
    Up,
    PullUp,
    Custom,
}

/// <summary>本地文件与远程文件的比对结果。</summary>
public enum ComposeChangeKind
{
    Added,
    Modified,
    Unchanged,
    RemoteOnly,
}

/// <summary>一次 Compose 探测结果（预期失败通过 State/Error 返回，不抛业务异常）。</summary>
public sealed record ComposeProbe(
    ComposeState State,
    ComposeKind Kind,
    string Version,
    bool Sudo,
    string? Error)
{
    public static ComposeProbe NotInstalled() => new(ComposeState.NotInstalled, ComposeKind.None, "", false, null);

    public static ComposeProbe Ready(string version, ComposeKind kind, bool sudo)
        => new(ComposeState.Ready, kind, version, sudo, null);

    public static ComposeProbe Fail(string error)
        => new(ComposeState.Failed, ComposeKind.None, "", false, error);
}

/// <summary>定位一个 compose 项目所需的参数（命令拼装用）。</summary>
public sealed class ComposeProjectRef
{
    public string Name { get; init; } = "";
    public string WorkingDir { get; init; } = "";
    public IReadOnlyList<string> ComposeFiles { get; init; } = Array.Empty<string>();

    public static ComposeProjectRef FromConfig(ComposeProjectConfig config) => new()
    {
        Name = config.ProjectName,
        WorkingDir = config.RemoteDir,
        ComposeFiles = config.ComposeFiles.ToList(),
    };
}

/// <summary>服务器上发现的一个 compose 项目。</summary>
public sealed class ComposeProjectInfo
{
    public ComposeProjectRef Ref { get; init; } = new();
    public string StatusText { get; init; } = "";
    public ComposeProjectState State { get; init; } = ComposeProjectState.Unknown;
    public int RunningCount { get; init; }
    public int TotalCount { get; init; }
    public bool Manual { get; init; }
    public string? Error { get; init; }
}

/// <summary>一个 compose 服务的容器信息。</summary>
public sealed class ComposeContainerInfo
{
    public string Project { get; init; } = "";
    public string Service { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string Status { get; init; } = "";
    public string Health { get; init; } = "";
    public string Image { get; init; } = "";
    public string Ports { get; init; } = "";
}

/// <summary>项目详情：配置中的服务 + 实际容器。</summary>
public sealed class ComposeProjectDetail
{
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ComposeContainerInfo> Containers { get; init; } = Array.Empty<ComposeContainerInfo>();
    public string? Error { get; init; }
}

/// <summary>项目发现结果。</summary>
public sealed class ComposeListResult
{
    public IReadOnlyList<ComposeProjectInfo> Projects { get; init; } = Array.Empty<ComposeProjectInfo>();
    public string? Error { get; init; }

    public static ComposeListResult Fail(string error) => new() { Error = error };
}

/// <summary>默认排除规则（可在项目设置中编辑）。</summary>
public static class ComposeDefaults
{
    public static readonly IReadOnlyList<string> ExcludePatterns = new[]
    {
        ".git", ".svn", ".idea", ".vscode", "node_modules", ".gradle", ".mvn",
        "__pycache__", ".DS_Store", "Thumbs.db", "*.log", "*.tmp", ".sshmanager-backups",
    };

    public static List<string> NewExcludes() => ExcludePatterns.ToList();
}

/// <summary>每个服务器上的 compose 项目绑定（持久化到 compose.json）。</summary>
public sealed class ComposeProjectConfig
{
    public string ProjectName { get; set; } = "";
    public bool Manual { get; set; }
    public string RemoteDir { get; set; } = "";
    public List<string> ComposeFiles { get; set; } = new();
    public string LocalDir { get; set; } = "";
    public List<string> ExcludePatterns { get; set; } = ComposeDefaults.NewExcludes();
    public ComposeUpgradeMode Mode { get; set; } = ComposeUpgradeMode.Auto;
    public string CustomCommand { get; set; } = "";
    /// <summary>文件有变化时强制重建容器（挂载 jar/配置时通常需要），默认开启。</summary>
    public bool ForceRecreate { get; set; } = true;
    public bool BackupEnabled { get; set; } = true;
    public int BackupKeep { get; set; } = 5;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ComposeProjectConfig Clone() => new()
    {
        ProjectName = ProjectName,
        Manual = Manual,
        RemoteDir = RemoteDir,
        ComposeFiles = ComposeFiles.ToList(),
        LocalDir = LocalDir,
        ExcludePatterns = ExcludePatterns.ToList(),
        Mode = Mode,
        CustomCommand = CustomCommand,
        ForceRecreate = ForceRecreate,
        BackupEnabled = BackupEnabled,
        BackupKeep = BackupKeep,
        UpdatedAtUtc = UpdatedAtUtc,
    };
}

/// <summary>临时选择的上传文件（不持久化）。</summary>
public sealed class ComposeExtraFile
{
    public string LocalPath { get; init; } = "";
    /// <summary>相对远程项目根目录的路径（统一使用 /）。</summary>
    public string RemoteRelativePath { get; init; } = "";
}

/// <summary>本地与远程文件的一次比对结果。</summary>
public sealed class ComposeFileChange
{
    public string RelativePath { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public string RemotePath { get; init; } = "";
    public long LocalSize { get; init; }
    public long RemoteSize { get; init; }
    public bool RemoteExists { get; init; }
    public ComposeChangeKind Kind { get; set; }
}

/// <summary>升级计划（比对结果 + 将执行的命令 + 备份位置）。</summary>
public sealed class ComposeDeployPlan
{
    public IReadOnlyList<ComposeFileChange> Files { get; init; } = Array.Empty<ComposeFileChange>();
    public bool BuildDetected { get; init; }
    public string CommandPreview { get; init; } = "";
    public string BackupPreview { get; init; } = "";
    public string RemoteDir { get; init; } = "";
    public bool RemoteDirExists { get; init; }
    public bool RemoteDirWritable { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int AddedCount => Files.Count(f => f.Kind == ComposeChangeKind.Added);
    public int ModifiedCount => Files.Count(f => f.Kind == ComposeChangeKind.Modified);
    public int UnchangedCount => Files.Count(f => f.Kind == ComposeChangeKind.Unchanged);
    public int RemoteOnlyCount => Files.Count(f => f.Kind == ComposeChangeKind.RemoteOnly);
}

/// <summary>扫描计划的结果；预期失败通过 Ok=false + Message 返回。</summary>
public sealed class ComposePlanResult
{
    public bool Ok { get; init; }
    public string? Message { get; init; }
    public ComposeDeployPlan? Plan { get; init; }

    public static ComposePlanResult Fail(string message) => new() { Ok = false, Message = message };
    public static ComposePlanResult Success(ComposeDeployPlan plan) => new() { Ok = true, Plan = plan };
}

/// <summary>一次升级请求。</summary>
public sealed class ComposeDeployRequest
{
    public ComposeProjectRef Project { get; init; } = new();
    public ComposeProjectConfig Config { get; init; } = new();
    public IReadOnlyList<ComposeExtraFile> ExtraFiles { get; init; } = Array.Empty<ComposeExtraFile>();
}

/// <summary>部署进度（由后台线程经 Progress&lt;T&gt; 回报到 UI 线程）。</summary>
public sealed record ComposeDeployProgress(
    string Stage,
    string Message,
    int FileIndex,
    int FileCount,
    long BytesDone,
    long TotalBytes,
    double Percent);

/// <summary>部署/回滚后的健康检查摘要。</summary>
public sealed class ComposeHealthInfo
{
    public int Total { get; init; }
    public int Running { get; init; }
    public int Exited { get; init; }
    public int Unhealthy { get; init; }
    public string? Error { get; init; }

    public string Summary
    {
        get
        {
            if (Error != null) return "健康检查失败：" + Error;
            if (Total == 0) return "未发现容器（可能尚未创建）";
            var text = $"容器 {Running}/{Total} 运行中";
            if (Exited > 0) text += $" · {Exited} 个未运行";
            if (Unhealthy > 0) text += $" · {Unhealthy} 个不健康";
            return text;
        }
    }
}

/// <summary>上传 + 校验 + 执行后的结果。</summary>
public sealed class ComposeApplyResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? BackupPath { get; init; }
    public string? Error { get; init; }
    public ComposeHealthInfo Health { get; init; } = new();
    public bool HasHealthWarning => Health.Error != null || (Health.Total > 0 && Health.Running == 0);
}

/// <summary>服务器端的一份备份归档。</summary>
public sealed class ComposeBackupInfo
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }
}

public sealed class ComposeBackupListResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ComposeBackupInfo> Items { get; init; } = Array.Empty<ComposeBackupInfo>();
}

public sealed class ComposeBackupActionResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
}
