using System.Collections.ObjectModel;
using Avalonia.Media;
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

/// <summary>编排页项目的行 ViewModel；命令全部转发给页面 VM。</summary>
public partial class ComposeProjectRowViewModel : ViewModelBase
{
    private readonly ComposeViewModel _parent;

    public ComposeProjectRowViewModel(ComposeViewModel parent, ComposeProjectInfo info, ComposeProjectConfig? binding)
    {
        _parent = parent;
        Info = info;
        Binding = binding;
        Ref = info.Ref;
        Name = info.Ref.Name;
        IsStale = false;
        State = info.State;
        StatusText = info.StatusText;
        ServiceCountText = info.TotalCount > 0 ? $"{info.RunningCount}/{info.TotalCount} 容器" : "无容器";
        BindingMismatch = binding != null
                          && binding.RemoteDir.Length > 0
                          && !UnixPath.Normalize(binding.RemoteDir)
                              .Equals(UnixPath.Normalize(info.Ref.WorkingDir), StringComparison.Ordinal);
    }

    /// <summary>只有绑定、未在服务器发现的项目（如已 down 或 compose 文件被删）。</summary>
    public ComposeProjectRowViewModel(ComposeViewModel parent, ComposeProjectConfig binding)
    {
        _parent = parent;
        Binding = binding;
        Ref = ComposeProjectRef.FromConfig(binding);
        Name = binding.ProjectName;
        IsStale = true;
        State = ComposeProjectState.Missing;
        StatusText = "未在服务器发现（可能已移除）";
        ServiceCountText = "—";
        BindingMismatch = false;
    }

    public ComposeProjectInfo? Info { get; }
    public ComposeProjectConfig? Binding { get; }
    public string Name { get; }
    public ComposeProjectRef Ref { get; }
    public bool IsStale { get; }
    public bool IsManual => Info?.Manual == true || Binding?.Manual == true;
    public bool HasBinding => Binding != null && (Binding.LocalDir.Length > 0 || Binding.ComposeFiles.Count > 0);
    public bool BindingMismatch { get; }
    public ComposeProjectState State { get; }
    public string StatusText { get; }
    public string ServiceCountText { get; }

    public string ConfigFilesText => Ref.ComposeFiles.Count > 0
        ? string.Join(" · ", Ref.ComposeFiles)
        : "（未找到 compose 文件）";

    public string WorkingDir => Ref.WorkingDir;
    public string BindingText => Binding?.LocalDir is { Length: > 0 } local
        ? "本地：" + local
        : "未绑定本地目录";

    public string StateLabel => State switch
    {
        ComposeProjectState.Running => "运行中",
        ComposeProjectState.Partial => "部分运行",
        ComposeProjectState.Stopped => "已停止",
        ComposeProjectState.ConfigMissing => "配置缺失",
        ComposeProjectState.Missing => "未发现",
        _ => "未知",
    };

    public IBrush StateBrush => new SolidColorBrush(Color.Parse(State switch
    {
        ComposeProjectState.Running => "#10b981",
        ComposeProjectState.Partial => "#f59e0b",
        ComposeProjectState.Stopped => "#8b93a5",
        ComposeProjectState.ConfigMissing => "#f43f5e",
        ComposeProjectState.Missing => "#f43f5e",
        _ => "#8b93a5",
    }));

    [ObservableProperty] private string _errorText = "";

    [RelayCommand] private Task Up() => _parent.UpProjectAsync(this);
    [RelayCommand] private Task Stop() => _parent.StopProjectAsync(this);
    [RelayCommand] private Task Restart() => _parent.RestartProjectAsync(this);
    [RelayCommand] private Task Pull() => _parent.PullProjectAsync(this);
    [RelayCommand] private Task Build() => _parent.BuildProjectAsync(this);
    [RelayCommand] private Task Recreate() => _parent.RecreateProjectAsync(this);
    [RelayCommand] private Task Down() => _parent.DownProjectAsync(this);
    [RelayCommand] private Task Logs() => _parent.ShowProjectLogsAsync(this);
    [RelayCommand] private Task Upgrade() => _parent.UpgradeAsync(this);
    [RelayCommand] private Task Backups() => _parent.ShowBackupsAsync(this);
    [RelayCommand] private Task EditCompose() => _parent.EditComposeAsync(this);
    [RelayCommand] private Task OpenDir() => _parent.OpenProjectDirAsync(this);
    [RelayCommand] private Task EditBinding() => _parent.EditBindingAsync(this);
    [RelayCommand] private Task RemoveBinding() => _parent.RemoveBindingAsync(this);
}

/// <summary>项目详情中的一个服务（可能是多个副本中的某一个容器，或尚未创建容器的配置服务）。</summary>
public partial class ComposeServiceRowViewModel : ViewModelBase
{
    private readonly ComposeViewModel _parent;
    private readonly ComposeProjectRowViewModel _project;

