using CommunityToolkit.Mvvm.ComponentModel;
using SshManager.App.Services;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

public partial class ConnectDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "22";
    [ObservableProperty] private string _username = "root";

    /// <summary>登录密码（由窗口代码后置从 PasswordBox 同步）。</summary>
    public string? Password { get; set; }

    [ObservableProperty] private bool _authPassword = true;
    [ObservableProperty] private bool _authKey;
    [ObservableProperty] private string _keyPath = "";

    /// <summary>私钥口令（同样由代码后置同步）。</summary>
    public string? KeyPassphrase { get; set; }

    [ObservableProperty] private bool _saveToProfiles = true;

    /// <summary>文件管理无权限时自动 sudo 提权访问（保存到 Profile）。</summary>
    [ObservableProperty] private bool _autoSudo = true;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectText = "连 接";

    public SshSession? Session { get; private set; }

    /// <summary>连接成功后的最终配置（含已确认指纹）。</summary>
    public SshProfile Profile { get; private set; } = new();

    partial void OnAuthPasswordChanged(bool value)
    {
        if (value && AuthKey) AuthKey = false;
    }

    partial void OnAuthKeyChanged(bool value)
    {
        if (value && AuthPassword) AuthPassword = false;
    }

    partial void OnIsConnectingChanged(bool value) => ConnectText = value ? "连接中…" : "连 接";

    /// <summary>从已保存的配置预填（密码不回填，指纹保留避免重复询问）。</summary>
    public void Prefill(SshProfile p)
    {
        Name = p.Name;
        Host = p.Host;
        Port = p.Port.ToString();
        Username = p.Username;
        AuthPassword = p.AuthMode == AuthMode.Password;
        AuthKey = !AuthPassword;
        KeyPath = p.KeyPath ?? "";
        AutoSudo = p.AutoSudo;
        Profile = p;
    }

    public async Task<bool> ConnectAsync()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Host)) { Error = "请填写主机地址。"; return false; }
        if (string.IsNullOrWhiteSpace(Username)) { Error = "请填写用户名。"; return false; }
        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535) { Error = "端口无效（1-65535）。"; return false; }
        if (AuthKey)
        {
            if (string.IsNullOrWhiteSpace(KeyPath)) { Error = "请选择私钥文件。"; return false; }
            if (!File.Exists(KeyPath.Trim())) { Error = "私钥文件不存在。"; return false; }
        }

        var profile = new SshProfile
        {
            Name = Name.Trim(),
            Host = Host.Trim(),
            Port = port,
            Username = Username.Trim(),
            AuthMode = AuthKey ? AuthMode.PrivateKey : AuthMode.Password,
            Password = AuthPassword ? Password : null,
            KeyPath = AuthKey ? KeyPath.Trim() : null,
            KeyPassphrase = AuthKey ? KeyPassphrase : null,
            AcceptedFingerprint = Profile.AcceptedFingerprint,
            AutoSudo = AutoSudo,
        };

        IsConnecting = true;
        try
        {
            Session = await SshSession.ConnectAsync(profile, fp => Dialogs.ConfirmFingerprintAsync(fp));
            Profile = profile;
            return true;
        }
        catch (SshManagerException ex)
        {
            Error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Error = "连接失败：" + ex.Message;
            return false;
        }
        finally
        {
            IsConnecting = false;
        }
    }
}
