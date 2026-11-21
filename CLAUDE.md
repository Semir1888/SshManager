# SshManager 项目说明（供 AI 助手）

> 读完本文即可开始工作。功能清单、版本更新日志见 `README.md`。

## 1. 项目是什么

- **C# / .NET 8 + Avalonia UI 11.3.23** 的 Linux 远程管理桌面工具，MVVM（CommunityToolkit.Mvvm 8.4.0），SSH 用 SSH.NET 2026.0.1。
- 服务器端**零依赖**：只需 SSH 服务；文件/命令/PTY 全部走标准 SSH 通道。
- 模块：连接管理、系统概览、文件管理（含 sudo 提权、在线编辑）、应用中心（apt/snap/flatpak）、Docker 管理（容器/镜像 + Compose 编排）、网络设置（多 IPv4/WiFi/UFW/主机名）、命令台（单次执行 + 交互终端 PTY）、传输队列。
- 界面文字、代码注释、提交说明均使用**中文**。版本号在 `src/SshManager.App/ViewModels/MainWindowViewModel.cs` 的 `Version` 常量（当前 v0.10.0）。

## 2. 构建与运行

```bash
dotnet build                     # Debug
dotnet run --project src/SshManager.App
# 发布单文件（目标机需 .NET 8）：见 README.md
```

- 目标框架 `net8.0`；当前机器 SDK 9.x 可正常构建。
- 配置目录：`%APPDATA%/SshManager/`（`profiles.json`、`ui.json`、`error.log`）。**密码永不落盘**，sudo 密码仅保存在 `SshSession.SudoPassword` 内存中。
- **构建坑**：若应用正在 VS Code 调试器（`vsdbg-ui.exe` / `.NET Host`）中运行，`bin/Debug` 的 DLL 被锁定，`dotnet build` 会失败。先停止调试；或用隔离输出验证：
  ```bash
  dotnet build src/SshManager.App/SshManager.App.csproj --no-incremental -o build-verify
  ```
  `build-verify/` 已加入 `.gitignore`。
- 不要擅自结束用户正在调试的进程；也不要主动 `git commit`（除非用户明确要求）。

## 3. 目录与关键类

```
src/SshManager.Core/                 # 无 UI 的核心库
  Models/       SshProfile, CommandResult, DirListing/FileReadResult/StatResult,
                DeleteResult/WriteResult, DockerModels, ComposeModels,
                NetworkModels, RemoteFileInfo, SystemSnapshot
  Services/     SshSession.cs   ← SSH/SFTP/命令/sudo，核心中的核心
                PtySession.cs   ← ShellStream 持久 PTY
                DockerService / ComposeService / ComposeDeployService /
                ComposeBindingStore / NetworkService（含 .Environment/.Runtime/
                .Persisted/.Netplan/.Apply 分部）/ IpValidation / NetplanConfig /
                NmcliConfig / RollbackProtocol / AppManagerService /
                SystemInfoService / ProfileStore
  Helpers/      UnixPath.cs（ShellQuote / Normalize / Parent / Combine / 权限解析）
  Exceptions.cs SshManagerException / PermissionDeniedException /
                SudoPasswordRequiredException / SessionDisconnectedException

src/SshManager.App/                  # Avalonia UI
  Program.cs    ← 全局异常兜底（AppDomain / TaskScheduler / Dispatcher），写 error.log
  App.axaml     ← 颜色、字体、全局样式、AntdSwitchTheme
  ViewModels/   MainWindowViewModel（会话生命周期/主标签/服务器列表/传输抽屉）
                FileManagerViewModel（导航/文件操作/编辑标签/sudo）
                EditorTabViewModel（单个编辑标签）
                DockerViewModel / ComposeViewModel（编排页）
                ComposeUpgradeViewModel / ComposeProjectDialogViewModel /
                ComposeBackupsViewModel
                NetworkViewModel + IpConfigPanelViewModel（多 IP/持久化/回滚）
                AppCenterViewModel
                ConsoleViewModel（单次/PTY 模式）+ TerminalViewModel（PTY 桥接）
                OverviewViewModel / ConnectDialogViewModel / ProfileItemViewModel
  Views/        MainWindow.axaml（侧栏、顶部标签、传输抽屉）
                FileManagerView.axaml（WrapPanel 操作行 + 内部编辑标签）
                EditorView.axaml（编辑器）
                ConsoleView.axaml + TerminalControl.cs（自研 xterm 渲染/输入）
                DockerView.axaml（列表 + 可调高度日志面板）
                ComposeView.axaml（顶级「编排」页：项目/服务主从 + 日志）
                ComposeUpgradeDialog / ComposeProjectDialog / ComposeBackupsDialog
                IpConfigView（多 IPv4/多 DNS/预览/待确认横幅）
                NetworkApplyDialog（接管/SSH 风险确认）
                NetworkView / AppCenterView / 各对话框
  Services/     TransferService（上传/下载队列 + sudo 降级）
                SudoRunner（网络写操作 sudo 弹窗/密码错误重试）
                Terminal/{TerminalBuffer, VtDecoder, TerminalWidth}
                UiLog（节流日志文本）/ UiSettings / Dialogs / CommandLibrary /
                Converters / FileTypeIcons
```

