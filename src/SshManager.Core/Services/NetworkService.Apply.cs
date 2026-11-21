using System.Text;
using System.Text.Json;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>IPv4 下发、120 秒看门狗、应用校验、提交/回滚。</summary>
public sealed partial class NetworkService
{
    private Action<string>? _applyLog;

    private void LogStep(string message) => _applyLog?.Invoke(message);

    // ================================================================ 计划

    public Task<IpApplyPlan> BuildIpApplyPlanAsync(
        NetInterface iface,
        IpConfig cfg,
        PersistentIpConfig persisted,
        NetworkEnvironment env,
        bool allowTakeover,
        CancellationToken ct = default)
    {
        var ifaceName = CleanIfaceName(iface.Name);
        cfg = cfg with { Iface = ifaceName };
        var errors = IpValidation.Validate(
            cfg, iface.Ipv4Addresses, env.SshServerIp, out var sshRemoved, out var sshUnknown);
        var warnings = new List<string>();

        if (iface.UnsupportedKind)
            errors.Add("该网卡是 bridge/bond/vlan 或桥接成员，暂不支持在此修改 IP。请在其上层接口配置。");
        if (persisted.ReadError.Length > 0)
            errors.Add(persisted.ReadError);

        string backend;
        string target;
        string renderer = "";
        var takeoverFiles = new List<string>();

        var netplanUsable = env.NetplanInstalled;
        var netplanDefines = persisted.Source == IpSourceKind.Netplan && persisted.IsAuthoritative;
        if (netplanDefines)
        {
            backend = "netplan";
        }
        else if (persisted.Source == IpSourceKind.NetworkManager
                 && !persisted.NmEphemeral
                 && persisted.NmUuid.Length > 0)
        {
            // 已有可写的持久 NM 连接：继续用 nmcli，避免 netplan 生成 profile 与它冲突
            backend = "nm";
        }
        else if (netplanUsable)
        {
            backend = "netplan";
        }
        else if (env.NmRunning && persisted.NmUuid.Length > 0)
        {
            backend = "nm";
            errors.Add("该 NetworkManager 连接是临时/netplan 生成的，重启后会失效。请先创建持久连接，或安装 netplan 后再改。");
        }
        else
        {
            backend = "";
            errors.Add("未找到可持久化的网络后端（需要 netplan 或可写的 NetworkManager 连接）。");
        }

        target = backend switch
        {
            "netplan" => NetplanTargetPath(ifaceName),
            "nm" => persisted.NmUuid,
            _ => "",
        };
        if (backend == "netplan")
        {
            renderer = persisted.Renderer.Length > 0
                ? persisted.Renderer
                : (iface.NmManaged && env.NmRunning ? "NetworkManager" : "networkd");

            if (persisted.ConflictComplex)
                errors.Add("检测到无法安全接管的 netplan 配置（其他文件也定义了该网卡，或包含复杂结构）。请先在服务器上合并配置。");
            else if (persisted.ConflictNeedsTakeover)
            {
                takeoverFiles.AddRange(persisted.ConflictFiles);
                if (!allowTakeover)
                    warnings.Add("其他 netplan 文件也定义了该网卡：需要勾选“允许接管”后才会应用。");
                else
                    warnings.Add("将备份并移除以下文件中的该网卡配置，由 99-sshmanager 文件接管：" +
                                 string.Join("、", persisted.ConflictFiles));
            }
            if (env.CloudInitPresent)
                warnings.Add("检测到 cloud-init：若其网络配置在重启时重建，可能覆盖本次修改；接管后如仍被重建，请手工禁用 cloud-init 网络配置。");
        }
        if (backend == "nm" && target.Length == 0)
            errors.Add("未找到 NetworkManager 连接 UUID。");

        if (iface.HasMultipleDefaultRoutes)
            warnings.Add("该网卡当前存在多条默认路由；本次只会写入一个默认网关。");

        var preview = backend switch
        {
            "netplan" => BuildNetplanYaml(cfg, renderer),
            "nm" when target.Length > 0 =>
                NmcliConfig.BuildModifyCommand(target, cfg) + "\n" + NmcliConfig.BuildUpCommand(target),
            _ => "",
        };

        return Task.FromResult(new IpApplyPlan
        {
            Backend = backend,
            Target = target,
            Renderer = renderer,
            Preview = preview,
            BackupRoot = RollbackProtocol.StateDir,
            NeedTakeover = takeoverFiles.Count > 0,
            TakeoverFiles = takeoverFiles,
            Warnings = warnings,
            Errors = errors,
            SshIpRemoved = sshRemoved,
            SshIpRemovedUnknown = sshUnknown,
            SshServerIp = env.SshServerIp,
            Iface = ifaceName,
            TargetConfig = new IpConfigSnapshot
            {
                Mode = cfg.Mode,
                Addresses = cfg.Addresses.ToList(),
                Gateway = cfg.Gateway,
                Dns = cfg.Dns.ToList(),
            },
        });
    }

