namespace SshManager.Core.Models;

/// <summary>部署模式（对应离线部署包的四种动作）。</summary>
public enum DeployMode
{
    /// <summary>只读预检：磁盘 / 已有 SSTS / 端口 80 / kiosk 组件。</summary>
    Check,
    /// <summary>全新部署：安装 Docker/snapd/kiosk 并导入镜像与数据库（干净服务器专用）。</summary>
    Full,
    /// <summary>更新应用 / Web / 配置：只处理 update/ 中的内容，不动已装环境。</summary>
    Update,
    /// <summary>部署/更新 kiosk 运行环境：不动应用、容器、数据库。</summary>
    Kiosk,
    /// <summary>自定义命令。</summary>
    Custom,
}

/// <summary>预检条目的状态。</summary>
public enum PreflightState
{
    Ok,
    Warn,
    Bad,
    Info,
}

/// <summary>部署包内一项关键文件/目录的探测结果。</summary>
public sealed record BundleItem(string Name, string Detail, bool Found);

/// <summary>一条服务器预检结果。</summary>
public sealed record PreflightItem(string Name, string Detail, PreflightState State);

/// <summary>部署包目录扫描结果。</summary>
public sealed class BundleScanResult
{
    public bool DirExists { get; init; }

    public string Dir { get; init; } = "";

    public string SizeText { get; set; } = "";

    public string Error { get; set; } = "";

    /// <summary>关键文件/目录探测（remote/、offline/、update/、镜像包等）。</summary>
    public List<BundleItem> Items { get; init; } = new();

    /// <summary>候选入口脚本（相对部署包根目录的路径，按优先级排序）。</summary>
    public List<string> CandidateScripts { get; init; } = new();
}

/// <summary>服务器预检结果（含模式推荐）。</summary>
public sealed class PreflightResult
{
    public List<PreflightItem> Items { get; init; } = new();

    public bool DockerInstalled { get; init; }

    public bool ExistingSsts { get; init; }

    public string ComposeDir { get; init; } = "";

    /// <summary>根据现状推荐的部署模式。</summary>
    public DeployMode RecommendedMode { get; init; }

    /// <summary>推荐理由（一句话）。</summary>
    public string Recommendation { get; init; } = "";
}
