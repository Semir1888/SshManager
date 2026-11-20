namespace SshManager.Core;

/// <summary>带用户可读信息的业务异常。</summary>
public class SshManagerException : Exception
{
    public SshManagerException(string message) : base(message) { }
}

/// <summary>需要 sudo 密码才能继续。</summary>
public sealed class SudoPasswordRequiredException : SshManagerException
{
    public SudoPasswordRequiredException(string message) : base(message) { }
}

/// <summary>当前 SSH 用户对该路径没有权限（可由调用方决定是否 sudo 降级重试）。</summary>
public sealed class PermissionDeniedException : SshManagerException
{
    public PermissionDeniedException(string message) : base(message) { }
}

/// <summary>会话已经断开。</summary>
public sealed class SessionDisconnectedException : SshManagerException
{
    public SessionDisconnectedException(string message) : base(message) { }
}