## 4. 必须遵守的架构约定（都是踩过的坑）

### 4.1 后台任务内不要用异常表达“预期内失败”

VS Code 调试器会在 `Task.Run` 委托内**首次抛出**异常时判定“用户代码未处理”并中断（用户看到的就是“崩溃”），即使 await 之后有 catch。因此 Core 层的预期失败（权限不足 / 路径不存在 / 命令超时 / 连接断开）必须在委托内部捕获并返回结果对象：

- `SshSession.SftpAttemptAsync<T>` / `SftpActionAttemptAsync`：SFTP 操作，返回 `SftpAttempt<T>` / `string?` 错误。
- `SshSession.RunAsync`：内部 catch `SshOperationTimeoutException`，返回退出码 -1 的 `CommandResult`。
- 结果类型：
  - `DirListing.Items/UsedSudo/Error`
  - `FileReadResult.Bytes/UsedSudo/Error`
  - `StatResult.Info/PermissionDenied/Error`
  - `RenameAsync` / `MkdirAsync` 返回 `string?`（null=成功）
  - `DeleteResult` / `WriteResult`
- `TransferService.RunTransferAsync`：上传/下载的 Task.Run 内捕获异常，回到异步方法再 rethrow。
- UI 层检查 `Error` / `PermissionDenied` 并写入 `ErrorText` 或状态栏，**不要再让异常穿透到界面**。

### 4.2 sudo 与权限降级

- `SshSession.WrapSudo(cmd)` 生成 `echo '<密码>' | sudo -S -p '' <cmd>`；无密码时抛 `SudoPasswordRequiredException`，UI 弹窗补密码后重试。
- **含 `&&` / `if` / 管道的复合命令必须用 `WrapSudoShell(cmd)`**（`sudo sh -c '<cmd>'`）；直接 `WrapSudo` 只会把第一段交给 sudo，后续段仍以普通用户执行。二进制流用 `WrapSudoRaw`（不合并 stderr）。
- 列目录：`ListDirectoryExAsync` → 先 SFTP，权限不足且 AutoSudo 开时降级 `sudo find`，返回 `UsedSudo` 徽章。
- 读文件：`ReadFileBytesExAsync` → SFTP，失败降级 `sudo cat`。
- 删除 / 写文件：返回 `DeleteStatus` / `WriteStatus`，由 UI 决定是否 `sudo rm` / 临时文件 + `sudo cp`。
- 每个连接配置有 `SshProfile.AutoSudo`（默认 true），可在连接对话框关闭。
- 连接失败/无权限的**友好提示**，不要弹未处理异常。

