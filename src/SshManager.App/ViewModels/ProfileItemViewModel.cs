using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.Core.Models;

namespace SshManager.App.ViewModels;

public partial class ProfileItemViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    public SshProfile Model { get; }

    public ProfileItemViewModel(MainWindowViewModel owner, SshProfile model)
    {
        _owner = owner;
        Model = model;
    }

    public string Name => Model.DisplayName;
    public string Host => Model.HostSummary;
    public string LastText => Model.LastConnectAt?.ToString("MM-dd HH:mm") ?? "未连接过";

    /// <summary>是否为当前已连接的服务器（侧栏显示绿色标识）。</summary>
    [ObservableProperty] private bool _isCurrent;

    [RelayCommand]
    private Task Connect() => _owner.ConnectProfileAsync(this);

    [RelayCommand]
    private Task Edit() => _owner.EditProfileAsync(this);

    [RelayCommand]
    private Task Remove() => _owner.RemoveProfileAsync(this);
}
