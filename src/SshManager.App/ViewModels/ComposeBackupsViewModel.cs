using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

/// <summary>备份管理：列出 / 恢复 / 删除项目目录下的 .sshmanager-backups 归档。</summary>
public partial class ComposeBackupsViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly ComposeDeployService _deploy;
    private readonly ComposeProjectRef _project;
    private readonly StringBuilder _log = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _logText = "";

    public ObservableCollection<ComposeBackupRowViewModel> Items { get; } = new();
    public string ProjectTitle => _project.Name;
    public string BackupDirText => UnixPath.Combine(_project.WorkingDir, ".sshmanager-backups");

    public ComposeBackupsViewModel(
        SshSession session, ComposeDeployService deploy, ComposeProjectRef project)
    {
        _session = session;
        _deploy = deploy;
        _project = project;
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取备份…";
        try
        {
            var result = await ExecuteWithSudoRetryAsync(() => _deploy.ListBackupsAsync(_project));
            Items.Clear();
            if (!result.Ok)
            {
                StatusText = result.Error ?? "读取备份失败。";
                IsEmpty = true;
                return;
            }
            foreach (var item in result.Items)
                Items.Add(new ComposeBackupRowViewModel(this, item));
            IsEmpty = Items.Count == 0;
            StatusText = IsEmpty ? "暂无备份" : $"共 {Items.Count} 份备份";
        }
        catch (SshManagerException ex)
        {
            StatusText = ex.Message;
            IsEmpty = true;
        }
        catch (Exception ex)
        {
            StatusText = "读取备份失败：" + ex.Message;
            IsEmpty = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RestoreAsync(ComposeBackupRowViewModel row)
    {
        if (IsBusy) return;
        if (!await Dialogs.ConfirmAsync(
                "恢复备份",
                $"将使用备份恢复项目文件，并强制重建容器：\n{row.Info.Path}\n\n" +
                "本次新增、备份中没有的文件不会被删除。\n继续？",
                danger: true, okText: "恢复", mono: true))
            return;

        IsBusy = true;
        StatusText = "正在恢复备份…";
        AppendLog(">>> 恢复 " + row.Info.Name);
        try
        {
            var result = await ExecuteWithSudoRetryAsync(() => _deploy.RollbackAsync(
                _project, row.Info.Path,
                line => AppendLog(line),
                null, CancellationToken.None));

            if (result.Success)
            {
                AppendLog("✔ 恢复完成。健康检查：" + result.Health.Summary);
                StatusText = "恢复完成。";
            }
            else
            {
                AppendLog("✖ 恢复失败：" + (result.Error ?? "未知错误"));
                StatusText = "恢复失败。";
            }
        }
        catch (SshManagerException ex)
        {
            AppendLog("✖ " + ex.Message);
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            AppendLog("✖ " + ex.Message);
            StatusText = "恢复失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteAsync(ComposeBackupRowViewModel row)
    {
        if (IsBusy) return;
        if (!await Dialogs.ConfirmAsync(
                "删除备份",
                $"删除备份文件 “{row.Info.Name}”？此操作不可恢复。",
                danger: true, okText: "删除"))
            return;

        IsBusy = true;
        var deleted = false;
        try
        {
            var result = await ExecuteWithSudoRetryAsync(
                () => _deploy.DeleteBackupAsync(_project, row.Info.Name));
            StatusText = result.Ok ? "已删除备份。" : (result.Error ?? "删除失败。");
            deleted = result.Ok;
        }
        catch (SshManagerException ex) { StatusText = ex.Message; }
        catch (Exception ex) { StatusText = "删除失败：" + ex.Message; }
        finally
        {
            IsBusy = false;
        }

        if (deleted) await LoadAsync();
    }

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        LogText = _log.ToString();
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
                $"用户 {_session.Profile.Username} 需要 sudo 权限才能操作备份目录，请输入密码（仅保存在本次会话内存）：",
                "", password: true);
            if (!ok || pw.Length == 0)
                throw new SshManagerException("已取消：未提供 sudo 密码。");
            _session.SudoPassword = pw;
            return await action();
        }
    }
}

public partial class ComposeBackupRowViewModel : ViewModelBase
{
    private readonly ComposeBackupsViewModel _parent;

    public ComposeBackupRowViewModel(ComposeBackupsViewModel parent, ComposeBackupInfo info)
    {
        _parent = parent;
        Info = info;
    }

    public ComposeBackupInfo Info { get; }
    public string Name => Info.Name;
    public string SizeText => UnixPath.HumanSize(Info.Size);
    public string TimeText => Info.ModifiedUtc == DateTime.MinValue
        ? "—"
        : Info.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    [RelayCommand] private Task Restore() => _parent.RestoreAsync(this);
    [RelayCommand] private Task Delete() => _parent.DeleteAsync(this);
}