    public ComposeServiceRowViewModel(
        ComposeViewModel parent, ComposeProjectRowViewModel project,
        string service, ComposeContainerInfo? container)
    {
        _parent = parent;
        _project = project;
        ServiceName = service;
        Container = container;
        ContainerName = container?.Name ?? "";
        State = container?.State ?? "";
        StatusText = container?.Status ?? "未创建";
        HealthText = container?.Health switch
        {
            "healthy" => "健康",
            "unhealthy" => "不健康",
            "starting" => "启动中",
            _ => "—",
        };
        Image = container?.Image ?? "";
        PortsText = container?.Ports ?? "";
        HasContainer = container != null;
        IsRunning = container?.State.Equals("running", StringComparison.OrdinalIgnoreCase) == true;
    }

    public ComposeContainerInfo? Container { get; }
    public string ServiceName { get; }
    public string ContainerName { get; }
    public string State { get; }
    public string StatusText { get; }
    public string HealthText { get; }
    public string Image { get; }
    public string PortsText { get; }
    public bool HasContainer { get; }
    public bool IsRunning { get; }
    public bool IsUnhealthy => Container?.Health.Contains("unhealthy", StringComparison.OrdinalIgnoreCase) == true;

    public string StateLabel => !HasContainer
        ? "未创建"
        : State.ToLowerInvariant() switch
        {
            "running" => "运行中",
            "exited" => "已退出",
            "created" => "已创建",
            "restarting" => "重启中",
            "paused" => "已暂停",
            "dead" => "异常",
            _ => State.Length > 0 ? State : "未知",
        };

    public IBrush StateBrush => new SolidColorBrush(Color.Parse(
        IsUnhealthy ? "#f43f5e"
        : !HasContainer ? "#8b93a5"
        : IsRunning ? "#10b981"
        : State.Equals("restarting", StringComparison.OrdinalIgnoreCase) ? "#f59e0b"
        : "#f43f5e"));

    [RelayCommand] private Task Logs() => _parent.ShowServiceLogsAsync(_project, this);
    [RelayCommand] private Task Restart() => _parent.RestartServiceAsync(_project, this);
    [RelayCommand] private Task Recreate() => _parent.RecreateServiceAsync(_project, this);
    [RelayCommand] private void Exec() => _parent.ExecService(_project, this);
}

