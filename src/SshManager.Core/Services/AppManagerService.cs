using System.Text.RegularExpressions;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>已安装应用管理：apt/dpkg + snap + flatpak 的查看、卸载、安装。</summary>
public partial class AppManagerService(SshSession session)
{
    /// <summary>合并三个来源的应用列表。</summary>
    public async Task<List<RemoteApp>> ListAppsAsync(CancellationToken ct = default)
    {
        var list = new List<RemoteApp>();
        await TryAddAptAsync(list, ct).ConfigureAwait(false);
        await TryAddSnapAsync(list, ct).ConfigureAwait(false);
        await TryAddFlatpakAsync(list, ct).ConfigureAwait(false);
        return list;
    }

    // ------------------------------------------------------------------ 列表

    private async Task TryAddAptAsync(List<RemoteApp> list, CancellationToken ct)
    {
        try
        {
            var res = await session.RunAsync(
                "LC_ALL=C dpkg-query -W -f='${binary:Package}\\t${Version}\\t${db:Status-Abbrev}\\t${Description}\\n' 2>/dev/null",
                TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            foreach (var raw in res.Output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                var seg = line.Split('\t');
                if (seg.Length < 3) continue;
                if (!seg[2].TrimStart().StartsWith("ii")) continue; // 只保留已安装
                var id = seg[0].Trim();
                list.Add(new RemoteApp
                {
                    Id = id,
                    Name = id.Split(':')[0],
                    Version = seg[1].Trim(),
                    Source = AppSource.Apt,
                    Description = seg.Length > 3 ? seg[3].Trim() : "",
                });
            }
        }
        catch { /* dpkg 不可用时忽略该来源 */ }
    }

    private async Task TryAddSnapAsync(List<RemoteApp> list, CancellationToken ct)
    {
        try
        {
            var res = await session.RunAsync("LC_ALL=C snap list 2>/dev/null",
                TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            foreach (var raw in res.Output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("Name ")) continue;
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 2) continue;
                // 列：Name Version Rev Tracking Publisher Notes(可空)
                var notes = "";
                if (t.Length >= 6 && (t[^1].StartsWith("classic") || t[^1].StartsWith("disabled")))
                    notes = t[^1];
                list.Add(new RemoteApp
                {
                    Id = t[0],
                    Name = t[0],
                    Version = t[1],
                    Source = AppSource.Snap,
                    Description = $"snap · rev {t[2]}",
                    Extra = notes,
                });
            }
        }
        catch { /* 未安装 snap 时忽略 */ }
    }

    private async Task TryAddFlatpakAsync(List<RemoteApp> list, CancellationToken ct)
    {
        try
        {
            var res = await session.RunAsync(
                "LC_ALL=C flatpak list --app --columns=application,version,origin --csv 2>/dev/null",
                TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            foreach (var raw in res.Output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("Application,")) continue;
                var seg = line.Split(',');
                if (seg.Length < 1 || seg[0].Length == 0) continue;
                list.Add(new RemoteApp
                {
                    Id = seg[0],
                    Name = seg[0].Split('.')[^1],
                    Version = seg.Length > 1 ? seg[1] : "",
                    Source = AppSource.Flatpak,
                    Description = seg.Length > 2 ? $"flatpak · {seg[2]}" : "flatpak",
                });
            }
        }
        catch { /* 未安装 flatpak 时忽略 */ }
    }

    // ------------------------------------------------------------------ 卸载 / 安装

    public Task<CommandResult> UninstallAsync(RemoteApp app, Action<string> onLine, CancellationToken ct = default) => app.Source switch
    {
        AppSource.Apt => RunSudoStreaming(
            $"DEBIAN_FRONTEND=noninteractive apt-get remove -y --purge {Quote(app.Id)}", onLine, ct),
        AppSource.Snap => RunSudoStreaming($"snap remove {Quote(app.Id)}", onLine, ct),
        AppSource.Flatpak => RunFlatpakUninstallAsync(app.Id, onLine, ct),
        _ => throw new InvalidOperationException(),
    };

    private async Task<CommandResult> RunFlatpakUninstallAsync(string id, Action<string> onLine, CancellationToken ct)
    {
        var systemRes = await RunSudoStreaming($"flatpak uninstall -y --system {Quote(id)}", onLine, ct).ConfigureAwait(false);
        if (systemRes.WasCancelled || systemRes.Success) return systemRes;
        onLine("-- 系统级卸载失败，尝试用户级（--user）--");
        return await session.RunStreamingAsync($"flatpak uninstall -y --user {Quote(id)}", onLine, onLine, ct).ConfigureAwait(false);
    }

    public Task<CommandResult> InstallAsync(AppSource source, string packageId, Action<string> onLine, CancellationToken ct = default)
    {
        ValidatePackageName(packageId);
        return source switch
        {
            AppSource.Apt => RunSudoStreaming(
                $"DEBIAN_FRONTEND=noninteractive apt-get install -y {Quote(packageId)}", onLine, ct),
            AppSource.Snap => RunSudoStreaming($"snap install {Quote(packageId)}", onLine, ct),
            AppSource.Flatpak => RunFlatpakInstallAsync(packageId, onLine, ct),
            _ => throw new InvalidOperationException(),
        };
    }

    private async Task<CommandResult> RunFlatpakInstallAsync(string id, Action<string> onLine, CancellationToken ct)
    {
        var systemRes = await RunSudoStreaming($"flatpak install -y --system {Quote(id)}", onLine, ct).ConfigureAwait(false);
        if (systemRes.WasCancelled || systemRes.Success) return systemRes;
        onLine("-- 系统级安装失败，尝试用户级（--user）--");
        return await session.RunStreamingAsync($"flatpak install -y --user {Quote(id)}", onLine, onLine, ct).ConfigureAwait(false);
    }

    private Task<CommandResult> RunSudoStreaming(string command, Action<string> onLine, CancellationToken ct)
    {
        // WrapSudo 已附加 2>&1，stderr 会合并在 stdout 中输出
        return session.RunStreamingAsync(session.WrapSudo(command), onLine, null, ct);
    }

    /// <summary>包名只允许安全字符，防止命令注入。</summary>
    public static void ValidatePackageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !PackageNameRegex().IsMatch(name))
            throw new SshManagerException("包名不合法：只允许字母、数字与 . + - _ 字符。");
    }

    private static string Quote(string s) => UnixPath.ShellQuote(s);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.+_-]*$")]
    private static partial Regex PackageNameRegex();
}
