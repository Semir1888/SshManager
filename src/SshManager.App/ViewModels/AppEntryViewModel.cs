using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.Core.Models;

namespace SshManager.App.ViewModels;

public partial class AppEntryViewModel : ViewModelBase
{
    private readonly AppCenterViewModel _parent;

    public RemoteApp Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Version => string.IsNullOrWhiteSpace(Model.Version) ? "—" : Model.Version;
    public string Source => Model.SourceLabel;
    public string Description => Model.Description;
    public string Extra => Model.Extra;

    public IBrush SourceBrush { get; }

    public AppEntryViewModel(RemoteApp model, AppCenterViewModel parent)
    {
        Model = model;
        _parent = parent;
        SourceBrush = model.Source switch
        {
            AppSource.Apt => new SolidColorBrush(Avalonia.Media.Color.Parse("#f59e0b")),
            AppSource.Snap => new SolidColorBrush(Avalonia.Media.Color.Parse("#34d399")),
            AppSource.Flatpak => new SolidColorBrush(Avalonia.Media.Color.Parse("#f472b6")),
            _ => new SolidColorBrush(Avalonia.Media.Color.Parse("#8b93a5")),
        };
    }

    [RelayCommand]
    private Task Uninstall() => _parent.UninstallAsync(this);
}
