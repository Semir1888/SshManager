using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

/// <summary>网卡列表行。</summary>
public partial class NetIfItemViewModel : ViewModelBase
{
    public required NetInterface Model { get; init; }
    public string Name => Model.Name;
    public bool IsWifi => Model.IsWifi;
    public string Kind => Model.IsWifi ? "无线" : "有线";
    public string StateText => Model.State == "UP" ? "已启用" : Model.State;
    public string AddrText => Model.Ipv4Addresses.Count > 0
        ? string.Join("  ", Model.Ipv4Addresses.Select(a => a.Cidr))
        : "无 IPv4 地址";
    public string MethodText => Model.NmManaged
        ? (Model.NmMethod == "manual" ? "NetworkManager · 静态"
            : Model.NmMethod == "auto" ? "NetworkManager · DHCP" : "NetworkManager")
        : "netplan / networkd";
    public string AddressSummary => Model.Ipv4Addresses.Count > 0
        ? string.Join(", ", Model.Ipv4Addresses.Select(a => a.Cidr))
        : "无 IPv4 地址";
    public string Ipv6Summary => Model.Ipv6Addresses.Count > 0
        ? string.Join(", ", Model.Ipv6Addresses.Take(4))
        : "";
    public string GatewaySummary => Model.Gateways.Count > 0
        ? string.Join(", ", Model.Gateways)
        : "—";
    public string DnsSummary => Model.DnsServers.Count > 0
        ? string.Join(", ", Model.DnsServers)
        : "—";
    public bool Unsupported => Model.UnsupportedKind;
}

/// <summary>WiFi 扫描行。</summary>
public partial class WifiItemViewModel : ViewModelBase
{
    private readonly NetworkViewModel _parent;
    public required WifiNetwork Model { get; init; }
    public string Ssid => Model.Ssid;
    public string SecurityText => Model.IsOpen ? "开放" : Model.Security;
    public string Channel => Model.Channel;
    public string Signal => Model.Signal;
    public bool InUse => Model.InUse;
    public bool IsOpen => Model.IsOpen;
    public bool IsSecured => !Model.IsOpen;
    public int SignalNum => int.TryParse(Model.Signal, out var s) ? s : 0;

    public WifiItemViewModel(NetworkViewModel parent) => _parent = parent;

    [RelayCommand]
    private Task Connect() => _parent.ConnectWifiAsync(this);
}

/// <summary>已保存 WiFi 连接行。</summary>
public partial class WifiConnItemViewModel : ViewModelBase
{
    private readonly NetworkViewModel _parent;
    public required WifiConnection Model { get; init; }
    public string Name => Model.Name;
    public string Device => string.IsNullOrEmpty(Model.Device) ? "未连接" : Model.Device;
    public bool IsActive => !string.IsNullOrEmpty(Model.Device);

    public WifiConnItemViewModel(NetworkViewModel parent) => _parent = parent;

    [RelayCommand]
    private Task Disconnect() => _parent.DisconnectWifiAsync(this);

    [RelayCommand]
    private Task Forget() => _parent.ForgetWifiAsync(this);
}

/// <summary>UFW 规则行。</summary>
public partial class UfwRuleItemViewModel : ViewModelBase
{
    private readonly NetworkViewModel _parent;
    public required UfwRule Model { get; init; }
    public int Num => Model.Num;
    public string Action => Model.Action;
    public string To => Model.To;
    public string From => Model.From;
    public Avalonia.Media.IBrush ActionBrush => Action.StartsWith("ALLOW")
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#10b981"))
        : Action.StartsWith("LIMIT")
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f59e0b"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e"));

    public UfwRuleItemViewModel(NetworkViewModel parent) => _parent = parent;

    [RelayCommand]
    private Task Edit() => _parent.EditUfwRuleAsync(this);

    [RelayCommand]
    private Task Delete() => _parent.DeleteUfwRuleAsync(this);
}

