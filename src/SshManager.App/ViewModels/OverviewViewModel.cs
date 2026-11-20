using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.Core.Helpers;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

public partial class OverviewViewModel : ViewModelBase
{
    private readonly SshSession _session;
    private readonly SystemInfoService _info;
    private readonly MainWindowViewModel _owner;
    private DispatcherTimer? _timer;

    [ObservableProperty] private string _os = "—";
    [ObservableProperty] private string _kernel = "—";
    [ObservableProperty] private string _arch = "—";
    [ObservableProperty] private string _hostname = "—";
    [ObservableProperty] private string _lanIp = "—";
    [ObservableProperty] private string _uptime = "—";
    [ObservableProperty] private string _bootTime = "—";
    [ObservableProperty] private string _cpuModel = "—";

    [ObservableProperty] private double _cpuPercent = -1;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double _memPercent = -1;
    [ObservableProperty] private string _memPercentText = "—";
    [ObservableProperty] private string _memText = "—";
    [ObservableProperty] private double _diskPercent = -1;
    [ObservableProperty] private string _diskPercentText = "—";
    [ObservableProperty] private string _diskText = "—";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private string _updatedText = "";

    public OverviewViewModel(SshSession session, MainWindowViewModel owner)
    {
        _session = session;
        _owner = owner;
        _info = new SystemInfoService(session);
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _timer.Tick += async (_, _) =>
                {
                    if (!IsLoading) await LoadAsync();
                };
            }
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var s = await _info.GetSnapshotAsync();
            Os = Dash(s.Os);
            Kernel = Dash(s.Kernel);
            Arch = Dash(s.Arch);
            Hostname = Dash(s.Hostname);
            LanIp = Dash(s.LanIp);
            Uptime = Dash(s.Uptime);
            BootTime = string.IsNullOrWhiteSpace(s.BootTime) ? "—" : $"开机于 {s.BootTime}";
            CpuModel = Dash(s.CpuModel);

            CpuPercent = s.CpuUsagePercent ?? -1;
            CpuText = s.CpuUsagePercent is > 0 ? s.CpuUsagePercent.Value.ToString("0.0") + " %" : "—";

            var memUsed = s.MemTotalBytes - s.MemAvailableBytes;
            MemPercent = s.MemUsedPercent ?? -1;
            MemPercentText = s.MemUsedPercent is >= 0 ? s.MemUsedPercent.Value.ToString("0.0") + " %" : "—";
            MemText = s.MemTotalBytes > 0
                ? $"{UnixPath.HumanSize(memUsed)} / {UnixPath.HumanSize(s.MemTotalBytes)}"
                : "—";

            DiskPercent = s.DiskUsedPercent ?? -1;
            DiskPercentText = s.DiskUsedPercent is >= 0 ? s.DiskUsedPercent.Value.ToString("0.0") + " %" : "—";
            DiskText = s.DiskTotalBytes > 0
                ? $"{UnixPath.HumanSize(s.DiskUsedBytes)} / {UnixPath.HumanSize(s.DiskTotalBytes)}"
                : "—";

            UpdatedText = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            UpdatedText = "刷新失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    public void Cleanup() => _timer?.Stop();

    /// <summary>快捷命令按钮：跳转到命令台执行。</summary>
    [RelayCommand]
    private void QuickRun(string? command)
    {
        if (!string.IsNullOrWhiteSpace(command))
            _owner.RunInConsole(command);
    }
}
