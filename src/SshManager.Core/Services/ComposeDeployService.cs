using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>
/// Compose 一键升级：本地文件枚举/排除、远程比对、备份、SFTP 上传（sudo 降级 + 原子替换）、
/// 配置校验、按升级方式执行、健康检查、回滚与备份管理。
/// 预期失败通过结果对象返回；仅 sudo 缺密码抛 SudoPasswordRequiredException 供 UI 补密。
/// </summary>
public sealed class ComposeDeployService
{
    private const string BackupDirName = ".sshmanager-backups";

    private readonly SshSession _session;
    private readonly ComposeService _compose;

    public ComposeDeployService(SshSession session, ComposeService compose)
    {
        _session = session;
        _compose = compose;
    }

    // ================================================================ 扫描 / 比对

    public async Task<ComposePlanResult> BuildPlanAsync(
        ComposeDeployRequest request,
        bool forceAll,
        IProgress<ComposeDeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        var config = request.Config;
        var project = request.Project;

        if (string.IsNullOrWhiteSpace(config.LocalDir) && request.ExtraFiles.Count == 0)
            return ComposePlanResult.Fail("请选择本地部署目录，或至少添加一个文件。");
        if (string.IsNullOrWhiteSpace(config.RemoteDir))
            return ComposePlanResult.Fail("请填写远程项目目录。");
        if (config.ComposeFiles.Count == 0)
            return ComposePlanResult.Fail("请至少指定一个 compose 文件。");
        if (!string.IsNullOrWhiteSpace(config.LocalDir) && !Directory.Exists(config.LocalDir))
            return ComposePlanResult.Fail("本地部署目录不存在：" + config.LocalDir);

        progress?.Report(new ComposeDeployProgress("Scanning", "正在枚举本地文件…", 0, 0, 0, 0, 2));

        var matchers = BuildMatchers(config.ExcludePatterns);

        LocalScan local;
        try
        {
            local = await Task.Run(() => EnumerateLocal(config, request.ExtraFiles, matchers), ct);
        }
        catch (OperationCanceledException)
        {
            return ComposePlanResult.Fail("已取消。");
        }
        catch (Exception ex)
        {
            return ComposePlanResult.Fail("读取本地目录失败：" + ex.Message);
        }
        if (local.Error != null) return ComposePlanResult.Fail(local.Error);

        progress?.Report(new ComposeDeployProgress("Comparing", "正在读取远程文件…", 0, 0, 0, 0, 10));

        RemoteScan remote;
        try
        {
            remote = await ListRemoteFilesAsync(config.RemoteDir, matchers, ct);
        }
        catch (SudoPasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return ComposePlanResult.Fail("读取远程目录失败：" + ex.Message);
        }
        if (remote.Error != null) return ComposePlanResult.Fail(remote.Error);

        var warnings = new List<string>(local.Warnings);
        warnings.AddRange(remote.Warnings);

        var remoteDirExists = remote.Files.Count > 0 || await TestAsync($"test -d {UnixPath.ShellQuote(config.RemoteDir)}", ct);
        var remoteDirWritable = await IsWritableAsync(config.RemoteDir, ct);

        var changes = new List<ComposeFileChange>();
        var sameSize = new List<ComposeFileChange>();
        var localSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in local.Files)
        {
            localSet.Add(file.RelativePath);
            var remoteExists = remote.Files.TryGetValue(file.RelativePath, out var remoteSize);
            var kind = !remoteExists
                ? ComposeChangeKind.Added
                : remoteSize != file.Size
                    ? ComposeChangeKind.Modified
                    : ComposeChangeKind.Unchanged;

            var change = new ComposeFileChange
            {
                RelativePath = file.RelativePath,
                LocalPath = file.FullPath,
                RemotePath = UnixPath.Combine(config.RemoteDir, file.RelativePath),
                LocalSize = file.Size,
                RemoteSize = remoteSize,
                RemoteExists = remoteExists,
                Kind = kind,
            };
            changes.Add(change);
            if (kind == ComposeChangeKind.Unchanged) sameSize.Add(change);
        }

        foreach (var pair in remote.Files)
        {
            if (localSet.Contains(pair.Key)) continue;
            changes.Add(new ComposeFileChange
            {
                RelativePath = pair.Key,
                RemotePath = UnixPath.Combine(config.RemoteDir, pair.Key),
                RemoteSize = pair.Value,
                RemoteExists = true,
                Kind = ComposeChangeKind.RemoteOnly,
            });
        }

