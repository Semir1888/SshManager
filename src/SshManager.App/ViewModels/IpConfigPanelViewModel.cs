using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.App.Views;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

/// <summary>网卡 IPv4 配置面板：多地址、多 DNS、单网关、预览、持久化与 120 秒回滚。</summary>
public partial class IpConfigPanelViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly NetworkService _net;
    private readonly MainWindowViewModel _owner;
    private readonly Func<Task> _refreshInterfaces;
    private readonly SudoRunner _sudo;

    private readonly StringBuilder _applyLog = new();
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _pendingTimer;
    private DateTime _pendingDeadlineUtc;
    private NetIfItemViewModel? _row;
    private NetInterface? _iface;
    private NetworkEnvironment? _env;
    private PersistentIpConfig? _persisted;
    private PendingIpChange? _pending;
    private bool _suppress;
    private bool _initialized;

    public IpConfigPanelViewModel(
        SshSession session,
        NetworkService net,
        MainWindowViewModel owner,
        Func<Task> refreshInterfaces)
    {
        _session = session;
        _net = net;
        _owner = owner;
        _refreshInterfaces = refreshInterfaces;
        _sudo = new SudoRunner(session, net);
    }

    public ObservableCollection<IpAddressRowViewModel> AddressRows { get; } = new();
    public ObservableCollection<DnsRowViewModel> DnsRows { get; } = new();

    [ObservableProperty] private bool _useDhcp = true;
    [ObservableProperty] private bool _useStatic;
    [ObservableProperty] private string _gatewayText = "";
    [ObservableProperty] private string _ifStatus = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _ipv6Text = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _persistenceText = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _hasValidationError;
    [ObservableProperty] private string _sshRiskText = "";
    [ObservableProperty] private bool _hasSshRisk;
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private string _pendingText = "";
    [ObservableProperty] private string _pendingRemaining = "";
    [ObservableProperty] private bool _isUnsupported;
    [ObservableProperty] private string _unsupportedText = "";
    [ObservableProperty] private bool _isPreviewOpen;
    [ObservableProperty] private bool _canReadPersisted;
    [ObservableProperty] private string _applyLogText = "";
    [ObservableProperty] private bool _isApplyLogOpen;

    // ---------------------------------------------------------------- 选择网卡

    public void SetInterface(NetIfItemViewModel? row)
    {
        var model = row?.Model;
        var nameChanged = !string.Equals(_row?.Name, row?.Name, StringComparison.Ordinal);
        _row = row;
        _iface = model;

        if (model == null)
        {
            _initialized = false;
            _iface = null;
            SummaryText = "";
            Ipv6Text = "";
            SourceText = "";
            PersistenceText = "";
            IsUnsupported = false;
            CanApply = false;
            return;
        }

        UpdateSummary();
        IsUnsupported = model.UnsupportedKind;
        UnsupportedText = model.UnsupportedKind
            ? "该网卡是 bridge/bond/vlan 或桥接成员，暂不支持在此修改 IP。"
            : "";

        if (nameChanged || !_initialized)
        {
            _initialized = true;
            _ = ReloadAsync(force: true);
        }
        else
        {
            // 刷新只更新摘要与 pending，不覆盖正在编辑的表单
            _ = RefreshPendingAsync();
        }
    }

    private void UpdateSummary()
    {
        var m = _iface;
        if (m == null) return;
        var v4 = m.Ipv4Addresses.Count > 0
            ? string.Join(", ", m.Ipv4Addresses.Select(a => a.Cidr))
            : "无 IPv4 地址";
        SummaryText = $"状态 {m.State} · MAC {m.Mac} · IPv4 {v4}";
        Ipv6Text = m.Ipv6Addresses.Count > 0
            ? "IPv6（只读）：" + string.Join(", ", m.Ipv6Addresses.Take(4))
            : "";
    }

    // ---------------------------------------------------------------- 载入

    private async Task ReloadAsync(bool force)
    {
        if (_iface == null) return;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsLoading = true;
        CanApply = false;
        try
        {
            var env = await _net.GetEnvironmentAsync(force);
            var persisted = await _net.GetPersistedIpConfigAsync(_iface, env, cts.Token);
            if (cts.IsCancellationRequested) return;
            _env = env;
            _persisted = persisted;

            if (force || !_initialized)
                FillForm(persisted, _iface);

            SourceText = persisted.Source switch
            {
                IpSourceKind.Netplan => "netplan",
                IpSourceKind.NetworkManager => "NetworkManager",
                IpSourceKind.RuntimeOnly => "运行态（未找到持久化配置）",
                IpSourceKind.Unknown => "无法读取持久化配置",
                _ => "未配置",
            };
            PersistenceText = persisted.Source switch
            {
                IpSourceKind.Netplan => "持久化文件：" + persisted.PrimaryFile,
                IpSourceKind.NetworkManager => $"NM 连接：{persisted.NmConnectionName}（{persisted.NmFile}）",
                IpSourceKind.Unknown => persisted.ReadError,
                IpSourceKind.RuntimeOnly => "仅运行态；应用前请先读取持久化配置。",
                _ => "",
            };
            CanReadPersisted = persisted.Source == IpSourceKind.Unknown
                               || persisted.ReadError.Length > 0;
            // 不要覆盖上一次应用失败留下的详细状态
            if (persisted.ReadError.Length > 0 && string.IsNullOrWhiteSpace(IfStatus))
                IfStatus = persisted.ReadError;

            RebuildFormState();
            await RefreshPendingAsync();
        }
        catch (OperationCanceledException) { }
        catch (SessionDisconnectedException)
        {
            IfStatus = "连接已断开。";
        }
        catch (Exception ex)
        {
            IfStatus = "读取网络配置失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
                RebuildFormState();
            }
        }
    }

    private void FillForm(PersistentIpConfig persisted, NetInterface iface)
    {
        var authoritative = persisted.Source is IpSourceKind.Netplan or IpSourceKind.NetworkManager
                            && persisted.IsAuthoritative;

        var mode = authoritative ? persisted.Mode
            : iface.Ipv4Addresses.Count > 0 ? IpMode.Static : IpMode.Dhcp;
        var addresses = authoritative ? persisted.Addresses : iface.Ipv4Addresses;
        var gateway = authoritative ? persisted.Gateway : iface.Gateway;
        var dns = authoritative ? persisted.Dns : iface.DnsServers;

        _suppress = true;
        UseDhcp = mode == IpMode.Dhcp;
        UseStatic = !UseDhcp;
        GatewayText = gateway;
        AddressRows.Clear();
        foreach (var a in addresses)
            AddressRows.Add(new IpAddressRowViewModel(this, a.Address, a.Prefix.ToString()));
        if (AddressRows.Count == 0)
            AddressRows.Add(new IpAddressRowViewModel(this));
        DnsRows.Clear();
        foreach (var d in dns)
            DnsRows.Add(new DnsRowViewModel(this, d));
        if (DnsRows.Count == 0)
            DnsRows.Add(new DnsRowViewModel(this));
        _suppress = false;
        OnFormChanged();
    }

    [RelayCommand]
    private async Task Reload()
    {
        await _refreshInterfaces();
        await ReloadAsync(force: true);
    }

    [RelayCommand]
    private async Task ReadPersisted()
    {
        if (_iface == null) return;
        var (ok, pw) = await Dialogs.InputAsync(
            "sudo 密码",
            $"读取 {_iface.Name} 的 netplan 持久化配置需要 sudo 权限，请输入密码（仅保存在本次会话内存）：",
            "", password: true);
        if (!ok || pw.Length == 0) return;
        _session.SudoPassword = pw;
        _env = await _net.GetEnvironmentAsync(force: true);
        await ReloadAsync(force: true);
    }

    // ---------------------------------------------------------------- 表单

    internal void OnFormChanged()
    {
        if (_suppress) return;
        RebuildFormState();
    }

    partial void OnUseDhcpChanged(bool value)
    {
        if (_suppress) return;
        if (value && UseStatic)
        {
            _suppress = true;
            UseStatic = false;
            _suppress = false;
        }
        OnFormChanged();
    }

    partial void OnUseStaticChanged(bool value)
    {
        if (_suppress) return;
        if (value && UseDhcp)
        {
            _suppress = true;
            UseDhcp = false;
            _suppress = false;
        }
        OnFormChanged();
    }

    partial void OnGatewayTextChanged(string value) => OnFormChanged();

    internal void AddAddressRow()
    {
        AddressRows.Add(new IpAddressRowViewModel(this));
        OnFormChanged();
    }

    // 方法名不要以 Command 结尾：否则会与 RelayCommand 生成的公开命令属性重名，
    // 编译绑定会解析到私有方法并抛 MethodAccessException。
    [RelayCommand]
    private void AddAddress() => AddAddressRow();

    internal void RemoveAddress(IpAddressRowViewModel row)
    {
        AddressRows.Remove(row);
        OnFormChanged();
    }

    internal void AddDnsRow()
    {
        DnsRows.Add(new DnsRowViewModel(this));
        OnFormChanged();
    }

    [RelayCommand]
    private void AddDns() => AddDnsRow();

    internal void RemoveDns(DnsRowViewModel row)
    {
        DnsRows.Remove(row);
        OnFormChanged();
    }

    private IpConfig BuildConfig()
    {
        var mode = UseDhcp ? IpMode.Dhcp : IpMode.Static;
        var addresses = new List<Ipv4Address>();
        var dns = new List<string>();
        if (mode == IpMode.Static)
        {
            foreach (var row in AddressRows)
            {
                if (IpValidation.ValidateAddressRow(row.Address, row.Mask, out var a) == null)
                    addresses.Add(a);
            }
        }
        foreach (var row in DnsRows)
        {
            if (IpValidation.TryParseIPv4Strict(row.Server, out var d) && !dns.Contains(d))
                dns.Add(d);
        }

        var gateway = (GatewayText ?? "").Trim();
        if (gateway.Length > 0 && IpValidation.TryParseIPv4Strict(gateway, out var gw))
            gateway = gw;
        return new IpConfig(_iface?.Name ?? "", mode, addresses, gateway, dns);
    }

    private void RebuildFormState()
    {
        if (_suppress) return;
        if (_iface == null || _env == null)
        {
            CanApply = false;
            return;
        }

        var errors = new List<string>();
        if (!UseDhcp)
        {
            foreach (var row in AddressRows)
            {
                row.ErrorText = IpValidation.ValidateAddressRow(row.Address, row.Mask, out _) ?? "";
                if (row.ErrorText.Length > 0) errors.Add("地址：" + row.ErrorText);
            }
            if (AddressRows.Count == 0)
                errors.Add("静态模式至少需要一个 IPv4 地址。");
        }
        else
        {
            foreach (var row in AddressRows) row.ErrorText = "";
        }

        foreach (var row in DnsRows)
        {
            if (string.IsNullOrWhiteSpace(row.Server))
            {
                row.ErrorText = "";
                continue;
            }
            row.ErrorText = IpValidation.TryParseIPv4Strict(row.Server, out _)
                ? ""
                : "DNS 格式不正确。";
            if (row.ErrorText.Length > 0) errors.Add("DNS：" + row.ErrorText);
        }

        var cfg = BuildConfig();
        var validation = IpValidation.Validate(
            cfg, _iface.Ipv4Addresses, _env.SshServerIp, out var sshRemoved, out var sshUnknown);
        errors.AddRange(validation);

        ValidationText = string.Join("\n", errors.Distinct());
        HasValidationError = errors.Count > 0;

        HasSshRisk = false;
        SshRiskText = "";
        if (sshRemoved)
        {
            HasSshRisk = true;
            SshRiskText = $"新配置会移除当前 SSH 地址 {_env.SshServerIp}；应用后可能断线，120 秒未确认将自动回滚。";
        }
        else if (UseDhcp && _env.SshServerIp.Length > 0
                 && _iface.Ipv4Addresses.Any(a => a.Address.Equals(_env.SshServerIp, StringComparison.Ordinal)))
        {
            HasSshRisk = true;
            SshRiskText = "切换为 DHCP 后，当前 SSH 地址可能变化；建议保留一个静态地址或使用自动回滚。";
        }
        else if (sshUnknown)
        {
            HasSshRisk = true;
            SshRiskText = "无法确定当前 SSH 连接使用的服务器地址，请谨慎修改。";
        }

        PreviewText = BuildPreview(cfg);
        CanApply = !IsUnsupported
                   && !HasValidationError
                   && !IsBusy
                   && !IsLoading
                   && !IsPending
                   && _persisted is { Source: IpSourceKind.Netplan or IpSourceKind.NetworkManager }
                   && _persisted.ReadError.Length == 0;
    }

    private string BuildPreview(IpConfig cfg)
    {
        var useNetplan = _env?.NetplanInstalled == true;
        if (!useNetplan && _persisted?.Source == IpSourceKind.NetworkManager)
        {
            var id = _persisted.NmUuid.Length > 0 ? _persisted.NmUuid : _persisted.NmConnectionName;
            return "目标：NetworkManager 连接 " + id + "\n\n" +
                   NmcliConfig.BuildModifyCommand(id, cfg) + "\n" +
                   NmcliConfig.BuildUpCommand(id);
        }

        var renderer = _persisted?.Renderer is { Length: > 0 } r
            ? r
            : (_iface?.NmManaged == true && _env?.NmRunning == true ? "NetworkManager" : "networkd");
        var path = NetworkService.NetplanTargetPath(_iface?.Name ?? "");
        return $"目标：{path}（renderer: {renderer}）\n\n" +
               NetworkService.BuildNetplanYaml(cfg, renderer);
    }

    // ---------------------------------------------------------------- 应用

    [RelayCommand]
    private async Task Apply()
    {
        if (_iface == null || _env == null || _persisted == null || IsBusy || IsPending) return;
        RebuildFormState();
        if (!CanApply)
        {
            IfStatus = HasValidationError ? "请先修正配置错误。" : "当前配置不可应用（请检查持久化配置/网卡类型）。";
            return;
        }

        _applyLog.Clear();
        ApplyLogText = "";
        IsApplyLogOpen = false;
        LogApply($"=== 应用 {_iface.Name} ===");

        // 每次应用前强制刷新 netplan 文件列表与冲突分析，避免使用页面加载时的旧缓存
        IsBusy = true;
        CanApply = false;
        try
        {
            LogApply("--- 刷新持久化配置与 netplan 文件列表 ---");
            _env = await _net.GetEnvironmentAsync(force: true);
            var fresh = await _net.GetPersistedIpConfigAsync(_iface, _env);
            _persisted = fresh;
            if (fresh.ReadError.Length > 0)
            {
                IfStatus = fresh.ReadError;
                CanReadPersisted = true;
                LogApply("刷新失败：" + fresh.ReadError);
                IsApplyLogOpen = true;
                return;
            }
        }
        catch (SessionDisconnectedException)
        {
            IfStatus = "连接已断开。";
            LogApply("连接已断开。");
            IsApplyLogOpen = true;
            return;
        }
        catch (Exception ex)
        {
            IfStatus = "刷新持久化配置失败：" + ex.Message;
            LogApply("刷新失败：" + ex.Message);
            IsApplyLogOpen = true;
            return;
        }
        finally
        {
            IsBusy = false;
            RebuildFormState();
        }

        if (_persisted.Source is not (IpSourceKind.Netplan or IpSourceKind.NetworkManager))
        {
            IfStatus = "未能读取持久化配置，无法应用。请点击“读取持久化配置（需要 sudo）”。";
            IsApplyLogOpen = true;
            return;
        }

        var cfg = BuildConfig();
        LogApply(cfg.UseDhcp
            ? "表单：DHCP"
            : $"表单：静态 {string.Join(", ", cfg.Cidrs)}；网关 {cfg.Gateway}；DNS {string.Join(", ", cfg.Dns)}");

        if (_persisted.SourceFiles.Count > 0)
            LogApply("netplan 来源文件：" + string.Join("、", _persisted.SourceFiles));
        if (_persisted.ConflictFiles.Count > 0)
            LogApply("待接管文件：" + string.Join("、", _persisted.ConflictFiles));
        if (_persisted.ConflictComplex)
            LogApply("⚠ 存在复杂或不可读的 netplan 文件，可能需要手动合并。");

        var plan = await _net.BuildIpApplyPlanAsync(_iface, cfg, _persisted, _env, allowTakeover: false);
        var dialogVm = new NetworkApplyDialogViewModel(plan);
        var dialog = new NetworkApplyDialog { DataContext = dialogVm };
        if (await dialog.ShowDialog<bool>(Dialogs.MainWindow!) != true)
            return;

        if (plan.NeedTakeover && !dialogVm.AllowTakeover)
        {
            IfStatus = "已取消：未允许接管其他 netplan 文件。";
            return;
        }
        if (dialogVm.AllowTakeover && plan.NeedTakeover)
            plan = await _net.BuildIpApplyPlanAsync(_iface, cfg, _persisted, _env, allowTakeover: true);
        if (plan.Errors.Count > 0)
        {
            IfStatus = string.Join("\n", plan.Errors);
            return;
        }

        IsBusy = true;
        IfStatus = "正在应用网络配置并校验…";
        CanApply = false;
        try
        {
            var result = await _net.ApplyIpConfigAsync(
                plan, cfg, _iface, cmd => _sudo.RunAsync(cmd), LogApply);

            LogApply("结果：" + result.Message);
            if (result.Error.Length > 0) LogApply("错误：" + result.Error);

            if (result.Success && result.Committed)
            {
                IfStatus = result.Message;
                _owner.SetStatus($"{_iface.Name} 网络配置已应用并持久化");
            }
            else if (result.Success && result.Pending)
            {
                IfStatus = result.Message;
                _owner.SetStatus($"{_iface.Name} 配置已应用，等待确认（120 秒后自动回滚）");
            }
            else if (result.RolledBack)
            {
                IfStatus = result.Message + (result.Error.Length > 0 ? "\n" + result.Error : "");
                _owner.SetStatus($"{_iface.Name} 网络配置失败：{FirstLine(result.Error)}", error: true);
                IsApplyLogOpen = true;
            }
            else
            {
                IfStatus = result.Error.Length > 0 ? result.Error : result.Message;
                if (result.SessionLost)
                    IfStatus = "SSH 连接可能已断开；120 秒内未确认将自动回滚。请重连后到网络页处理。";
                _owner.SetStatus($"{_iface.Name} 网络配置失败：{FirstLine(result.Error)}", error: true);
                IsApplyLogOpen = true;
            }
        }
        catch (SessionDisconnectedException)
        {
            LogApply("SSH 连接已断开；120 秒内未确认将自动回滚。");
            IfStatus = "SSH 连接已断开；120 秒内未确认将自动回滚。请重连后到网络页处理。";
            IsApplyLogOpen = true;
        }
        catch (Exception ex)
        {
            LogApply("应用失败：" + ex.Message);
            IfStatus = "应用失败：" + ex.Message;
            IsApplyLogOpen = true;
        }
        finally
        {
            IsBusy = false;
            try
            {
                await _refreshInterfaces();
                await ReloadAsync(force: true);
            }
            catch { /* 断线时忽略刷新失败 */ }
        }
    }

    private void LogApply(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => LogApply(line));
            return;
        }
        _applyLog.AppendLine(line);
        ApplyLogText = _applyLog.ToString();
    }

    [RelayCommand]
    private async Task Diagnose()
    {
        if (_iface == null || IsBusy) return;
        IsBusy = true;
        IsApplyLogOpen = true;
        LogApply("=== 诊断信息 ===");
        try
        {
            var text = await _net.CollectIpDiagnosticsAsync(_iface);
            LogApply(text);
            IfStatus = "诊断信息已生成，可点击“复制日志”反馈。";
        }
        catch (SessionDisconnectedException)
        {
            LogApply("连接已断开。");
        }
        catch (Exception ex)
        {
            LogApply("诊断失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            RebuildFormState();
        }
    }

    private static string FirstLine(string? text)
    {
        var t = (text ?? "").Replace("\r", "").Trim();
        if (t.Length == 0) return "未知错误";
        var i = t.IndexOf('\n');
        return (i >= 0 ? t[..i] : t).Trim();
    }

    // ---------------------------------------------------------------- pending

    private async Task RefreshPendingAsync()
    {
        try
        {
            var pending = await _net.GetPendingIpChangeAsync();
            if (pending == null || pending.Committed || pending.RolledBack)
            {
                _pending = null;
                IsPending = false;
                PendingText = "";
                PendingRemaining = "";
                _pendingTimer?.Stop();
                _pendingTimer = null;
                RebuildFormState();
                return;
            }

            _pending = pending;
            IsPending = true;
            PendingText = $"待确认变更：{pending.Iface} · {pending.Summary} · {pending.Backend}";
            _pendingDeadlineUtc = DateTime.UtcNow.AddSeconds(pending.RemainingSeconds);
            UpdateRemaining();
            EnsurePendingTimer();
            RebuildFormState();
        }
        catch (SessionDisconnectedException) { }
        catch (Exception) { /* pending 读取失败不阻断表单 */ }
    }

    private void EnsurePendingTimer()
    {
        if (_pendingTimer != null) return;
        _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pendingTimer.Tick += (_, _) => UpdateRemaining();
        _pendingTimer.Start();
    }

    private void UpdateRemaining()
    {
        if (_pending == null) return;
        var left = (int)Math.Max(0, (_pendingDeadlineUtc - DateTime.UtcNow).TotalSeconds);
        PendingRemaining = $"{left / 60:00}:{left % 60:00}";
        if (left <= 0)
        {
            _pendingTimer?.Stop();
            _pendingTimer = null;
            _ = RefreshPendingLaterAsync();
        }
    }

    private async Task RefreshPendingLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await RefreshPendingAsync();
    }

    [RelayCommand]
    private async Task CommitPending()
    {
        if (_pending == null || IsBusy) return;
        IsBusy = true;
        try
        {
            var err = await _net.CommitPendingAsync(_pending.Token, cmd => _sudo.RunAsync(cmd));
            IfStatus = err == null ? "已保留新配置，自动回滚已取消。" : "保留失败：" + err;
            await RefreshPendingAsync();
        }
        catch (SessionDisconnectedException)
        {
            IfStatus = "连接已断开。";
        }
        finally
        {
            IsBusy = false;
            RebuildFormState();
        }
    }

    [RelayCommand]
    private async Task RollbackPending()
    {
        if (_pending == null || IsBusy) return;
        if (!await Dialogs.ConfirmAsync(
                "立即回滚网络配置",
                $"将立即恢复 {_pending.Iface} 的旧配置。继续？",
                danger: true, okText: "立即回滚"))
            return;

        IsBusy = true;
        try
        {
            var err = await _net.RollbackPendingAsync(_pending.Token, cmd => _sudo.RunAsync(cmd));
            IfStatus = err == null ? "已回滚到旧配置。" : "回滚失败：" + err;
            await _refreshInterfaces();
            await RefreshPendingAsync();
        }
        catch (SessionDisconnectedException)
        {
            IfStatus = "连接已断开。";
        }
        finally
        {
            IsBusy = false;
            RebuildFormState();
        }
    }

    internal void Cleanup()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _pendingTimer?.Stop();
        _pendingTimer = null;
    }
}
