using System.Text.RegularExpressions;
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

/// <summary>添加手动 compose 项目 / 编辑项目绑定。</summary>
public partial class ComposeProjectDialogViewModel : ViewModelBase
{
    private static readonly string[] ComposeCandidates =
    {
        "compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml",
    };

    private readonly SshSession _session;
    private readonly ComposeService _compose;
    private readonly ComposeBindingStore _store = new();
    private readonly string _serverKey;
    private readonly ComposeProjectConfig? _existing;
    private readonly bool _manual;
    private readonly bool _nameEditable;

    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string _remoteDir = "";
    [ObservableProperty] private string _composeFilesText = "";
    [ObservableProperty] private string _localDir = "";
    [ObservableProperty] private string _excludeText = "";
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private string _customCommand = "";
    [ObservableProperty] private bool _forceRecreate;
    [ObservableProperty] private bool _backupEnabled = true;
    [ObservableProperty] private int _backupKeep = 5;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCustomMode;
    [ObservableProperty] private string _statusText = "";

    public ComposeProjectConfig? Result { get; private set; }
    /// <summary>只有“新增手动项目”允许改项目名；编辑已有发现项目时项目名是服务器上的标识，不能改。</summary>
    public bool IsNameEditable => _nameEditable;
    public string Title => _existing == null && _nameEditable ? "添加 Compose 项目" : "编辑项目绑定";

    public ComposeProjectDialogViewModel(
        SshSession session,
        ComposeService compose,
        string serverKey,
        ComposeProjectConfig? existing,
        ComposeProjectRef? project,
        bool manual)
    {
        _session = session;
        _compose = compose;
        _serverKey = serverKey;
        _existing = existing;
        _manual = manual;
        _nameEditable = existing == null && project == null;

        var cfg = existing?.Clone() ?? new ComposeProjectConfig();
        if (string.IsNullOrWhiteSpace(cfg.ProjectName)) cfg.ProjectName = project?.Name ?? "";
        if (string.IsNullOrWhiteSpace(cfg.RemoteDir)) cfg.RemoteDir = project?.WorkingDir ?? "";
        if (cfg.ComposeFiles.Count == 0 && project != null)
            cfg.ComposeFiles = project.ComposeFiles.ToList();
        if (cfg.ExcludePatterns == null || cfg.ExcludePatterns.Count == 0)
            cfg.ExcludePatterns = ComposeDefaults.NewExcludes();

        ProjectName = cfg.ProjectName;
        RemoteDir = cfg.RemoteDir;
        ComposeFilesText = string.Join("\n", cfg.ComposeFiles);
        LocalDir = cfg.LocalDir;
        ExcludeText = string.Join("\n", cfg.ExcludePatterns);
        ModeIndex = (int)cfg.Mode;
        CustomCommand = cfg.CustomCommand;
        ForceRecreate = cfg.ForceRecreate;
        BackupEnabled = cfg.BackupEnabled;
        BackupKeep = Math.Clamp(cfg.BackupKeep, 1, 50);
        IsCustomMode = cfg.Mode == ComposeUpgradeMode.Custom;
    }

    partial void OnModeIndexChanged(int value)
    {
        IsCustomMode = value == (int)ComposeUpgradeMode.Custom;
        if (IsCustomMode) StatusText = "";
    }

    [RelayCommand]
    private async Task BrowseRemote()
    {
        var start = string.IsNullOrWhiteSpace(RemoteDir) ? "/home" : RemoteDir;
        var dlg = new FolderPickerDialog(_session, start);
        if (await dlg.ShowDialog<bool>(Dialogs.MainWindow!) != true) return;
        RemoteDir = dlg.Result;
        if (IsNameEditable && string.IsNullOrWhiteSpace(ProjectName))
            ProjectName = SanitizeProjectName(UnixPath.FileName(dlg.Result));
    }

