namespace SshManager.App.Services;

/// <summary>命令库条目：Name=中文含义，Command=命令，Category=分类；Danger=危险操作；NeedEdit=含占位符需先编辑。</summary>
public sealed record QuickCommand(string Name, string Command, string Category, bool Danger = false, bool NeedEdit = false)
{
    public override string ToString() => Name;
}

/// <summary>常用命令库（命令台快捷面板与命令库弹窗共用）。</summary>
public static class CommandLibrary
{
    public static readonly string[] Categories =
    {
        "系统信息", "磁盘与文件", "进程与内存", "网络", "服务管理",
        "软件包", "Docker", "日志", "用户与权限", "定时任务", "压缩与备份", "硬件信息",
    };

    /// <summary>顶部平铺的快捷命令（只读安全命令，点按即执行）。</summary>
    public static readonly IReadOnlyList<QuickCommand> Chips = new QuickCommand[]
    {
        new QuickCommand("磁盘占用", "df -h", "磁盘与文件"),
        new QuickCommand("内存", "free -h", "进程与内存"),
        new QuickCommand("进程 Top", "top -bn1 | head -n 15", "进程与内存"),
        new QuickCommand("内存 Top", "ps aux --sort=-%mem | head -n 16", "进程与内存"),
        new QuickCommand("端口监听", "ss -tulpn", "网络"),
        new QuickCommand("失败服务", "systemctl --failed", "服务管理"),
        new QuickCommand("最近日志", "journalctl -n 50 --no-pager", "日志"),
        new QuickCommand("网卡地址", "ip -br a", "网络"),
        new QuickCommand("Docker 容器", "docker ps", "Docker"),
    };

