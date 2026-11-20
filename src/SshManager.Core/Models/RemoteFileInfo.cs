namespace SshManager.Core.Models;

/// <summary>SFTP 目录列表中的一项。</summary>
public sealed class RemoteFileInfo
{
    public required string Name { get; init; }
    public required string FullName { get; init; }

    /// <summary>目录（符号链接不算目录，避免误删目标内容）。</summary>
    public bool IsDirectory { get; init; }

    public bool IsSymbolicLink { get; init; }
    public long Length { get; init; }
    public DateTime ModifiedUtc { get; init; }

    /// <summary>形如 -rw-r--r-- 的权限字符串。</summary>
    public string Permissions { get; init; } = "";
}