        if (sameSize.Count > 0)
        {
            progress?.Report(new ComposeDeployProgress("Comparing", "正在比对文件内容（MD5）…", 0, 0, 0, 0, 18));
            Dictionary<string, string> localHashes;
            try
            {
                var candidates = sameSize.ToList();
                localHashes = await Task.Run(() => ComputeLocalMd5(candidates, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return ComposePlanResult.Fail("已取消。");
            }

            var remoteHashes = await ComputeRemoteMd5Async(
                config.RemoteDir, sameSize.Select(c => c.RelativePath).ToList(), warnings, ct);

            foreach (var change in sameSize)
            {
                if (localHashes.TryGetValue(change.RelativePath, out var lh)
                    && remoteHashes.TryGetValue(change.RelativePath, out var rh)
                    && lh.Equals(rh, StringComparison.OrdinalIgnoreCase))
                {
                    change.Kind = ComposeChangeKind.Unchanged;
                }
                else
                {
                    change.Kind = ComposeChangeKind.Modified;
                }
            }
        }

        var buildDetected = local.HasDockerfile
                            || await _compose.DetectBuildAsync(project, local.HasDockerfile, ct);

        var hasAnyChange = forceAll
            ? local.Files.Count > 0
            : changes.Any(c => c.Kind is ComposeChangeKind.Added or ComposeChangeKind.Modified);

        var commandPreview = _compose.BuildUpgradeCommand(
            project, config.Mode, buildDetected, hasAnyChange,
            config.ForceRecreate, config.CustomCommand);

        // 实际归档名在执行时按时间戳生成，这里预览备份目录
        var backupPreview = UnixPath.Combine(config.RemoteDir, BackupDirName);

        if (!remoteDirExists)
            warnings.Add("远程项目目录不存在，上传时会尝试创建。");
        else if (!remoteDirWritable)
            warnings.Add("远程项目目录当前用户不可写，备份/上传将尝试使用 sudo。");

        var rootPrefix = config.RemoteDir.EndsWith('/') ? config.RemoteDir : config.RemoteDir + "/";
        foreach (var raw in project.ComposeFiles)
        {
            var f = ComposeService.ResolveComposeFile(project, raw);
            if (f.Length == 0) continue;
            if (!f.StartsWith(rootPrefix, StringComparison.Ordinal))
                warnings.Add($"compose 文件 {f} 不在远程项目目录内：目录同步不会覆盖它，请用临时文件映射或手动上传。");
        }

        if (local.Files.Count > 0)
        {
            var composePaths = project.ComposeFiles
                .Select(f => ComposeService.ResolveComposeFile(project, f))
                .Where(f => f.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (composePaths.Count > 0
                && !changes.Any(c => c.Kind != ComposeChangeKind.RemoteOnly
                                     && composePaths.Contains(UnixPath.Normalize(c.RemotePath))))
            {
                warnings.Add("本次本地目录中没有与项目 compose 文件路径对应的文件（" +
                             string.Join("、", composePaths) +
                             "）：如果修改了 compose 文件，请检查本地文件名或用“额外文件”映射。");
            }
        }

        if (!hasAnyChange)
            warnings.Add("未检测到需要上传的文件变化；仍可执行升级命令（例如仅拉取镜像）。");
        if (buildDetected && !hasAnyChange)
            warnings.Add("检测到 build 配置，但本次没有文件变化。");
        if (buildDetected)
            warnings.Add("备份目录 .sshmanager-backups 位于项目目录内：若 Dockerfile 使用 COPY . .，建议把它加入 .dockerignore，避免备份进入构建上下文。");
        if (config.Mode == ComposeUpgradeMode.Auto && hasAnyChange && !buildDetected)
        {
            warnings.Add(config.ForceRecreate
                ? "未检测到 Dockerfile/build 配置：将强制重建容器，使挂载的 jar/配置文件生效。"
                : "未勾选“强制重建容器”：仅执行 up -d，挂载的 jar/配置文件变化可能不会让容器重启。");
        }
        if (config.Mode == ComposeUpgradeMode.Custom && string.IsNullOrWhiteSpace(config.CustomCommand))
            warnings.Add("自定义升级命令为空，执行将失败。");

        var ordered = changes
            .OrderBy(c => c.Kind switch
            {
                ComposeChangeKind.Added => 0,
                ComposeChangeKind.Modified => 1,
                ComposeChangeKind.Unchanged => 2,
                _ => 3,
            })
            .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report(new ComposeDeployProgress("Planning", "比对完成。", 0, ordered.Count, 0, 0, 25));

        return ComposePlanResult.Success(new ComposeDeployPlan
        {
            Files = ordered,
            BuildDetected = buildDetected,
            CommandPreview = commandPreview,
            BackupPreview = backupPreview,
            RemoteDir = config.RemoteDir,
            RemoteDirExists = remoteDirExists,
            RemoteDirWritable = remoteDirWritable,
            Warnings = warnings,
        });
    }

    // ---------------------------------------------------------------- 本地枚举

    private sealed record LocalFile(string FullPath, string RelativePath, long Size);

    private sealed class LocalScan
    {
        public List<LocalFile> Files { get; } = new();
        public List<string> Warnings { get; } = new();
        public string? Error { get; set; }
        public bool HasDockerfile { get; set; }
    }

    private sealed class ExcludeMatcher
    {
        public Regex Pattern { get; init; } = null!;
        public bool MatchFullPath { get; init; }
    }

    private static LocalScan EnumerateLocal(
        ComposeProjectConfig config,
        IReadOnlyList<ComposeExtraFile> extras,
        IReadOnlyList<ExcludeMatcher> matchers)
    {
        var scan = new LocalScan();
        var root = config.LocalDir;

        try
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                if (!Directory.Exists(root))
                {
                    scan.Error = "本地部署目录不存在：" + root;
                    return scan;
                }

                var pending = new Stack<string>();
                pending.Push(Path.GetFullPath(root));

                while (pending.Count > 0)
                {
                    var dir = pending.Pop();
                    string[] subDirs;
                    string[] files;
                    try
                    {
                        subDirs = Directory.GetDirectories(dir);
                        files = Directory.GetFiles(dir);
                    }
                    catch (Exception ex)
                    {
                        scan.Warnings.Add($"无法读取本地目录 {dir}：{ex.Message}");
                        continue;
                    }

                    foreach (var sub in subDirs)
                    {
                        try
                        {
                            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                                continue;
                        }
                        catch { continue; }
                        pending.Push(sub);
                    }

                    foreach (var file in files)
                    {
                        try
                        {
                            var attrs = File.GetAttributes(file);
                            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                            var info = new FileInfo(file);
                            if (!info.Exists) continue;

                            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                            if (rel.Length == 0 || rel.StartsWith("../", StringComparison.Ordinal)) continue;
                            if (rel.StartsWith(BackupDirName + "/", StringComparison.Ordinal)) continue;
                            if (rel.Contains('\n') || rel.Contains('\r'))
                            {
                                scan.Warnings.Add($"跳过文件名含换行的文件：{rel}");
                                continue;
                            }
                            if (IsExcluded(rel, matchers)) continue;

                            scan.Files.Add(new LocalFile(file, rel, info.Length));
                            if (IsDockerfileName(Path.GetFileName(file)))
                                scan.HasDockerfile = true;
                        }
                        catch (Exception ex)
                        {
                            scan.Warnings.Add($"读取本地文件失败 {file}：{ex.Message}");
                        }
                    }
                }
            }

            foreach (var extra in extras)
            {
                if (!File.Exists(extra.LocalPath))
                {
                    scan.Warnings.Add("找不到临时选择的文件：" + extra.LocalPath);
                    continue;
                }
                var rel = NormalizeRelative(extra.RemoteRelativePath);
                if (rel.Length == 0)
                    rel = Path.GetFileName(extra.LocalPath);
                if (rel.Length == 0 || rel.Contains("..") || rel.Contains('\n') || rel.Contains('\r'))
                {
                    scan.Warnings.Add("临时文件的目标路径不合法：" + extra.RemoteRelativePath);
                    continue;
                }
                var info = new FileInfo(extra.LocalPath);
                scan.Files.RemoveAll(f => f.RelativePath.Equals(rel, StringComparison.Ordinal));
                scan.Files.Add(new LocalFile(extra.LocalPath, rel, info.Length));
                if (IsDockerfileName(Path.GetFileName(extra.LocalPath)))
                    scan.HasDockerfile = true;
            }

            scan.Files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            scan.Error = "读取本地目录失败：" + ex.Message;
        }

        return scan;
    }