### 4.3 编辑器（FileManager 内部标签页）

- `EditorTabs` 的 `ItemsControl` **必须**使用 `<Panel/>` 作为 `ItemsPanel`；默认 `StackPanel` 会以无限高度测量，`EditorView/TextBox` 被撑到全文高度，内部滚动条永远不出现。
- 编辑标签“保持活动”：切换只改 `IsVisible`，不重建视图，光标/滚动不丢。
- 编辑器样式在 `App.axaml` 的 `TextBox.editor`（内部滚动条 Auto）；`EditorTextBox` 负责 Ctrl+S、Tab 缩进、行列跟踪。
- 保存时保留 BOM / 换行符 / 结尾换行，并检测外部修改；二进制、非 UTF-8、>2MB 拒绝编辑。

### 4.4 窄窗口布局

- 所有操作行/工具栏用 `WrapPanel`（如文件管理操作行、AppCenter、Docker、Network、Console），不要用不可收缩的横向 `StackPanel` 放按钮。
- 长提示文本加 `TextTrimming="CharacterEllipsis"`；路径框等可压缩控件设 `MinWidth`。
- 顶部主标签与文件管理内部标签条放在横向 `ScrollViewer` 中。
- 窗口 `MinWidth=960`；主内容区约 682px（侧栏展开）。改动布局后在 960×540 下自检。

### 4.5 PTY 终端与中文宽度

- `PtySession`：`ssh.CreateShellStream("xterm-256color", cols, rows, ...)`，`Resize` 用 `ChangeWindowSize` 通知远端 SIGWINCH。
- 数据流：后台读线程 → `Dispatcher.UIThread.Post` → `VtDecoder.Feed` → `TerminalBuffer`；**decoder/buffer 只允许在 UI 线程访问**。
- `TerminalBuffer` 是固定 Cols×Rows 的 `Cell` 网格 + 5000 行回滚 + 备选屏幕；`Cell` 有 `Ch/颜色/Flags/Continuation`。
- **中文按双宽字符处理**：`TerminalWidth.IsWide` 判断 BMP 的 CJK/假名/谚文/全角；`Put` 让宽字符占 2 格（第二格 `Continuation`）、处理行尾换行；`Backspace` 跳过续格；擦除/插入/删除/缩放后 `RepairWideCells` 清理半格；渲染时跨 2 格居中绘制并 `PushClip`，续格不重复绘制。
- 远端 vim/htop 按 wcwidth 认为中文占 2 列，本地必须一致；否则光标/CPR/重绘会错位。
- 单次执行模式没有 TTY：htop/top/vim/less 等交互命令会直接报终端错误。`ConsoleViewModel.IsInteractiveCommand` 会在单次模式提示切换到“交互终端（PTY）”；`top -b...` 批处理仍允许。
- 已知限制：非 BMP（emoji、CJK 扩展 B）和组合字符未支持；完整 Rune/字素改造属于大工程。

### 4.6 线程模型

- UI 只在 UI 线程更新；Core 的 PTY/传输回调在后台线程，必须 `Dispatcher.UIThread.Post`。
- `SshSession` 内部有 `_sshLock` / `_sftpLock`；`TransferService` 使用独立 SFTP 连接，避免大文件占用交互通道。
- `UiLog` 以 70ms 节流、上限 20 万字符；日志视图滚动用 `ScrollViewer.ScrollToEnd()`（Docker 在新日志到达时 Post 到 Background，且用户有选区时不强制滚动）。

### 4.7 连接 / 会话 / 侧栏标识

- `MainWindowViewModel` 持有 `_session` 与 `_currentProfileModel`；每次连接成功都会**重建 `Profiles`**（按 LastConnectAt 排序），`RebuildProfiles()` 负责给当前服务器标记 `ProfileItemViewModel.IsCurrent`。
- 侧栏当前服务器显示左侧绿色竖条 + “已连接”徽章；断开 / 心跳超时 / 手动断开都在 `HandleSessionLost` 清除。
- `ConnectionLost` 事件用捕获的 `session` + `ReferenceEquals(_session, session)` 守卫，避免旧会话误清新连接。
- 连接对话框打开时：密码登录聚焦密码框（`PwBox`），私钥登录聚焦私钥路径（`KeyPathBox`）。

