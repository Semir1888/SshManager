using SshManager.Core;
using SshManager.Core.Services;

namespace SshManager.App.Services;

/// <summary>
/// 网络写操作的 sudo 执行器：缺密码/密码错误时弹窗重试（最多 3 次）。
/// 复合命令统一走 WrapSudoShell，避免只提权第一段。
/// </summary>
public sealed class SudoRunner
{
    private readonly SshSession _session;
    private readonly NetworkService _net;

    public SudoRunner(SshSession session, NetworkService net)
    {
        _session = session;
        _net = net;
    }

    public async Task<string?> RunAsync(string command, int timeoutSeconds = 180)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var r = await _net.RunSudoShellAsync(command, TimeSpan.FromSeconds(timeoutSeconds));
                if (r.ExitCode == 0) return null;
                var err = (r.Output + " " + r.Error).Trim();
                if (!IsPasswordError(err)) return err.Length > 0 ? err : $"命令失败（退出码 {r.ExitCode}）。";
                _session.SudoPassword = null;
            }
            catch (SudoPasswordRequiredException)
            {
                // 继续弹窗
            }
            catch (SessionDisconnectedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要 sudo 权限修改网络配置，请输入密码（仅保存在本次会话内存）：",
                "", password: true);
            if (!ok || pw.Length == 0)
                return "已取消：未提供 sudo 密码。";
            _session.SudoPassword = pw;
        }
        return "sudo 密码错误或权限不足。";
    }

    private static bool IsPasswordError(string text)
        => text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase)
           || text.Contains("密码不正确", StringComparison.OrdinalIgnoreCase)
           || text.Contains("抱歉，请重试", StringComparison.OrdinalIgnoreCase);
}
