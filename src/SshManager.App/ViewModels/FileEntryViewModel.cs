using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.Core.Models;
using SshManager.Core.Helpers;
using SshManager.App.Services;

namespace SshManager.App.ViewModels;

public partial class FileEntryViewModel : ViewModelBase
{
    private readonly FileManagerViewModel _parent;

    public RemoteFileInfo Model { get; }

    public string Name => Model.Name;
    public string FullName => Model.FullName;
    public bool IsDirectory => Model.IsDirectory;
    public bool IsSymlink => Model.IsSymbolicLink;
    public string SizeText => Model.IsDirectory ? "—" : UnixPath.HumanSize(Model.Length);
    public long SizeBytes => Model.Length;
    public string PermsText => Model.Permissions;
    public string DateText => Model.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public Geometry Icon { get; }
    public IBrush IconBrush { get; }

    internal FileEntryViewModel(RemoteFileInfo model, FileManagerViewModel parent)
    {
        Model = model;
        _parent = parent;
        (Icon, IconBrush) = FileTypeIcons.Resolve(model);
    }

    [RelayCommand]
    private Task Open() => _parent.OpenEntryAsync(this);

    /// <summary>右键“编辑”：目录则进入，文件则打开编辑器（与双击一致）。</summary>
    [RelayCommand]
    private Task Edit() => _parent.OpenInEditorAsync(this);

    [RelayCommand]
    private Task Download() => _parent.RequestEntryDownloadAsync(this);

    [RelayCommand]
    private Task Delete() => _parent.DeleteEntriesAsync(new[] { this });

    [RelayCommand]
    private Task Rename() => _parent.RenameEntryAsync(this);

    [RelayCommand]
    private Task Properties() => _parent.ShowPropertiesAsync(this);
}