    [RelayCommand]
    private async Task BrowseLocal()
    {
        var top = Dialogs.MainWindow;
        if (top == null) return;
        var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择本地部署目录（与远程目录按相对路径对应）",
            AllowMultiple = false,
        });
        var path = dirs.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) LocalDir = path;
    }

    /// <summary>在远程目录中识别标准 compose 文件。</summary>
    [RelayCommand]
    private async Task DetectComposeFiles()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(RemoteDir))
        {
            StatusText = "请先选择远程目录。";
            return;
        }

        IsBusy = true;
        StatusText = "正在识别 compose 文件…";
        try
        {
            foreach (var name in ComposeCandidates)
            {
                var path = UnixPath.Combine(RemoteDir.Trim(), name);
                if (await _session.ExistsAsync(path))
                {
                    ComposeFilesText = path;
                    if (IsNameEditable && string.IsNullOrWhiteSpace(ProjectName))
                        ProjectName = SanitizeProjectName(UnixPath.FileName(RemoteDir.Trim()));
                    StatusText = "已识别：" + path;
                    return;
                }
            }
            StatusText = "未找到标准 compose 文件，请手动填写路径。";
        }
        catch (Exception ex)
        {
            StatusText = "识别失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存绑定；返回 true 时调用方关闭对话框。</summary>
    public async Task<bool> TrySaveAsync()
    {
        if (IsBusy) return false;

        var name = (ProjectName ?? "").Trim();
        if (name.Length == 0)
        {
            StatusText = "请填写项目名。";
            return false;
        }
        // 只有新增手动项目时校验命名；已发现项目的名字来自服务器，必须原样保留
        if (IsNameEditable && !Regex.IsMatch(name, "^[a-z0-9][a-z0-9_-]*$"))
        {
            StatusText = "项目名只能包含小写字母、数字、下划线和连字符，且以字母或数字开头。";
            return false;
        }

        var remote = (RemoteDir ?? "").Trim();
        if (remote.Length == 0)
        {
            StatusText = "请填写远程项目目录。";
            return false;
        }
        remote = UnixPath.Normalize(remote);

        var files = (ComposeFilesText ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (files.Count == 0)
        {
            StatusText = "请至少填写一个 compose 文件路径。";
            return false;
        }

        var excludes = (ExcludeText ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var config = new ComposeProjectConfig
        {
            ProjectName = name,
            Manual = _manual,
            RemoteDir = remote,
            ComposeFiles = files,
            LocalDir = (LocalDir ?? "").Trim(),
            ExcludePatterns = excludes.Count > 0 ? excludes : ComposeDefaults.NewExcludes(),
            Mode = (ComposeUpgradeMode)Math.Clamp(ModeIndex, 0, 4),
            CustomCommand = (CustomCommand ?? "").Trim(),
            ForceRecreate = ForceRecreate,
            BackupEnabled = BackupEnabled,
            BackupKeep = Math.Clamp(BackupKeep, 1, 50),
        };

        IsBusy = true;
        StatusText = "正在校验 compose 配置…";
        try
        {
            var projectRef = new ComposeProjectRef
            {
                Name = name,
                WorkingDir = remote,
                ComposeFiles = files,
            };
            var validate = await ExecuteWithSudoRetryAsync(() => _compose.ValidateAsync(projectRef));
            if (validate.ExitCode != 0)
            {
                var ok = await Dialogs.ConfirmAsync(
                    "配置校验失败",
                    "docker compose config -q 返回错误：\n" + FirstLine(validate.Combined) +
                    "\n\n仍然保存绑定？",
                    okText: "仍然保存");
                if (!ok)
                {
                    StatusText = "已取消保存，请检查 compose 文件路径/内容。";
                    return false;
                }
            }

            _store.Upsert(_serverKey, config);
            Result = config;
            return true;
        }
        catch (SshManagerException ex)
        {
            StatusText = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

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
                $"校验 compose 需要 sudo 权限，请输入用户 {_session.Profile.Username} 的密码（仅保存在本次会话内存）：",
                "", password: true);
            if (!ok || pw.Length == 0)
                throw new SshManagerException("已取消：未提供 sudo 密码。");
            _session.SudoPassword = pw;
            return await action();
        }
    }

    private static string SanitizeProjectName(string name)
    {
        var s = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9_-]", "-").Trim('-');
        if (s.Length == 0) return "project";
        if (!char.IsLetterOrDigit(s[0])) s = "p-" + s;
        return s;
    }

    private static string FirstLine(string? text)
    {
        var t = (text ?? "").Replace("\r", "").Trim();
        if (t.Length == 0) return "未知错误";
        var i = t.IndexOf('\n');
        return (i >= 0 ? t[..i] : t).Trim();
    }
}
