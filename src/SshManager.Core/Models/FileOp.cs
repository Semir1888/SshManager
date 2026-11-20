namespace SshManager.Core.Models;

/// <summary>文件删除结果（权限不足等情况不抛异常，返回状态由调用方决定后续动作）。</summary>
public enum DeleteStatus
{
    Ok,
    PermissionDenied,
    NotFound,
    Failed,
}

public sealed record DeleteResult(DeleteStatus Status, string? Error = null)
{
    public static readonly DeleteResult Success = new(DeleteStatus.Ok);

    /// <summary>供 UI 展示的简短原因（不含内部细节）。</summary>
    public string Describe() => Status switch
    {
        DeleteStatus.Ok => "已删除",
        DeleteStatus.PermissionDenied => "权限不足",
        DeleteStatus.NotFound => "远程路径不存在（可能已被删除）",
        _ => Error is { Length: > 0 } ? Error : "删除失败",
    };
}

/// <summary>文件写入结果（编辑器保存；权限不足等情况不抛异常，由调用方决定是否 sudo 兜底）。</summary>
public enum WriteStatus
{
    Ok,
    PermissionDenied,
    NotFound,
    Failed,
}

public sealed record WriteResult(WriteStatus Status, string? Error = null)
{
    public static readonly WriteResult Success = new(WriteStatus.Ok);

    public string Describe() => Status switch
    {
        WriteStatus.Ok => "已保存",
        WriteStatus.PermissionDenied => "权限不足",
        WriteStatus.NotFound => "远程文件不存在（可能已被删除或移动）",
        _ => Error is { Length: > 0 } ? Error : "保存失败",
    };
}
