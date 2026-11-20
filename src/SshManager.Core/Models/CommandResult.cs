namespace SshManager.Core.Models;

/// <summary>一次远程命令的执行结果。</summary>
public sealed record CommandResult(int ExitCode, string Output, string Error, bool WasCancelled = false)
{
    public bool Success => !WasCancelled && ExitCode == 0;

    public string Combined
    {
        get
        {
            var o = Output ?? "";
            var e = Error ?? "";
            if (o.Length == 0) return e;
            if (e.Length == 0) return o;
            return o + "\n" + e;
        }
    }
}
