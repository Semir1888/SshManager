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

    /// <summary>是否被剪切（列表项变淡，粘贴成功后清除）。</summary>
    [ObservableProperty] private bool _isCut;

    /// <summary>拖拽悬停时高亮（文件夹行）。</summary>
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>是否为支持右键解压的压缩包文件。</summary>
    public bool CanExtract => !IsDirectory && ArchiveFormats.IsSupported(Name);

    internal FileEntryViewModel(RemoteFileInfo model, FileManagerViewModel parent)
    {
        Model = model;
        _parent = parent;
        (Icon, IconBrush) = FileTypeIcons.Resolve(model);
    }

    [RelayCommand]
    private Task Open() => _parent.OpenEntryAsync(this);

    [RelayCommand]
    private void Copy() => _parent.CopyEntries(_parent.SelectionOr(this));

    [RelayCommand]
    private void Cut() => _parent.CutEntries(_parent.SelectionOr(this));

    [RelayCommand]
    private Task ExtractHere() => _parent.ExtractAsync(this, toOwnFolder: false);

    [RelayCommand]
    private Task ExtractToFolder() => _parent.ExtractAsync(this, toOwnFolder: true);

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