### 4.8 Docker 页

- 根 Grid 行：`Auto(横幅), Auto(工具栏), *(列表), Auto(拖拽条), Auto(日志), Auto(提示)`。
- 列表容器 `MinHeight=80`，两个 DataGrid 显式 `Vertical/HorizontalScrollBarVisibility="Auto"`。
- 日志面板高度绑定 `DockerViewModel.LogPanelHeight`（默认 180），上方 8px 拖拽条调节；`PanelOpen=false` 时拖拽条/面板隐藏，Auto 行自动收起，列表占满。`DockerView.axaml.cs` 在尺寸变化/打开时 clamp。
- 日志是 `SelectableTextBlock`（可选中，Ctrl+A / Ctrl+C，另有“复制全部”按钮）；`LogScroller` 纵横滚动条 Auto。
- `RefreshAllAsync` 失败时设置 `State=Failed` + `FailText`，显示失败卡片与“重新检测”，不要把异常抛给后台任务。
- 「编排」是独立顶部主标签（紧邻 Docker），Docker 页保持原有容器/镜像布局，两者互不影响。

### 4.9 全局异常兜底

- `Program.cs` 注册 AppDomain / TaskScheduler / Dispatcher.UIThread 三个通道，异常写入 `%APPDATA%/SshManager/error.log`，UI 线程异常 `Handled=true` 保持进程存活。
- 但**不要依赖兜底**：预期失败必须用 4.1 的结果对象处理；兜底只用于真正的未知异常。

### 4.10 文件管理：拖拽 / 复制粘贴 / 解压 / 拖出 Windows

- 内部拖拽：`FileManagerView.axaml.cs` 用 Tunnel PointerPressed/Moved 从 DataGrid 行发起；自定义格式是 **bytes 应用格式** `sshmanager.remote-files`（JSON 载荷 `RemoteDragPayload`）。
  - 必须用 `DataFormat.CreateBytesApplicationFormat`；Windows OLE 在 drop 侧会把未知格式还原为 byte[]，string 应用格式往返后匹配不上。
  - 默认移动，Ctrl=复制；拖到文件夹行进入该文件夹，拖到空白=当前目录；`CanDrop` 阻止拖入自身/子目录。
- 剪贴板：`FileManagerViewModel` 内 `List<FileTransferItem>` + `_clipboardCut/_clipboardSudo`；Ctrl+C/X/V、右键复制/剪切/列表空白粘贴、Esc 清除；剪切项 `FileEntryViewModel.IsCut` 变淡，导航后 `ApplyClipboardMarks` 重新标记。
- 跨目录移动/复制：`MoveCopyAsync` 用 `mv --` / `cp -a --`；覆盖时先 `rm -rf --`；冲突用 `Dialogs.ChoiceAsync`（覆盖/跳过/取消）；`sourceSudo || IsSudoDir` 时走 sudo，否则权限失败询问 sudo 重试；命令超时 10 分钟；结果对象 + `ErrorText`，不向 UI 抛异常。
- Windows 拖入：外部 `DataFormats.Files` 仍走 `UploadLocalPaths(paths, targetDir)`；落点是文件夹行就上传到该文件夹，否则当前目录。
- 拖出到 Windows：`DragOutStaging` 在拖拽开始时创建本地占位文件/目录，用 `StorageProvider.TryGet*FromPathAsync` 预解析 IStorageItem；拖出窗口后后台执行 `tar ... -cf -` 流下载（`SshSession.RunToFileAsync` + `WrapSudoRaw`，绝不能带 `2>&1`），用 `System.Formats.Tar` 解包；`DataFormat.File` 用懒加载 getter，失败返回 null（Avalonia 会跳过）。staging 不依赖 UI 线程；松手过早会阻塞等待。临时目录启动时清理 1 天前的。
- 解压：`ArchiveFormats` 支持 `.zip/.tar/.tar.gz/.tgz/.tar.bz2/.tbz2/.tar.xz/.txz/.7z`；右键“解压到当前目录/同名文件夹”；zip 优先 unzip、退化 7z；复用 sudo 与 10 分钟超时；缺工具时提示安装。