    private static string NormalizeRelative(string path)
        => (path ?? "").Replace('\\', '/').TrimStart('/').Trim();

    private static bool IsDockerfileName(string name)
        => name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase)
           || name.Equals("dockerfile", StringComparison.OrdinalIgnoreCase);

    private static List<ExcludeMatcher> BuildMatchers(IEnumerable<string> patterns)
    {
        var list = new List<ExcludeMatcher>();
        foreach (var raw in patterns)
        {
            var p = NormalizeRelative(raw);
            if (p.Length == 0) continue;
            if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
            var full = p.Contains('/');
            list.Add(new ExcludeMatcher
            {
                Pattern = new Regex(GlobToRegex(p), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                MatchFullPath = full,
            });
        }
        return list;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    private static bool IsExcluded(string relativePath, IReadOnlyList<ExcludeMatcher> matchers)
    {
        foreach (var m in matchers)
        {
            if (m.MatchFullPath)
            {
                if (m.Pattern.IsMatch(relativePath)) return true;
            }
            else
            {
                foreach (var seg in relativePath.Split('/'))
                    if (m.Pattern.IsMatch(seg)) return true;
            }
        }
        return false;
    }

    private static Dictionary<string, string> ComputeLocalMd5(
        IReadOnlyCollection<ComposeFileChange> files, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var fs = File.OpenRead(f.LocalPath);
                dict[f.RelativePath] = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
            }
            catch { /* 读不到则视为变化 */ }
        }
        return dict;
    }

    // ---------------------------------------------------------------- 远程枚举 / MD5

    private sealed class RemoteScan
    {
        public Dictionary<string, long> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new();
        public string? Error { get; set; }
        public bool UsedSudo { get; set; }
    }

    private async Task<RemoteScan> ListRemoteFilesAsync(
        string root, IReadOnlyList<ExcludeMatcher> matchers, CancellationToken ct)
    {
        var scan = new RemoteScan();
        var cmd = $"LC_ALL=C find {UnixPath.ShellQuote(root)} -type f -printf '%s|%P\\0'";
        var r = await RunFileShellAsync(cmd, TimeSpan.FromMinutes(5), ct);

        if (r.ExitCode != 0 && string.IsNullOrEmpty(r.Output))
        {
            if (IsMissingPath(r))
                return scan; // 目录不存在：全部按新增处理
            scan.Error = "读取远程目录失败：" + FirstLine(r.Combined);
            return scan;
        }
        if (r.ExitCode != 0)
            scan.Warnings.Add("读取远程目录时出现部分错误（部分文件可能未参与比对）：" + FirstLine(r.Error));

        foreach (var entry in r.Output.Split('\0'))
        {
            if (entry.Length == 0) continue;
            var bar = entry.IndexOf('|');
            if (bar <= 0) continue;
            if (!long.TryParse(entry[..bar], out var size)) continue;
            var rel = entry[(bar + 1)..];
            if (rel.Length == 0) continue;
            if (rel.StartsWith(BackupDirName + "/", StringComparison.Ordinal)) continue;
            var name = rel[(rel.LastIndexOf('/') + 1)..];
            if (name.EndsWith(".sshmanager-tmp", StringComparison.Ordinal)) continue;
            if (rel.Contains('\n') || rel.Contains('\r')) continue;
            if (IsExcluded(rel, matchers)) continue;
            scan.Files[rel] = size;
        }
        return scan;
    }

    private async Task<Dictionary<string, string>> ComputeRemoteMd5Async(
        string root,
        IReadOnlyList<string> relativePaths,
        List<string> warnings,
        CancellationToken ct)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (relativePaths.Count == 0) return hashes;

        var chunks = new List<List<string>>();
        var current = new List<string>();
        var length = 0;
        foreach (var p in relativePaths)
        {
            if (current.Count >= 100 || length + p.Length > 50000)
            {
                if (current.Count > 0) chunks.Add(current);
                current = new List<string>();
                length = 0;
            }
            current.Add(p);
            length += p.Length + 3;
        }
        if (current.Count > 0) chunks.Add(current);

        foreach (var chunk in chunks)
        {
            if (ct.IsCancellationRequested) break;
            var args = string.Join(' ', chunk.Select(UnixPath.ShellQuote));
            var cmd = $"cd {UnixPath.ShellQuote(root)} && LC_ALL=C md5sum -- {args}";
            var r = await RunFileShellAsync(cmd, TimeSpan.FromMinutes(5), ct);
            if (r.ExitCode != 0)
            {
                warnings.Add("远程 MD5 比对失败（将按已修改处理）：" + FirstLine(r.Combined));
                continue;
            }

            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line[0] == '\\') continue; // 含转义字符的罕见文件名不参与精确比对
                var idx = line.IndexOf("  ", StringComparison.Ordinal);
                if (idx <= 0) continue;
                var hash = line[..idx].Trim();
                var path = line[(idx + 2)..];
                if (hash.Length == 0 || path.Length == 0) continue;
                hashes[path] = hash;
            }
        }
        return hashes;
    }

    // ---------------------------------------------------------------- 备份

    private async Task<(string? Path, string? Error, long Size)> CreateBackupAsync(
        ComposeProjectRef project,
        IReadOnlyList<ComposeFileChange> files,
        int keep,
        Action<string>? onLine,
        CancellationToken ct)
    {
        var existing = files
            .Where(f => f.RemoteExists)
            .Select(f => f.RelativePath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (existing.Count == 0) return (null, null, 0);

        var backupDir = UnixPath.Combine(project.WorkingDir, BackupDirName);
        var archive = UnixPath.Combine(
            backupDir,
            DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + ".tar.gz");

        var mk = await RunFileShellAsync(
            $"mkdir -p -- {UnixPath.ShellQuote(backupDir)}", TimeSpan.FromMinutes(2), ct);
        if (mk.ExitCode != 0)
            return (null, "创建备份目录失败：" + FirstLine(mk.Combined), 0);

        var listFile = "/tmp/.sshmanager-bk-" + Guid.NewGuid().ToString("N")[..12] + ".list";
        var content = string.Join('\n', existing) + "\n";
        var write = await _session.WriteFileBytesAsync(
            listFile, Encoding.UTF8.GetBytes(content), ct);
        if (write.Status != WriteStatus.Ok)
            return (null, "写入备份清单失败：" + write.Describe(), 0);

        try
        {
            var tarCmd = $"tar -czf {UnixPath.ShellQuote(archive)} -C {UnixPath.ShellQuote(project.WorkingDir)} " +
                         $"--no-recursion -T {UnixPath.ShellQuote(listFile)}";
            var tar = await RunFileShellAsync(tarCmd, TimeSpan.FromMinutes(10), ct);
            if (tar.ExitCode != 0)
            {
                // 清理可能写了一半的归档，避免出现在备份列表中
                try { await _session.RunAsync($"rm -f -- {UnixPath.ShellQuote(archive)}", TimeSpan.FromSeconds(10), ct); }
                catch { }
                return (null, "备份失败（未修改任何文件）：" + FirstLine(tar.Combined), 0);
            }

            var stat = await _session.RunAsync(
                $"stat -c %s {UnixPath.ShellQuote(archive)}", TimeSpan.FromSeconds(10), ct);
            var size = long.TryParse(stat.Output.Trim(), out var n) ? n : 0;

            onLine?.Invoke($"已备份 {existing.Count} 个文件到 {archive}");
            await PruneBackupsAsync(project, keep, onLine, ct);
            return (archive, null, size);
        }
        finally
        {
            try { await _session.RunAsync($"rm -f -- {UnixPath.ShellQuote(listFile)}", TimeSpan.FromSeconds(10), ct); }
            catch { }
        }
    }

    private async Task PruneBackupsAsync(
        ComposeProjectRef project, int keep, Action<string>? onLine, CancellationToken ct)
    {
        keep = Math.Clamp(keep, 1, 50);
        var dir = UnixPath.Combine(project.WorkingDir, BackupDirName);
        var r = await RunFileShellAsync(
            $"LC_ALL=C find {UnixPath.ShellQuote(dir)} -maxdepth 1 -type f -name '*.tar.gz' -printf '%f\\n'",
            TimeSpan.FromMinutes(1), ct);
        if (r.ExitCode != 0) return;

        var names = r.Output.Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .OrderByDescending(x => x, StringComparer.Ordinal)
            .ToList();
        if (names.Count <= keep) return;

        var toDelete = names.Skip(keep)
            .Select(n => UnixPath.Combine(dir, n))
            .ToList();
        var cmd = "rm -f -- " + string.Join(' ', toDelete.Select(UnixPath.ShellQuote));
        var d = await RunFileShellAsync(cmd, TimeSpan.FromMinutes(1), ct);
        if (d.ExitCode == 0)
            onLine?.Invoke($"已清理 {toDelete.Count} 份旧备份。");
    }

    // ================================================================ 执行升级

    public async Task<ComposeApplyResult> ApplyAsync(
        ComposeDeployRequest request,
        ComposeDeployPlan plan,
        IReadOnlyCollection<string> selectedRelativePaths,
        Action<string>? onLine = null,
        IProgress<ComposeDeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        var selected = plan.Files
            .Where(f => f.Kind != ComposeChangeKind.RemoteOnly
                        && selectedRelativePaths.Contains(f.RelativePath))
            .ToList();

        if (selected.Count == 0)
            return Fail("未选择任何要上传的文件。");
        if (string.IsNullOrWhiteSpace(request.Config.RemoteDir))
            return Fail("远程项目目录为空。");
        if (request.Config.Mode == ComposeUpgradeMode.Custom
            && string.IsNullOrWhiteSpace(request.Config.CustomCommand))
            return Fail("自定义升级命令为空。");

        var project = request.Project;
        var config = request.Config;
        string? backupPath = null;

        try
        {
            if (config.BackupEnabled)
            {
                progress?.Report(new ComposeDeployProgress(
                    "BackingUp", "正在备份将被覆盖的远程文件…", 0, selected.Count, 0, 0, 8));
                var bk = await CreateBackupAsync(project, selected, config.BackupKeep, onLine, ct);
                if (bk.Error != null)
                    return new ComposeApplyResult { Success = false, Error = bk.Error };
                backupPath = bk.Path;
            }

            var totalBytes = selected.Sum(f => Math.Max(0, f.LocalSize));
            progress?.Report(new ComposeDeployProgress(
                "Uploading", "正在上传文件…", 0, selected.Count, 0, totalBytes, 10));

            var upload = await UploadFilesAsync(project, selected, onLine, progress, ct);
            if (upload.Cancelled)
                return new ComposeApplyResult
                {
                    Success = false,
                    Cancelled = true,
                    BackupPath = backupPath,
                    Error = "已取消上传。",
                };
            if (upload.Error != null)
                return new ComposeApplyResult
                {
                    Success = false,
                    BackupPath = backupPath,
                    Error = upload.Error,
                };

            progress?.Report(new ComposeDeployProgress(
                "Validating", "正在校验 compose 配置…", selected.Count, selected.Count, totalBytes, totalBytes, 76));
            var validate = await _compose.ValidateAsync(project, ct);
            if (validate.WasCancelled)
                return new ComposeApplyResult
                {
                    Success = false,
                    Cancelled = true,
                    BackupPath = backupPath,
                    Error = "已取消配置校验。",
                };
            if (validate.ExitCode != 0)
                return new ComposeApplyResult
                {
                    Success = false,
                    BackupPath = backupPath,
                    Error = "配置校验失败：" + FirstLine(validate.Combined),
                };
            onLine?.Invoke("✔ compose 配置校验通过。");

            progress?.Report(new ComposeDeployProgress(
                "Running", "正在执行升级命令…", selected.Count, selected.Count, totalBytes, totalBytes, 82));
            onLine?.Invoke("执行：" + plan.CommandPreview
                .Replace("\r\n", " ; ")
                .Replace("\n", " ; "));

            var run = await _compose.RunUpgradeAsync(
                project, config.Mode, plan.BuildDetected, selected.Count > 0,
                config.ForceRecreate, config.CustomCommand,
                line => onLine?.Invoke(line), ct);

            if (run.WasCancelled)
                return new ComposeApplyResult
                {
                    Success = false,
                    Cancelled = true,
                    BackupPath = backupPath,
                    Error = "已停止升级命令。",
                };
            if (run.ExitCode != 0)
                return new ComposeApplyResult
                {
                    Success = false,
                    BackupPath = backupPath,
                    Error = $"升级命令失败（退出码 {run.ExitCode}）。",
                };

            progress?.Report(new ComposeDeployProgress(
                "HealthCheck", "正在检查容器状态…", selected.Count, selected.Count, totalBytes, totalBytes, 95));
            var health = await _compose.GetHealthAsync(project, ct);
            onLine?.Invoke("健康检查：" + health.Summary);

            progress?.Report(new ComposeDeployProgress(
                "Completed", "升级完成。", selected.Count, selected.Count, totalBytes, totalBytes, 100));
            return new ComposeApplyResult
            {
                Success = true,
                BackupPath = backupPath,
                Health = health,
            };
        }
        catch (OperationCanceledException)
        {
            return new ComposeApplyResult
            {
                Success = false,
                Cancelled = true,
                BackupPath = backupPath,
                Error = "已取消。",
            };
        }
        catch (SudoPasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return new ComposeApplyResult
            {
                Success = false,
                BackupPath = backupPath,
                Error = ex.Message,
            };
        }
    }

    // ---------------------------------------------------------------- 上传

    private async Task<(bool Ok, string? Error, bool Cancelled)> UploadFilesAsync(
        ComposeProjectRef project,
        IReadOnlyList<ComposeFileChange> files,
        Action<string>? onLine,
        IProgress<ComposeDeployProgress>? progress,
        CancellationToken ct)
    {
        using var client = _session.CreateSftpClient();

        var dirs = files.Select(f => UnixPath.Parent(f.RemotePath))
            .Distinct(StringComparer.Ordinal).ToList();
        var dirError = await EnsureDirectoriesAsync(client, dirs, ct);
        if (dirError != null) return (false, dirError, false);

        var totalBytes = files.Sum(f => Math.Max(0, f.LocalSize));
        long doneBytes = 0;
        long lastTick = 0;

        for (int i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) return (false, null, true);
            var file = files[i];
            var index = i;

            onLine?.Invoke($"上传 {file.RelativePath}（{i + 1}/{files.Count}）");

            var result = await UploadOneAsync(
                client, file,
                current =>
                {
                    var now = Environment.TickCount64;
                    if (now - lastTick < 100 && current < file.LocalSize) return;
                    lastTick = now;
                    var overall = doneBytes + Math.Clamp(current, 0, file.LocalSize);
                    var percent = totalBytes <= 0
                        ? 10
                        : 10 + Math.Clamp(overall * 60.0 / totalBytes, 0, 60);
                    progress?.Report(new ComposeDeployProgress(
                        "Uploading",
                        $"上传 {file.RelativePath}（{index + 1}/{files.Count}）",
                        index + 1, files.Count, overall, totalBytes, percent));
                },
                ct);

            if (result.Cancelled) return (false, null, true);
            if (result.Error != null) return (false, result.Error, false);
            doneBytes += Math.Max(0, file.LocalSize);
        }

        return (true, null, false);
    }

    private async Task<(string? Error, bool Cancelled, long Uploaded)> UploadOneAsync(
        SftpClient client,
        ComposeFileChange file,
        Action<long> onBytes,
        CancellationToken ct)
    {
        var parent = UnixPath.Parent(file.RemotePath);
        var temp = UnixPath.Combine(
            parent, "." + Guid.NewGuid().ToString("N")[..12] + ".sshmanager-tmp");

        Exception? error = null;
        await Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(file.LocalPath);
                client.UploadFile(fs, temp, true, b =>
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    onBytes((long)b);
                });
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, ct);

        if (error is OperationCanceledException)
        {
            await TryDeleteFileAsync(temp, CancellationToken.None);
            return (null, true, 0);
        }

        if (error != null)
        {
            if (IsPermissionError(error))
                return await UploadViaTempSudoAsync(client, file, onBytes, ct);
            return ("上传失败：" + error.Message, false, 0);
        }

        try
        {
            // 尽量保留目标文件原有权限；随后原子替换
            var mvCmd = $"if [ -f {UnixPath.ShellQuote(file.RemotePath)} ]; then " +
                        $"chmod --reference={UnixPath.ShellQuote(file.RemotePath)} {UnixPath.ShellQuote(temp)} 2>/dev/null || true; " +
                        $"fi; mv -f -- {UnixPath.ShellQuote(temp)} {UnixPath.ShellQuote(file.RemotePath)}";
            var mv = await RunFileShellAsync(mvCmd, TimeSpan.FromMinutes(1), ct);
            if (mv.ExitCode != 0)
            {
                await TryDeleteFileAsync(temp, CancellationToken.None);
                return ("替换目标文件失败：" + FirstLine(mv.Combined), false, 0);
            }

            onBytes(file.LocalSize);
            return (null, false, file.LocalSize);
        }
        catch
        {
            await TryDeleteFileAsync(temp, CancellationToken.None);
            throw;
        }
    }

    private async Task<(string? Error, bool Cancelled, long Uploaded)> UploadViaTempSudoAsync(
        SftpClient client,
        ComposeFileChange file,
        Action<long> onBytes,
        CancellationToken ct)
    {
        var remoteTemp = "/tmp/.sshmanager-up-" + Guid.NewGuid().ToString("N")[..12];
        Exception? error = null;

        await Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(file.LocalPath);
                client.UploadFile(fs, remoteTemp, true, b =>
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    onBytes((long)b);
                });
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, ct);

        if (error is OperationCanceledException)
        {
            await TryDeleteFileAsync(remoteTemp, CancellationToken.None);
            return (null, true, 0);
        }
        if (error != null) return ("上传到临时目录失败：" + error.Message, false, 0);

        try
        {
            var parent = UnixPath.Parent(file.RemotePath);
            var cmd = $"mkdir -p -- {UnixPath.ShellQuote(parent)} && " +
                      $"cp -- {UnixPath.ShellQuote(remoteTemp)} {UnixPath.ShellQuote(file.RemotePath)}";
            var r = await RunFileShellAsync(cmd, TimeSpan.FromMinutes(10), ct);
            if (r.ExitCode != 0)
                return ("sudo 复制失败：" + FirstLine(r.Combined), false, 0);
            onBytes(file.LocalSize);
            return (null, false, file.LocalSize);
        }
        finally
        {
            await TryDeleteFileAsync(remoteTemp, CancellationToken.None);
        }
    }

    private async Task<string?> EnsureDirectoriesAsync(
        SftpClient client, IReadOnlyList<string> dirs, CancellationToken ct)
    {
        var denied = new List<string>();
        string? error = null;

        await Task.Run(() =>
        {
            try
            {
                foreach (var dir in dirs)
                {
                    if (ct.IsCancellationRequested)
                    {
                        error = "已取消。";
                        return;
                    }

                    var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var cur = "";
                    foreach (var part in parts)
                    {
                        cur += "/" + part;
                        bool exists;
                        try { exists = client.Exists(cur); }
                        catch
                        {
                            denied.Add(dir);
                            break;
                        }
                        if (exists) continue;
                        try { client.CreateDirectory(cur); }
                        catch (SftpPermissionDeniedException)
                        {
                            denied.Add(dir);
                            break;
                        }
                        catch (Exception ex)
                        {
                            error ??= ex.Message;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }, ct);

        if (error != null) return "创建远程目录失败：" + error;
        if (denied.Count > 0)
        {
            var cmd = "mkdir -p -- " + string.Join(' ', denied.Distinct().Select(UnixPath.ShellQuote));
            var r = await RunFileShellAsync(cmd, TimeSpan.FromMinutes(2), ct);
            if (r.ExitCode != 0)
                return "创建远程目录失败：" + FirstLine(r.Combined);
        }
        return null;
    }

    // ================================================================ 回滚 / 备份管理

    public async Task<ComposeApplyResult> RollbackAsync(
        ComposeProjectRef project,
        string? backupArchivePath,
        Action<string>? onLine = null,
        IProgress<ComposeDeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project.WorkingDir))
            return Fail("项目目录为空，无法回滚。");

        try
        {
            var backupDir = UnixPath.Normalize(UnixPath.Combine(project.WorkingDir, BackupDirName));

            if (string.IsNullOrWhiteSpace(backupArchivePath))
            {
                var list = await ListBackupsAsync(project, ct);
                if (!list.Ok || list.Items.Count == 0)
                    return Fail("未找到可用的备份。");
                backupArchivePath = list.Items[0].Path;
            }

            var archive = UnixPath.Normalize(backupArchivePath);
            if (!archive.StartsWith(backupDir + "/", StringComparison.Ordinal)
                || !archive.EndsWith(".tar.gz", StringComparison.Ordinal))
                return Fail("备份路径不合法。");

            progress?.Report(new ComposeDeployProgress(
                "RollingBack", "正在恢复备份文件…", 0, 0, 0, 0, 20));
            var tar = await RunFileShellAsync(
                $"tar -xzf {UnixPath.ShellQuote(archive)} -C {UnixPath.ShellQuote(project.WorkingDir)}",
                TimeSpan.FromMinutes(10), ct);
            if (tar.ExitCode != 0)
                return Fail("恢复备份失败：" + FirstLine(tar.Combined));
            onLine?.Invoke("已恢复备份：" + archive);

            progress?.Report(new ComposeDeployProgress(
                "RollingBack", "正在强制重建容器…", 0, 0, 0, 0, 60));
            var run = await _compose.RunUpgradeAsync(
                project, ComposeUpgradeMode.Up, false, false, true, "",
                line => onLine?.Invoke(line), ct);
            if (run.WasCancelled)
                return new ComposeApplyResult { Success = false, Cancelled = true, Error = "已停止回滚。" };
            if (run.ExitCode != 0)
                return Fail($"回滚后重建失败（退出码 {run.ExitCode}）。");

            var health = await _compose.GetHealthAsync(project, ct);
            onLine?.Invoke("回滚完成，健康检查：" + health.Summary);
            progress?.Report(new ComposeDeployProgress(
                "RolledBack", "回滚完成。", 0, 0, 0, 0, 100));
            return new ComposeApplyResult
            {
                Success = true,
                BackupPath = archive,
                Health = health,
            };
        }
        catch (OperationCanceledException)
        {
            return new ComposeApplyResult { Success = false, Cancelled = true, Error = "已取消。" };
        }
        catch (SudoPasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<ComposeBackupListResult> ListBackupsAsync(
        ComposeProjectRef project, CancellationToken ct = default)
    {
        var dir = UnixPath.Combine(project.WorkingDir, BackupDirName);
        try
        {
            var r = await RunFileShellAsync(
                $"LC_ALL=C find {UnixPath.ShellQuote(dir)} -maxdepth 1 -type f -name '*.tar.gz' -printf '%f|%s|%T@\\n'",
                TimeSpan.FromMinutes(1), ct);
            if (r.ExitCode != 0)
            {
                if (IsMissingPath(r)) return new ComposeBackupListResult { Ok = true };
                return new ComposeBackupListResult { Ok = false, Error = FirstLine(r.Combined) };
            }

            var items = new List<ComposeBackupInfo>();
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split('|');
                if (parts.Length < 3) continue;
                if (!long.TryParse(parts[1], out var size)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch))
                    epoch = 0;
                var modified = epoch > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)epoch).UtcDateTime
                    : DateTime.MinValue;
                items.Add(new ComposeBackupInfo
                {
                    Name = parts[0],
                    Path = UnixPath.Combine(dir, parts[0]),
                    Size = size,
                    ModifiedUtc = modified,
                });
            }

            return new ComposeBackupListResult
            {
                Ok = true,
                Items = items.OrderByDescending(i => i.Name, StringComparer.Ordinal).ToList(),
            };
        }
        catch (SudoPasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return new ComposeBackupListResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<ComposeBackupActionResult> DeleteBackupAsync(
        ComposeProjectRef project, string archiveName, CancellationToken ct = default)
    {
        var name = Path.GetFileName(archiveName);
        if (!name.Equals(archiveName, StringComparison.Ordinal)
            || !name.EndsWith(".tar.gz", StringComparison.Ordinal))
            return new ComposeBackupActionResult { Ok = false, Error = "备份文件名不合法。" };

        var path = UnixPath.Combine(UnixPath.Combine(project.WorkingDir, BackupDirName), name);
        try
        {
            var r = await RunFileShellAsync(
                $"rm -f -- {UnixPath.ShellQuote(path)}", TimeSpan.FromMinutes(1), ct);
            return r.ExitCode == 0
                ? new ComposeBackupActionResult { Ok = true }
                : new ComposeBackupActionResult { Ok = false, Error = FirstLine(r.Combined) };
        }
        catch (SudoPasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return new ComposeBackupActionResult { Ok = false, Error = ex.Message };
        }
    }

    // ================================================================ 工具方法

    /// <summary>执行文件类 shell 命令；权限不足且开启 AutoSudo 时用 sudo shell 重试。</summary>
    private async Task<CommandResult> RunFileShellAsync(
        string command, TimeSpan timeout, CancellationToken ct)
    {
        var r = await _session.RunAsync(command, timeout, ct);
        if (r.ExitCode == 0) return r;

        if (_session.Profile.AutoSudo && IsPermissionDenied(r))
        {
            var wrapped = _session.WrapSudoShell(command);
            r = await _session.RunAsync(wrapped, timeout, ct);
        }
        return r;
    }

    private async Task<bool> TestAsync(string command, CancellationToken ct)
    {
        var r = await RunFileShellAsync(command, TimeSpan.FromSeconds(15), ct);
        return r.ExitCode == 0;
    }

    private async Task<bool> IsWritableAsync(string dir, CancellationToken ct)
    {
        var r = await _session.RunAsync(
            $"test -w {UnixPath.ShellQuote(dir)}", TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode == 0) return true;

        if (_session.Profile.AutoSudo && !string.IsNullOrEmpty(_session.SudoPassword))
        {
            try
            {
                var s = await _session.RunAsync(
                    _session.WrapSudo($"test -w {UnixPath.ShellQuote(dir)}"),
                    TimeSpan.FromSeconds(15), ct);
                return s.ExitCode == 0;
            }
            catch { /* 仅作为提示 */ }
        }
        return false;
    }

    private async Task TryDeleteFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await _session.RunAsync(
                $"rm -f -- {UnixPath.ShellQuote(path)}", TimeSpan.FromSeconds(10), ct);
        }
        catch { /* 清理失败不影响主流程 */ }
    }

    private static bool IsPermissionError(Exception ex)
        => ex is SftpPermissionDeniedException
           || ex is PermissionDeniedException
           || ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("权限不足");

    private static bool IsPermissionDenied(CommandResult r)
    {
        var text = r.Output + " " + r.Error;
        return text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
               || text.Contains("权限不足")
               || text.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingPath(CommandResult r)
    {
        var text = r.Output + " " + r.Error;
        return text.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cannot access", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string text)
    {
        var t = (text ?? "").Replace("\r", "").Trim();
        if (t.Length == 0) return "未知错误";
        var i = t.IndexOf('\n');
        return (i >= 0 ? t[..i] : t).Trim();
    }

    private static ComposeApplyResult Fail(string message)
        => new() { Success = false, Error = message };
}