    // ================================================================ 应用

    public async Task<IpApplyResult> ApplyIpConfigAsync(
        IpApplyPlan plan,
        IpConfig cfg,
        NetInterface iface,
        Func<string, Task<string?>> sudoRun,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        _applyLog = onLog;
        LogStep("=== 应用网络配置 ===");
        LogStep($"后端：{plan.Backend}；目标：{plan.Target}；renderer：{plan.Renderer}");
        if (plan.NeedTakeover)
            LogStep("接管 netplan 文件：" + string.Join("、", plan.TakeoverFiles));
        foreach (var w in plan.Warnings) LogStep("⚠ " + w);

        if (plan.Errors.Count > 0)
        {
            foreach (var e in plan.Errors) LogStep("✖ " + e);
            return new IpApplyResult
            {
                Success = false,
                Error = string.Join("\n", plan.Errors),
                Target = plan.Target,
            };
        }
        if (plan.Backend.Length == 0)
            return new IpApplyResult { Success = false, Error = "没有可用的网络后端。", Target = plan.Target };

        var token = RollbackProtocol.NewToken();
        var serverNow = await GetServerEpochAsync(ct);
        var deadline = serverNow + RollbackProtocol.RollbackSeconds;

        try
        {
            var install = await InstallWatchdogAsync(token, sudoRun, ct);
            if (install != null)
                return new IpApplyResult { Success = false, Error = install, Token = token, Target = plan.Target };

            if (plan.Backend == "netplan")
                return await ApplyNetplanAsync(plan, cfg, iface, token, deadline, sudoRun, ct);
            return await ApplyNmAsync(plan, cfg, iface, token, deadline, sudoRun, ct);
        }
        catch (SessionDisconnectedException)
        {
            LogStep("SSH 连接已断开；120 秒内未确认将自动回滚。");
            return new IpApplyResult
            {
                Success = false,
                Pending = true,
                SessionLost = true,
                Token = token,
                Target = plan.Target,
                Message = "SSH 连接已断开。120 秒内未确认将自动回滚；请重连后到网络页处理。",
            };
        }
        catch (Exception ex)
        {
            // 尽量立即回滚；失败则交给看门狗
            LogStep("✖ 应用过程出现异常：" + FirstLine(ex.Message));
            var rb = await SafeSudoAsync(sudoRun, RollbackProtocol.BuildRollbackNowCommand(token));
            return new IpApplyResult
            {
                Success = false,
                RolledBack = rb == null,
                Pending = rb != null,
                Token = token,
                Target = plan.Target,
                Error = ex.Message,
                Message = rb == null ? "应用出错，已恢复旧配置。" : "应用出错，已登记 120 秒自动回滚。",
            };
        }
    }