    public static readonly IReadOnlyList<QuickCommand> All = new QuickCommand[]
    {
        // ---------------------------------------------------------------- 系统信息
        new QuickCommand("系统发行版", "cat /etc/os-release", "系统信息"),
        new QuickCommand("内核与架构", "uname -a", "系统信息"),
        new QuickCommand("主机名与系统信息", "hostnamectl", "系统信息"),
        new QuickCommand("运行时长与平均负载", "uptime", "系统信息"),
        new QuickCommand("当前登录的会话", "who", "系统信息"),
        new QuickCommand("系统当前时间", "date", "系统信息"),
        new QuickCommand("时间与时区同步状态", "timedatectl", "系统信息"),
        new QuickCommand("最近开关机记录", "last reboot -n 10", "系统信息"),

        // ---------------------------------------------------------------- 磁盘与文件
        new QuickCommand("磁盘空间占用（人类可读）", "df -h", "磁盘与文件"),
        new QuickCommand("磁盘分区与挂载点", "lsblk -f", "磁盘与文件"),
        new QuickCommand("当前目录总大小", "du -sh .", "磁盘与文件"),
        new QuickCommand("当前目录各项大小排序", "du -sh * 2>/dev/null | sort -rh | head -n 20", "磁盘与文件"),
        new QuickCommand("全盘大于 100MB 的文件 Top20", "sudo find / -xdev -type f -size +100M -exec du -h {} + 2>/dev/null | sort -rh | head -n 20", "磁盘与文件"),
        new QuickCommand("统计当前目录文件总数", "find . -type f | wc -l", "磁盘与文件"),
        new QuickCommand("按名称全盘查找文件", "find / -name \"nginx.conf\" 2>/dev/null", "磁盘与文件", NeedEdit: true),
        new QuickCommand("查看文件末尾 50 行", "tail -n 50 /var/log/syslog", "磁盘与文件", NeedEdit: true),
        new QuickCommand("实时滚动查看日志（点停止结束）", "tail -f /var/log/syslog", "磁盘与文件", NeedEdit: true),
        new QuickCommand("查看文件头 50 行", "head -n 50 /etc/hosts", "磁盘与文件", NeedEdit: true),
        new QuickCommand("当前目录详情（含隐藏）", "ls -lah", "磁盘与文件"),
        new QuickCommand("查看文件类型", "file /etc/passwd", "磁盘与文件", NeedEdit: true),

        // ---------------------------------------------------------------- 进程与内存
        new QuickCommand("进程快照 Top12", "top -bn1 | head -n 12", "进程与内存"),
        new QuickCommand("内存占用 Top15", "ps aux --sort=-%mem | head -n 16", "进程与内存"),
        new QuickCommand("CPU 占用 Top15", "ps aux --sort=-%cpu | head -n 16", "进程与内存"),
        new QuickCommand("内存使用情况", "free -h", "进程与内存"),
        new QuickCommand("按名称查找进程", "ps aux | grep -i nginx | grep -v grep", "进程与内存", NeedEdit: true),
        new QuickCommand("强制结束进程（填 PID）", "sudo kill -9 1234", "进程与内存", Danger: true, NeedEdit: true),
        new QuickCommand("查看僵尸进程", "ps aux | awk '$8 ~ /Z/ {print $2, $11}'", "进程与内存"),
        new QuickCommand("系统进程树", "ps -ejH | head -n 40", "进程与内存"),

        // ---------------------------------------------------------------- 网络
        new QuickCommand("网卡与 IP 地址", "ip -br a", "网络"),
        new QuickCommand("路由表与默认网关", "ip r", "网络"),
        new QuickCommand("端口监听情况", "ss -tulpn", "网络"),
        new QuickCommand("连接统计摘要", "ss -s", "网络"),
        new QuickCommand("测试外网连通", "ping -c 4 223.5.5.5", "网络"),
        new QuickCommand("测试端口连通性", "timeout 3 bash -c '</dev/tcp/127.0.0.1/80' && echo 端口开放 || echo 端口不通", "网络", NeedEdit: true),
        new QuickCommand("域名解析测试", "getent hosts www.baidu.com", "网络", NeedEdit: true),
        new QuickCommand("追踪路由路径", "tracepath -n 223.5.5.5", "网络"),
        new QuickCommand("防火墙（ufw）状态", "sudo ufw status verbose", "网络"),
        new QuickCommand("iptables 规则", "sudo iptables -L -n --line-numbers | head -n 40", "网络"),
        new QuickCommand("DNS 配置", "cat /etc/resolv.conf", "网络"),

        // ---------------------------------------------------------------- 服务管理
        new QuickCommand("已失败的服务", "systemctl --failed", "服务管理"),
        new QuickCommand("正在运行的服务", "systemctl list-units --type=service --state=running --no-pager | head -n 30", "服务管理"),
        new QuickCommand("全部服务状态", "systemctl list-units --type=service --no-pager | head -n 40", "服务管理"),
        new QuickCommand("查看某服务状态", "systemctl status nginx --no-pager", "服务管理", NeedEdit: true),
        new QuickCommand("重启某服务", "sudo systemctl restart nginx", "服务管理", Danger: true, NeedEdit: true),
        new QuickCommand("开机自启服务列表", "systemctl list-unit-files --type=service --state=enabled | head -n 30", "服务管理"),
        new QuickCommand("重载服务配置", "sudo systemctl daemon-reload", "服务管理", Danger: true),
        new QuickCommand("设置开机自启并启动", "sudo systemctl enable --now nginx", "服务管理", Danger: true, NeedEdit: true),

        // ---------------------------------------------------------------- 软件包
        new QuickCommand("已安装包总数", "dpkg -l | grep -c '^ii'", "软件包"),
        new QuickCommand("最近安装/升级的包", "grep -E \" install | upgrade \" /var/log/dpkg.log 2>/dev/null | tail -n 20", "软件包"),
        new QuickCommand("查找已安装的包", "dpkg -l | grep -i python", "软件包", NeedEdit: true),
        new QuickCommand("更新软件源", "sudo apt update", "软件包"),
        new QuickCommand("升级全部软件", "sudo apt upgrade -y", "软件包", Danger: true),
        new QuickCommand("清理无用依赖", "sudo apt autoremove -y", "软件包", Danger: true),
        new QuickCommand("查看包安装了哪些文件", "dpkg -L nginx", "软件包", NeedEdit: true),
        new QuickCommand("apt 下载缓存占用", "du -sh /var/cache/apt/archives", "软件包"),

        // ---------------------------------------------------------------- Docker
        new QuickCommand("运行中的容器", "docker ps", "Docker"),
        new QuickCommand("全部容器（含已停止）", "docker ps -a", "Docker"),
        new QuickCommand("容器资源占用", "docker stats --no-stream", "Docker"),
        new QuickCommand("镜像列表", "docker images", "Docker"),
        new QuickCommand("查看容器日志（末尾 100 行）", "docker logs --tail 100 容器名或ID", "Docker", NeedEdit: true),
        new QuickCommand("重启容器", "docker restart 容器名或ID", "Docker", Danger: true, NeedEdit: true),
        new QuickCommand("清理已停止的容器", "docker container prune -f", "Docker", Danger: true),
        new QuickCommand("清理悬空镜像", "docker image prune -f", "Docker", Danger: true),
        new QuickCommand("Docker 磁盘占用", "docker system df", "Docker"),

        // ---------------------------------------------------------------- 日志
        new QuickCommand("系统最近 50 条日志", "journalctl -n 50 --no-pager", "日志"),
        new QuickCommand("本次开机的错误日志", "journalctl -p err -b --no-pager | tail -n 40", "日志"),
        new QuickCommand("某服务的日志（末尾 100 行）", "journalctl -u nginx -n 100 --no-pager", "日志", NeedEdit: true),
        new QuickCommand("日志磁盘占用", "journalctl --disk-usage", "日志"),
        new QuickCommand("清理旧日志（保留 2 天）", "sudo journalctl --vacuum-time=2d", "日志", Danger: true),

        // ---------------------------------------------------------------- 用户与权限
        new QuickCommand("当前用户身份", "id", "用户与权限"),
        new QuickCommand("系统可登录用户", "grep -v nologin /etc/passwd | cut -d: -f1,3", "用户与权限"),
        new QuickCommand("最近登录记录", "last -n 15", "用户与权限"),
        new QuickCommand("sudo 组成员", "getent group sudo", "用户与权限"),
        new QuickCommand("修改文件权限", "sudo chmod 755 /path/to/file", "用户与权限", Danger: true, NeedEdit: true),
        new QuickCommand("修改文件归属", "sudo chown root:root /path/to/file", "用户与权限", Danger: true, NeedEdit: true),

        // ---------------------------------------------------------------- 定时任务
        new QuickCommand("当前用户的定时任务", "crontab -l", "定时任务"),
        new QuickCommand("系统定时器列表", "systemctl list-timers --all --no-pager | head -n 20", "定时任务"),

        // ---------------------------------------------------------------- 压缩与备份
        new QuickCommand("打包目录为 tar.gz", "tar -czf backup.tar.gz 目录名", "压缩与备份", NeedEdit: true),
        new QuickCommand("解压 tar.gz", "tar -xzf backup.tar.gz", "压缩与备份", NeedEdit: true),
        new QuickCommand("查看压缩包内容", "tar -tzf backup.tar.gz | head -n 30", "压缩与备份", NeedEdit: true),
        new QuickCommand("目录同步备份", "rsync -av --progress /src/ /dst/", "压缩与备份", NeedEdit: true),
        new QuickCommand("校验文件 MD5", "md5sum 文件名", "压缩与备份", NeedEdit: true),

        // ---------------------------------------------------------------- 硬件信息
        new QuickCommand("CPU 型号与核心数", "lscpu | grep -E \"Model name|^CPU\\(s\\)\"", "硬件信息"),
        new QuickCommand("内存信息", "cat /proc/meminfo | head -n 5", "硬件信息"),
        new QuickCommand("磁盘健康状态", "sudo smartctl -H /dev/sda", "硬件信息", NeedEdit: true),
        new QuickCommand("温度传感器", "sensors 2>/dev/null || echo 未安装 lm-sensors（可 apt install lm-sensors）", "硬件信息"),
        new QuickCommand("USB 设备列表", "lsusb", "硬件信息"),
        new QuickCommand("PCI 设备列表", "lspci | head -n 30", "硬件信息"),
    };
}