### 4.11 编排（Compose）页

- 入口：顶部主标签「编排」（紧邻 Docker）。`MainWindowViewModel` 持有 `ComposeViewModel ComposeVm`，连接成功时创建，断开时 `Cleanup()` 并置空；`SelectTabInternal("compose")` 里调用 `SetActive(true)` + `ActivateAsync()`（首次探测/之后静默刷新），切到其他标签调用 `SetActive(false)` 停止自动刷新。`DockerViewModel` 不持有 Compose 实例。
- `ComposeService`：探测优先 `docker compose`，回退 `docker-compose`（v1）；项目发现 v2 用 `docker compose ls -a --format json`，v1/旧版从 `docker ps` 的 compose labels 推导；解析类命令一律 `LC_ALL=C` 且用 `WrapSudoRaw`/`RunAsync` 分离 stderr。项目命令统一带 `--project-directory <工作目录> -f <文件…> -p <项目名>`。
- `ComposeDeployService`：
  - 本地枚举在 `Task.Run` 内 catch 所有 IO/权限异常；排除规则同时用于本地与远程列表；远程比对 `find -printf '%s|%P\0'`，大小相同再 `md5sum`。
  - 备份只包含“本次会覆盖且远程已存在”的文件：`tar -czf <项目>/.sshmanager-backups/<时间戳>.tar.gz -C <项目> --no-recursion -T <清单>`；备份失败必须在修改任何文件之前中止。
  - 上传使用独立 SFTP 连接，先写目标目录临时文件 `.<rand>.sshmanager-tmp`，再 `chmod --reference` + `mv -f` 原子替换；目录不可写时走 `/tmp` + `sudo cp`。上传完成前目标文件保持不变。
  - 上传后 `config -q` 校验；Auto 模式：有 Dockerfile/build → `up -d --build`，无 build → `up -d`；有文件变化且勾选“强制重建”（默认开启）时追加 `--force-recreate`，确保挂载 jar/配置生效；无变化时只 `up -d`。
  - 回滚：校验归档必须在 `.sshmanager-backups/` 下且 `.tar.gz`，解压后 `up -d --force-recreate`；不删除“本次新增、备份中没有”的文件。
- 绑定持久化：`%APPDATA%/SshManager/compose.json`，服务器键 `用户名@主机:端口`；发现项目按 `ProjectName` 匹配绑定，只有绑定的 stale 项目也显示在列表。绑定完整时打开升级对话框会自动扫描变更。
- **日志中绝不输出 `WrapSudo*` 生成的命令**（包含密码）；只显示逻辑命令（`ComposeService.BuildUpgradeCommand` 不含 sudo 前缀）。`UiLog.Line` 还会对超时等回显的 `echo 'pw' | sudo -S -p ''` 做防御性脱敏。
- 备份目录位于项目目录 `.sshmanager-backups/`；若 Dockerfile 使用 `COPY . .`，应提醒用户将其加入 `.dockerignore`（升级计划会自动给出该警告）。
- 编排页“编辑 compose / 打开目录”通过 `MainWindowViewModel.OpenRemoteFileInFilesAsync` / `OpenRemoteDirectoryInFilesAsync` 跳转文件管理，复用其 sudo 读取与编辑器。
- 自动刷新会重建项目行 VM；`OnSelectedProjectChanged` 只在**项目名变化**时取消日志/清空服务列表，避免每 5 秒杀掉 `logs -f`。