    private async Task<IpApplyResult> ApplyNetplanAsync(
        IpApplyPlan plan,
        IpConfig cfg,
        NetInterface iface,
        string token,
        long deadline,
        Func<string, Task<string?>> sudoRun,
        CancellationToken ct)
    {
        LogStep("--- netplan：创建备份目录 ---");
        var backup = RollbackProtocol.BackupDir(token);
        var target = plan.Target;
        var mk = await SafeSudoAsync(sudoRun,
            $"mkdir -p {Q(backup)} {Q(backup + "/disabled")} && chmod 700 {Q(backup)}");
        if (mk != null) return Fail(token, plan.Target, mk);

        // 备份目标文件
        LogStep("--- 备份目标 99 文件 ---");
        var bkTarget = await SafeSudoAsync(sudoRun,
            $"if [ -f {Q(target)} ]; then cp -a -- {Q(target)} {Q(backup + "/target.yaml")}; fi");
        if (bkTarget != null) return Fail(token, plan.Target, bkTarget);

        // 接管冲突文件：先全部备份并写 map；pending/current 写好后再移除原文件，
        // 保证任何失败都有可执行的回滚状态。
        LogStep($"--- 备份冲突文件（{plan.TakeoverFiles.Count} 个） ---");
        var mapLines = new List<string>();
        for (var i = 0; i < plan.TakeoverFiles.Count; i++)
        {
            var file = plan.TakeoverFiles[i];
            var saved = $"{i:00}-{Path.GetFileName(file)}";
            var cp = await SafeSudoAsync(sudoRun,
                $"cp -a -- {Q(file)} {Q(backup + "/disabled/" + saved)}");
            if (cp != null) return await RollbackAndFail(sudoRun, token, plan.Target, "备份冲突文件失败：" + cp, ct);
            mapLines.Add(file + "\t" + saved);
        }

        if (mapLines.Count > 0)
        {
            var mapTmp = $"/tmp/sshmanager-net-map-{token}.txt";
            var w = await _s.WriteFileBytesAsync(mapTmp,
                Encoding.UTF8.GetBytes(string.Join('\n', mapLines) + "\n"), ct);
            if (w.Status != WriteStatus.Ok)
                return await RollbackAndFail(sudoRun, token, plan.Target, "写入冲突文件清单失败：" + w.Describe(), ct);
            var im = await SafeSudoAsync(sudoRun,
                $"install -m 600 -- {Q(mapTmp)} {Q(backup + "/disabled.map")}");
            await TryRemoveTempAsync(mapTmp, ct);
            if (im != null) return await RollbackAndFail(sudoRun, token, plan.Target, im, ct);
        }

        // pending env/json + current（必须在移除冲突文件之前写好）
        LogStep("--- 写入 pending/current 状态 ---");
        var envVars = new Dictionary<string, string>
        {
            ["BACKEND"] = "netplan",
            ["TARGET_FILE"] = target,
            ["BACKUP_DIR"] = backup,
        };
        var meta = new RollbackProtocol.PendingMeta(
            token, iface.Name, "netplan", target, deadline,
            cfg.UseDhcp ? "DHCP" : $"{cfg.Addresses.Count} 个静态地址");
        var stateErr = await WritePendingStateAsync(
            token, envVars, meta, sudoRun, ct);
        if (stateErr != null) return await RollbackAndFail(sudoRun, token, plan.Target, stateErr, ct);

        // 看门狗必须在任何破坏性操作之前启动：即使紧接着断线也能回滚
        LogStep("--- 启动 120 秒回滚看门狗 ---");
        var start = await SafeSudoAsync(sudoRun, RollbackProtocol.BuildStartCommand(token));
        if (start != null)
            return await RollbackAndFail(sudoRun, token, plan.Target, "启动回滚看门狗失败：" + start, ct);

        if (plan.TakeoverFiles.Count > 0) LogStep("--- 移除冲突文件 ---");
        foreach (var file in plan.TakeoverFiles)
        {
            var rm = await SafeSudoAsync(sudoRun, $"rm -f -- {Q(file)}");
            if (rm != null) return await RollbackAndFail(sudoRun, token, plan.Target, "移除冲突文件失败：" + rm, ct);
        }

        // 写候选 YAML + generate 预校验
        LogStep("--- 写入候选 YAML 并 netplan generate ---");
        var yaml = BuildNetplanYaml(cfg with { Iface = plan.Iface }, plan.Renderer);
        var yamlTmp = $"/tmp/sshmanager-netplan-{token}.yaml";
        var yw = await _s.WriteFileBytesAsync(yamlTmp, Encoding.UTF8.GetBytes(yaml), ct);
        if (yw.Status != WriteStatus.Ok)
            return await RollbackAndFail(sudoRun, token, plan.Target, "写入临时文件失败：" + yw.Describe(), ct);
        var install = await SafeSudoAsync(sudoRun,
            $"install -m 600 -- {Q(yamlTmp)} {Q(target)} && netplan generate");
        await TryRemoveTempAsync(yamlTmp, ct);
        if (install != null)
            return await RollbackAndFail(sudoRun, token, plan.Target, "配置校验失败：" + install, ct);

        // 应用前校验合并后的有效配置，提前发现其他 netplan 文件残留的旧地址/DNS/路由
        LogStep("--- 预应用有效配置校验 ---");
        var effectiveYaml = await ReadNetplanTextAsync(ct);
        if (!string.IsNullOrWhiteSpace(effectiveYaml))
        {
            var (effectiveOk, effectiveErr) = NetplanConfig.EffectiveMatches(
                effectiveYaml, plan.Iface, cfg with { Iface = plan.Iface });
            if (!effectiveOk)
            {
                // 直接找出残留地址来自哪个 netplan 文件，避免用户只看到“地址集合不一致”
                var parsed = NetplanConfig.ParseEffective(effectiveYaml, plan.Iface);
                var expectedCidrs = cfg.Addresses.Select(a => a.Cidr)
                    .ToHashSet(StringComparer.Ordinal);
                var extraIps = parsed.Addresses
                    .Where(a => !expectedCidrs.Contains(a.Cidr))
                    .Select(a => a.Address)
                    .Distinct()
                    .ToList();
                var sourceText = "";
                if (extraIps.Count > 0)
                {
                    var grepCmd = "for ip in " + string.Join(' ', extraIps.Select(Q)) +
                                  "; do grep -Rl -- \"$ip\" /etc/netplan /lib/netplan " +
                                  "/usr/lib/netplan /run/netplan 2>/dev/null | sed \"s/^/[$ip] /\"; done";
                    CommandResult grep;
                    if (_s.IsRoot || !string.IsNullOrEmpty(_s.SudoPassword))
                        grep = await _s.RunAsync(_s.WrapSudoShell(grepCmd),
                            TimeSpan.FromSeconds(30), ct);
                    else
                        grep = await _s.RunAsync(grepCmd, TimeSpan.FromSeconds(30), ct);
                    sourceText = (grep.Output + grep.Error).Trim();
                    LogStep("残留地址来源：\n" + (sourceText.Length > 0 ? sourceText : "（未找到，可能来自 /run 或非 netplan 配置）"));
                }
                var detail = effectiveErr;
                if (sourceText.Length > 0) detail += "\n残留地址来源：\n" + sourceText;
                return await RollbackAndFail(sudoRun, token, plan.Target,
                    "生成后的有效配置与目标不一致：" + detail, ct);
            }
        }

        LogStep("--- netplan apply ---");
        var apply = await SafeSudoAsync(sudoRun, "netplan apply");
        if (apply != null)
            return await RollbackAndFail(sudoRun, token, plan.Target, "netplan apply 失败：" + apply, ct);

        LogStep("--- 运行时/有效配置校验 ---");
        var (ok, verifyErr) = await VerifyNetplanAsync(plan.Iface, cfg, ct);
        if (!_s.IsConnected)
        {
            return new IpApplyResult
            {
                Success = false,
                Pending = true,
                SessionLost = true,
                Token = token,
                Target = target,
                Message = "SSH 连接已断开。120 秒内未确认将自动回滚；请重连后到网络页处理。",
            };
        }
        if (!ok)
            return await RollbackAndFail(sudoRun, token, plan.Target, "应用后校验失败：" + verifyErr, ct);

        if (plan.SshIpRemoved)
        {
            LogStep("新配置移除了当前 SSH 地址：保持看门狗，不自动提交。");
            return new IpApplyResult
            {
                Success = true,
                Pending = true,
                Token = token,
                Target = target,
                Message = "已应用，但新配置不包含当前 SSH 地址。120 秒后会自动回滚；如已确认新地址可连通，请在网络页点“保留新配置”。",
            };
        }

        LogStep("--- 写入 commit（取消自动回滚） ---");
        var commit = await SafeSudoAsync(sudoRun, RollbackProtocol.BuildCommitCommand(token));
        if (commit != null)
        {
            LogStep("写入 commit 失败：" + FirstLine(commit));
            return new IpApplyResult
            {
                Success = true,
                Pending = true,
                Token = token,
                Target = target,
                Message = "已应用，但写入确认标记失败，将按 120 秒回滚处理：" + commit,
            };
        }
        LogStep("✔ 已提交，自动回滚已取消。");
        return new IpApplyResult
        {
            Success = true,
            Committed = true,
            Token = token,
            Target = target,
            Message = "已应用并校验通过，持久化配置已写入；自动回滚已取消。",
        };
    }

