using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.App.Views;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

/// <summary>升级向导：左边设置，右边变更预览；扫描完成后返回决策。</summary>
public partial class ComposeUpgradeViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly ComposeService _compose;
    private readonly ComposeDeployService _deploy;
    private readonly ComposeProjectRef _sourceProject;
    private readonly string _serverKey;
    private readonly bool _manual;
    private ComposeDeployPlan? _plan;
    private bool _suppressInvalidate;
    private bool _suppressSelection;
    private int _scanVersion;

    [ObservableProperty] private string _localDir = "";
    [ObservableProperty] private string _remoteDir = "";
    [ObservableProperty] private string _composeFilesText = "";
    [ObservableProperty] private string _excludeText = "";
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private string _customCommand = "";
    [ObservableProperty] private bool _forceRecreate;
    [ObservableProperty] private bool _backupEnabled = true;
    [ObservableProperty] private int _backupKeep = 5;
    [ObservableProperty] private bool _saveBinding = true;
    [ObservableProperty] private bool _forceAll;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _isCustomMode;
    [ObservableProperty] private string _statusText = "配置好本地/远程目录后，点击“扫描变更”。";
    [ObservableProperty] private string _commandPreview = "";
    [ObservableProperty] private string _backupPreview = "";
    [ObservableProperty] private string _summaryText = "尚未扫描";
    [ObservableProperty] private string _remoteStateText = "";

    public ObservableCollection<ComposeChangeRowViewModel> Changes { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<ComposeExtraFileRowViewModel> ExtraFiles { get; } = new();
    public bool HasWarnings => Warnings.Count > 0;
    public string ProjectTitle => _sourceProject.Name;

    public ComposeUpgradeDecision? Decision { get; private set; }

    public ComposeUpgradeViewModel(
        SshSession session,
        ComposeService compose,
        ComposeDeployService deploy,
        ComposeProjectRef sourceProject,
        ComposeProjectConfig? binding,
        string serverKey)
    {
        _session = session;
        _compose = compose;
        _deploy = deploy;
        _sourceProject = sourceProject;
        _serverKey = serverKey;
        _manual = binding?.Manual == true;

        var cfg = binding?.Clone() ?? new ComposeProjectConfig();
        if (string.IsNullOrWhiteSpace(cfg.ProjectName)) cfg.ProjectName = sourceProject.Name;
        if (string.IsNullOrWhiteSpace(cfg.RemoteDir)) cfg.RemoteDir = sourceProject.WorkingDir;
        if (cfg.ComposeFiles.Count == 0) cfg.ComposeFiles = sourceProject.ComposeFiles.ToList();
        if (cfg.ExcludePatterns == null || cfg.ExcludePatterns.Count == 0)
            cfg.ExcludePatterns = ComposeDefaults.NewExcludes();
        cfg.BackupKeep = Math.Clamp(cfg.BackupKeep, 1, 50);

        _suppressInvalidate = true;
        LocalDir = cfg.LocalDir;
        RemoteDir = cfg.RemoteDir;
        ComposeFilesText = string.Join("\n", cfg.ComposeFiles);
        ExcludeText = string.Join("\n", cfg.ExcludePatterns);
        ModeIndex = (int)cfg.Mode;
        CustomCommand = cfg.CustomCommand;
        ForceRecreate = cfg.ForceRecreate;
        BackupEnabled = cfg.BackupEnabled;
        BackupKeep = cfg.BackupKeep;
        IsCustomMode = cfg.Mode == ComposeUpgradeMode.Custom;
        SaveBinding = true;
        _suppressInvalidate = false;
    }

    // ---------------------------------------------------------------- 设置联动

    partial void OnLocalDirChanged(string value) => Invalidate();
    partial void OnRemoteDirChanged(string value) => Invalidate();
    partial void OnComposeFilesTextChanged(string value) => Invalidate();
    partial void OnExcludeTextChanged(string value) => Invalidate();

    partial void OnModeIndexChanged(int value)
    {
        IsCustomMode = value == (int)ComposeUpgradeMode.Custom;
        RefreshCommandPreview();
    }

    partial void OnCustomCommandChanged(string value) => RefreshCommandPreview();
    partial void OnForceRecreateChanged(bool value) => RefreshCommandPreview();
    partial void OnBackupEnabledChanged(bool value)
    {
        if (_plan != null)
            BackupPreview = value ? _plan.BackupPreview : "（本次不备份）";
    }

    partial void OnForceAllChanged(bool value)
    {
        if (_plan == null) return;
        _suppressSelection = true;
        foreach (var c in Changes)
        {
            if (c.Change.Kind == ComposeChangeKind.Unchanged)
                c.IsSelected = value;
        }
        _suppressSelection = false;
        OnSelectionChanged();
    }

    private void Invalidate()
    {
        if (_suppressInvalidate) return;
        _scanVersion++;
        _plan = null;
        HasPlan = false;
        CanStart = false;
        Changes.Clear();
        Warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
        CommandPreview = "";
        BackupPreview = "";
        RemoteStateText = "";
        SummaryText = "尚未扫描";
        StatusText = "设置已修改，请重新扫描。";
    }

    private void OnSelectionChanged()
    {
        if (_suppressSelection) return;
        RefreshCommandPreview();
    }

    private void RefreshCommandPreview()
    {
        if (_plan == null) return;
        var config = BuildConfig();
        var hasChanges = Changes.Any(c =>
            c.IsSelected && c.Change.Kind != ComposeChangeKind.RemoteOnly);
        CommandPreview = _compose.BuildUpgradeCommand(
            EffectiveProject(config), config.Mode, _plan.BuildDetected,
            hasChanges, config.ForceRecreate, config.CustomCommand);
        CanStart = Changes.Any(c => c.IsSelected);
    }

    /// <summary>绑定已完整时，打开对话框后自动扫描，减少一次点击。</summary>
    public void AutoScanIfReady()
    {
        if (IsScanning || HasPlan) return;
        if (string.IsNullOrWhiteSpace(RemoteDir)) return;
        if (string.IsNullOrWhiteSpace(LocalDir) && ExtraFiles.Count == 0) return;
        if (string.IsNullOrWhiteSpace(ComposeFilesText)) return;
        _ = Scan();
    }

    // ---------------------------------------------------------------- 扫描

    [RelayCommand]
    private async Task Scan()
    {
        if (IsScanning) return;
        if (string.IsNullOrWhiteSpace(LocalDir) && ExtraFiles.Count == 0)
        {
            StatusText = "请选择本地部署目录，或至少添加一个文件。";
            return;
        }
        if (string.IsNullOrWhiteSpace(RemoteDir))
        {
            StatusText = "请填写远程项目目录。";
            return;
        }

        var config = BuildConfig();
        if (config.ComposeFiles.Count == 0)
        {
            StatusText = "请至少填写一个 compose 文件路径。";
            return;
        }

        IsScanning = true;
        StatusText = "正在枚举文件并比对…";
        _plan = null;
        HasPlan = false;
        CanStart = false;
        Changes.Clear();
        Warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
        CommandPreview = "";
        BackupPreview = "";
        RemoteStateText = "";
        SummaryText = "正在扫描…";
        var version = ++_scanVersion;
        try
        {
            var request = new ComposeDeployRequest
            {
                Project = EffectiveProject(config),
                Config = config,
                ExtraFiles = ExtraFiles.Select(e => new ComposeExtraFile
                {
                    LocalPath = e.LocalPath,
                    RemoteRelativePath = e.RemoteRelativePath,
                }).ToList(),
            };

            var result = await ExecuteWithSudoRetryAsync(
                () => _deploy.BuildPlanAsync(request, ForceAll));

            if (version != _scanVersion)
                return; // 扫描期间设置被修改，丢弃旧结果

            if (!result.Ok || result.Plan == null)
            {
                _plan = null;
                HasPlan = false;
                StatusText = result.Message ?? "比对失败。";
                return;
            }

            _plan = result.Plan;
            Populate(result.Plan);
            HasPlan = true;
            StatusText = "比对完成。确认文件与命令后点击“开始升级”。";
        }
        catch (SshManagerException ex)
        {
            HasPlan = false;
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            HasPlan = false;
            StatusText = "比对失败：" + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Populate(ComposeDeployPlan plan)
    {
        Changes.Clear();
        foreach (var change in plan.Files)
        {
            var selected = change.Kind is ComposeChangeKind.Added or ComposeChangeKind.Modified
                           || (ForceAll && change.Kind == ComposeChangeKind.Unchanged);
            Changes.Add(new ComposeChangeRowViewModel(change, OnSelectionChanged)
            {
                IsSelected = selected,
            });
        }

        Warnings.Clear();
        foreach (var w in plan.Warnings) Warnings.Add(w);
        OnPropertyChanged(nameof(HasWarnings));

        CommandPreview = plan.CommandPreview;
        BackupPreview = BackupEnabled ? plan.BackupPreview : "（本次不备份）";
        RemoteStateText = plan.RemoteDirExists
            ? plan.RemoteDirWritable
                ? "远程目录存在且可写"
                : "远程目录存在，当前用户不可写（将尝试 sudo）"
            : "远程目录不存在（将尝试创建）";
        SummaryText = $"新增 {plan.AddedCount} · 修改 {plan.ModifiedCount} · " +
                      $"未变 {plan.UnchangedCount} · 仅远程 {plan.RemoteOnlyCount}";
        CanStart = Changes.Any(c => c.IsSelected);
    }

    // ---------------------------------------------------------------- 文件 / 目录选择

    [RelayCommand]
    private async Task BrowseLocal()
    {
        var top = Dialogs.MainWindow;
        if (top == null) return;
        var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择本地部署目录",
            AllowMultiple = false,
        });
        var path = dirs.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) LocalDir = path;
    }

    [RelayCommand]
    private async Task BrowseRemote()
    {
        var start = string.IsNullOrWhiteSpace(RemoteDir) ? _sourceProject.WorkingDir : RemoteDir;
        var dlg = new FolderPickerDialog(_session, start);
        if (await dlg.ShowDialog<bool>(Dialogs.MainWindow!) == true)
            RemoteDir = dlg.Result;
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        var top = Dialogs.MainWindow;
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = true,
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) continue;
            ExtraFiles.Add(new ComposeExtraFileRowViewModel(
                path,
                () => Invalidate(),
                row => { ExtraFiles.Remove(row); Invalidate(); }));
        }
        if (files.Count > 0) Invalidate();
    }

    // ---------------------------------------------------------------- 决策

    public bool TryBuildDecision()
    {
        if (_plan == null)
        {
            StatusText = "请先点击“扫描变更”。";
            return false;
        }
        if (!Changes.Any(c => c.IsSelected))
        {
            StatusText = "请至少勾选一个要上传的文件。";
            return false;
        }

        var config = BuildConfig();
        if (config.Mode == ComposeUpgradeMode.Custom && string.IsNullOrWhiteSpace(config.CustomCommand))
        {
            StatusText = "升级方式为“自定义命令”，请填写要执行的命令。";
            return false;
        }

        Decision = new ComposeUpgradeDecision(
            config,
            EffectiveProject(config),
            _plan,
            Changes.Where(c => c.IsSelected).Select(c => c.Change.RelativePath).ToList(),
            SaveBinding);
        return true;
    }

    private ComposeProjectConfig BuildConfig()
    {
        var files = ComposeFilesText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var excludes = ExcludeText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var remote = (RemoteDir ?? "").Trim();
        if (remote.Length > 0) remote = UnixPath.Normalize(remote);

        return new ComposeProjectConfig
        {
            ProjectName = _sourceProject.Name,
            Manual = _manual,
            RemoteDir = remote,
            ComposeFiles = files,
            LocalDir = LocalDir.Trim(),
            ExcludePatterns = excludes.Count > 0 ? excludes : ComposeDefaults.NewExcludes(),
            Mode = (ComposeUpgradeMode)Math.Clamp(ModeIndex, 0, 4),
            CustomCommand = CustomCommand.Trim(),
            ForceRecreate = ForceRecreate,
            BackupEnabled = BackupEnabled,
            BackupKeep = Math.Clamp(BackupKeep, 1, 50),
        };
    }

    private ComposeProjectRef EffectiveProject(ComposeProjectConfig config) => new()
    {
        Name = _sourceProject.Name,
        WorkingDir = config.RemoteDir,
        ComposeFiles = config.ComposeFiles.ToList(),
    };

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
                $"用户 {_session.Profile.Username} 需要 sudo 权限才能读取/写入远程目录，请输入密码（仅保存在本次会话内存）：",
                "", password: true);
            if (!ok || pw.Length == 0)
                throw new SshManagerException("已取消：未提供 sudo 密码。");
            _session.SudoPassword = pw;
            return await action();
        }
    }
}

