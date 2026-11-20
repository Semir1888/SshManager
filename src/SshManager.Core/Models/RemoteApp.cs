namespace SshManager.Core.Models;

public enum AppSource
{
    Apt,
    Snap,
    Flatpak,
}

/// <summary>服务器上已安装的一个应用/包。</summary>
public sealed class RemoteApp
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public AppSource Source { get; init; }
    public string Description { get; init; } = "";

    /// <summary>附加信息：snap 的 rev、flatpak 的 origin 等。</summary>
    public string Extra { get; init; } = "";

    public string SourceLabel => Source switch
    {
        AppSource.Apt => "APT",
        AppSource.Snap => "Snap",
        AppSource.Flatpak => "Flatpak",
        _ => Source.ToString(),
    };
}
