using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>120 秒自动回滚协议：token、状态文件、看门狗脚本、pending 元数据。</summary>
public static class RollbackProtocol
{
    public const int RollbackSeconds = 120;
    public const string StateDir = "/var/lib/sshmanager-net";
    public const string ScriptPath = "/usr/local/sbin/sshmanager-netrollback";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string NewToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsValidToken(string? token)
        => !string.IsNullOrEmpty(token)
           && token.Length == 32
           && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string PendingEnvPath(string token) => $"{StateDir}/pending-{token}.env";
    public static string PendingJsonPath(string token) => $"{StateDir}/pending-{token}.json";
    public static string CommitPath(string token) => $"{StateDir}/commit-{token}";
    public static string ResultPath(string token) => $"{StateDir}/result-{token}";
    public static string BackupDir(string token) => $"{StateDir}/backup-{token}";
    public static string LogPath(string token) => $"{StateDir}/rollback-{token}.log";
    public static string CurrentPath => $"{StateDir}/current";

    public sealed record PendingMeta(
        string Token,
        string Iface,
        string Backend,
        string Target,
        long DeadlineEpoch,
        string Summary);

    public static string BuildPendingJson(PendingMeta meta)
        => JsonSerializer.Serialize(meta, JsonOpts);

    public static string BuildPendingEnv(IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        foreach (var pair in vars)
            sb.Append(pair.Key).Append('=').Append(UnixPath.ShellQuote(pair.Value)).Append('\n');
        return sb.ToString();
    }

    public static PendingIpChange? ParsePending(string? json, long serverNowEpoch)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<PendingMeta>(json, JsonOpts);
            if (meta == null || !IsValidToken(meta.Token)) return null;
            var remaining = (int)Math.Clamp(meta.DeadlineEpoch - serverNowEpoch, 0, RollbackSeconds);
            return new PendingIpChange
            {
                Token = meta.Token,
                Iface = meta.Iface,
                Backend = meta.Backend,
                Target = meta.Target,
                DeadlineEpoch = meta.DeadlineEpoch,
                ServerNowEpoch = serverNowEpoch,
                RemainingSeconds = remaining,
                Summary = meta.Summary,
            };
        }
        catch
        {
            return null;
        }
    }

    public static long NowEpoch(DateTime utcNow) => new DateTimeOffset(utcNow).ToUnixTimeSeconds();

    /// <summary>看门狗脚本（root 执行，不含任何密码）。</summary>
    public static string BuildWatchdogScript() => """
#!/bin/sh
set -u
STATE=/var/lib/sshmanager-net
token="${1:-}"
mode="${2:-}"
printf '%s' "$token" | grep -Eq '^[0-9a-f]{32}$' || exit 64

[ "$mode" = "--now" ] || [ "$mode" = "--timer" ] || sleep 120

if command -v flock >/dev/null 2>&1; then
  exec 9>"$STATE/lock" 2>/dev/null || exit 75
  flock -w 10 9 || exit 75
fi

[ "$(cat "$STATE/current" 2>/dev/null || true)" = "$token" ] || exit 0

if [ -f "$STATE/commit-$token" ]; then
  echo "committed $(date +%s)" > "$STATE/result-$token"
  rm -f "$STATE/current" "$STATE/pending-$token.env" "$STATE/pending-$token.json"
  exit 0
fi

if [ -f "$STATE/pending-$token.env" ]; then
  . "$STATE/pending-$token.env"
  if [ "${BACKEND:-}" = "netplan" ]; then
    if [ -f "$BACKUP_DIR/target.yaml" ]; then
      cp -a "$BACKUP_DIR/target.yaml" "$TARGET_FILE"
    else
      rm -f "$TARGET_FILE"
    fi
    if [ -f "$BACKUP_DIR/disabled.map" ]; then
      while IFS='	' read -r orig saved; do
        [ -n "${orig:-}" ] || continue
        [ -f "$BACKUP_DIR/disabled/$saved" ] && cp -a "$BACKUP_DIR/disabled/$saved" "$orig"
      done < "$BACKUP_DIR/disabled.map"
    fi
    netplan generate >/dev/null 2>&1 || true
    netplan apply >/dev/null 2>&1 || true
  elif [ "${BACKEND:-}" = "nm" ]; then
    nmcli connection modify "$NM_ID" ipv4.method "$OLD_METHOD" ipv4.addresses "$OLD_ADDRS" \
      ipv4.gateway "$OLD_GW" ipv4.dns "$OLD_DNS" ipv4.ignore-auto-dns "$OLD_IGNORE_DNS" \
      ipv4.routes "$OLD_ROUTES" >/dev/null 2>&1 || true
    nmcli connection up "$NM_ID" >/dev/null 2>&1 || true
  fi
fi

echo "rolledback $(date +%s)" > "$STATE/result-$token"
rm -f "$STATE/current" "$STATE/pending-$token.env" "$STATE/pending-$token.json"
""";

    /// <summary>把已上传到 /tmp 的脚本安装到 sbin，并准备状态目录。</summary>
    public static string BuildInstallScriptCommand(string uploadedScriptPath)
        => $"mkdir -p {UnixPath.ShellQuote(StateDir)} && chmod 755 {UnixPath.ShellQuote(StateDir)} && " +
           $"install -m 755 -- {UnixPath.ShellQuote(uploadedScriptPath)} {UnixPath.ShellQuote(ScriptPath)}";

    /// <summary>启动 120 秒看门狗；优先 systemd timer，回退 setsid+nohup。</summary>
    public static string BuildStartCommand(string token)
    {
        if (!IsValidToken(token)) throw new ArgumentException("非法 token", nameof(token));
        var log = LogPath(token);
        return $"if command -v systemd-run >/dev/null 2>&1 && " +
               $"systemd-run --collect --unit=sshmanager-netrollback-{token} " +
               $"--on-active={RollbackSeconds}s --timer-property=AccuracySec=1s " +
               $"{UnixPath.ShellQuote(ScriptPath)} {token} --timer; then :; " +
               $"elif command -v setsid >/dev/null 2>&1; then " +
               $"setsid nohup {UnixPath.ShellQuote(ScriptPath)} {token} " +
               $">{UnixPath.ShellQuote(log)} 2>&1 </dev/null & " +
               $"else nohup {UnixPath.ShellQuote(ScriptPath)} {token} " +
               $">{UnixPath.ShellQuote(log)} 2>&1 </dev/null & fi";
    }

    public static string BuildCommitCommand(string token)
    {
        if (!IsValidToken(token)) throw new ArgumentException("非法 token", nameof(token));
        var commit = CommitPath(token);
        return $"umask 077; printf '%s\\n' {UnixPath.ShellQuote(token)} > {UnixPath.ShellQuote(commit)}";
    }

    public static string BuildRollbackNowCommand(string token)
    {
        if (!IsValidToken(token)) throw new ArgumentException("非法 token", nameof(token));
        return $"{UnixPath.ShellQuote(ScriptPath)} {token} --now";
    }

    public static string FormatDeadline(long serverNowEpoch)
        => (serverNowEpoch + RollbackSeconds).ToString(CultureInfo.InvariantCulture);
}
