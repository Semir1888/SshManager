using System.Text.Json;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>
/// Docker 管理：通过服务器上的 docker CLI（--format '{{json .}}' 结构化输出）实现
/// 容器/镜像列表、生命周期操作、日志、资源占用与清理。
/// 服务器无需安装任何额外软件；普通用户不在 docker 组时自动改用 sudo
/// （密码经 stdin 注入，不回显）。非 root 且未保存 sudo 密码时，
/// 操作会抛出 <see cref="SudoPasswordRequiredException"/>，由界面提示补齐后重试。
/// </summary>
public sealed class DockerService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SshSession _session;
    private bool _needsSudo;

    public DockerService(SshSession session)
    {
        _session = session;
    }

    /// <summary>该会话的 docker 是否需要 sudo 包装（探测后自动记录）。</summary>
    public bool NeedsSudo => _needsSudo;

    // ---------------------------------------------------------------- 探测

    /// <summary>
    /// 探测 docker 是否安装、是否可访问；成功时返回版本号。
    /// 需要 sudo 但没有密码时抛 SudoPasswordRequiredException（调用方补齐后重试）。
    /// </summary>
    public async Task<DockerProbe> ProbeAsync(CancellationToken ct = default)
    {
        var probeCmd = "docker version --format '{{.Server.Version}}' 2>&1";
        var r = await _session.RunAsync(probeCmd, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var text = (r.Output + r.Error).Trim();

        if (r.ExitCode == 127 || text.Contains("command not found", StringComparison.OrdinalIgnoreCase))
            return DockerProbe.NotInstalled();

        if (r.ExitCode == 0 && text.Length > 0)
            return DockerProbe.Ready(text.Split('\n')[0].Trim(), sudo: false);

        if (text.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            // 普通用户不在 docker 组：改用 sudo（可能抛 SudoPasswordRequiredException）
            var rs = await _session.RunAsync(_session.WrapSudo(probeCmd), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var t2 = (rs.Output + rs.Error).Trim();
            if (rs.ExitCode == 0 && t2.Length > 0)
            {
                _needsSudo = true;
                return DockerProbe.Ready(t2.Split('\n')[0].Trim(), sudo: true);
            }
            return DockerProbe.Fail("sudo 也无法访问 Docker：" + FirstLine(t2));
        }

        if (text.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase))
            return DockerProbe.Fail("Docker 已安装但守护进程未运行（systemctl start docker）");

        return DockerProbe.Fail("无法访问 Docker：" + FirstLine(text));
    }

    // ---------------------------------------------------------------- 列表

    public async Task<IReadOnlyList<DockerContainer>> ListContainersAsync(CancellationToken ct = default)
    {
        var r = await DockerAsync("ps -a --format '{{json .}}'", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        EnsureOk(r, "获取容器列表");
        return ParseLines<DockerContainer>(r.Output);
    }

    public async Task<IReadOnlyList<DockerImage>> ListImagesAsync(CancellationToken ct = default)
    {
        var r = await DockerAsync("images --format '{{json .}}'", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        EnsureOk(r, "获取镜像列表");
        return ParseLines<DockerImage>(r.Output);
    }

    /// <summary>一次性采样各容器 CPU / 内存占用（docker stats --no-stream，约需 1~2 秒）。</summary>
    public async Task<IReadOnlyList<DockerStats>> StatsAsync(CancellationToken ct = default)
    {
        var r = await DockerAsync("stats --no-stream --format '{{json .}}'", ct, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        EnsureOk(r, "获取资源占用");
        return ParseLines<DockerStats>(r.Output);
    }

    /// <summary>磁盘占用概览（镜像/容器/卷/构建缓存）。旧版 docker 不支持 --format 时返回空列表。</summary>
    public async Task<IReadOnlyList<DockerDiskUsage>> DiskUsageAsync(CancellationToken ct = default)
    {
        var r = await DockerAsync("system df --format '{{json .}}'", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        EnsureOk(r, "获取磁盘占用");
        return ParseLines<DockerDiskUsage>(r.Output);
    }

    // ---------------------------------------------------------------- 容器操作

    public Task<CommandResult> StartAsync(string id, CancellationToken ct = default)
        => DockerAsync($"start {UnixPath.ShellQuote(id)}", ct, TimeSpan.FromMinutes(2));

    public Task<CommandResult> StopAsync(string id, CancellationToken ct = default)
        => DockerAsync($"stop {UnixPath.ShellQuote(id)}", ct, TimeSpan.FromMinutes(2));

    public Task<CommandResult> RestartAsync(string id, CancellationToken ct = default)
        => DockerAsync($"restart {UnixPath.ShellQuote(id)}", ct, TimeSpan.FromMinutes(3));

    /// <summary>强制删除容器（docker rm -f，调用方需自行确认）。</summary>
    public Task<CommandResult> RemoveContainerAsync(string id, CancellationToken ct = default)
        => DockerAsync($"rm -f {UnixPath.ShellQuote(id)}", ct, TimeSpan.FromMinutes(2));

    // ---------------------------------------------------------------- 镜像操作

    /// <summary>强制删除镜像（docker rmi -f，调用方需自行确认）。</summary>
    public Task<CommandResult> RemoveImageAsync(string id, CancellationToken ct = default)
        => DockerAsync($"rmi -f {UnixPath.ShellQuote(id)}", ct, TimeSpan.FromMinutes(3));

    /// <summary>清理悬空镜像（dangling，&lt;none&gt;:&lt;none&gt;），不影响带标签镜像。</summary>
    public Task<CommandResult> PruneImagesAsync(CancellationToken ct = default)
        => DockerAsync("image prune -f", ct, TimeSpan.FromMinutes(10));

    /// <summary>深度清理：已停止容器、悬空镜像、未使用网络、构建缓存（不影响运行中容器）。</summary>
    public Task<CommandResult> SystemPruneAsync(CancellationToken ct = default)
        => DockerAsync("system prune -f", ct, TimeSpan.FromMinutes(15));

    // ---------------------------------------------------------------- 日志

    /// <summary>拉取容器尾部日志（stdout/stderr 混合，逐行回调）。</summary>
    public Task<CommandResult> LogsAsync(string id, int tail, Action<string> onLine, CancellationToken ct = default)
        => _session.RunStreamingAsync(
            Cmd($"logs --tail {Math.Clamp(tail, 1, 50000)} {UnixPath.ShellQuote(id)}"),
            onLine, onLine, ct, TimeSpan.FromMinutes(5));

    /// <summary>拼接“进入容器终端”的提示命令（bash 优先，无 bash 时提示改用 sh）。</summary>
    public static string BuildExecCommand(string containerName)
        => $"docker exec -it {UnixPath.ShellQuote(containerName)} bash";

    // ---------------------------------------------------------------- 基础设施

    private string Cmd(string args) => _needsSudo
        ? _session.WrapSudo("docker " + args)
        : "docker " + args;

    private Task<CommandResult> DockerAsync(string args, CancellationToken ct, TimeSpan timeout)
        => _session.RunAsync(Cmd(args), timeout, ct);

    private static void EnsureOk(CommandResult r, string op)
    {
        if (r.ExitCode == 0) return;
        var msg = FirstLine((r.Output + " " + r.Error).Trim());
        throw new SshManagerException($"{op}失败（退出码 {r.ExitCode}）：{msg}");
    }

    /// <summary>逐行反序列化 JSON；个别行解析失败（警告、旧版输出等）直接跳过。</summary>
    private static IReadOnlyList<T> ParseLines<T>(string output) where T : class
    {
        var list = new List<T>();
        if (string.IsNullOrEmpty(output)) return list;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, JsonOpts);
                if (item != null) list.Add(item);
            }
            catch (JsonException)
            {
                // 跳过无法解析的行
            }
        }
        return list;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "未知错误";
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }
}