### 4.12 网络设置（多 IPv4 / 持久化 / 120 秒回滚）

- 入口：网络页的 IP 卡片。`NetworkViewModel.Ip` 是 `IpConfigPanelViewModel`（多地址/DNS/预览/应用/回滚）；WiFi/UFW/主机名仍在 `NetworkViewModel`。`NetworkViewModel.Cleanup()` 由 `MainWindowViewModel.CleanupSession()` 调用。
- **读取**：运行时 `ip -j link/addr` + `ip -j route show default`（旧 iproute2 回退 `ip -br`）、`resolvectl dns <iface>`、`nmcli`；持久化配置优先 `netplan get`（YamlDotNet 解析）与 `nmcli -g ipv4.*`。读不到持久化配置时 UI 标注“运行态”，提供“读取持久化配置（需要 sudo）”；**绝不用运行态冒充持久化态**。
- **YAML 读取必须用 `WrapSudoRaw` + 命令内 `2>/dev/null`**（`netplan get`、`cat /etc/netplan/*.yaml`）：`WrapSudo` 会追加 `2>&1`，把 sudo/netplan 的 warning 混进 YAML，导致 `ParseEffective` 失败、预应用校验误判并回滚（已踩坑）。`NetplanConfig.SanitizeYaml` 还会忽略 `network:` 之前的噪声行作为兜底。
- **netplan 整体配置必须优先用 sudo 读取**：非 root 直读 600 权限的 `/etc/netplan/*.yaml` 时，`netplan get` 可能“退出码 0 但输出为空/不完整”，不能因为直读 exit 0 就跳过 sudo（会导致误判“没有 netplan 配置”、跳过冲突分析、应用后 DNS/地址残留）。`ReadNetplanYamlAsync` 已统一为 sudo 优先。
- **RelayCommand 方法名不要以 `Command` 结尾**：会与源生成器生成的 `XxxCommand` 属性重名，编译绑定会解析到私有方法并抛 `MethodAccessException`（`AddAddressCommand`/`AddDnsCommand` 已踩）。方法用 `AddAddress`/`AddDns`，生成的属性才是 `AddAddressCommand`/`AddDnsCommand`。
- **写入后端**：netplan 存在时优先写 `/etc/netplan/99-sshmanager-<iface>.yaml`，文件级 `renderer`（NM 管理 → NetworkManager，否则 networkd）；`netplan generate` 预校验 + `netplan get` 校验合并后的有效地址/网关/DNS。无 netplan 时用持久化 NM 连接（`/run` 下临时 profile 直接拒绝，避免重启失效）。
- **netplan 列表是跨文件拼接**：只写 99 文件清不掉旧 `addresses`/`nameservers`/默认路由。程序分析其他 netplan 文件；仅 `/etc/netplan` 下且只定义该网卡的文件允许“接管”（备份 + 移除原文件，回滚还原），复杂/只读文件拒绝。cloud-init 可能在重启时重建其文件，UI 给出警告。
- **sudo**：所有含 `;`/`&&`/管道的命令必须用 `NetworkService.RunSudoShellAsync`（`WrapSudoShell`）；`SudoRunner` 缺密码/密码错误弹窗重试；回滚脚本内绝不含密码。
- **120 秒看门狗**：状态目录 `/var/lib/sshmanager-net/`（token/current/commit/pending/backup）；脚本 `/usr/local/sbin/sshmanager-netrollback` 由程序安装，优先 `systemd-run --on-active=120s`，回退 `setsid/nohup`。应用后轮询运行时地址/网关 + `netplan get` 有效配置，成功才写 commit；SSH 断开或校验失败则回滚。新配置移除当前 SSH 地址（`$SSH_CONNECTION` 第 3 段 server_ip）时**不自动提交**，等用户重连后“保留新配置”或“立即回滚”；存在 pending 时禁止新应用。
- 校验：严格四段 IPv4、连续掩码、地址去重、网络/广播地址、网关必须落在某个地址子网内、DNS 严格校验；只支持单默认网关与 IPv4 配置，IPv6 仅展示。
- **应用日志/诊断**：`ApplyIpConfigAsync` 的 `onLog` 记录每个阶段与逻辑命令（绝不记录 `WrapSudo*` 生成的含密码命令）；失败时 IP 卡片自动展开“应用日志 / 诊断”，可复制；“诊断…”只读收集 `ip -j`、netplan 有效配置、99 文件、NM 连接、回滚状态，**只读该网卡，不 cat 全部 netplan（避免 WiFi 密码）**。状态栏错误文案包含首行详细原因，且刷新不得覆盖 `IfStatus`。
- **应用前强制刷新**：不能复用页面加载时的 `_env`/`_persisted`；每次 Apply 先 `GetEnvironmentAsync(force:true)` + `GetPersistedIpConfigAsync`，确保 netplan 文件列表与冲突分析是最新的。有效配置校验失败时，用残留 IP 对 `/etc/netplan`、`/lib/netplan`、`/usr/lib/netplan`、`/run/netplan` 做 `grep -Rl`，把来源文件写进日志/错误；简单 `/etc` 文件可通过“允许接管”备份移除，复杂文件需手动合并。
- XAML：`IpConfigView`（DataContext 为 `NetworkViewModel`，表单绑定 `Ip.*`）、`NetworkApplyDialog`（接管/SSH 风险复选框）；行模板 `IpAddressRowViewModel`/`DnsRowViewModel` 都必须写 `x:DataType`。

