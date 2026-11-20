using System.Text;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>
/// 一键部署（离线部署包）支持：
/// 1) 扫描服务器上已上传的部署包目录（remote/ offline/ update/ 镜像包等关键文件）；
/// 2) 服务器只读预检（Docker / 已有 SSTS / 端口 80 / snapd / 磁盘），并给出模式推荐；
/// 3) 按模式生成默认执行命令（用户可编辑）。
/// 实际部署由部署包内的 remote/*.sh 脚本完成，本服务只负责定位与驱动。
/// </summary>
public class DeployService(SshSession session)
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(60);

    // ------------------------------------------------------------------ 部署包扫描

    // 模板中 __DIR__ 会被替换为 shell 转义后的部署包路径
    private const string ScanScriptTemplate = """
D=__DIR__
if [ ! -d "$D" ]; then echo 'ERR|NOTDIR'; exit 0; fi
S=$(timeout 25 du -sh "$D" 2>/dev/null | awk '{print $1}')
echo "SIZE|${S:-未知}"
for m in remote offline update kiosk db ssts images.tar.gz images-mediamtx.tar.gz static.tar.gz SHA256SUMS deploy.sh; do
  if [ -e "$D/$m" ]; then echo "ITEM|$m|1"; else echo "ITEM|$m|0"; fi
done
for s in remote/deploy-remote.sh remote/deploy.sh remote/install.sh remote/*.sh scripts/*.sh *.sh; do
  for f in "$D"/$s; do
    if [ -f "$f" ]; then r=${f#"$D"/}; case " $SEEN " in *" $r "*) ;; *) echo "SCRIPT|$r"; SEEN="$SEEN $r ";; esac; fi
  done
done
if [ -d "$D/update" ]; then
  for f in "$D"/update/*; do
    [ -e "$f" ] && echo "UPD|$(basename "$f")"
  done
fi
""";

    public async Task<BundleScanResult> ScanBundleAsync(string dir, CancellationToken ct = default)
    {
        var d = UnixPath.Normalize(dir);
        if (d == "/")
            throw new SshManagerException("请填写部署包所在的目录，不能是根目录。");

        var script = ScanScriptTemplate.Replace("__DIR__", UnixPath.ShellQuote(d));
        var res = await session.RunAsync(script, ScanTimeout, ct).ConfigureAwait(false);
        return ParseScan(d, res.Combined);
    }

    private static BundleScanResult ParseScan(string dir, string output)
    {
        var r = new BundleScanResult { Dir = dir };
        var known = new HashSet<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var sep = line.IndexOf('|');
            if (sep <= 0) continue;
            var key = line[..sep];
            var val = line[(sep + 1)..];
            switch (key)
            {
                case "ERR" when val == "NOTDIR":
                    return new BundleScanResult { Dir = dir, Error = "目录不存在（检查路径与拼写）" };
                case "SIZE":
                    r.SizeText = val;
                    break;
                case "ITEM":
                {
                    var seg = val.Split('|');
                    if (seg.Length == 2 && known.Add(seg[0]))
                        r.Items.Add(new BundleItem(seg[0], seg[1] == "1" ? "已就绪" : "缺失", seg[1] == "1"));
                    break;
                }
                case "SCRIPT":
                    if (val.Length > 0 && known.Add("script:" + val))
                        r.CandidateScripts.Add(val);
                    break;
                case "UPD":
                    if (val.Length > 0 && known.Add("upd:" + val))
                        r.Items.Add(new BundleItem("update/" + val, "待更新内容", true));
                    break;
            }
        }
        if (r.Items.Count == 0)
            r.Error = "目录存在但未发现任何已知部署包文件（请确认这是离线部署包根目录）";
        return r;
    }

    // ------------------------------------------------------------------ 服务器预检

    // 模板中 __DIR__ 会被替换为 shell 转义后的部署包路径（磁盘检查用）
    private const string PreflightScriptTemplate = """
echo "OS|$(. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME")"
echo "KERNEL|$(uname -sr) ($(uname -m))"
AV=$(df -Bg --output=avail __DIR__ 2>/dev/null | tail -1 | tr -dc '0-9')
echo "DISK|${AV:-?}G"
if command -v docker >/dev/null 2>&1; then
  echo "DOCKER|$(docker --version 2>/dev/null | cut -d, -f1)"
  echo "DOCKACT|$(systemctl is-active docker 2>/dev/null || echo unknown)"
  if docker ps >/dev/null 2>&1; then
    if docker container inspect ssts >/dev/null 2>&1; then
      echo "SSTS|exists"
      W=$(docker container inspect ssts --format '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' 2>/dev/null)
      echo "COMPOSEDIR|$W"
      echo "RUNNING|$(docker ps --filter name=ssts --format '{{.Names}}' | paste -sd, -)"
    else
      echo "SSTS|none"
    fi
  else
    echo "DOCKERACCESS|no"
  fi
else
  echo "DOCKER|none"
fi
if ss -tln 2>/dev/null | grep -q ':80 '; then echo "PORT80|busy"; else echo "PORT80|free"; fi
if command -v snap >/dev/null 2>&1; then
  SV=$(snap version 2>/dev/null | awk 'NR==2{print $2}')
  echo "SNAPD|${SV:-已安装}"
  if snap list 2>/dev/null | grep -q '^chromium'; then echo "CHROMIUM|installed"; else echo "CHROMIUM|none"; fi
  if snap list 2>/dev/null | grep -q '^ubuntu-frame'; then echo "FRAME|installed"; else echo "FRAME|none"; fi
else
  echo "SNAPD|none"
fi
""";

    /// <summary>只读预检。非 root 且已具备 sudo 密码时自动以 sudo 读取 docker/snap 信息。</summary>
    public async Task<PreflightResult> PreflightAsync(string bundleDir, CancellationToken ct = default)
    {
        var d = string.IsNullOrWhiteSpace(bundleDir) ? "/" : UnixPath.Normalize(bundleDir);
        var script = PreflightScriptTemplate.Replace("__DIR__", UnixPath.ShellQuote(d));

        // 尽量以 sudo 执行（只读命令），无密码时降级为普通用户执行
        string command;
        if (session.IsRoot)
            command = $"bash -c {UnixPath.ShellQuote(script)}";
        else if (!string.IsNullOrEmpty(session.SudoPassword))
            command = session.WrapSudo($"bash -c {UnixPath.ShellQuote(script)}");
        else
            command = $"bash -c {UnixPath.ShellQuote(script)} 2>&1";

        var res = await session.RunAsync(command, PreflightTimeout, ct).ConfigureAwait(false);
        return ParsePreflight(res.Combined);
    }

    private static PreflightResult ParsePreflight(string output)
    {
        var map = new Dictionary<string, string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sep = line.IndexOf('|');
            if (sep <= 0) continue;
            var k = line[..sep];
            if (!map.ContainsKey(k))
                map[k] = line[(sep + 1)..];
        }

        string Get(string k) => map.TryGetValue(k, out var v) ? v : "";

        bool dockerInstalled = Get("DOCKER") is { Length: > 0 } dv && dv != "none";
        bool noDockerAccess = Get("DOCKERACCESS") == "no";
        bool existing = Get("SSTS") == "exists";
        var composeDir = Get("COMPOSEDIR").Trim();
        var running = Get("RUNNING").Trim();
        var diskGb = Get("DISK").TrimEnd('G', '?');
        _ = int.TryParse(diskGb, out var disk);
        var port80 = Get("PORT80");
        var chromium = Get("CHROMIUM");
        var frame = Get("FRAME");
        var snapd = Get("SNAPD");

        var items = new List<PreflightItem>
        {
            new("操作系统", Get("OS") is { Length: > 0 } os ? os : "未知", PreflightState.Info),
            new("内核", Get("KERNEL"), PreflightState.Info),
            dockerInstalled
                ? new PreflightItem("Docker", Get("DOCKER") + " · 服务 " + Get("DOCKACT"), PreflightState.Ok)
                : new PreflightItem("Docker", "未安装（全新部署会自动安装离线版）", PreflightState.Warn),
        };

        if (dockerInstalled && noDockerAccess)
            items.Add(new PreflightItem("容器读取", "当前用户无 docker 权限（提供 sudo 密码后可完整预检）", PreflightState.Warn));
        else if (dockerInstalled && !existing)
            items.Add(new PreflightItem("SSTS 容器", "未发现（可全新部署）", PreflightState.Ok));
        else if (existing)
        {
            items.Add(new PreflightItem("SSTS 容器", "已存在：" + (running.Length > 0 ? running : "未运行"),
                running.Length > 0 ? PreflightState.Warn : PreflightState.Bad));
            if (composeDir.Length > 0)
                items.Add(new PreflightItem("部署目录", composeDir, PreflightState.Info));
        }

        items.Add(port80 switch
        {
            "busy" => new PreflightItem("端口 80", "已被占用" + (existing ? "（SSTS 正常现象）" : "（注意排查）"),
                existing ? PreflightState.Info : PreflightState.Warn),
            "free" => new PreflightItem("端口 80", "空闲", PreflightState.Ok),
            _ => new PreflightItem("端口 80", "无法检测（ss 命令不可用）", PreflightState.Warn),
        });

        items.Add(snapd is { Length: > 0 } sv && sv != "none"
            ? new PreflightItem("snapd", "v" + sv, PreflightState.Ok)
            : new PreflightItem("snapd", "未安装（离线包可自动安装）", PreflightState.Warn));
        if (chromium.Length > 0)
            items.Add(new PreflightItem("Chromium kiosk", chromium == "installed" ? "已安装" : "未安装", chromium == "installed" ? PreflightState.Ok : PreflightState.Warn));
        if (frame.Length > 0)
            items.Add(new PreflightItem("ubuntu-frame", frame == "installed" ? "已安装" : "未安装", frame == "installed" ? PreflightState.Ok : PreflightState.Warn));

        if (disk > 0)
            items.Add(new PreflightItem("可用磁盘", $"{disk} GB" + (disk < 15 ? "（偏小：全新部署建议 ≥ 60GB）" : ""),
                disk < 40 ? PreflightState.Warn : PreflightState.Ok));

        DeployMode recommended;
        string reason;
        if (existing)
        {
            recommended = DeployMode.Update;
            reason = "检测到已有 SSTS 部署：请使用「更新应用/Web/配置」或「部署/更新 kiosk」，全新部署会创建第二套系统，请勿使用。";
        }
        else if (dockerInstalled)
        {
            recommended = DeployMode.Full;
            reason = "已安装 Docker 但未发现 SSTS：可执行全新部署（注意端口 80 与磁盘占用）。";
        }
        else
        {
            recommended = DeployMode.Full;
            reason = "服务器尚未安装 Docker：属于干净机器，建议执行全新部署。";
        }

        return new PreflightResult
        {
            Items = items,
            DockerInstalled = dockerInstalled,
            ExistingSsts = existing,
            ComposeDir = composeDir,
            RecommendedMode = recommended,
            Recommendation = reason,
        };
    }

    // ------------------------------------------------------------------ 命令构建

    /// <summary>按模式生成默认执行命令（部署包内的 remote 脚本入口）。</summary>
    public static string DefaultCommand(DeployMode mode, string bundleDir, string? entryScript)
    {
        var d = UnixPath.Normalize(bundleDir);
        var script = string.IsNullOrWhiteSpace(entryScript) ? "remote/deploy-remote.sh" : entryScript.Trim().TrimStart('/');
        var abs = script.StartsWith("/") ? script : UnixPath.Combine(d, script);
        var arg = mode switch
        {
            DeployMode.Check => "--mode=check",
            DeployMode.Full => "--mode=full",
            DeployMode.Update => "--mode=update",
            DeployMode.Kiosk => "--mode=kiosk",
            _ => "",
        };
        var sb = new StringBuilder("bash ").Append(UnixPath.ShellQuote(abs));
        if (arg.Length > 0) sb.Append(' ').Append(arg);
        return sb.ToString();
    }
}
