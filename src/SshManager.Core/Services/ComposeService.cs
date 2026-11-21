using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>
/// Docker Compose 管理：探测、项目发现、项目详情、生命周期命令与日志。
/// 所有命令通过 SshSession 执行；Docker 守护进程权限不足时自动降级 sudo。
/// 预期失败通过结果对象返回，不抛业务异常（sudo 缺密码除外，由 UI 补密后重试）。
/// </summary>
public sealed class ComposeService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SshSession _session;

    private ComposeKind _kind = ComposeKind.None;
    private string _version = "";
    private bool _needsSudo;

    public ComposeService(SshSession session) => _session = session;

    public ComposeKind Kind => _kind;
    public string Version => _version;
    public bool NeedsSudo => _needsSudo;

    // ---------------------------------------------------------------- 探测

    public async Task<ComposeProbe> ProbeAsync(CancellationToken ct = default)
    {
        _kind = ComposeKind.None;
        _version = "";

        var v2 = await _session.RunAsync(
            "LC_ALL=C docker compose version --short 2>&1", TimeSpan.FromSeconds(15), ct);
        var v2Text = Clean(v2.Output + " " + v2.Error);

        if (v2.ExitCode == 0 && v2Text.Length > 0 && !IsComposeCommandMissing(v2Text))
        {
            _kind = ComposeKind.V2;
            var m = Regex.Match(v2Text, @"(\d+\.\d+\.\d+)");
            _version = m.Success ? m.Groups[1].Value : FirstLine(v2Text);
        }
        else
        {
            var v1 = await _session.RunAsync(
                "LC_ALL=C docker-compose version --short 2>&1", TimeSpan.FromSeconds(15), ct);
            var v1Text = Clean(v1.Output + " " + v1.Error);

            if (v1.ExitCode == 0 && v1Text.Length > 0 && !IsCommandMissing(v1Text))
            {
                _kind = ComposeKind.V1;
                var m = Regex.Match(v1Text, @"(\d+\.\d+\.\d+)");
                _version = m.Success ? m.Groups[1].Value : FirstLine(v1Text);
            }
            else
            {
                var v1b = await _session.RunAsync(
                    "LC_ALL=C docker-compose version 2>&1", TimeSpan.FromSeconds(15), ct);
                var v1bText = Clean(v1b.Output + " " + v1b.Error);
                if (v1b.ExitCode == 0 && v1bText.Contains("docker-compose version", StringComparison.OrdinalIgnoreCase))
                {
                    _kind = ComposeKind.V1;
                    var m = Regex.Match(v1bText, @"(\d+\.\d+\.\d+)");
                    _version = m.Success ? m.Groups[1].Value : FirstLine(v1bText);
                }
            }
        }

        if (_kind == ComposeKind.None)
        {
            var all = v2Text;
            if (IsComposeCommandMissing(all) || v2.ExitCode == 127)
                return ComposeProbe.NotInstalled();
            var v1Check = await _session.RunAsync(
                "LC_ALL=C command -v docker-compose 2>&1", TimeSpan.FromSeconds(10), ct);
            if (v1Check.ExitCode != 0)
                return ComposeProbe.NotInstalled();
            return ComposeProbe.Fail("未找到可用的 Docker Compose：" + FirstLine(all));
        }

        // 守护进程 / 权限探测：docker info 需要访问 socket。
        var infoCmd = "LC_ALL=C docker info --format '{{.ServerVersion}}' 2>&1";
        var info = await _session.RunAsync(
            _needsSudo ? _session.WrapSudo(infoCmd) : infoCmd,
            TimeSpan.FromSeconds(20), ct);
        var infoText = Clean(info.Output + " " + info.Error);

        if (info.ExitCode != 0 && !_needsSudo && IsPermissionDenied(info))
        {
            _needsSudo = true;
            info = await _session.RunAsync(_session.WrapSudo(infoCmd), TimeSpan.FromSeconds(20), ct);
            infoText = Clean(info.Output + " " + info.Error);
        }

        if (info.ExitCode == 0)
            return ComposeProbe.Ready(_version, _kind, _needsSudo);

        if (infoText.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || infoText.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase))
        {
            return ComposeProbe.Fail("Docker 守护进程未运行（systemctl start docker）。");
        }
        if (IsPermissionDenied(info))
            return ComposeProbe.Fail("当前用户无权访问 Docker（可将用户加入 docker 组，或提供 sudo 密码）。");
        return ComposeProbe.Fail("Compose 不可用：" + FirstLine(infoText));
    }

    // ---------------------------------------------------------------- 项目发现

    /// <summary>列出服务器上发现的 compose 项目（v2 用 ls，v1/旧版用容器 labels 回退）。</summary>
    public async Task<ComposeListResult> ListProjectsAsync(CancellationToken ct = default)
    {
        if (_kind == ComposeKind.None)
            return ComposeListResult.Fail("尚未检测到 Docker Compose，请先点“重新检测”。");

        try
        {
            string lsOutput = "";
            if (_kind == ComposeKind.V2)
            {
                var ls = await RunComposeAsync("ls -a --format json", TimeSpan.FromSeconds(30), ct, raw: true);
                if (ls.ExitCode == 0 || LooksLikeJson(ls.Output))
                    lsOutput = ls.Output;
            }

            var ps = await _session.RunAsync(
                DockerCmd(PsLabelFormat), TimeSpan.FromSeconds(30), ct);
            if (!ps.Success && IsPermissionDenied(ps) && !_needsSudo)
            {
                _needsSudo = true;
                ps = await _session.RunAsync(DockerCmd(PsLabelFormat), TimeSpan.FromSeconds(30), ct);
            }
            if (!ps.Success && ps.ExitCode != 0 && lsOutput.Length == 0)
                return ComposeListResult.Fail("读取 compose 项目失败：" + FirstLine(ps.Combined));

            var lsItems = ParseComposeLs(lsOutput);
            var rows = ParsePsRows(ps.Output);

            return new ComposeListResult { Projects = MergeProjects(lsItems, rows) };
        }
        catch (SudoPasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return ComposeListResult.Fail("读取 compose 项目失败：" + ex.Message);
        }
    }

    private sealed class ComposeLsItem
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string ConfigFiles { get; set; } = "";
    }

    private sealed class ProjectBuilder
    {
        public string Name { get; init; } = "";
        public string WorkingDir { get; set; } = "";
        /// <summary>原始 config_files 标签值（可能含逗号分隔的多个文件），等 working_dir 明确后再拆分。</summary>
        public List<string> RawConfigFiles { get; } = new();
        public string LsStatus { get; set; } = "";
        public int Running { get; set; }
        public int Total { get; set; }
    }

    private static List<ComposeLsItem> ParseComposeLs(string output)
    {
        var list = new List<ComposeLsItem>();
        var text = (output ?? "").Trim();
        if (text.Length == 0) return list;

        var start = text.IndexOf('[');
        if (start >= 0)
        {
            var json = text[start..];
            try
            {
                var items = JsonSerializer.Deserialize<List<ComposeLsItem>>(json, JsonOpts);
                if (items != null) return items;
            }
            catch (JsonException) { /* 回退逐行 */ }
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                var item = JsonSerializer.Deserialize<ComposeLsItem>(line, JsonOpts);
                if (item != null) list.Add(item);
            }
            catch (JsonException) { /* 忽略警告行 */ }
        }
        return list;
    }

    private static IReadOnlyList<ComposeProjectInfo> MergeProjects(
        IReadOnlyList<ComposeLsItem> lsItems,
        IReadOnlyList<PsRow> rows)
    {
        var map = new Dictionary<string, ProjectBuilder>(StringComparer.Ordinal);

        ProjectBuilder Get(string name)
        {
            if (!map.TryGetValue(name, out var b))
            {
                b = new ProjectBuilder { Name = name };
                map[name] = b;
            }
            return b;
        }

        foreach (var item in lsItems)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            var b = Get(item.Name.Trim());
            b.LsStatus = item.Status ?? "";
            if (!string.IsNullOrWhiteSpace(item.ConfigFiles)
                && !b.RawConfigFiles.Contains(item.ConfigFiles))
                b.RawConfigFiles.Add(item.ConfigFiles);
        }

        foreach (var row in rows)
        {
            var b = Get(row.Project);
            if (b.WorkingDir.Length == 0 && row.WorkingDir.Length > 0)
                b.WorkingDir = row.WorkingDir;
            if (!string.IsNullOrWhiteSpace(row.ConfigFiles)
                && !b.RawConfigFiles.Contains(row.ConfigFiles))
                b.RawConfigFiles.Add(row.ConfigFiles);
            b.Total++;
            if (row.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                b.Running++;
        }

        var result = new List<ComposeProjectInfo>();
        foreach (var b in map.Values)
        {
            var workDir = b.WorkingDir;

            // working_dir 可能来自容器标签；拿到后再拆分相对路径
            var files = new List<string>();
            foreach (var raw in b.RawConfigFiles)
            {
                foreach (var f in SplitConfigFiles(raw, workDir))
                    if (!files.Contains(f)) files.Add(f);
            }
            if (workDir.Length == 0 && files.Count > 0)
                workDir = UnixPath.Parent(files[0]);
            if (workDir.Length == 0) workDir = "/";
            for (int i = 0; i < files.Count; i++)
                files[i] = UnixPath.Normalize(files[i]);

            ComposeProjectState state;
            if (files.Count == 0)
                state = ComposeProjectState.ConfigMissing;
            else if (b.Total > 0)
                state = b.Running == 0
                    ? ComposeProjectState.Stopped
                    : b.Running == b.Total
                        ? ComposeProjectState.Running
                        : ComposeProjectState.Partial;
            else
                state = ParseLsState(b.LsStatus);

            var statusText = b.LsStatus.Length > 0
                ? b.LsStatus
                : b.Total > 0
                    ? $"{b.Running}/{b.Total} 运行中"
                    : "未运行";

            result.Add(new ComposeProjectInfo
            {
                Ref = new ComposeProjectRef
                {
                    Name = b.Name,
                    WorkingDir = workDir,
                    ComposeFiles = files,
                },
                StatusText = statusText,
                State = state,
                RunningCount = b.Running,
                TotalCount = b.Total,
            });
        }

        return result.OrderBy(p => p.Ref.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ComposeProjectState ParseLsState(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return ComposeProjectState.Unknown;
        var matches = Regex.Matches(status, @"([a-zA-Z]+)\((\d+)\)");
        int running = 0, total = 0;
        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups[2].Value, out var n)) continue;
            total += n;
            if (m.Groups[1].Value.Equals("running", StringComparison.OrdinalIgnoreCase))
                running += n;
        }
        if (total == 0) return ComposeProjectState.Unknown;
        return running == 0
            ? ComposeProjectState.Stopped
            : running == total
                ? ComposeProjectState.Running
                : ComposeProjectState.Partial;
    }

    // ---------------------------------------------------------------- 项目详情

    public async Task<ComposeProjectDetail> GetDetailAsync(
        ComposeProjectRef project, CancellationToken ct = default)
    {
        try
        {
            var services = new List<string>();
            var cfg = await RunComposeAsync(
                $"{ProjectArgs(project)}config --services", TimeSpan.FromMinutes(1), ct, raw: true);
            if (cfg.ExitCode == 0)
            {
                foreach (var raw in cfg.Output.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!services.Contains(line)) services.Add(line);
                }
            }

            var ps = await RunComposeAsync(
                $"{ProjectArgs(project)}ps -a --format json", TimeSpan.FromMinutes(2), ct, raw: true);
            var containers = ParseComposePs(ps.Output);

            // ps 失败或旧版不支持 JSON 时，用 docker ps 的项目标签回退
            if (containers.Count == 0)
                containers = (await ListContainersByLabelAsync(project.Name, ct)).ToList();

            // 用 docker ps 补齐 compose ps 可能缺失的镜像/端口
            if (containers.Count > 0)
            {
                var labelRows = await ReadPsRowsAsync(ct);
                var byName = labelRows
                    .GroupBy(r => r.Names, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                for (int i = 0; i < containers.Count; i++)
                {
                    var c = containers[i];
                    if (byName.TryGetValue(c.Name, out var row))
                    {
                        containers[i] = new ComposeContainerInfo
                        {
                            Project = c.Project,
                            Service = c.Service.Length > 0 ? c.Service : row.Service,
                            Name = c.Name,
                            State = c.State.Length > 0 ? c.State : row.State,
                            Status = c.Status.Length > 0 ? c.Status : row.Status,
                            Health = c.Health.Length > 0 ? c.Health : HealthFromStatus(row.Status),
                            Image = c.Image.Length > 0 ? c.Image : row.Image,
                            Ports = c.Ports.Length > 0 ? c.Ports : row.Ports,
                        };
                    }
                }
            }

            var error = cfg.ExitCode != 0 && containers.Count == 0
                ? FirstLine(cfg.Combined)
                : null;

            return new ComposeProjectDetail
            {
                Services = services,
                Containers = containers,
                Error = string.IsNullOrWhiteSpace(error) ? null : error,
            };
        }
        catch (SudoPasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return new ComposeProjectDetail { Error = ex.Message };
        }
    }

    private sealed class ComposePsItem
    {
        [JsonPropertyName("Name")] public string Name { get; set; } = "";
        [JsonPropertyName("Service")] public string Service { get; set; } = "";
        [JsonPropertyName("State")] public string State { get; set; } = "";
        [JsonPropertyName("Status")] public string Status { get; set; } = "";
        [JsonPropertyName("Health")] public string Health { get; set; } = "";
        [JsonPropertyName("Image")] public string Image { get; set; } = "";
        [JsonPropertyName("Publishers")] public JsonElement Publishers { get; set; }
    }

    private static List<ComposeContainerInfo> ParseComposePs(string output)
    {
        var list = new List<ComposeContainerInfo>();
        var text = (output ?? "").Trim();
        if (text.Length == 0) return list;

        List<ComposePsItem>? items = null;
        var start = text.IndexOf('[');
        if (start >= 0)
        {
            try { items = JsonSerializer.Deserialize<List<ComposePsItem>>(text[start..], JsonOpts); }
            catch (JsonException) { items = null; }
        }

        if (items == null)
        {
            items = new List<ComposePsItem>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    var item = JsonSerializer.Deserialize<ComposePsItem>(line, JsonOpts);
                    if (item != null) items.Add(item);
                }
                catch (JsonException) { /* 忽略 */ }
            }
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name) && string.IsNullOrWhiteSpace(item.Service))
                continue;
            var health = item.Health;
            if (string.IsNullOrWhiteSpace(health)) health = HealthFromStatus(item.Status);
            list.Add(new ComposeContainerInfo
            {
                Service = item.Service,
                Name = item.Name,
                State = item.State,
                Status = item.Status,
                Health = health,
                Image = item.Image,
                Ports = FormatPublishers(item.Publishers),
            });
        }
        return list;
    }

    private static string FormatPublishers(JsonElement publishers)
    {
        if (publishers.ValueKind != JsonValueKind.Array) return "";
        var list = new List<string>();
        foreach (var p in publishers.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            string Get(string name)
                => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "")
                    : "";

            var target = Get("TargetPort");
            var published = Get("PublishedPort");
            if (target.Length == 0 || published.Length == 0 || published == "0") continue;
            var url = Get("URL");
            var proto = Get("Protocol");
            var host = url.Length > 0 && url != "0.0.0.0" && url != "::" ? url + ":" : "";
            list.Add($"{host}{published}->{target}/{proto}");
        }
        return string.Join(", ", list);
    }

    private async Task<IReadOnlyList<ComposeContainerInfo>> ListContainersByLabelAsync(
        string projectName, CancellationToken ct)
    {
        var rows = await ReadPsRowsAsync(ct);
        var list = new List<ComposeContainerInfo>();
        foreach (var row in rows)
        {
            if (!row.Project.Equals(projectName, StringComparison.Ordinal)) continue;
            list.Add(new ComposeContainerInfo
            {
                Project = row.Project,
                Service = row.Service,
                Name = FirstName(row.Names),
                State = row.State,
                Status = row.Status,
                Health = HealthFromStatus(row.Status),
                Image = row.Image,
                Ports = row.Ports,
            });
        }
        return list;
    }

    private sealed record PsRow(
        string Id, string Names, string Image, string State, string Status, string Ports,
        string Project, string Service, string ConfigFiles, string WorkingDir);

    private const string PsLabelFormat =
        "ps -a --format '{{.ID}}|{{.Names}}|{{.Image}}|{{.State}}|{{.Status}}|{{.Ports}}|" +
        "{{.Label \"com.docker.compose.project\"}}|{{.Label \"com.docker.compose.service\"}}|" +
        "{{.Label \"com.docker.compose.project.config_files\"}}|" +
        "{{.Label \"com.docker.compose.project.working_dir\"}}'";

    private async Task<IReadOnlyList<PsRow>> ReadPsRowsAsync(CancellationToken ct)
    {
        var r = await _session.RunAsync(DockerCmd(PsLabelFormat), TimeSpan.FromSeconds(30), ct);
        if (!r.Success && IsPermissionDenied(r) && !_needsSudo)
        {
            _needsSudo = true;
            r = await _session.RunAsync(DockerCmd(PsLabelFormat), TimeSpan.FromSeconds(30), ct);
        }
        return ParsePsRows(r.Output);
    }

    private static List<PsRow> ParsePsRows(string output)
    {
        var list = new List<PsRow>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 10) continue;
            // 端口等字段理论上不含 |；若出现则把中间多出的段归入 Ports
            var ports = parts.Length > 10
                ? string.Join("|", parts.Skip(5).Take(parts.Length - 9))
                : parts[5];
            var row = new PsRow(
                parts[0], parts[1], parts[2], parts[3], parts[4], ports,
                parts[^4], parts[^3], parts[^2], parts[^1]);
            if (row.Project.Length == 0) continue;
            list.Add(row);
        }
        return list;
    }

    private static string FirstName(string names)
    {
        var i = names.IndexOf(',');
        return i >= 0 ? names[..i].Trim() : names.Trim();
    }

    private static string HealthFromStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "";
        if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase)) return "unhealthy";
        if (status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase)) return "healthy";
        if (status.Contains("health: starting", StringComparison.OrdinalIgnoreCase)) return "starting";
        return "";
    }

    private static List<string> SplitConfigFiles(string text, string workingDir)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split(','))
        {
            var f = raw.Trim();
            if (f.Length == 0) continue;
            if (!f.StartsWith('/') && !string.IsNullOrEmpty(workingDir))
                f = UnixPath.Combine(workingDir, f);
            f = UnixPath.Normalize(f);
            if (!list.Contains(f)) list.Add(f);
        }
        return list;
    }

    // ---------------------------------------------------------------- 生命周期命令

    public Task<CommandResult> UpAsync(
        ComposeProjectRef project, bool build, bool forceRecreate, CancellationToken ct = default)
    {
        var args = "up -d --remove-orphans"
                   + (build ? " --build" : "")
                   + (forceRecreate ? " --force-recreate" : "");
        return RunComposeAsync($"{ProjectArgs(project)}{args}", TimeSpan.FromMinutes(60), ct);
    }

    public Task<CommandResult> UpStreamingAsync(
        ComposeProjectRef project, bool build, bool forceRecreate,
        Action<string> onLine, CancellationToken ct = default)
    {
        var args = "up -d --remove-orphans"
                   + (build ? " --build" : "")
                   + (forceRecreate ? " --force-recreate" : "");
        return RunStreamingComposeAsync(
            $"{ProjectArgs(project)}{args}", onLine, TimeSpan.FromMinutes(60), ct);
    }

    public Task<CommandResult> DownAsync(ComposeProjectRef project, CancellationToken ct = default)
        => RunComposeAsync($"{ProjectArgs(project)}down --remove-orphans",
            TimeSpan.FromMinutes(10), ct);

    public Task<CommandResult> DownStreamingAsync(
        ComposeProjectRef project, Action<string> onLine, CancellationToken ct = default)
        => RunStreamingComposeAsync(
            $"{ProjectArgs(project)}down --remove-orphans", onLine, TimeSpan.FromMinutes(10), ct);

    public Task<CommandResult> PullStreamingAsync(
        ComposeProjectRef project, string? service, Action<string> onLine, CancellationToken ct = default)
        => RunStreamingComposeAsync(
            $"{ProjectArgs(project)}pull --ignore-pull-failures" +
            (string.IsNullOrWhiteSpace(service) ? "" : " " + UnixPath.ShellQuote(service)),
            onLine, TimeSpan.FromMinutes(60), ct);

    public Task<CommandResult> BuildStreamingAsync(
        ComposeProjectRef project, string? service, Action<string> onLine, CancellationToken ct = default)
        => RunStreamingComposeAsync(
            $"{ProjectArgs(project)}build" +
            (string.IsNullOrWhiteSpace(service) ? "" : " " + UnixPath.ShellQuote(service)),
            onLine, TimeSpan.FromMinutes(60), ct);

    public Task<CommandResult> RecreateServiceStreamingAsync(
        ComposeProjectRef project, string service, Action<string> onLine, CancellationToken ct = default)
        => RunStreamingComposeAsync(
            $"{ProjectArgs(project)}up -d --force-recreate --no-deps {UnixPath.ShellQuote(service)}",
            onLine, TimeSpan.FromMinutes(30), ct);

    public Task<CommandResult> StopAsync(
        ComposeProjectRef project, string? service, CancellationToken ct = default)
        => RunComposeAsync(
            $"{ProjectArgs(project)}stop{(string.IsNullOrWhiteSpace(service) ? "" : " " + UnixPath.ShellQuote(service))}",
            TimeSpan.FromMinutes(5), ct);

    public Task<CommandResult> RestartAsync(
        ComposeProjectRef project, string? service, CancellationToken ct = default)
        => RunComposeAsync(
            $"{ProjectArgs(project)}restart{(string.IsNullOrWhiteSpace(service) ? "" : " " + UnixPath.ShellQuote(service))}",
            TimeSpan.FromMinutes(5), ct);

    public Task<CommandResult> PullAsync(
        ComposeProjectRef project, string? service, CancellationToken ct = default)
        => RunComposeAsync(
            $"{ProjectArgs(project)}pull --ignore-pull-failures{(string.IsNullOrWhiteSpace(service) ? "" : " " + UnixPath.ShellQuote(service))}",
            TimeSpan.FromMinutes(60), ct);

    public Task<CommandResult> BuildAsync(
        ComposeProjectRef project, string? service, CancellationToken ct = default)
        => RunComposeAsync(
            $"{ProjectArgs(project)}build{(string.IsNullOrWhiteSpace(service) ? "" : " " + UnixPath.ShellQuote(service))}",
            TimeSpan.FromMinutes(60), ct);

    public Task<CommandResult> RecreateServiceAsync(
        ComposeProjectRef project, string service, CancellationToken ct = default)
        => RunComposeAsync(
            $"{ProjectArgs(project)}up -d --force-recreate --no-deps {UnixPath.ShellQuote(service)}",
            TimeSpan.FromMinutes(30), ct);

    public Task<CommandResult> ValidateAsync(ComposeProjectRef project, CancellationToken ct = default)
        => RunComposeAsync($"{ProjectArgs(project)}config -q", TimeSpan.FromMinutes(2), ct);

    /// <summary>项目聚合日志或单服务日志；follow=true 时保持流式输出。</summary>
    public Task<CommandResult> LogsAsync(
        ComposeProjectRef project,
        string? service,
        int tail,
        bool follow,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        var args = $"logs --no-color --tail {Math.Clamp(tail, 1, 50000)}"
                   + (follow ? " --follow" : "")
                   + (string.IsNullOrWhiteSpace(service) ? "" : " " + UnixPath.ShellQuote(service));
        var cmd = Cmd($"{ProjectArgs(project)}{args}");
        var timeout = follow ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(5);
        return _session.RunStreamingAsync(cmd, onLine, onLine, ct, timeout);
    }

    /// <summary>长时间操作（build / pull / up）：流式输出，权限不足时自动切换到 sudo 重试一次。</summary>
    private async Task<CommandResult> RunStreamingComposeAsync(
        string args, Action<string> onLine, TimeSpan timeout, CancellationToken ct)
    {
        var tail = new List<string>();
        void Sink(string line)
        {
            lock (tail)
            {
                if (tail.Count < 200) tail.Add(line);
            }
            onLine(line);
        }

        var r = await _session.RunStreamingAsync(Cmd(args), Sink, Sink, ct, timeout);
        if (!_needsSudo && !r.WasCancelled && LinesContainPermissionDenied(tail))
        {
            _needsSudo = true;
            onLine("（权限不足，改用 sudo 重试…）");
            r = await _session.RunStreamingAsync(Cmd(args), Sink, Sink, ct, timeout);
        }
        return r;
    }

    /// <summary>按项目设置的升级方式执行 pull/build/up；返回最终命令结果。</summary>
    public async Task<CommandResult> RunUpgradeAsync(
        ComposeProjectRef project,
        ComposeUpgradeMode mode,
        bool buildDetected,
        bool hasChanges,
        bool forceRecreate,
        string customCommand,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        var pa = ProjectArgs(project);
        var force = forceRecreate ? " --force-recreate" : "";
        switch (mode)
        {
            case ComposeUpgradeMode.PullUp:
            {
                var pull = await RunStreamingComposeAsync(
                    $"{pa}pull --ignore-pull-failures", onLine, TimeSpan.FromMinutes(60), ct);
                if (pull.WasCancelled) return pull;
                if (pull.ExitCode != 0)
                    onLine("⚠ 拉取镜像失败或部分失败，继续尝试启动（可能使用本地已有镜像）。");
                return await RunStreamingComposeAsync(
                    $"{pa}up -d --remove-orphans{force}", onLine, TimeSpan.FromMinutes(60), ct);
            }
            case ComposeUpgradeMode.Custom:
            {
                var script = $"cd {UnixPath.ShellQuote(project.WorkingDir)} && {customCommand}";
                var cmd = _needsSudo
                    ? _session.WrapSudoShell(script)
                    : $"sh -c {UnixPath.ShellQuote(script)}";
                return await _session.RunStreamingAsync(
                    cmd, onLine, onLine, ct, TimeSpan.FromMinutes(60));
            }
            case ComposeUpgradeMode.Build:
                return await RunStreamingComposeAsync(
                    $"{pa}up -d --build --remove-orphans{force}",
                    onLine, TimeSpan.FromMinutes(60), ct);
            case ComposeUpgradeMode.Up:
                return await RunStreamingComposeAsync(
                    $"{pa}up -d --remove-orphans{force}",
                    onLine, TimeSpan.FromMinutes(60), ct);
            default:
                if (buildDetected)
                {
                    var buildForce = hasChanges && forceRecreate ? " --force-recreate" : "";
                    return await RunStreamingComposeAsync(
                        $"{pa}up -d --build --remove-orphans{buildForce}",
                        onLine, TimeSpan.FromMinutes(60), ct);
                }
                if (hasChanges)
                {
                    var changeForce = forceRecreate ? " --force-recreate" : "";
                    return await RunStreamingComposeAsync(
                        $"{pa}up -d --remove-orphans{changeForce}",
                        onLine, TimeSpan.FromMinutes(60), ct);
                }
                return await RunStreamingComposeAsync(
                    $"{pa}up -d --remove-orphans",
                    onLine, TimeSpan.FromMinutes(60), ct);
        }
    }

    /// <summary>生成升级命令预览（不含 sudo 包装，绝不把密码写进日志）。</summary>
    public string BuildUpgradeCommand(
        ComposeProjectRef project,
        ComposeUpgradeMode mode,
        bool buildDetected,
        bool hasChanges,
        bool forceRecreate,
        string customCommand)
    {
        var pa = ProjectArgs(project);
        var force = forceRecreate ? " --force-recreate" : "";
        return mode switch
        {
            ComposeUpgradeMode.Build =>
                $"{Bin} {pa}up -d --build --remove-orphans{force}",
            ComposeUpgradeMode.Up =>
                $"{Bin} {pa}up -d --remove-orphans{force}",
            ComposeUpgradeMode.PullUp =>
                $"{Bin} {pa}pull --ignore-pull-failures{Environment.NewLine}{Bin} {pa}up -d --remove-orphans{force}",
            ComposeUpgradeMode.Custom =>
                $"cd {UnixPath.ShellQuote(project.WorkingDir)} && {customCommand}",
            _ => buildDetected
                ? $"{Bin} {pa}up -d --build --remove-orphans{(hasChanges && forceRecreate ? " --force-recreate" : "")}"
                : hasChanges
                    ? $"{Bin} {pa}up -d --remove-orphans{(forceRecreate ? " --force-recreate" : "")}"
                    : $"{Bin} {pa}up -d --remove-orphans",
        };
    }

    // ---------------------------------------------------------------- 工具方法

    /// <summary>是否检测到 build 配置（compose config JSON 优先，失败回退 grep / Dockerfile）。</summary>
    public async Task<bool> DetectBuildAsync(
        ComposeProjectRef project, bool localDockerfile, CancellationToken ct = default)
    {
        var cfg = await RunComposeAsync(
            $"{ProjectArgs(project)}config --format json", TimeSpan.FromMinutes(2), ct, raw: true);
        var json = ExtractJsonObject(cfg.Output);
        if (json != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("services", out var services)
                    && services.ValueKind == JsonValueKind.Object)
                {
                    foreach (var svc in services.EnumerateObject())
                        if (svc.Value.ValueKind == JsonValueKind.Object
                            && svc.Value.TryGetProperty("build", out _))
                            return true;
                }
            }
            catch (JsonException) { /* 回退 grep */ }
        }

        foreach (var raw in project.ComposeFiles)
        {
            var file = ResolveComposeFile(project, raw);
            if (file.Length == 0) continue;
            var g = await _session.RunAsync(
                $"LC_ALL=C grep -Eq '^[[:space:]]*build[[:space:]]*:' {UnixPath.ShellQuote(file)}",
                TimeSpan.FromSeconds(15), ct);
            if (g.ExitCode == 0) return true;
        }

        if (localDockerfile) return true;

        if (!string.IsNullOrWhiteSpace(project.WorkingDir))
        {
            var dockerfile = UnixPath.Combine(project.WorkingDir, "Dockerfile");
            var t = await _session.RunAsync(
                $"test -f {UnixPath.ShellQuote(dockerfile)}", TimeSpan.FromSeconds(10), ct);
            if (t.ExitCode == 0) return true;
        }
        return false;
    }

    public async Task<ComposeHealthInfo> GetHealthAsync(
        ComposeProjectRef project, CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var detail = await GetDetailAsync(project, ct);
            if (detail.Containers.Count == 0 && detail.Error != null)
                return new ComposeHealthInfo { Error = detail.Error };

            var total = detail.Containers.Count;
            var running = detail.Containers.Count(c =>
                c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
            var unhealthy = detail.Containers.Count(c =>
                c.Health.Contains("unhealthy", StringComparison.OrdinalIgnoreCase));
            return new ComposeHealthInfo
            {
                Total = total,
                Running = running,
                Exited = Math.Max(0, total - running),
                Unhealthy = unhealthy,
            };
        }
        catch (Exception ex)
        {
            return new ComposeHealthInfo { Error = ex.Message };
        }
    }

    public string BuildExecCommand(ComposeProjectRef project, string service)
        => $"{Bin} {ProjectArgs(project)}exec {UnixPath.ShellQuote(service)} bash";

    /// <summary>把 compose 文件路径解析为绝对路径（相对路径按项目工作目录解析）。</summary>
    public static string ResolveComposeFile(ComposeProjectRef project, string file)
    {
        var f = (file ?? "").Trim();
        if (f.Length == 0) return "";
        if (!f.StartsWith('/') && !string.IsNullOrWhiteSpace(project.WorkingDir))
            f = UnixPath.Combine(project.WorkingDir, f);
        return UnixPath.Normalize(f);
    }

    public static string ProjectArgs(ComposeProjectRef project)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(project.WorkingDir))
            sb.Append("--project-directory ").Append(UnixPath.ShellQuote(project.WorkingDir)).Append(' ');
        foreach (var raw in project.ComposeFiles)
        {
            var f = ResolveComposeFile(project, raw);
            if (f.Length == 0) continue;
            sb.Append("-f ").Append(UnixPath.ShellQuote(f)).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(project.Name))
            sb.Append("-p ").Append(UnixPath.ShellQuote(project.Name)).Append(' ');
        return sb.ToString();
    }

    private string Bin => _kind == ComposeKind.V1 ? "docker-compose" : "docker compose";

    private string Cmd(string args)
        => _needsSudo ? _session.WrapSudo($"{Bin} {args}") : $"{Bin} {args}";

    private string CmdRaw(string args)
        => _needsSudo ? _session.WrapSudoRaw($"{Bin} {args}") : $"{Bin} {args}";

    private string DockerCmd(string args)
        => _needsSudo ? _session.WrapSudo("docker " + args) : "docker " + args;

    private async Task<CommandResult> RunComposeAsync(
        string args, TimeSpan timeout, CancellationToken ct, bool raw = false)
    {
        var r = await _session.RunAsync(raw ? CmdRaw(args) : Cmd(args), timeout, ct);
        if (!_needsSudo && IsPermissionDenied(r))
        {
            _needsSudo = true;
            r = await _session.RunAsync(raw ? CmdRaw(args) : Cmd(args), timeout, ct);
        }
        return r;
    }

    private static bool LinesContainPermissionDenied(IReadOnlyList<string> lines)
    {
        for (int i = Math.Max(0, lines.Count - 80); i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || l.Contains("权限不足"))
                return true;
        }
        return false;
    }

    private static bool IsPermissionDenied(CommandResult r)
    {
        var text = r.Output + " " + r.Error;
        return text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
               || text.Contains("权限不足")
               || text.Contains("Got permission denied while trying to connect", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandMissing(string text)
    {
        var t = text ?? "";
        return t.Contains("command not found", StringComparison.OrdinalIgnoreCase)
               || t.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
               || t.Contains("not recognized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsComposeCommandMissing(string text)
        => IsCommandMissing(text)
           || text.Contains("is not a docker command", StringComparison.OrdinalIgnoreCase)
           || text.Contains("unknown command \"compose\"", StringComparison.OrdinalIgnoreCase)
           || text.Contains("No such command: compose", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeJson(string text)
    {
        var t = (text ?? "").TrimStart();
        return t.StartsWith("[") || t.StartsWith("{");
    }

    private static string? ExtractJsonObject(string text)
    {
        var t = (text ?? "").Trim();
        var i = t.IndexOf('{');
        if (i < 0) return null;
        return t[i..];
    }

    private static string Clean(string text)
        => (text ?? "").Replace("\r", "").Trim();

    private static string FirstLine(string text)
    {
        var t = Clean(text);
        if (t.Length == 0) return "未知错误";
        var i = t.IndexOf('\n');
        return (i >= 0 ? t[..i] : t).Trim();
    }
}