## 5. UI 样式系统（App.axaml）

- 主题色资源（深/浅两套）：`C_Bg / C_Side / C_Card / C_Card2 / C_Border / C_Hover / C_Press / C_Fg / C_Fg2 / C_Fg3 / C_Acc / C_AccHi / C_AccLo / C_AccFg / C_Sel / C_Danger / C_Warn / C_Info / C_TermBg`。
- 字体：`MonoFont`（终端/代码）、`UiFont`（界面，含中文字体回退）。
- 常用 class：`.primary .danger .ghost .tool .toolmini .tab .itab .card .card.sub .badge .t-title .t-h2 .t-dim .mono .slim .grip .editor`。
- `ProgressBar.slim` 必须显式 `MinWidth=0`：Fluent 主题给水平 ProgressBar 默认 `MinWidth=200`，不覆盖会在窄卡片/16px 转圈处撑破布局（Overview 曾因此紊乱）。
- 全局 `Button/ToggleButton` 内容水平/垂直居中；Fluent 主题为基底。
- `AntdSwitchTheme`：AntD 风格 44×22 开关（终端模式切换），用 `Theme="{StaticResource AntdSwitchTheme}"` 引用。
- 项目启用**编译期绑定校验**（`AvaloniaUseCompiledBindingsByDefault=true`）：XAML 必须写 `x:DataType`，绑定错误会在编译时暴露。

## 6. 验证清单（改完必做）