/// <summary>变更预览中的一行（可勾选）。</summary>
public partial class ComposeChangeRowViewModel : ViewModelBase
{
    private readonly Action? _onSelectionChanged;

    public ComposeChangeRowViewModel(ComposeFileChange change, Action? onSelectionChanged = null)
    {
        Change = change;
        _onSelectionChanged = onSelectionChanged;
    }

    public ComposeFileChange Change { get; }
    public string RelativePath => Change.RelativePath;
    public bool Selectable => Change.Kind != ComposeChangeKind.RemoteOnly;
    public string KindText => Change.Kind switch
    {
        ComposeChangeKind.Added => "新增",
        ComposeChangeKind.Modified => "修改",
        ComposeChangeKind.Unchanged => "未变",
        ComposeChangeKind.RemoteOnly => "仅远程",
        _ => "未知",
    };

    public IBrush KindBrush => new SolidColorBrush(Color.Parse(Change.Kind switch
    {
        ComposeChangeKind.Added => "#10b981",
        ComposeChangeKind.Modified => "#f59e0b",
        ComposeChangeKind.Unchanged => "#8b93a5",
        ComposeChangeKind.RemoteOnly => "#3b82f6",
        _ => "#8b93a5",
    }));

    public string LocalSizeText => Change.Kind == ComposeChangeKind.RemoteOnly
        ? "—"
        : UnixPath.HumanSize(Change.LocalSize);

    public string RemoteSizeText => Change.RemoteExists
        ? UnixPath.HumanSize(Change.RemoteSize)
        : "—";

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged?.Invoke();
}

/// <summary>升级时临时选择的上传文件。</summary>
public partial class ComposeExtraFileRowViewModel : ViewModelBase
{
    private readonly Action? _onChanged;
    private readonly Action<ComposeExtraFileRowViewModel>? _onRemove;

    public ComposeExtraFileRowViewModel(
        string localPath,
        Action? onChanged = null,
        Action<ComposeExtraFileRowViewModel>? onRemove = null)
    {
        LocalPath = localPath;
        _remoteRelativePath = Path.GetFileName(localPath);
        _onChanged = onChanged;
        _onRemove = onRemove;
    }

    public string LocalPath { get; }
    public string FileName => Path.GetFileName(LocalPath);

    [ObservableProperty] private string _remoteRelativePath;

    partial void OnRemoteRelativePathChanged(string value) => _onChanged?.Invoke();

    [RelayCommand]
    private void Remove() => _onRemove?.Invoke(this);
}