/// <summary>Docker 页「编排」子页：项目发现、详情、生命周期操作、日志与一键升级。</summary>
public partial class ComposeViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly MainWindowViewModel _owner;
    private readonly ComposeService _compose;
    private readonly ComposeDeployService _deploy;
    private readonly ComposeBindingStore _bindingStore = new();
    private readonly string _serverKey;

    private bool _activated;
    private bool _active;
    private bool _refreshing;
    private bool _suppressSelectionEffects;
    private DispatcherTimer? _autoTimer;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _logCts;
    private CancellationTokenSource? _deployCts;
    private string? _logProjectName;
    private string? _logService;
    private string? _selectedName;
    private string? _backupProjectName;
    private ComposeProjectRef? _backupProject;
    private List<ComposeProjectRowViewModel> _allProjects = new();

    [ObservableProperty] private ComposeState _state = ComposeState.Unknown;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private bool _sudoMode;
    [ObservableProperty] private string _failText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDeploying;
    [ObservableProperty] private double _deployPercent;
    [ObservableProperty] private string _deployStatusText = "";
    [ObservableProperty] private string? _lastBackupPath;
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ComposeProjectRowViewModel? _selectedProject;

    // 日志 / 操作输出 / 部署日志共用面板
    public UiLog Logs { get; } = new();
    [ObservableProperty] private bool _panelOpen;
    [ObservableProperty] private string _logTitle = "操作输出";
    [ObservableProperty] private int _logTail = 500;
    [ObservableProperty] private bool _logsLoading;
    [ObservableProperty] private bool _followLogs = true;
    [ObservableProperty] private bool _isLogFollowing;
    [ObservableProperty] private double _logPanelHeight = 200;

    public ObservableCollection<ComposeProjectRowViewModel> Projects { get; } = new();
    public ObservableCollection<ComposeProjectRowViewModel> FilteredProjects { get; } = new();
    public ObservableCollection<ComposeServiceRowViewModel> Services { get; } = new();
    public IReadOnlyList<int> TailChoices { get; } = new[] { 100, 500, 2000, 5000 };

    public bool IsReady => State == ComposeState.Ready;
    public bool IsLoadingState => State is ComposeState.Unknown or ComposeState.Probing;
    public bool IsNotInstalled => State == ComposeState.NotInstalled;
    public bool IsFailedState => State == ComposeState.Failed;
    public bool HasProject => SelectedProject != null;
    public bool HasRollback => LastBackupPath != null && !IsDeploying;
    public string ProjectsCountText => $"编排（{Projects.Count}）";
    public string ProjectHeaderText => SelectedProject?.Name ?? "";
    public string ComposeFilesText => SelectedProject?.ConfigFilesText ?? "";
    public string WorkingDirText => SelectedProject?.WorkingDir ?? "";
    public string ProjectStateText => SelectedProject?.StateLabel ?? "";
    public string ProjectStatusText => SelectedProject?.StatusText ?? "";
    public string BindingText => SelectedProject?.BindingText ?? "";
    public bool HasWarnings => SelectedProject?.BindingMismatch == true;

    public ComposeViewModel(SshSession session, MainWindowViewModel owner)
    {
        _session = session;
        _owner = owner;
        _serverKey = ComposeBindingStore.ServerKey(session.Profile);
        _compose = new ComposeService(session);
        _deploy = new ComposeDeployService(session, _compose);
    }

    internal void SetActive(bool active)
    {
        _active = active;
        if (active) EnsureAutoTimer();
        else _autoTimer?.Stop();
    }

    // ---------------------------------------------------------------- 探测 / 刷新

    public async Task ActivateAsync()
    {
        _active = true;
        EnsureAutoTimer();
        if (!_activated)
        {
            _activated = true;
            await ProbeAsync();
        }
        else if (State == ComposeState.Ready)
        {
            await RefreshProjectsAsync(silent: true);
        }
    }

    [RelayCommand]
    private Task Recheck() => ProbeAsync();

    [RelayCommand]
    private Task Refresh() => RefreshProjectsAsync();

    private async Task ProbeAsync()
    {
        State = ComposeState.Probing;
        FailText = "";
        try
        {
            var probe = await ExecuteWithSudoRetryAsync(() => _compose.ProbeAsync());
            switch (probe.State)
            {
                case ComposeState.Ready:
                    VersionText = $"Compose {probe.Version}" +
                                  (probe.Kind == ComposeKind.V1 ? "（docker-compose）" : "（docker compose）");
                    SudoMode = probe.Sudo;
                    State = ComposeState.Ready;
                    await RefreshProjectsAsync();
                    break;
                case ComposeState.NotInstalled:
                    VersionText = "Docker Compose";
                    State = ComposeState.NotInstalled;
                    break;
                default:
                    VersionText = "Docker Compose";
                    FailText = probe.Error ?? "未知错误";
                    State = ComposeState.Failed;
                    break;
            }
        }
        catch (SshManagerException ex)
        {
            FailText = ex.Message;
            State = ComposeState.Failed;
        }
        catch (Exception ex)
        {
            FailText = ex.Message;
            State = ComposeState.Failed;
        }
    }

    private async Task RefreshProjectsAsync(bool silent = false)
    {
        if (State != ComposeState.Ready || _refreshing || IsBusy || IsDeploying) return;
        _refreshing = true;
        if (!silent) IsLoading = true;
        try
        {
            var result = await ExecuteWithSudoRetryAsync(() => _compose.ListProjectsAsync());
            if (result.Error != null)
            {
                FailText = result.Error;
                State = ComposeState.Failed;
                return;
            }

            var bindings = _bindingStore.LoadForServer(_serverKey)
                .GroupBy(b => b.ProjectName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var rows = new List<ComposeProjectRowViewModel>();
            foreach (var info in result.Projects)
            {
                bindings.TryGetValue(info.Ref.Name, out var binding);
                rows.Add(new ComposeProjectRowViewModel(this, info, binding));
            }
            foreach (var binding in bindings.Values)
            {
                if (result.Projects.Any(p => p.Ref.Name.Equals(binding.ProjectName, StringComparison.Ordinal)))
                    continue;
                rows.Add(new ComposeProjectRowViewModel(this, binding));
            }

            rows.Sort((a, b) =>
            {
                var ar = a.State == ComposeProjectState.Running ? 0 : a.State == ComposeProjectState.Partial ? 1 : 2;
                var br = b.State == ComposeProjectState.Running ? 0 : b.State == ComposeProjectState.Partial ? 1 : 2;
                return ar != br ? ar.CompareTo(br) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            var selectedName = _selectedName;
            _allProjects = rows;
            Projects.Clear();
            foreach (var row in rows) Projects.Add(row);
            RebuildFilteredProjects(selectedName, loadDetailIfChanged: false);
            OnPropertyChanged(nameof(ProjectsCountText));

            if (SelectedProject != null)
                await LoadDetailAsync(SelectedProject, silent: true);
        }
        catch (SshManagerException ex)
        {
            FailText = ex.Message;
            State = ComposeState.Failed;
        }
        catch (Exception ex)
        {
            FailText = "Compose 刷新失败：" + ex.Message;
            State = ComposeState.Failed;
            _owner.SetStatus("Compose 刷新失败：" + ex.Message, error: true);
        }
        finally
        {
            _refreshing = false;
            IsLoading = false;
        }
    }

    partial void OnStateChanged(ComposeState value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsLoadingState));
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(IsFailedState));
    }

    partial void OnSearchTextChanged(string value)
        => RebuildFilteredProjects(_selectedName, loadDetailIfChanged: true);

    partial void OnSelectedProjectChanged(ComposeProjectRowViewModel? value)
    {
        NotifyProjectHeader();
        if (_suppressSelectionEffects) return;

        var previousName = _selectedName;
        _selectedName = value?.Name;
        ApplySelectionSideEffects(previousName);

        _ = LoadDetailAsync(value);
    }

    private void NotifyProjectHeader()
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectHeaderText));
        OnPropertyChanged(nameof(ComposeFilesText));
        OnPropertyChanged(nameof(WorkingDirText));
        OnPropertyChanged(nameof(ProjectStateText));
        OnPropertyChanged(nameof(ProjectStatusText));
        OnPropertyChanged(nameof(BindingText));
        OnPropertyChanged(nameof(HasWarnings));
    }

    /// <summary>只有真正切换项目（项目名变化）时才停日志、清空服务列表与回滚路径。</summary>
    private void ApplySelectionSideEffects(string? previousName)
    {
        var currentName = SelectedProject?.Name;
        if (string.Equals(currentName, previousName, StringComparison.Ordinal)) return;

        if (!string.Equals(currentName, _backupProjectName, StringComparison.Ordinal))
        {
            LastBackupPath = null;
            _backupProject = null;
        }
        CancelLogs();
        Services.Clear();
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value) EnsureAutoTimer();
        else _autoTimer?.Stop();
    }

    partial void OnLastBackupPathChanged(string? value) => OnPropertyChanged(nameof(HasRollback));

    partial void OnIsDeployingChanged(bool value) => OnPropertyChanged(nameof(HasRollback));

    private void EnsureAutoTimer()
    {
        if (!_active || !AutoRefresh) return;
        if (_autoTimer == null)
        {
            _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoTimer.Tick += (_, _) =>
            {
                if (_active && AutoRefresh && IsReady && !_refreshing && !IsBusy && !IsDeploying)
                    _ = RefreshProjectsAsync(silent: true);
            };
        }
        _autoTimer.Start();
    }

    /// <summary>
    /// 重建过滤后的项目列表并尽量保持选中项。重建期间抑制 SelectedProject 的副作用，
    /// 避免 ListBox 在集合清空时把选中项短暂置 null，导致日志流被误停。
    /// </summary>
    private void RebuildFilteredProjects(string? preferredName, bool loadDetailIfChanged)
    {
        var previousName = _selectedName;

        _suppressSelectionEffects = true;
        try
        {
            var f = (SearchText ?? "").Trim();
            FilteredProjects.Clear();
            foreach (var p in _allProjects)
            {
                if (f.Length > 0
                    && !p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                    && !p.WorkingDir.Contains(f, StringComparison.OrdinalIgnoreCase)
                    && !p.ConfigFilesText.Contains(f, StringComparison.OrdinalIgnoreCase))
                    continue;
                FilteredProjects.Add(p);
            }

            var next = FilteredProjects.FirstOrDefault(p => p.Name == preferredName)
                       ?? FilteredProjects.FirstOrDefault();
            SelectedProject = next;
        }
        finally
        {
            _suppressSelectionEffects = false;
        }

        ApplySelectionSideEffects(previousName);
        _selectedName = SelectedProject?.Name;

        if (loadDetailIfChanged
            && !_refreshing
            && !string.Equals(_selectedName, previousName, StringComparison.Ordinal))
            _ = LoadDetailAsync(SelectedProject);
    }

    // ---------------------------------------------------------------- 项目详情

    private async Task LoadDetailAsync(ComposeProjectRowViewModel? row, bool silent = false)
    {
        if (row == null)
        {
            Services.Clear();
            return;
        }

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        if (!silent) row.ErrorText = "";

        try
        {
            var detail = await ExecuteWithSudoRetryAsync(() => _compose.GetDetailAsync(row.Ref, cts.Token));
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedProject, row)) return;

            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < detail.Services.Count; i++) order[detail.Services[i]] = i;

            var rows = new List<ComposeServiceRowViewModel>();
            foreach (var container in detail.Containers
                         .OrderBy(c => order.TryGetValue(c.Service, out var i) ? i : int.MaxValue)
                         .ThenBy(c => c.Service, StringComparer.Ordinal)
                         .ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                rows.Add(new ComposeServiceRowViewModel(this, row, container.Service, container));
            }
            foreach (var service in detail.Services)
            {
                if (detail.Containers.All(c => !c.Service.Equals(service, StringComparison.Ordinal)))
                    rows.Add(new ComposeServiceRowViewModel(this, row, service, null));
            }

            Services.Clear();
            foreach (var r in rows) Services.Add(r);
            row.ErrorText = string.IsNullOrWhiteSpace(detail.Error) ? "" : detail.Error;
        }
        catch (OperationCanceledException) { }
        catch (SshManagerException ex) { row.ErrorText = ex.Message; }
        catch (Exception ex) { row.ErrorText = ex.Message; }
    }

    // ---------------------------------------------------------------- 项目操作

    public Task UpProjectAsync(ComposeProjectRowViewModel row)
        => OpStreamingAsync($"启动项目 {row.Name}",
            (line, ct) => _compose.UpStreamingAsync(row.Ref, build: false, forceRecreate: false, line, ct));

    public Task StopProjectAsync(ComposeProjectRowViewModel row)
        => OpAsync($"停止项目 {row.Name}", ct => _compose.StopAsync(row.Ref, null, ct));

    public Task RestartProjectAsync(ComposeProjectRowViewModel row)
        => OpAsync($"重启项目 {row.Name}", ct => _compose.RestartAsync(row.Ref, null, ct));

    public Task PullProjectAsync(ComposeProjectRowViewModel row)
        => OpStreamingAsync($"拉取项目镜像 {row.Name}",
            (line, ct) => _compose.PullStreamingAsync(row.Ref, null, line, ct));

    public Task BuildProjectAsync(ComposeProjectRowViewModel row)
        => OpStreamingAsync($"构建项目镜像 {row.Name}",
            (line, ct) => _compose.BuildStreamingAsync(row.Ref, null, line, ct));

    public Task RecreateProjectAsync(ComposeProjectRowViewModel row)
        => OpStreamingAsync($"强制重建项目 {row.Name}",
            (line, ct) => _compose.UpStreamingAsync(row.Ref, build: false, forceRecreate: true, line, ct));

    public async Task DownProjectAsync(ComposeProjectRowViewModel row)
    {
        if (!await Dialogs.ConfirmAsync(
                "停止并移除项目",
                $"将对项目 “{row.Name}” 执行 docker compose down：\n停止并删除容器与网络（数据卷保留）。\n\n继续？",
                danger: true, okText: "停止并移除"))
            return;

        await OpStreamingAsync($"停止并移除项目 {row.Name}",
            (line, ct) => _compose.DownStreamingAsync(row.Ref, line, ct));
    }

    public Task RestartServiceAsync(ComposeProjectRowViewModel project, ComposeServiceRowViewModel service)
        => OpAsync($"重启服务 {service.ServiceName}",
            ct => _compose.RestartAsync(project.Ref, service.ServiceName, ct));

    public Task RecreateServiceAsync(ComposeProjectRowViewModel project, ComposeServiceRowViewModel service)
        => OpStreamingAsync($"重建服务 {service.ServiceName}",
            (line, ct) => _compose.RecreateServiceStreamingAsync(project.Ref, service.ServiceName, line, ct));

    public void ExecService(ComposeProjectRowViewModel project, ComposeServiceRowViewModel service)
    {
        if (!service.HasContainer)
        {
            _owner.SetStatus($"服务 {service.ServiceName} 尚未创建容器，无法进入终端。", error: true);
            return;
        }
        var cmd = _compose.BuildExecCommand(project.Ref, service.ServiceName);
        _owner.PrefillInConsole(cmd);
        _owner.SetStatus("命令已填入终端，回车执行；若容器没有 bash，可把结尾的 bash 改为 sh。");
    }

    private async Task OpAsync(
        string title, Func<CancellationToken, Task<CommandResult>> action)
    {
        if (IsBusy || IsDeploying) return;
        IsBusy = true;
        PanelOpen = true;
        LogTitle = "操作输出";
        Logs.Line(">>> " + title);
        try
        {
            var result = await ExecuteWithSudoRetryAsync(() => action(CancellationToken.None));
            WriteCommandResult(result);
        }
        catch (SshManagerException ex) { Logs.Line("✖ " + ex.Message); }
        catch (Exception ex) { Logs.Line("✖ " + ex.Message); }
        finally
        {
            IsBusy = false;
            await RefreshProjectsAsync(silent: true);
            if (SelectedProject != null) await LoadDetailAsync(SelectedProject, silent: true);
        }
    }

    private async Task OpStreamingAsync(
        string title, Func<Action<string>, CancellationToken, Task<CommandResult>> action)
    {
        if (IsBusy || IsDeploying) return;
        IsBusy = true;
        PanelOpen = true;
        LogTitle = "操作输出";
        Logs.Line(">>> " + title);
        try
        {
            var result = await ExecuteWithSudoRetryAsync(
                () => action(line => Logs.Line(line), CancellationToken.None));
            WriteCommandResult(result);
        }
        catch (SshManagerException ex) { Logs.Line("✖ " + ex.Message); }
        catch (Exception ex) { Logs.Line("✖ " + ex.Message); }
        finally
        {
            IsBusy = false;
            await RefreshProjectsAsync(silent: true);
            if (SelectedProject != null) await LoadDetailAsync(SelectedProject, silent: true);
        }
    }

    private void WriteCommandResult(CommandResult result)
    {
        if (result.WasCancelled) Logs.Line("— 已停止 —");
        else if (result.ExitCode == 0) Logs.Line("✔ 完成");
        else Logs.Line($"✖ 失败（退出码 {result.ExitCode}）：{FirstLine(result.Combined)}");
    }

    // ---------------------------------------------------------------- 日志

    public async Task ShowProjectLogsAsync(ComposeProjectRowViewModel row)
    {
        PanelOpen = true;
        LogTitle = $"日志 · {row.Name}";
        _logProjectName = row.Name;
        _logService = null;
        await RestartLogsAsync();
    }

    public async Task ShowServiceLogsAsync(
        ComposeProjectRowViewModel project, ComposeServiceRowViewModel service)
    {
        PanelOpen = true;
        LogTitle = $"日志 · {project.Name} / {service.ServiceName}";
        _logProjectName = project.Name;
        _logService = service.ServiceName;
        await RestartLogsAsync();
    }

    [RelayCommand]
    private async Task RefreshLogs()
    {
        if (_logProjectName == null) return;
        await RestartLogsAsync();
    }

    [RelayCommand]
    private void StopLogs()
    {
        try { _logCts?.Cancel(); } catch { }
        IsLogFollowing = false;
    }

    [RelayCommand]
    private void ClearLog() => Logs.Clear();

    [RelayCommand]
    private void TogglePanel() => PanelOpen = !PanelOpen;

    public void SetLogTail(int tail)
    {
        if (LogTail == tail) return;
        LogTail = tail;
        if (_logProjectName != null && PanelOpen) _ = RestartLogsAsync();
    }

    partial void OnFollowLogsChanged(bool value)
    {
        if (_logProjectName != null && PanelOpen) _ = RestartLogsAsync();
    }

    private void CancelLogs()
    {
        try { _logCts?.Cancel(); } catch { }
        _logCts = null;
        IsLogFollowing = false;
        _logProjectName = null;
        _logService = null;
    }

    private async Task RestartLogsAsync()
    {
        try { _logCts?.Cancel(); } catch { }
        _logCts = null;
        if (_logProjectName == null) return;

        var row = Projects.FirstOrDefault(p => p.Name.Equals(_logProjectName, StringComparison.Ordinal));
        if (row == null) return;

        var cts = new CancellationTokenSource();
        _logCts = cts;
        Logs.Clear();
        LogsLoading = true;
        IsLogFollowing = FollowLogs;

        try
        {
            var got = false;
            var result = await ExecuteWithSudoRetryAsync(() => _compose.LogsAsync(
                row.Ref, _logService, LogTail, FollowLogs,
                line =>
                {
                    if (!ReferenceEquals(_logCts, cts)) return;
                    got = true;
                    Logs.Line(line);
                }, cts.Token));

            if (!ReferenceEquals(_logCts, cts)) return;
            if (cts.IsCancellationRequested || result.WasCancelled)
                Logs.Line("— 已停止跟踪 —");
            else if (!got)
                Logs.Line(result.ExitCode == 0
                    ? "（暂无日志输出）"
                    : $"✖ 获取日志失败（退出码 {result.ExitCode}）：{FirstLine(result.Combined)}");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_logCts, cts)) Logs.Line("— 已停止跟踪 —");
        }
        catch (SshManagerException ex)
        {
            if (ReferenceEquals(_logCts, cts)) Logs.Line("✖ " + ex.Message);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_logCts, cts)) Logs.Line("✖ " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_logCts, cts))
            {
                _logCts = null;
                IsLogFollowing = false;
                LogsLoading = false;
            }
        }
    }

    // ---------------------------------------------------------------- 一键升级 / 回滚

    public async Task UpgradeAsync(ComposeProjectRowViewModel row)
    {
        if (IsBusy || IsDeploying) return;

        var binding = _bindingStore.LoadForServer(_serverKey)
            .FirstOrDefault(b => b.ProjectName.Equals(row.Name, StringComparison.Ordinal));
        var vm = new ComposeUpgradeViewModel(_session, _compose, _deploy, row.Ref, binding, _serverKey);
        var dialog = new ComposeUpgradeDialog { DataContext = vm };
        var ok = await dialog.ShowDialog<bool>(Dialogs.MainWindow!);
        if (ok != true || vm.Decision == null) return;

        var decision = vm.Decision;
        if (decision.SaveBinding)
            _bindingStore.Upsert(_serverKey, decision.Config);

        // 远程目录不可写 / Docker 需要 sudo 时，提前要密码，避免上传到一半才弹窗
        if (!_session.IsRoot
            && string.IsNullOrEmpty(_session.SudoPassword)
            && ((!decision.Plan.RemoteDirWritable && _session.Profile.AutoSudo) || _compose.NeedsSudo))
        {
            var (pwOk, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                "升级需要 sudo 权限（远程目录不可写或 Docker 需要提权）。请输入密码（仅保存在本次会话内存）：",
                "", password: true);
            if (!pwOk || pw.Length == 0)
            {
                _owner.SetStatus("已取消升级：未提供 sudo 密码。", error: true);
                return;
            }
            _session.SudoPassword = pw;
        }

        await ExecuteUpgradeAsync(row, decision);
    }

    private async Task ExecuteUpgradeAsync(ComposeProjectRowViewModel row, ComposeUpgradeDecision decision)
    {
        IsDeploying = true;
        PanelOpen = true;
        LogTitle = "升级 · " + row.Name;
        Logs.Clear();
        LastBackupPath = null;
        _backupProjectName = row.Name;
        _backupProject = decision.Project;
        DeployPercent = 0;
        DeployStatusText = "准备升级…";

        try { _deployCts?.Cancel(); } catch { }
        _deployCts?.Dispose();
        var cts = new CancellationTokenSource();
        _deployCts = cts;

        var progress = new Progress<ComposeDeployProgress>(p =>
        {
            DeployPercent = p.Percent;
            DeployStatusText = p.Message;
        });

        var request = new ComposeDeployRequest
        {
            Project = decision.Project,
            Config = decision.Config,
        };

        try
        {
            var result = await ExecuteWithSudoRetryAsync(() => _deploy.ApplyAsync(
                request, decision.Plan, decision.SelectedPaths,
                line => Logs.Line(line), progress, cts.Token));

            LastBackupPath = result.BackupPath;
            if (result.Cancelled)
            {
                Logs.Line("— 升级已取消 —");
            }
            else if (result.Success)
            {
                Logs.Line("✔ 升级完成。");
                if (result.Health.Error != null
                    || (result.Health.Total > 0 && result.Health.Running == 0)
                    || result.Health.Unhealthy > 0)
                    Logs.Line("⚠ " + result.Health.Summary);
            }
            else
            {
                Logs.Line("✖ 升级失败：" + (result.Error ?? "未知错误"));
                if (result.BackupPath != null)
                    Logs.Line("可点击日志面板上的「回滚」恢复：" + result.BackupPath);
            }
        }
        catch (SshManagerException ex) { Logs.Line("✖ " + ex.Message); }
        catch (Exception ex) { Logs.Line("✖ 升级出错：" + ex.Message); }
        finally
        {
            IsDeploying = false;
            DeployPercent = 0;
            DeployStatusText = "";
            if (ReferenceEquals(_deployCts, cts))
            {
                _deployCts = null;
                cts.Dispose();
            }
            await RefreshProjectsAsync(silent: true);
            if (SelectedProject != null) await LoadDetailAsync(SelectedProject, silent: true);
        }
    }

    [RelayCommand]
    private void CancelDeploy()
    {
        try { _deployCts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private async Task Rollback()
    {
        if (SelectedProject == null || LastBackupPath == null || IsDeploying) return;
        if (!await Dialogs.ConfirmAsync(
                "回滚项目",
                $"将使用以下备份恢复项目文件，并强制重建容器：\n{LastBackupPath}\n\n" +
                "注意：本次升级新增、备份中没有的文件不会被删除。\n继续？",
                danger: true, okText: "回滚", mono: true))
            return;

        IsDeploying = true;
        PanelOpen = true;
        LogTitle = "回滚 · " + SelectedProject.Name;
        try { _deployCts?.Cancel(); } catch { }
        _deployCts?.Dispose();
        var cts = new CancellationTokenSource();
        _deployCts = cts;

        var progress = new Progress<ComposeDeployProgress>(p =>
        {
            DeployPercent = p.Percent;
            DeployStatusText = p.Message;
        });

        try
        {
            var project = _backupProject ?? SelectedProject.Ref;
            var result = await ExecuteWithSudoRetryAsync(() => _deploy.RollbackAsync(
                project, LastBackupPath,
                line => Logs.Line(line), progress, cts.Token));

            if (result.Success)
            {
                Logs.Line("✔ 回滚完成。健康检查：" + result.Health.Summary);
                LastBackupPath = null;
                _backupProjectName = null;
                _backupProject = null;
            }
            else
            {
                Logs.Line("✖ 回滚失败：" + (result.Error ?? "未知错误"));
            }
        }
        catch (SshManagerException ex) { Logs.Line("✖ " + ex.Message); }
        catch (Exception ex) { Logs.Line("✖ 回滚出错：" + ex.Message); }
        finally
        {
            IsDeploying = false;
            DeployPercent = 0;
            DeployStatusText = "";
            if (ReferenceEquals(_deployCts, cts))
            {
                _deployCts = null;
                cts.Dispose();
            }
            await RefreshProjectsAsync(silent: true);
            if (SelectedProject != null) await LoadDetailAsync(SelectedProject, silent: true);
        }
    }

    public async Task ShowBackupsAsync(ComposeProjectRowViewModel row)
    {
        var binding = _bindingStore.LoadForServer(_serverKey)
            .FirstOrDefault(b => b.ProjectName.Equals(row.Name, StringComparison.Ordinal));
        var projectRef = binding != null
            ? new ComposeProjectRef
            {
                Name = row.Name,
                WorkingDir = binding.RemoteDir.Length > 0 ? binding.RemoteDir : row.Ref.WorkingDir,
                ComposeFiles = binding.ComposeFiles.Count > 0 ? binding.ComposeFiles : row.Ref.ComposeFiles,
            }
            : row.Ref;

        var vm = new ComposeBackupsViewModel(_session, _deploy, projectRef);
        var dialog = new ComposeBackupsDialog { DataContext = vm };
        await dialog.ShowDialog<bool>(Dialogs.MainWindow!);
        await RefreshProjectsAsync(silent: true);
    }

    // ---------------------------------------------------------------- 跳转 / 绑定

    public async Task EditComposeAsync(ComposeProjectRowViewModel row)
    {
        var file = row.Ref.ComposeFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file))
        {
            _owner.SetStatus("未找到 compose 文件路径。", error: true);
            return;
        }
        await _owner.OpenRemoteFileInFilesAsync(file);
    }

    public async Task OpenProjectDirAsync(ComposeProjectRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.Ref.WorkingDir))
        {
            _owner.SetStatus("未找到项目目录。", error: true);
            return;
        }
        await _owner.OpenRemoteDirectoryInFilesAsync(row.Ref.WorkingDir);
    }

    [RelayCommand]
    private async Task AddProject()
    {
        var vm = new ComposeProjectDialogViewModel(
            _session, _compose, _serverKey, null, null, manual: true);
        var dialog = new ComposeProjectDialog { DataContext = vm };
        if (await dialog.ShowDialog<bool>(Dialogs.MainWindow!) == true)
            await RefreshProjectsAsync();
    }

    public async Task EditBindingAsync(ComposeProjectRowViewModel row)
    {
        var binding = _bindingStore.LoadForServer(_serverKey)
            .FirstOrDefault(b => b.ProjectName.Equals(row.Name, StringComparison.Ordinal));
        var vm = new ComposeProjectDialogViewModel(
            _session, _compose, _serverKey, binding, row.Ref, manual: row.IsManual);
        var dialog = new ComposeProjectDialog { DataContext = vm };
        if (await dialog.ShowDialog<bool>(Dialogs.MainWindow!) == true)
            await RefreshProjectsAsync();
    }

    public async Task RemoveBindingAsync(ComposeProjectRowViewModel row)
    {
        var binding = _bindingStore.LoadForServer(_serverKey)
            .FirstOrDefault(b => b.ProjectName.Equals(row.Name, StringComparison.Ordinal));
        if (binding == null)
        {
            _owner.SetStatus("该项目没有本地绑定记录。", error: true);
            return;
        }

        if (!await Dialogs.ConfirmAsync(
                "移除项目绑定",
                $"移除 “{row.Name}” 的本地绑定与升级设置？\n不会停止或删除服务器上的容器。",
                danger: true, okText: "移除绑定"))
            return;

        _bindingStore.Remove(_serverKey, row.Name);
        await RefreshProjectsAsync();
        _owner.SetStatus("已移除项目绑定（服务器容器未受影响）。");
    }

    // ---------------------------------------------------------------- sudo / 清理

    /// <summary>缺少 sudo 密码时弹窗补一次，然后重试；用户取消则转成 SshManagerException。</summary>
    private async Task<T> ExecuteWithSudoRetryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SudoPasswordRequiredException)
        {
            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要 sudo 权限才能完成此操作，请输入密码（仅保存在本次会话内存）：",
                "", password: true);
            if (!ok || pw.Length == 0)
                throw new SshManagerException("已取消：未提供 sudo 密码。");
            _session.SudoPassword = pw;
            return await action();
        }
    }

    internal void Cleanup()
    {
        _active = false;
        _autoTimer?.Stop();
        _autoTimer = null;
        try { _detailCts?.Cancel(); } catch { }
        try { _logCts?.Cancel(); } catch { }
        try { _deployCts?.Cancel(); } catch { }
        _detailCts = null;
        _logCts = null;
        _deployCts = null;
    }

    private static string FirstLine(string? text)
    {
        var t = (text ?? "").Replace("\r", "").Trim();
        if (t.Length == 0) return "未知错误";
        var i = t.IndexOf('\n');
        return (i >= 0 ? t[..i] : t).Trim();
    }
}

/// <summary>升级向导返回给页面的决策。</summary>
public sealed record ComposeUpgradeDecision(
    ComposeProjectConfig Config,
    ComposeProjectRef Project,
    ComposeDeployPlan Plan,
    IReadOnlyList<string> SelectedPaths,
    bool SaveBinding);
