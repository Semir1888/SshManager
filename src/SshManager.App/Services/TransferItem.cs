using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.Core.Helpers;

namespace SshManager.App.Services;

public enum TransferDirection { Upload, Download }

public enum TransferStatus { Queued, Running, Done, Failed, Cancelled }

public partial class TransferItem : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public TransferDirection Direction { get; }
    public string FileName { get; }
    public string LocalPath { get; }
    public string RemotePath { get; }
    public long TotalBytes { get; internal set; }
    public bool IsUpload => Direction == TransferDirection.Upload;
    public System.Threading.CancellationTokenSource Cts { get; } = new();

    /// <summary>来自 sudo 提权目录的下载项：SFTP 读取大概率无权限，直接走 sudo cat。</summary>
    internal bool FromSudoDir { get; set; }

    /// <summary>路径细节（上传：本地 → 远程；下载反向）。</summary>
    public string Detail => IsUpload
        ? $"{LocalPath}  →  {RemotePath}"
        : $"{RemotePath}  →  {LocalPath}";

    internal long ProgressBytes;
    internal long LastSampleBytes;
    internal DateTime LastSampleAt = DateTime.UtcNow;
    internal double SpeedBps;

    private readonly Action<TransferItem> _onCancel;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _statusText = "排队中";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _canCancel = true;
    [ObservableProperty] private IBrush _statusBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#8b93a5"));

    internal TransferItem(TransferDirection dir, string localPath, string remotePath, long total, Action<TransferItem> onCancel)
    {
        Direction = dir;
        FileName = dir == TransferDirection.Upload
            ? System.IO.Path.GetFileName(localPath)
            : UnixPath.FileName(remotePath);
        LocalPath = localPath;
        RemotePath = remotePath;
        TotalBytes = total;
        _onCancel = onCancel;
    }

    internal void SetStatus(TransferStatus s)
    {
        StatusText = s switch
        {
            TransferStatus.Queued => "排队中",
            TransferStatus.Running => "传输中",
            TransferStatus.Done => "已完成",
            TransferStatus.Failed => "失败",
            TransferStatus.Cancelled => "已取消",
            _ => s.ToString(),
        };
        CanCancel = s is TransferStatus.Queued or TransferStatus.Running;
        StatusBrush = s switch
        {
            TransferStatus.Done => new SolidColorBrush(Avalonia.Media.Color.Parse("#10b981")),
            TransferStatus.Failed => new SolidColorBrush(Avalonia.Media.Color.Parse("#f43f5e")),
            TransferStatus.Running => new SolidColorBrush(Avalonia.Media.Color.Parse("#2dd4bf")),
            _ => new SolidColorBrush(Avalonia.Media.Color.Parse("#8b93a5")),
        };
    }

    [RelayCommand]
    private void CancelIt() => _onCancel(this);
}