1. `dotnet build`：0 警告 / 0 错误（调试器占用 bin 时用 `-o build-verify`）。
2. 启动冒烟：`src/SshManager.App/bin/Debug/net8.0/SshManager.exe` 能正常打开主窗口，`%APPDATA%/SshManager/error.log` 无新增异常。
3. 涉及连接后界面的改动（文件/Docker/终端/网络等）需在真实服务器上验证；常见场景：
   - 文件管理：窄窗口按钮换行、无权限目录（AutoSudo 开/关）、编辑长文件滚动条、保存；内部拖拽移动/复制、Ctrl+C/X/V、冲突覆盖/跳过、解压；从资源管理器拖入到指定文件夹；从远程列表拖出到资源管理器。
   - Docker：缩小窗口列表可滚动；打开/拖动/关闭日志面板；日志 Ctrl+A/C。
   - 编排：顶部标签紧邻 Docker、切换后状态保留；项目发现/手动添加；服务表；启停/重启/拉取/构建/重建/down；项目与服务 logs -f（停止）；窄窗口按钮换行。
   - 网络（必须在真实服务器验证）：多 IPv4/多 DNS/单网关；DHCP↔静态；网关不在子网/重复/网络地址/DNS 非法被阻止；netplan 多文件冲突接管；非 root sudo；修改当前 SSH IP 断线后 120 秒自动回滚；保留旧 IP 新增新 IP 成功提交；重连后待确认横幅；重启后地址仍生效；纯 NM 与无 netplan 环境。
   - 一键升级（必须在真实服务器验证）：无变化/新增/修改/仅远程；大小相同但内容变化的配置（MD5）；Dockerfile 自动 `--build`；无 Dockerfile 时强制重建；备份生成与保留份数；`config -q` 失败中止；上传中断不损坏目标；失败回滚；根目录/无权限目录 sudo 补密；路径含空格/中文/单引号、多 `-f`、`.env`。
   - 终端：Switch 切换、PTY 中 `echo 中文`、`ls` 中文名、`vim`/`top`、缩放；单次模式交互命令提示。
   - 侧栏：连接/断开/重连后的“已连接”标识。
4. 不要提交 `bin/obj/publish/build-verify`；新文件放在项目根目录（此仓库根目录，不是子模块目录）。

## 7. 已知限制 / 可继续改进

- PTY 终端仅支持 BMP 双宽字符；emoji、非 BMP、组合字符、终端内选择复制尚未实现。
- AppCenter 的日志面板仍是固定 `Height=200` + 普通 `TextBlock`，可复用 Docker 的 `SelectableTextBlock` + 可调高度方案。
- Docker 容器表固定列宽合计较大，窄窗口依赖 DataGrid 横向滚动。
- 传输抽屉打开且窗口最小时，Docker 列表与日志无法同时舒适显示（可拖动/关闭日志面板）。
- `Deploy*` 相关文件仍存在但已不在导航中（历史遗留）。
- 文件拖出到 Windows 依赖服务器 `tar`；大文件/大目录在下载完成前松手会短暂阻塞 UI；Windows 不允许的文件名可能拖出失败；解压的 rar 未支持。
- 编排页首版依赖服务器 GNU `find -printf` / `tar` / `md5sum`；BusyBox 环境可能不支持，后续可加 SFTP 枚举回退。上传脚本默认 0644（可执行位需在升级命令中自行 `chmod +x`）。Compose v1 的部分版本可能缺少 `ls`/JSON 输出，已用容器 labels 回退但仍需真实服务器验证。
- 网络设置只配置 IPv4（IPv6 仅展示）、单个默认网关；不支持 DNS 搜索域/MTU/metric/静态路由。netplan 冲突接管仅支持 `/etc/netplan` 下只定义该网卡的简单文件，复杂/只读文件会拒绝；cloud-init 可能在重启时重建其文件。NetworkManager 临时 profile（`/run`）会拒绝应用。120 秒看门狗依赖 `systemd-run` 或 `setsid/nohup`，服务器重启会丢失看门狗（配置已落盘）。
- 没有自动化测试项目；终端宽度相关的纯逻辑可复制 `TerminalBuffer.cs`/`TerminalWidth.cs` 到临时控制台工程做单元测试；`ArchiveFormats` 也可单独做纯逻辑测试。

## 8. 工作方式（用户偏好）

- 先读 `README.md` + 本文，再检索相关代码；不要凭记忆猜 API。
- 非小事先说明**根因和方案**并等待确认（用户经常要求“先不要编码”），确认后再改。
- 改动保持最小、复用现有模式与样式；UI 文案和注释用中文。
- 改完必须构建 + 自测，并明确告诉用户哪些需要在真实服务器上验证。
- 不主动提交 git；不动用户正在调试的进程/文件锁。
