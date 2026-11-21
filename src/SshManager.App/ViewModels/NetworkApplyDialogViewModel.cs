using CommunityToolkit.Mvvm.ComponentModel;
using SshManager.Core.Models;

namespace SshManager.App.ViewModels;

/// <summary>网络配置应用确认框：后端/目标、预览、警告、接管与 SSH 风险确认。</summary>
public partial class NetworkApplyDialogViewModel : ViewModelBase
{
    public NetworkApplyDialogViewModel(IpApplyPlan plan)
    {
        Plan = plan;
    }

    public IpApplyPlan Plan { get; }
    public IReadOnlyList<string> Warnings => Plan.Warnings;
    public IReadOnlyList<string> Errors => Plan.Errors;
    public bool HasErrors => Plan.Errors.Count > 0;
    public bool HasWarnings => Plan.Warnings.Count > 0;
    public bool NeedTakeover => Plan.NeedTakeover;
    public bool HasSshRisk => Plan.SshIpRemoved || Plan.SshIpRemovedUnknown;

    public string BackendText => Plan.Backend switch
    {
        "netplan" => "netplan（写入 /etc/netplan 持久化文件）",
        "nm" => "NetworkManager（nmcli 连接配置）",
        _ => "未知后端",
    };

    public string TargetText => Plan.Target;
    public string RendererText => Plan.Renderer.Length > 0 ? "renderer: " + Plan.Renderer : "";
    public string PreviewText => Plan.Preview;
    public string ErrorText => string.Join("\n", Plan.Errors);
    public string SshRiskText => Plan.SshIpRemoved
        ? $"新配置不包含当前 SSH 连接使用的地址 {Plan.SshServerIp}。应用后会话可能断开；若断开或未确认，120 秒后自动恢复旧配置。"
        : "无法确定当前 SSH 连接使用的服务器地址；请谨慎操作，120 秒内未确认将自动回滚。";

    [ObservableProperty] private bool _allowTakeover;
    [ObservableProperty] private bool _acceptRisk;

    public bool CanApply => !HasErrors
                            && (!NeedTakeover || AllowTakeover)
                            && (!HasSshRisk || AcceptRisk);

    partial void OnAllowTakeoverChanged(bool value) => OnPropertyChanged(nameof(CanApply));
    partial void OnAcceptRiskChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    public bool TryConfirm() => CanApply;
}
