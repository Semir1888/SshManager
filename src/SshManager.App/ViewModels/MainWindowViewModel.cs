using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.App.Views;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileStore _store;
    private readonly TransferService _transfers;
    private readonly UiSettings _uiSettings;
    private readonly List<SshProfile> _models = new();
    private SshSession? _session;
    /// <summary>当前连接对应的已保存配置（用于侧栏“已连接”标识；未保存的新连接为 null）。</summary>
    private SshProfile? _currentProfileModel;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _statusTimer;

    public TransferService Transfers => _transfers;
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool _transfersOpen;
    [ObservableProperty] private double _transfersHeight = 240;
    [ObservableProperty] private string _transfersText = "传输";
    [ObservableProperty] private string _stateText = "未连接";
    [ObservableProperty] private bool _connDotOn;
    [ObservableProperty] private string _hostText = "";
    [ObservableProperty] private string _latencyText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private IBrush _statusBrush = new SolidColorBrush(Avalonia.Media.Colors.Transparent);
    [ObservableProperty] private bool _isDark = true;
    [ObservableProperty] private string _themeText = "深色模式";
    [ObservableProperty] private bool _sidebarCollapsed;

    /// <summary>侧栏宽度（随收起状态切换）。</summary>
    public double SidebarWidth => SidebarCollapsed ? 58d : 238d;

    [ObservableProperty] private bool _isTabOverview;
    [ObservableProperty] private bool _isTabFiles;
    [ObservableProperty] private bool _isTabApps;
    [ObservableProperty] private bool _isTabDocker;
    [ObservableProperty] private bool _isTabCompose;
    [ObservableProperty] private bool _isTabNetwork;
    [ObservableProperty] private bool _isTabConsole;

    public OverviewViewModel? OverviewVm { get; private set; }
    public FileManagerViewModel? FilesVm { get; private set; }
    public AppCenterViewModel? AppsVm { get; private set; }
    public DockerViewModel? DockerVm { get; private set; }
    public ComposeViewModel? ComposeVm { get; private set; }
    public NetworkViewModel? NetworkVm { get; private set; }
    public ConsoleViewModel? ConsoleVm { get; private set; }

    public const string Version = "v0.10.0";

    public MainWindowViewModel(ProfileStore store, TransferService transfers)
    {
        _store = store;
        _transfers = transfers;

        // 恢复上次的界面偏好（侧栏收起状态 / 主题）
        _uiSettings = UiSettings.Load();
        SidebarCollapsed = _uiSettings.SidebarCollapsed;
        TransfersHeight = Math.Clamp(_uiSettings.TransfersDrawerHeight, 140, 640);
        IsDark = _uiSettings.DarkTheme;
        ThemeText = IsDark ? "深色模式" : "浅色模式";
        if (Avalonia.Application.Current != null)
            Avalonia.Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        _models.AddRange(store.Load());
        RebuildProfiles();

        SelectTabInternal("overview");

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
    }

    // ---------------------------------------------------------------- 顶部命令

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        ThemeText = IsDark ? "深色模式" : "浅色模式";
        if (Avalonia.Application.Current != null)
            Avalonia.Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        _uiSettings.DarkTheme = IsDark;
        _uiSettings.Save();
    }

    /// <summary>收起/展开左侧栏，状态持久化，下次启动保持。</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        SidebarCollapsed = !SidebarCollapsed;
        _uiSettings.SidebarCollapsed = SidebarCollapsed;
        _uiSettings.Save();
        OnPropertyChanged(nameof(SidebarWidth));
    }

    [RelayCommand]
    private void ToggleTransfers() => TransfersOpen = !TransfersOpen;

    /// <summary>拖拽调节传输抽屉高度（限制在 140–640 之间）。</summary>
    internal void SetTransfersHeight(double h) => TransfersHeight = Math.Clamp(h, 140, 640);

    /// <summary>拖拽结束后持久化高度。</summary>
    internal void PersistTransfersHeight()
    {
        _uiSettings.TransfersDrawerHeight = (int)TransfersHeight;
        _uiSettings.Save();
    }

    [RelayCommand]
    private void ClearFinished() => _transfers.ClearFinished();

    [RelayCommand]
    private Task NewConnection() => OpenConnectDialogAsync(null);

    /// <summary>断开前检查编辑器未保存内容，避免误断丢失。</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (FilesVm?.HasUnsavedTabs == true)
        {
            if (!await Dialogs.ConfirmAsync(
                    "未保存的修改",
                    "文件编辑器中有未保存的内容，断开后将丢失。\n仍要断开连接？",
                    danger: true, okText: "断开"))
                return;
        }
        HandleSessionLost("手动断开");
    }

    [RelayCommand]
    private void SelectTab(string key) => SelectTabInternal(key);

    internal void SelectTabInternal(string key)
    {
        IsTabOverview = key == "overview";
        IsTabFiles = key == "files";
        IsTabApps = key == "apps";
        IsTabDocker = key == "docker";
        IsTabCompose = key == "compose";
        IsTabNetwork = key == "network";
        IsTabConsole = key == "console";
        CurrentView = key switch
        {
            "overview" => OverviewVm,
            "files" => FilesVm,
            "apps" => AppsVm,
            "docker" => DockerVm,
            "compose" => ComposeVm,
            "network" => NetworkVm,
            "console" => ConsoleVm,
            _ => null,
        };

        // 编排页按需激活：切走时停自动刷新，切回时探测/静默刷新
        ComposeVm?.SetActive(key == "compose");
        if (key == "compose") _ = ComposeVm?.ActivateAsync();
    }

    // ---------------------------------------------------------------- 连接管理

    internal async Task ConnectProfileAsync(ProfileItemViewModel item) => await OpenConnectDialogAsync(item.Model);

    internal async Task EditProfileAsync(ProfileItemViewModel item) => await OpenConnectDialogAsync(item.Model);

    internal async Task RemoveProfileAsync(ProfileItemViewModel item)
    {
        if (!await Dialogs.ConfirmAsync("删除服务器配置", $"删除 “{item.Name}” 的保存信息？", danger: true, okText: "删除"))
            return;
        _models.Remove(item.Model);
        if (ReferenceEquals(item.Model, _currentProfileModel))
            _currentProfileModel = null;
        _store.Save(_models);
        Profiles.Remove(item);
    }

    /// <summary>重建服务器列表，并重新标记当前连接的服务器（启动 / 连接成功时调用）。</summary>
    private void RebuildProfiles()
    {
        Profiles.Clear();
        foreach (var p in _models.OrderByDescending(p => p.LastConnectAt ?? DateTime.MinValue))
            Profiles.Add(new ProfileItemViewModel(this, p)
            {
                IsCurrent = ReferenceEquals(p, _currentProfileModel),
            });
    }

    private async Task OpenConnectDialogAsync(SshProfile? prefill)
    {
        var vm = new ConnectDialogViewModel();
        if (prefill != null) vm.Prefill(prefill.Clone());
        var win = new ConnectDialog { DataContext = vm };
        var ok = await win.ShowDialog<bool>(Dialogs.MainWindow!);
        if (ok != true || vm.Session == null) return;
        OnSessionEstablished(vm.Session, vm.Profile, vm.SaveToProfiles);
    }

    private void OnSessionEstablished(SshSession session, SshProfile profile, bool saveToList)
    {
        _session = session;
        profile.LastConnectAt = DateTime.Now;

        // 保存 / 更新配置（密码不落盘）
        var existing = _models.FirstOrDefault(m =>
            m.Host == profile.Host && m.Port == profile.Port && m.Username == profile.Username);
        if (existing != null)
        {
            existing.Name = profile.Name;
            existing.AuthMode = profile.AuthMode;
            existing.KeyPath = profile.KeyPath;
            existing.AcceptedFingerprint = profile.AcceptedFingerprint;
            existing.AutoSudo = profile.AutoSudo;
            existing.LastConnectAt = profile.LastConnectAt;
        }
        else if (saveToList)
        {
            _models.Add(profile);
        }

        // 侧栏“已连接”标识：标记当前连接对应的配置条目
        _currentProfileModel = existing ?? (saveToList ? profile : null);
        _store.Save(_models);
        RebuildProfiles();

        // 捕获当前 session：避免重连后旧会话的断开事件误清新连接的标识/界面
        session.ConnectionLost += reason => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_session, session))
                HandleSessionLost(reason);
        });

        _transfers.Bind(session);
        OverviewVm = new OverviewViewModel(session, this);
        FilesVm = new FileManagerViewModel(session, _transfers, this);
        AppsVm = new AppCenterViewModel(session);
        DockerVm = new DockerViewModel(session, this);
        ComposeVm = new ComposeViewModel(session, this);
        NetworkVm = new NetworkViewModel(session, this);
        ConsoleVm = new ConsoleViewModel(session);

        IsConnected = true;
        StateText = "已连接";
        ConnDotOn = true;
        HostText = profile.HostSummary;
        LatencyText = $"延迟 {session.Latency?.TotalMilliseconds:0} ms" + (session.IsRoot ? " · root" : " · sudo");
        SetStatus($"连接成功 · 延迟 {session.Latency?.TotalMilliseconds:0} ms" +
                  (session.IsRoot ? " · root 权限" : " · sudo 提权可用"));
        SelectTabInternal("overview");
    }

    private void HandleSessionLost(string reason)
    {
        if (!IsConnected) return;
        _currentProfileModel = null;
        foreach (var item in Profiles) item.IsCurrent = false;
        IsConnected = false;
        StateText = "已断开";
        ConnDotOn = false;
        HostText = "";
        LatencyText = "";
        SetStatus(reason == "手动断开" ? "已断开连接" : "连接已断开：" + reason, error: reason != "手动断开");
        CleanupSession();
    }

    private void CleanupSession()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        TransfersOpen = false;
        _transfers.Reset();
        OverviewVm?.Cleanup();
        OverviewVm = null;
        FilesVm = null;
        AppsVm = null;
        DockerVm?.Cleanup();
        DockerVm = null;
        ComposeVm?.Cleanup();
        ComposeVm = null;
        NetworkVm?.Cleanup();
        NetworkVm = null;
        ConsoleVm?.Cleanup(); // 关闭 PTY 交互终端
        ConsoleVm = null;
        CurrentView = null;
    }

    private void Poll()
    {
        var active = _transfers.ActiveCount;
        TransfersText = active > 0 ? $"传输 ({active})" : "传输";
        if (IsConnected && _session != null && !_session.IsConnected)
            HandleSessionLost("心跳超时");
    }

    // ---------------------------------------------------------------- 状态消息

    internal void SetStatus(string message, bool error = false)
    {
        StatusMessage = message;
        StatusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(error ? "#f43f5e" : "#98a1b3"));
        _statusTimer?.Stop();
        if (message.Length > 0)
        {
            if (_statusTimer == null)
            {
                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _statusTimer.Tick += (_, _) =>
                {
                    _statusTimer.Stop();
                    StatusMessage = "";
                };
            }
            _statusTimer.Start();
        }
    }

    /// <summary>概览页“常用命令”跳到命令台执行。</summary>
    internal void RunInConsole(string command)
    {
        if (ConsoleVm == null) return;
        SelectTabInternal("console");
        ConsoleVm.RunExternal(command);
    }

    /// <summary>Docker 页“终端”：把命令填入命令台但不执行（用户可修改后回车）。</summary>
    internal void PrefillInConsole(string command)
    {
        if (ConsoleVm == null) return;
        SelectTabInternal("console");
        ConsoleVm.PrefillExternal(command);
    }

    /// <summary>跳到文件管理并打开指定远程文件（编排页“编辑 compose”复用现成编辑器）。</summary>
    internal async Task OpenRemoteFileInFilesAsync(string path)
    {
        if (FilesVm == null) return;
        SelectTabInternal("files");
        await FilesVm.OpenRemotePathAsync(path);
    }

    /// <summary>跳到文件管理并导航到指定远程目录。</summary>
    internal async Task OpenRemoteDirectoryInFilesAsync(string path)
    {
        if (FilesVm == null) return;
        SelectTabInternal("files");
        await FilesVm.NavigateToAsync(path);
    }
}