    private async Task<IpApplyResult> ApplyNmAsync(
        IpApplyPlan plan,
        IpConfig cfg,
        NetInterface iface,
        string token,
        long deadline,
        Func<string, Task<string?>> sudoRun,
        CancellationToken ct)
    {
        LogStep("--- NetworkManager：创建备份目录 ---");
        var backup = RollbackProtocol.BackupDir(token);
        var id = plan.Target;
        var mk = await SafeSudoAsync(sudoRun,
            $"mkdir -p {Q(backup)} && chmod 700 {Q(backup)}");
        if (mk != null) return Fail(token, plan.Target, mk);

        var oldMethod = (await _s.RunAsync($"LC_ALL=C nmcli -g ipv4.method connection show {Q(id)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var oldAddrs = (await _s.RunAsync($"LC_ALL=C nmcli -g ipv4.addresses connection show {Q(id)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var oldGw = (await _s.RunAsync($"LC_ALL=C nmcli -g ipv4.gateway connection show {Q(id)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var oldDns = (await _s.RunAsync($"LC_ALL=C nmcli -g ipv4.dns connection show {Q(id)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var oldIgnore = (await _s.RunAsync($"LC_ALL=C nmcli -g ipv4.ignore-auto-dns connection show {Q(id)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();
        var oldRoutes = (await _s.RunAsync($"LC_ALL=C nmcli -g ipv4.routes connection show {Q(id)} 2>/dev/null",
            TimeSpan.FromSeconds(15), ct)).Output.Trim();

        LogStep("--- 写入 pending/current 状态 ---");
        var envVars = new Dictionary<string, string>
        {
            ["BACKEND"] = "nm",
            ["NM_ID"] = id,
            ["OLD_METHOD"] = oldMethod.Length > 0 ? oldMethod : "auto",
            ["OLD_ADDRS"] = oldAddrs,
            ["OLD_GW"] = oldGw,
            ["OLD_DNS"] = oldDns,
            ["OLD_IGNORE_DNS"] = oldIgnore.Length > 0 ? oldIgnore : "no",
            ["OLD_ROUTES"] = oldRoutes,
        };
        var meta = new RollbackProtocol.PendingMeta(
            token, iface.Name, "nm", id, deadline,
            cfg.UseDhcp ? "DHCP" : $"{cfg.Addresses.Count} 个静态地址");
        var stateErr = await WritePendingStateAsync(token, envVars, meta, sudoRun, ct);
        if (stateErr != null) return await RollbackAndFail(sudoRun, token, plan.Target, stateErr, ct);

        LogStep("--- 启动 120 秒回滚看门狗 ---");
        var start = await SafeSudoAsync(sudoRun, RollbackProtocol.BuildStartCommand(token));
        if (start != null)
            return await RollbackAndFail(sudoRun, token, plan.Target, "启动回滚看门狗失败：" + start, ct);

        LogStep("--- nmcli 修改连接并启用 ---");
        var modify = await SafeSudoAsync(sudoRun, NmcliConfig.BuildModifyCommand(id, cfg));
        if (modify != null)
            return await RollbackAndFail(sudoRun, token, plan.Target, "nmcli 修改失败：" + modify, ct);
        var up = await SafeSudoAsync(sudoRun, NmcliConfig.BuildUpCommand(id));
        if (up != null)
            return await RollbackAndFail(sudoRun, token, plan.Target, "nmcli 启用连接失败：" + up, ct);

        LogStep("--- 运行时校验 ---");
        var (ok, verifyErr) = await VerifyRuntimeAsync(plan.Iface, cfg, ct);
        if (!_s.IsConnected)
        {
            return new IpApplyResult
            {
                Success = false,
                Pending = true,
                SessionLost = true,
                Token = token,
                Target = plan.Target,
                Message = "SSH 连接已断开。120 秒内未确认将自动回滚；请重连后到网络页处理。",
            };
        }
        if (!ok)
            return await RollbackAndFail(sudoRun, token, plan.Target, "应用后校验失败：" + verifyErr, ct);

        if (plan.SshIpRemoved)
        {
            LogStep("新配置移除了当前 SSH 地址：保持看门狗，不自动提交。");
            return new IpApplyResult
            {
                Success = true,
                Pending = true,
                Token = token,
                Target = plan.Target,
                Message = "已应用，但新配置不包含当前 SSH 地址。120 秒后会自动回滚；如已确认新地址可连通，请在网络页点“保留新配置”。",
            };
        }

        LogStep("--- 写入 commit（取消自动回滚） ---");
        var commit = await SafeSudoAsync(sudoRun, RollbackProtocol.BuildCommitCommand(token));
        if (commit != null)
        {
            LogStep("写入 commit 失败：" + FirstLine(commit));
            return new IpApplyResult
            {
                Success = true,
                Pending = true,
                Token = token,
                Target = plan.Target,
                Message = "已应用，但写入确认标记失败，将按 120 秒回滚处理：" + commit,
            };
        }
        LogStep("✔ 已提交，自动回滚已取消。");
        return new IpApplyResult
        {
            Success = true,
            Committed = true,
            Token = token,
            Target = plan.Target,
            Message = "已应用并校验通过，NetworkManager 连接已持久化；自动回滚已取消。",
        };
    }

    // ================================================================ pending / 提交 / 回滚

    public async Task<PendingIpChange?> GetPendingIpChangeAsync(CancellationToken ct = default)
    {
        var cur = (await _s.RunAsync($"cat {Q(RollbackProtocol.CurrentPath)} 2>/dev/null",
            TimeSpan.FromSeconds(10), ct)).Output.Trim();
        if (!RollbackProtocol.IsValidToken(cur)) return null;

        var json = (await _s.RunAsync(
            $"cat {Q(RollbackProtocol.PendingJsonPath(cur))} 2>/dev/null",
            TimeSpan.FromSeconds(10), ct)).Output;
        if (string.IsNullOrWhiteSpace(json)) return null;

        var serverNow = await GetServerEpochAsync(ct);
        var pending = RollbackProtocol.ParsePending(json, serverNow);
        if (pending == null) return null;

        var committed = (await _s.RunAsync(
            $"test -f {Q(RollbackProtocol.CommitPath(cur))} && echo yes",
            TimeSpan.FromSeconds(10), ct)).Output.Contains("yes", StringComparison.OrdinalIgnoreCase);
        var result = (await _s.RunAsync(
            $"cat {Q(RollbackProtocol.ResultPath(cur))} 2>/dev/null",
            TimeSpan.FromSeconds(10), ct)).Output.Trim();

        return new PendingIpChange
        {
            Token = pending.Token,
            Iface = pending.Iface,
            Backend = pending.Backend,
            Target = pending.Target,
            DeadlineEpoch = pending.DeadlineEpoch,
            ServerNowEpoch = pending.ServerNowEpoch,
            RemainingSeconds = pending.RemainingSeconds,
            Committed = committed,
            RolledBack = result.StartsWith("rolledback", StringComparison.OrdinalIgnoreCase),
            Summary = pending.Summary,
            Result = result,
        };
    }

    public async Task<string?> CommitPendingAsync(
        string token, Func<string, Task<string?>> sudoRun, CancellationToken ct = default)
    {
        if (!RollbackProtocol.IsValidToken(token)) return "非法 token。";
        return await SafeSudoAsync(sudoRun, RollbackProtocol.BuildCommitCommand(token));
    }

    public async Task<string?> RollbackPendingAsync(
        string token, Func<string, Task<string?>> sudoRun, CancellationToken ct = default)
    {
        if (!RollbackProtocol.IsValidToken(token)) return "非法 token。";
        return await SafeSudoAsync(sudoRun, RollbackProtocol.BuildRollbackNowCommand(token));
    }

    public async Task<long> GetServerEpochAsync(CancellationToken ct = default)
    {
        var text = (await _s.RunAsync("date +%s", TimeSpan.FromSeconds(10), ct)).Output.Trim();
        return long.TryParse(text, out var n) ? n : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    // ================================================================ 校验

    private async Task<(bool Ok, string Error)> VerifyNetplanAsync(
        string iface, IpConfig cfg, CancellationToken ct)
    {
        var runtime = await VerifyRuntimeAsync(iface, cfg, ct);
        if (!runtime.Ok) return runtime;

        var effective = await ReadNetplanTextAsync(ct);
        if (string.IsNullOrWhiteSpace(effective))
        {
            LogStep("有效 netplan 配置不可读，以运行时校验为准。");
            return (true, "");
        }

        var parsed = NetplanConfig.ParseEffective(effective, iface);
        LogStep($"有效配置：地址=[{string.Join(", ", parsed.Addresses)}] 网关={parsed.Gateway} DNS=[{string.Join(", ", parsed.Dns)}] renderer={parsed.Renderer}");
        var match = NetplanConfig.EffectiveMatches(effective, iface, cfg with { Iface = iface });
        if (match.Ok) LogStep("有效配置校验通过。");
        else LogStep("有效配置校验失败：" + match.Error);
        return match.Ok ? (true, "") : (false, match.Error);
    }

    private async Task<(bool Ok, string Error)> VerifyRuntimeAsync(
        string iface, IpConfig cfg, CancellationToken ct)
    {
        var timeoutSeconds = cfg.UseDhcp ? 45 : 25;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var last = "尚未检测到地址。";

        while (DateTime.UtcNow < deadline)
        {
            if (!_s.IsConnected) return (false, "SSH 连接已断开。");

            var addrJson = (await _s.RunAsync(
                $"LC_ALL=C ip -j addr show dev {Q(iface)}",
                TimeSpan.FromSeconds(15), ct)).Output;
            var got = ParseIpv4Json(addrJson);
            if (cfg.UseDhcp)
            {
                if (got.Count > 0)
                {
                    LogStep("运行时校验通过（DHCP 已获取地址：" + string.Join(", ", got) + "）。");
                    return (true, "");
                }
                last = "DHCP 尚未获取到 IPv4 地址。";
            }
            else
            {
                var want = cfg.Addresses.Select(a => a.Cidr).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var now = got.OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (want.SequenceEqual(now))
                {
                    if (cfg.Gateway.Length == 0)
                    {
                        LogStep("运行时校验通过。");
                        return (true, "");
                    }
                    var routeJson = (await _s.RunAsync(
                        "LC_ALL=C ip -j route show default",
                        TimeSpan.FromSeconds(15), ct)).Output;
                    if (RouteHasGateway(routeJson, iface, cfg.Gateway))
                    {
                        LogStep("运行时校验通过（地址与网关均生效）。");
                        return (true, "");
                    }
                    last = $"地址已生效，但未检测到默认网关 {cfg.Gateway}。当前路由：{routeJson.Trim()}";
                }
                else
                {
                    last = $"地址集合不一致：期望 [{string.Join(", ", want)}]，实际 [{string.Join(", ", now)}]。";
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        LogStep("运行时校验失败：" + last);
        return (false, last);
    }

    private Task<string> ReadNetplanTextAsync(CancellationToken ct)
        => ReadNetplanYamlAsync(ct);

    private static List<string> ParseIpv4Json(string json)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("addr_info", out var infos) || infos.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var info in infos.EnumerateArray())
                {
                    if (info.ValueKind != JsonValueKind.Object) continue;
                    var family = info.TryGetProperty("family", out var f) ? f.GetString() : "";
                    if (family != "inet") continue;
                    var local = info.TryGetProperty("local", out var l) ? l.GetString() : "";
                    var prefix = info.TryGetProperty("prefixlen", out var p) && p.TryGetInt32(out var n) ? n : 0;
                    if (local != null) list.Add($"{local}/{prefix}");
                }
            }
        }
        catch { /* 解析失败按空处理 */ }
        return list;
    }

    private static bool RouteHasGateway(string json, string iface, string gateway)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var dev = el.TryGetProperty("dev", out var d) ? d.GetString() : "";
                var gw = el.TryGetProperty("gateway", out var g) ? g.GetString() : "";
                if (string.Equals(dev, iface, StringComparison.Ordinal)
                    && string.Equals(gw, gateway, StringComparison.Ordinal))
                    return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    // ================================================================ 工具

    private async Task<string?> InstallWatchdogAsync(
        string token, Func<string, Task<string?>> sudoRun, CancellationToken ct)
    {
        var upload = $"/tmp/sshmanager-netrollback-{token}.sh";
        var w = await _s.WriteFileBytesAsync(upload,
            Encoding.UTF8.GetBytes(RollbackProtocol.BuildWatchdogScript()), ct);
        if (w.Status != WriteStatus.Ok) return "上传回滚脚本失败：" + w.Describe();
        var err = await SafeSudoAsync(sudoRun, RollbackProtocol.BuildInstallScriptCommand(upload));
        await TryRemoveTempAsync(upload, ct);

        if (err == null)
        {
            // 仅保留最近 10 份备份；失败不影响本次应用
            var state = UnixPath.ShellQuote(RollbackProtocol.StateDir);
            await SafeSudoAsync(sudoRun,
                $"find {state} -maxdepth 1 -type d -name 'backup-*' -printf '%f\\n' 2>/dev/null | " +
                $"sort -r | tail -n +11 | while read -r d; do rm -rf -- {state}/\"$d\"; done; true");
        }
        return err;
    }

    private async Task<string?> WritePendingStateAsync(
        string token,
        IReadOnlyDictionary<string, string> envVars,
        RollbackProtocol.PendingMeta meta,
        Func<string, Task<string?>> sudoRun,
        CancellationToken ct)
    {
        var envTmp = $"/tmp/sshmanager-net-env-{token}";
        var jsonTmp = $"/tmp/sshmanager-net-json-{token}";
        var curTmp = $"/tmp/sshmanager-net-current-{token}";
        try
        {
            var ew = await _s.WriteFileBytesAsync(envTmp,
                Encoding.UTF8.GetBytes(RollbackProtocol.BuildPendingEnv(envVars)), ct);
            if (ew.Status != WriteStatus.Ok) return "写入 pending 环境失败：" + ew.Describe();
            var jw = await _s.WriteFileBytesAsync(jsonTmp,
                Encoding.UTF8.GetBytes(RollbackProtocol.BuildPendingJson(meta)), ct);
            if (jw.Status != WriteStatus.Ok) return "写入 pending 元数据失败：" + jw.Describe();
            var cw = await _s.WriteFileBytesAsync(curTmp,
                Encoding.UTF8.GetBytes(token + "\n"), ct);
            if (cw.Status != WriteStatus.Ok) return "写入 current 失败：" + cw.Describe();

            var cmd = $"install -m 600 -- {Q(envTmp)} {Q(RollbackProtocol.PendingEnvPath(token))} && " +
                      $"install -m 644 -- {Q(jsonTmp)} {Q(RollbackProtocol.PendingJsonPath(token))} && " +
                      $"install -m 644 -- {Q(curTmp)} {Q(RollbackProtocol.CurrentPath)}";
            return await SafeSudoAsync(sudoRun, cmd);
        }
        finally
        {
            await TryRemoveTempAsync(envTmp, ct);
            await TryRemoveTempAsync(jsonTmp, ct);
            await TryRemoveTempAsync(curTmp, ct);
        }
    }

    private async Task<string?> SafeSudoAsync(
        Func<string, Task<string?>> sudoRun, string command)
    {
        LogStep("$ " + command);
        try
        {
            var err = await sudoRun(command);
            if (err == null) LogStep("  ✔");
            else LogStep("  ✖ " + FirstLine(err));
            return err;
        }
        catch (SessionDisconnectedException) { throw; }
        catch (SudoPasswordRequiredException) { return "需要 sudo 密码。"; }
        catch (Exception ex)
        {
            LogStep("  ✖ " + FirstLine(ex.Message));
            return ex.Message;
        }
    }

    private async Task<IpApplyResult> RollbackAndFail(
        Func<string, Task<string?>> sudoRun,
        string token,
        string target,
        string error,
        CancellationToken ct)
    {
        LogStep("✖ 应用失败：" + FirstLine(error));
        LogStep("--- 立即恢复旧配置 ---");
        var rb = await SafeSudoAsync(sudoRun, RollbackProtocol.BuildRollbackNowCommand(token));
        if (rb == null)
        {
            LogStep("已恢复旧配置。");
            return new IpApplyResult
            {
                Success = false,
                RolledBack = true,
                Token = token,
                Target = target,
                Error = error,
                Message = "应用失败，已恢复旧配置。",
            };
        }

        LogStep("立即恢复失败：" + FirstLine(rb) + "；120 秒看门狗将继续尝试。");
        return new IpApplyResult
        {
            Success = false,
            Pending = true,
            Token = token,
            Target = target,
            Error = error,
            Message = "应用失败，立即恢复未成功；120 秒看门狗将继续尝试回滚。" + rb,
        };
    }

    /// <summary>只读诊断：收集 ip/netplan/NM/回滚状态，供 UI 复制反馈。</summary>
    public async Task<string> CollectIpDiagnosticsAsync(
        NetInterface iface, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var ifaceName = CleanIfaceName(iface.Name);

        void Add(string title, string text)
        {
            sb.AppendLine("=== " + title + " ===");
            sb.AppendLine((text ?? "").Trim());
            sb.AppendLine();
        }

        async Task AddCmd(string title, string command, bool sudo = false)
        {
            CommandResult r;
            if (sudo && (_s.IsRoot || !string.IsNullOrEmpty(_s.SudoPassword)))
                r = await _s.RunAsync(_s.WrapSudoRaw(command), TimeSpan.FromSeconds(25), ct);
            else
                r = await _s.RunAsync(command, TimeSpan.FromSeconds(25), ct);
            Add(title, (r.Output + "\n" + r.Error).Trim() + $"\n(exit {r.ExitCode})");
        }

        if (!_s.IsRoot && string.IsNullOrEmpty(_s.SudoPassword))
            sb.AppendLine("提示：当前会话没有 sudo 密码，部分命令可能因权限不足而失败。\n");

        Add("接口", $"name={ifaceName} state={iface.State} mac={iface.Mac} nmManaged={iface.NmManaged}");
        Add("IPv4/IPv6", string.Join(", ", iface.Addresses));
        Add("网关", string.Join(", ", iface.Gateways));
        Add("DNS", string.Join(", ", iface.DnsServers));

        await AddCmd("系统", "id; hostname; cat /etc/os-release 2>/dev/null | head -4; netplan --version 2>&1");
        await AddCmd("netplan 文件", "ls -l /etc/netplan 2>&1");
        await AddCmd("ip addr", $"LC_ALL=C ip -j addr show dev {Q(ifaceName)} 2>&1");
        await AddCmd("默认路由", "LC_ALL=C ip -j route show default 2>&1");
        await AddCmd("NM 设备", "LC_ALL=C nmcli -t -f DEVICE,STATE,CONNECTION device status 2>&1");
        await AddCmd("NM 连接",
            $"con=$(LC_ALL=C nmcli -g GENERAL.CONNECTION device show {Q(ifaceName)} 2>/dev/null); " +
            $"echo CON=$con; [ -n \"$con\" ] && LC_ALL=C nmcli -f " +
            "connection.id,connection.uuid,connection.filename,ipv4.method,ipv4.addresses," +
            "ipv4.gateway,ipv4.dns,connection.autoconnect connection show \"$con\" 2>&1");
        await AddCmd("netplan 有效配置",
            $"LC_ALL=C netplan get ethernets.{ifaceName} 2>/dev/null", sudo: true);
        await AddCmd("99 文件",
            $"cat {Q(NetplanTargetPath(ifaceName))} 2>/dev/null", sudo: true);
        await AddCmd("回滚状态",
            "ls -la /var/lib/sshmanager-net 2>/dev/null; echo CURRENT=; " +
            "cat /var/lib/sshmanager-net/current 2>/dev/null; echo; " +
            "cat /var/lib/sshmanager-net/result-* 2>/dev/null | tail -5", sudo: true);

        return sb.ToString();
    }

    public static string CleanIfaceName(string name)
        => (name ?? "").Split('@')[0].Trim();

    private static string FirstLine(string? text)
    {
        var t = (text ?? "").Replace("\r", "").Trim();
        if (t.Length == 0) return "未知错误";
        var i = t.IndexOf('\n');
        return (i >= 0 ? t[..i] : t).Trim();
    }

    private static IpApplyResult Fail(string token, string target, string error)
        => new()
        {
            Success = false,
            Token = token,
            Target = target,
            Error = error,
            Message = "应用未开始或已中止。",
        };

    private async Task TryRemoveTempAsync(string path, CancellationToken ct)
    {
        try
        {
            await _s.RunAsync($"rm -f -- {Q(path)}", TimeSpan.FromSeconds(10), ct);
        }
        catch { /* ignore */ }
    }
}
