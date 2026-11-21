using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SshManager.App.ViewModels;

/// <summary>一行 IPv4 地址（IP + 掩码/前缀）。</summary>
public partial class IpAddressRowViewModel : ViewModelBase
{
    private readonly IpConfigPanelViewModel _parent;

    internal IpAddressRowViewModel(IpConfigPanelViewModel parent, string address = "", string mask = "24")
    {
        _parent = parent;
        _address = address;
        _mask = mask;
    }

    [ObservableProperty] private string _address;
    [ObservableProperty] private string _mask;
    [ObservableProperty] private string _errorText = "";

    partial void OnAddressChanged(string value) => _parent.OnFormChanged();
    partial void OnMaskChanged(string value) => _parent.OnFormChanged();

    [RelayCommand]
    private void Remove() => _parent.RemoveAddress(this);
}

/// <summary>一行 DNS 服务器。</summary>
public partial class DnsRowViewModel : ViewModelBase
{
    private readonly IpConfigPanelViewModel _parent;

    internal DnsRowViewModel(IpConfigPanelViewModel parent, string server = "")
    {
        _parent = parent;
        _server = server;
    }

    [ObservableProperty] private string _server;
    [ObservableProperty] private string _errorText = "";

    partial void OnServerChanged(string value) => _parent.OnFormChanged();

    [RelayCommand]
    private void Remove() => _parent.RemoveDns(this);
}
