using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

public partial class AppCenterViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly AppManagerService _apps;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] bool _filterAll = true;
    [ObservableProperty] bool _filterApt;
    [ObservableProperty] bool _filterSnap;
    [ObservableProperty] bool _filterFlatpak;

    [ObservableProperty] private string _installName = "";
    [ObservableProperty] private bool _installApt = true;
    [ObservableProperty] private bool _installSnap;
    [ObservableProperty] private bool _installFlatpak;
    [ObservableProperty] private bool _panelOpen;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<AppEntryViewModel> All { get; } = new();
    public ObservableCollection<AppEntryViewModel> Entries { get; } = new();
    public UiLog Log { get; } = new();

    public AppCenterViewModel(SshSession session)
    {
        _session = session;
        _apps = new AppManagerService(session);
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private void SelectFilter(string key)
    {
        FilterAll = key == "all";
        FilterApt = key == "apt";
        FilterSnap = key == "snap";
        FilterFlatpak = key == "flatpak";
        ApplyFilter();
    }

    [RelayCommand]
    private void SelectInstallSource(string key)
    {
        InstallApt = key == "apt";
        InstallSnap = key == "snap";
        InstallFlatpak = key == "flatpak";
    }

    [RelayCommand]
    private void TogglePanel() => PanelOpen = !PanelOpen;

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var apps = await _apps.ListAppsAsync();
            All.Clear();
            foreach (var a in apps) All.Add(new AppEntryViewModel(a, this));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Line("加载失败：" + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var f = (SearchText ?? "").Trim();
        bool Pass(AppEntryViewModel a)
        {
            if (FilterApt && a.Model.Source != AppSource.Apt) return false;
            if (FilterSnap && a.Model.Source != AppSource.Snap) return false;
            if (FilterFlatpak && a.Model.Source != AppSource.Flatpak) return false;
            if (f.Length == 0) return true;
            return a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                || a.Id.Contains(f, StringComparison.OrdinalIgnoreCase)
                || (a.Description?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        Entries.Clear();
        foreach (var a in All.Where(Pass)) Entries.Add(a);
    }

    // ------------------------------------------------------------------ 卸载 / 安装

    public async Task UninstallAsync(AppEntryViewModel app)
    {
        if (IsBusy) return;
        var ok = await Dialogs.ConfirmAsync(
            "卸载确认",
            $"卸载 “{app.Name}”（来源 {app.Source}）？\n部分软件包被其他包依赖时 apt 会一并移除依赖它的包。",
            danger: true, okText: "卸载");
        if (!ok) return;

        PanelOpen = true;
        IsBusy = true;
        Log.Line($">>> 卸载 {app.Id} [{app.Source}]");
        try
        {
            var r = await ExecuteAsync(ct => _apps.UninstallAsync(app.Model, l => Log.Line(l), ct));
            Log.Line(r.Success ? "✔ 卸载完成" : (r.WasCancelled ? "已取消" : $"✖ 卸载失败（退出码 {r.ExitCode}）"));
        }
        catch (Exception ex)
        {
            Log.Line("✖ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var name = InstallName.Trim();
        if (name.Length == 0 || IsBusy) return;
        var source = InstallApt ? AppSource.Apt : InstallSnap ? AppSource.Snap : AppSource.Flatpak;

        PanelOpen = true;
        IsBusy = true;
        InstallName = "";
        Log.Line($">>> 安装 {name} [{(source == AppSource.Apt ? "APT" : source.ToString())}]");
        try
        {
            var r = await ExecuteAsync(ct => _apps.InstallAsync(source, name, l => Log.Line(l), ct));
            Log.Line(r.Success ? "✔ 安装完成" : (r.WasCancelled ? "已取消" : $"✖ 安装失败（退出码 {r.ExitCode}）"));
        }
        catch (Exception ex)
        {
            Log.Line("✖ " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _ = LoadAsync();
        }
    }

    /// <summary>遇到需要 sudo 密码时弹窗补齐后重试一次。</summary>
    private async Task<CommandResult> ExecuteAsync(Func<CancellationToken, Task<CommandResult>> f)
    {
        try
        {
            return await f(CancellationToken.None);
        }
        catch (SudoPasswordRequiredException)
        {
            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要提权执行此操作，请输入其密码：",
                "", password: true);
            if (!ok || pw.Length == 0)
                throw new SshManagerException("已取消：未提供 sudo 密码。");
            _session.SudoPassword = pw;
            return await f(CancellationToken.None);
        }
    }
}
