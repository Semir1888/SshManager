namespace SshManager.Core.Models;

/// <summary>一次目录列举结果（含是否经 sudo 提权读取的标记；Error 非空表示预期内失败，如权限不足）。</summary>
public sealed record DirListing(IReadOnlyList<RemoteFileInfo> Items, bool UsedSudo, string? Error = null);

/// <summary>一次远程文件读取结果（含是否经 sudo 提权读取的标记；Error 非空表示预期内失败）。</summary>
public sealed record FileReadResult(byte[] Bytes, bool UsedSudo, string? Error = null);

/// <summary>
/// 一次路径探测结果：<see cref="Info"/> 为空且 <see cref="Error"/> 为空表示路径不存在；
/// <see cref="PermissionDenied"/> 表示当前用户无权访问（调用方可据此走 sudo 降级）。
/// </summary>
public sealed record StatResult(RemoteFileInfo? Info, bool PermissionDenied, string? Error = null);