/// <summary>
/// 网络设置页：网卡 IP/网关/DNS（nmcli 或 netplan）、WiFi 扫描与连接、
/// UFW 防火墙、主机名。面向工程配置场景，敏感操作均二次确认。
/// </summary>
public partial class NetworkViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly NetworkService _net;
    private readonly MainWindowViewModel _owner;
    private bool _suppressIfSelection;

    public NetworkViewModel(SshSession session, MainWindowViewModel owner)
    {
        _session = session;
        _owner = owner;
        _net = new NetworkService(session);
        Ip = new IpConfigPanelViewModel(session, _net, owner, LoadInterfacesAsync);
        _ = LoadAllAsync();
    }

    // ------------------------------------------------------------------ 总体状态

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";

    // ------------------------------------------------------------------ 网卡

    public ObservableCollection<NetIfItemViewModel> Interfaces { get; } = new();
    public IpConfigPanelViewModel Ip { get; }

    [ObservableProperty] private NetIfItemViewModel? _selectedInterface;

    public bool HasInterface => SelectedInterface != null;

    partial void OnSelectedInterfaceChanged(NetIfItemViewModel? value)
    {
        // 列表重建时 ComboBox 会瞬时把选中项置空，这里的副作用会覆盖正在编辑的表单
        if (!_suppressIfSelection) Ip.SetInterface(value);
        OnPropertyChanged(nameof(HasInterface));
    }

    internal void Cleanup() => Ip.Cleanup();

    // ------------------------------------------------------------------ WiFi

    public ObservableCollection<WifiItemViewModel> WifiList { get; } = new();
    public ObservableCollection<WifiConnItemViewModel> WifiConns { get; } = new();

    [ObservableProperty] private bool _wifiSupported = true;
    [ObservableProperty] private bool _wifiBusy;
    [ObservableProperty] private string _wifiStatus = "";

    public bool HasWifiConns => WifiConns.Count > 0;

    [RelayCommand]
    private async Task ScanWifiAsync()
    {
        if (WifiBusy) return;
        WifiBusy = true;
        WifiStatus = "";
        try
        {
            WifiSupported = await _net.HasNmAsync();
            if (!WifiSupported) return;
            var list = await _net.ScanWifiAsync();
            WifiList.Clear();
            foreach (var w in list) WifiList.Add(new WifiItemViewModel(this) { Model = w });
            await LoadWifiConnsAsync();
            WifiStatus = $"扫描到 {WifiList.Count} 个网络";
        }
        catch (Exception ex) { WifiStatus = "扫描失败：" + ex.Message; }
        finally { WifiBusy = false; }
    }

    internal async Task ConnectWifiAsync(WifiItemViewModel item)
    {
        if (WifiBusy) return;
        WifiBusy = true;
        try
        {
            string? pw = null;
            if (!item.Model.IsOpen)
            {
                var (ok, v) = await Dialogs.InputAsync(
                    "连接 WiFi", $"输入 “{item.Ssid}” 的密码：", "", password: true, okText: "连接");
                if (!ok) return;
                pw = v;
            }
            else
            {
                if (!await Dialogs.ConfirmAsync("连接 WiFi", $"连接开放网络 “{item.Ssid}”？", okText: "连接"))
                    return;
            }
            WifiStatus = $"正在连接 {item.Ssid} …";
            var err = await _net.ConnectWifiAsync(item.Model.Dev, item.Model.Ssid, pw, SudoAsync);
            if (err != null) WifiStatus = "连接失败：" + err;
            else
            {
                WifiStatus = $"已连接 {item.Ssid}";
                _owner.SetStatus($"WiFi 已连接 {item.Ssid}");
                await Task.Delay(1200);
                await LoadAllAsync();
            }
        }
        finally { WifiBusy = false; }
    }

    internal async Task DisconnectWifiAsync(WifiConnItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.Model.Device)) return;
        var err = await SudoAsync($"nmcli device disconnect {Core.Helpers.UnixPath.ShellQuote(item.Model.Device)}");
        if (err != null) WifiStatus = "断开失败：" + err;
        else { WifiStatus = "已断开"; await Task.Delay(800); await LoadAllAsync(); }
    }

    internal async Task ForgetWifiAsync(WifiConnItemViewModel item)
    {
        if (!await Dialogs.ConfirmAsync("忘记 WiFi", $"删除已保存的连接 “{item.Name}”？", danger: true, okText: "忘记"))
            return;
        var err = await _net.ForgetWifiAsync(item.Model.Uuid, SudoAsync);
        if (err != null) WifiStatus = "删除失败：" + err;
        else { WifiStatus = "已删除"; await LoadWifiConnsAsync(); }
    }

    private async Task LoadWifiConnsAsync()
    {
        if (!await _net.HasNmAsync()) return;
        var conns = await _net.ListWifiConnectionsAsync();
        WifiConns.Clear();
        foreach (var c in conns) WifiConns.Add(new WifiConnItemViewModel(this) { Model = c });
        OnPropertyChanged(nameof(HasWifiConns));
    }

    // ------------------------------------------------------------------ 防火墙

    public ObservableCollection<UfwRuleItemViewModel> UfwRules { get; } = new();

    [ObservableProperty] private bool _ufwInstalled = true;
    [ObservableProperty] private bool _ufwActive;
    [ObservableProperty] private string _ufwIncoming = "";
    [ObservableProperty] private string _ufwOutgoing = "";
    [ObservableProperty] private string _ufwStatus = "";
    [ObservableProperty] private bool _ufwBusy;
    [ObservableProperty] private string _addPort = "";
    [ObservableProperty] private string _addProto = "TCP";
    [ObservableProperty] private string _addAction = "允许";
    [ObservableProperty] private string _addFrom = "";
    [ObservableProperty] private string _defaultIncomingChoice = "deny";

    public string UfwToggleText => UfwActive ? "禁用防火墙" : "启用防火墙";

    public IReadOnlyList<string> ProtoChoices { get; } = new[] { "TCP", "UDP", "任意" };
    public IReadOnlyList<string> ActionChoices { get; } = new[] { "允许", "拒绝", "限制" };
    public IReadOnlyList<string> DefaultInChoices { get; } = new[] { "deny", "reject", "allow" };

    partial void OnUfwActiveChanged(bool value) => OnPropertyChanged(nameof(UfwToggleText));

    [RelayCommand]
    private async Task ToggleUfwAsync()
    {
        if (UfwBusy) return;
        UfwBusy = true;
        try
        {
            var enable = !UfwActive;
            if (enable && !await Dialogs.ConfirmAsync(
                    "启用防火墙",
                    "启用 UFW 后，未明确放行的入站连接都会被拒绝。\n请确认已放行 SSH（22 端口），否则会把自己锁在门外。\n继续？",
                    danger: true, okText: "启用"))
                return;
            var err = await _net.SetUfwEnabledAsync(enable, SudoAsync);
            if (err != null) UfwStatus = "操作失败：" + err;
            else
            {
                UfwStatus = enable ? "防火墙已启用" : "防火墙已禁用";
                _owner.SetStatus(UfwStatus);
                await LoadUfwAsync();
            }
        }
        finally { UfwBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyUfwDefaultAsync()
    {
        if (UfwBusy) return;
        UfwBusy = true;
        try
        {
            var err = await _net.SetUfwDefaultAsync(DefaultIncomingChoice, SudoAsync);
            if (err != null) UfwStatus = "设置失败：" + err;
            else
            {
                UfwStatus = $"默认入站策略已设为 {DefaultIncomingChoice}";
                await LoadUfwAsync();
            }
        }
        finally { UfwBusy = false; }
    }

    [RelayCommand]
    private async Task AddUfwRuleAsync()
    {
        if (UfwBusy) return;
        var port = AddPort.Trim();
        var from = AddFrom.Trim();
        if (port.Length == 0 && from.Length == 0) { UfwStatus = "请填写端口（或来源）。"; return; }
        // 端口允许数字 / 范围（8000:8010）/ 服务名（ssh），具体由 ufw 自行校验
        var cmd = NetworkService.BuildAddRuleCommand(AddAction, port, AddProto, from);
        if (cmd == null) { UfwStatus = "不限来源时必须指定端口。"; return; }

        UfwBusy = true;
        try
        {
            if (!await Dialogs.ConfirmAsync("添加防火墙规则", $"执行：{cmd}", okText: "添加", mono: true))
                return;
            var err = await _net.AddUfwRuleAsync(cmd, SudoAsync);
            if (err != null) UfwStatus = "添加失败：" + err;
            else
            {
                UfwStatus = "规则已添加";
                AddPort = "";
                AddFrom = "";
                await LoadUfwAsync();
            }
        }
        finally { UfwBusy = false; }
    }

    internal async Task DeleteUfwRuleAsync(UfwRuleItemViewModel item)
    {
        if (!await Dialogs.ConfirmAsync("删除防火墙规则",
                $"删除规则 [{item.Num}] {item.Action} {item.To}（来自 {item.From}）？",
                danger: true, okText: "删除"))
            return;
        var err = await _net.DeleteUfwRuleAsync(item.Num, SudoAsync);
        if (err != null) UfwStatus = "删除失败：" + err;
        else { UfwStatus = "规则已删除"; await LoadUfwAsync(); }
    }

    /// <summary>修改规则：弹表单预填解析结果，确认后“删原编号 + 添加新规则”一次执行。</summary>
    internal async Task EditUfwRuleAsync(UfwRuleItemViewModel item)
    {
        var (action, outbound, port, proto, from) = ParseUfwRule(item.Model);
        var dlg = new Views.UfwRuleDialog(true, action, outbound, port, proto, from);
        var ok = await dlg.ShowDialog<bool>(Dialogs.MainWindow!);
        if (!ok) return;

        var cmd = NetworkService.BuildAddRuleCommand(dlg.Action, dlg.Port, dlg.Proto, dlg.From, dlg.Outbound);
        if (cmd == null) { UfwStatus = "不限来源时必须指定端口。"; return; }

        var v6Note = (item.To.Contains("(v6)") || item.From.Contains("(v6)"))
            ? "\n注意：原规则为 IPv6 行，保存后会按服务器设置同时重建 v4/v6。"
            : "";
        if (!await Dialogs.ConfirmAsync("修改防火墙规则",
                $"将删除规则 [{item.Num}] 并添加：\n{cmd}{v6Note}\n\n继续？",
                danger: true, okText: "保存修改", mono: true))
            return;

        UfwBusy = true;
        try
        {
            var err = await _net.ModifyUfwRuleAsync(item.Num, cmd, SudoAsync);
            if (err != null) UfwStatus = "修改失败（原规则可能已被删除，请刷新查看）：" + err;
            else
            {
                UfwStatus = "规则已修改";
                await LoadUfwAsync();
            }
        }
        finally { UfwBusy = false; }
    }

    /// <summary>把 ufw 规则文本解析回表单字段：动作/方向/端口/协议/来源。</summary>
    internal static (string Action, bool Outbound, string Port, string Proto, string From) ParseUfwRule(UfwRule r)
    {
        // 动作 + 方向："ALLOW IN" / "DENY OUT" / "LIMIT" ...
        var act = r.Action ?? "";
        var outbound = act.EndsWith(" OUT", StringComparison.OrdinalIgnoreCase);
        var action = act.StartsWith("ALLOW", StringComparison.OrdinalIgnoreCase) ? "允许"
            : act.StartsWith("DENY", StringComparison.OrdinalIgnoreCase) ? "拒绝"
            : act.StartsWith("LIMIT", StringComparison.OrdinalIgnoreCase) ? "限制"
            : "允许";

        // 目标："22/tcp (OpenSSH)" / "Anywhere" / "8000:8010/udp" → 剥注释
        var to = (r.To ?? "").Trim();
        var cut = to.IndexOf(" (");
        if (cut > 0) to = to[..cut];
        to = to.Trim();
        string port, proto;
        if (to.Length == 0 || to.Equals("Anywhere", StringComparison.OrdinalIgnoreCase))
        {
            port = "";
            proto = "任意";
        }
        else if (to.EndsWith("/tcp", StringComparison.OrdinalIgnoreCase))
        {
            port = to[..^4];
            proto = "TCP";
        }
        else if (to.EndsWith("/udp", StringComparison.OrdinalIgnoreCase))
        {
            port = to[..^4];
            proto = "UDP";
        }
        else
        {
            port = to;
            proto = "任意";
        }

        // 来源："Anywhere" → 空；剥 " (v6)" 注释
        var from = (r.From ?? "").Trim();
        if (from.EndsWith(" (v6)", StringComparison.OrdinalIgnoreCase)) from = from[..^5].Trim();
        if (from.Length == 0 || from.Equals("Anywhere", StringComparison.OrdinalIgnoreCase)) from = "";

        return (action, outbound, port, proto, from);
    }

    private async Task LoadUfwAsync()
    {
        var st = await _net.GetUfwAsync();
        UfwInstalled = st.Installed;
        UfwActive = st.Active;
        UfwIncoming = st.DefaultIncoming;
        UfwOutgoing = st.DefaultOutgoing;
        DefaultIncomingChoice = st.DefaultIncoming switch
        {
            "allow" => "allow", "reject" => "reject", _ => "deny",
        };
        UfwRules.Clear();
        foreach (var r in st.Rules) UfwRules.Add(new UfwRuleItemViewModel(this) { Model = r });
    }

    // ------------------------------------------------------------------ 主机名

    [ObservableProperty] private string _hostname = "";
    [ObservableProperty] private string _hostnameInput = "";
    [ObservableProperty] private string _hostnameStatus = "";

    [RelayCommand]
    private async Task SetHostnameAsync()
    {
        var name = HostnameInput.Trim();
        if (!NetworkService.IsValidHostname(name))
        {
            HostnameStatus = "主机名不合法（仅字母/数字/点/中划线，63 字符内）。";
            return;
        }
        if (!await Dialogs.ConfirmAsync("修改主机名", $"把主机名从 “{Hostname}” 改为 “{name}”？", okText: "修改"))
            return;
        var err = await _net.SetHostnameAsync(name, SudoAsync);
        if (err != null) HostnameStatus = "修改失败：" + err;
        else
        {
            HostnameStatus = "已修改";
            Hostname = name;
            HostnameInput = "";
            _owner.SetStatus($"主机名已改为 {name}");
        }
    }

    // ------------------------------------------------------------------ 加载

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsBusy) return;
        await LoadAllAsync();
    }

    private async Task LoadAllAsync()
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            await LoadInterfacesAsync();
            WifiSupported = await _net.HasNmAsync();
            if (WifiSupported && WifiList.Count == 0)
            {
                var list = await _net.ScanWifiAsync();
                WifiList.Clear();
                foreach (var w in list) WifiList.Add(new WifiItemViewModel(this) { Model = w });
            }
            if (WifiSupported) await LoadWifiConnsAsync();
            await LoadUfwAsync();
            Hostname = await _net.GetHostnameAsync();
        }
        catch (Exception ex)
        {
            ErrorText = "读取网络信息失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadInterfacesAsync()
    {
        var list = await _net.ListInterfacesAsync();
        var selected = SelectedInterface?.Name;

        _suppressIfSelection = true;
        try
        {
            Interfaces.Clear();
            foreach (var i in list) Interfaces.Add(new NetIfItemViewModel { Model = i });
            SelectedInterface = Interfaces.FirstOrDefault(i => i.Name == selected)
                                ?? Interfaces.FirstOrDefault();
        }
        finally
        {
            _suppressIfSelection = false;
        }

        // 重建完成后只同步一次面板；同名时只更新摘要，不覆盖表单
        Ip.SetInterface(SelectedInterface);
    }

    // ------------------------------------------------------------------ sudo

    /// <summary>sudo 执行：缺密码或密码错误时弹窗补齐重试。成功返回 null，失败返回错误信息。</summary>
    private async Task<string?> SudoAsync(string command)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var err = await RunSudoAsync(command);
                if (err == null) return null;
                if (!IsSudoPasswordError(err)) return err;

                // 密码错误：清空后重新弹窗
                _session.SudoPassword = null;
            }
            catch (SudoPasswordRequiredException)
            {
                // 继续弹窗
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要修改系统网络参数，请输入其密码：",
                "", password: true);
            if (!ok || pw.Length == 0)
                return "已取消：未提供 sudo 密码。";
            _session.SudoPassword = pw;
        }
        return "sudo 密码错误或权限不足。";
    }

    private static bool IsSudoPasswordError(string text)
        => text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase)
           || text.Contains("密码不正确", StringComparison.OrdinalIgnoreCase)
           || text.Contains("抱歉，请重试", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> RunSudoAsync(string command)
    {
        // 用 shell 包装整条命令：netplan/UFW 修改包含 ; 与 &&，普通 sudo 只会提权第一段
        var r = await _net.RunSudoShellAsync(command, TimeSpan.FromSeconds(90));
        return r.ExitCode == 0 ? null : (r.Output + " " + r.Error).Trim();
    }
}
