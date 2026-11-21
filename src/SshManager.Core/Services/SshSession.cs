using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SshManager.Core.Helpers;
using SshManager.Core.Models;

namespace SshManager.Core.Services;

/// <summary>
/// 一条已建立的 SSH/SFTP 会话。
/// 交互操作（列目录/删除/重命名等）复用内部 SftpClient；大文件传输请调用
/// <see cref="CreateSftpClient"/> 单独建立连接，避免长时间占用交互通道。
/// </summary>
public sealed class SshSession : IDisposable
{
    private readonly SshClient _ssh;
    private readonly SftpClient _sftp;
    private readonly object _sftpLock = new();
    private readonly object _sshLock = new();
    private bool _disposed;
    private int _lostReported;

    public SshProfile Profile { get; }

    /// <summary>远程用户是否为 root（root 时无需 sudo 包装）。</summary>
    public bool IsRoot { get; private set; }

    public TimeSpan? Latency { get; private set; }

    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>sudo 密码。密码登录时默认等于登录密码；私钥登录时按需弹窗设置。</summary>
    public string? SudoPassword { get; set; }

    /// <summary>连接意外断开时触发（参数为原因），回调在后台线程。</summary>
    public event Action<string>? ConnectionLost;

    public bool IsConnected
    {
        get
        {
            try { return _ssh.IsConnected && _sftp.IsConnected; }
            catch { return false; }
        }
    }

    private SshSession(SshProfile profile, SshClient ssh, SftpClient sftp)
    {
        Profile = profile;
        _ssh = ssh;
        _sftp = sftp;
        if (profile.AuthMode == AuthMode.Password)
            SudoPassword = profile.Password;
    }

    // ---------------------------------------------------------------- 连接

    public static async Task<SshSession> ConnectAsync(
        SshProfile profile,
        Func<string, Task<bool>> confirmHostKeyAsync,
        CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => ConnectInternal(profile, confirmHostKeyAsync), ct).ConfigureAwait(false);
        }
        catch (SshManagerException) { throw; }
        catch (SshAuthenticationException)
        {
            throw new SshManagerException("认证失败：请检查用户名、密码或私钥文件。");
        }
        catch (SshConnectionException ex)
        {
            throw new SshManagerException($"SSH 连接失败：{ex.Message}");
        }
        catch (SocketException ex)
        {
            throw new SshManagerException($"无法连接到 {profile.Host}:{profile.Port}（{ex.Message}）");
        }
        catch (Exception ex)
        {
            throw new SshManagerException($"连接失败：{ex.Message}");
        }
    }

    private static SshSession ConnectInternal(SshProfile profile, Func<string, Task<bool>> confirmHostKeyAsync)
    {
        SshClient? ssh = null;
        SftpClient? sftp = null;
        var declined = false;
        try
        {
            ssh = new SshClient(CreateConnectionInfo(profile));
            ssh.KeepAliveInterval = TimeSpan.FromSeconds(25);
            ssh.HostKeyReceived += (_, e) =>
            {
                var fp = FormatFingerprint(e.FingerPrint);
                if (!string.IsNullOrEmpty(profile.AcceptedFingerprint) &&
                    profile.AcceptedFingerprint == fp)
                {
                    e.CanTrust = true;
                    return;
                }
                bool ok;
                try { ok = confirmHostKeyAsync(fp).GetAwaiter().GetResult(); }
                catch { ok = false; }
                if (ok) { profile.AcceptedFingerprint = fp; e.CanTrust = true; }
                else { declined = true; e.CanTrust = false; }
            };

            try { ssh.Connect(); }
            catch when (declined) { throw new SshManagerException("已取消连接：未信任该服务器的主机密钥。"); }

            sftp = new SftpClient(CreateConnectionInfo(profile));
            sftp.KeepAliveInterval = TimeSpan.FromSeconds(25);
            sftp.BufferSize = 32768;
            sftp.Connect();

            var session = new SshSession(profile, ssh, sftp);

            // 判断是否 root + 测量往返延迟
            session.IsRoot = session.Run("id -u", TimeSpan.FromSeconds(10)).Output.Trim() == "0";
            var sw = Stopwatch.StartNew();
            long best = long.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                sw.Restart();
                session.Run("true", TimeSpan.FromSeconds(10));
                sw.Stop();
                best = Math.Min(best, sw.ElapsedMilliseconds);
            }
            session.Latency = TimeSpan.FromMilliseconds(Math.Max(0, best));
            return session;
        }
        catch
        {
            try { ssh?.Dispose(); } catch { }
            try { sftp?.Dispose(); } catch { }
            throw;
        }
    }

    private static ConnectionInfo CreateConnectionInfo(SshProfile p)
    {
        AuthenticationMethod auth;
        if (p.AuthMode == AuthMode.PrivateKey)
        {
            var key = string.IsNullOrEmpty(p.KeyPassphrase)
                ? new PrivateKeyFile(p.KeyPath ?? "")
                : new PrivateKeyFile(p.KeyPath ?? "", p.KeyPassphrase!);
            auth = new PrivateKeyAuthenticationMethod(p.Username, key);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(p.Username, p.Password ?? "");
        }
        var ci = new ConnectionInfo(p.Host, p.Port, p.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        return ci;
    }

    public static string FormatFingerprint(byte[]? fp) =>
        fp == null ? "" : string.Join(":", fp.Select(b => b.ToString("x2")));

    /// <summary>为传输等长任务单独建立一条 SFTP 连接（调用方负责 Dispose）。</summary>
    public SftpClient CreateSftpClient()
    {
        var c = new SftpClient(CreateConnectionInfo(Profile))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(25),
            BufferSize = 32768,
        };
        c.Connect();
        return c;
    }

    // ---------------------------------------------------------------- 命令

    public CommandResult Run(string command, TimeSpan? timeout = null)
    {
        if (!IsConnected) throw new SessionDisconnectedException("连接已断开，请重新连接。");
        lock (_sshLock)
        {
            using var cmd = _ssh.CreateCommand(command);
            if (timeout != null) cmd.CommandTimeout = timeout.Value;
            try
            {
                cmd.Execute();
                return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result ?? "", cmd.Error ?? "");
            }
            catch (Exception ex) when (IsConnectionDead(ex))
            {
                OnConnectionLost(ex.Message);
                throw new SessionDisconnectedException("连接已断开：" + ex.Message);
            }
        }
    }

    public Task<CommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                return Run(command, timeout);
            }
            catch (SshOperationTimeoutException ex)
            {
                // 命令超时属于预期内失败：在 Task.Run 委托内部捕获，
                // 避免调试器在“首次抛出”处中断（例如未安装 Docker 的服务器上 sudo 探测超时）
                var secs = (timeout ?? TimeSpan.FromMinutes(30)).TotalSeconds;
                return new CommandResult(-1, "",
                    $"命令执行超时（{secs:0} 秒）：{ex.Message}");
            }
        }, ct);

    /// <summary>
    /// 流式执行命令：stdout/stderr 逐行回调（回调在后台线程，UI 需自行调度）。
    /// 注意：输出只通过回调交付；此处的 Output/Error 字段为空。
    /// （SSH.NET 的 Result/Error 会排空输出管道，与流式读取相互争抢，因此流式路径绝不读取它们。）
    /// </summary>
    public async Task<CommandResult> RunStreamingAsync(
        string command,
        Action<string>? onStdoutLine,
        Action<string>? onStderrLine,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        if (!IsConnected) throw new SessionDisconnectedException("连接已断开，请重新连接。");

        SshCommand? cmd = null;
        try
        {
            cmd = _ssh.CreateCommand(command);
            cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(30);

            // 先捕获两个输出流再启动执行，确保数据从第一字节起都进入泵读取
            var outStream = cmd.OutputStream;
            var errStream = cmd.ExtendedOutputStream;
            var tOut = Task.Run(() => Pump(outStream, onStdoutLine));
            var tErr = Task.Run(() => Pump(errStream, onStderrLine));

            // ExecuteAsync 完成时不读取输出流（EndExecute/Result 会排空管道并抢走流式数据）
            try
            {
                await cmd.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ct.IsCancellationRequested
                                       && ex is OperationCanceledException or ObjectDisposedException or TaskCanceledException)
            {
                // 用户主动停止
            }
            catch (OperationCanceledException)
            {
                throw new SshManagerException($"命令执行超时（{(timeout ?? TimeSpan.FromMinutes(30)).TotalMinutes:0} 分钟）。");
            }
            catch (SshOperationTimeoutException)
            {
                throw new SshManagerException($"命令执行超时（{(timeout ?? TimeSpan.FromMinutes(30)).TotalMinutes:0} 分钟）。");
            }
            catch (Exception ex)
            {
                OnConnectionLost(ex.Message);
                throw new SessionDisconnectedException("连接已断开：" + ex.Message);
            }

            // 通道关闭后管道仍可继续读取剩余缓冲数据，等待两个泵排干
            try { await Task.WhenAll(tOut, tErr).ConfigureAwait(false); }
            catch { /* 通道关闭时读取可能抛异常，忽略 */ }

            if (ct.IsCancellationRequested)
                return new CommandResult(-1, "", "已停止", WasCancelled: true);
            return new CommandResult(cmd.ExitStatus ?? -1, "", "");
        }
        finally
        {
            try { cmd?.Dispose(); } catch { }
        }
    }

    private static void Pump(Stream stream, Action<string>? callback)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            if (callback == null)
            {
                while (reader.ReadLine() != null) { }
                return;
            }
            string? line;
            while ((line = reader.ReadLine()) != null)
                callback(line.TrimEnd('\r'));
        }
        catch { /* 取消 / 通道关闭时正常退出 */ }
    }

    /// <summary>把命令包装为 sudo 执行；非 root 且没有 sudo 密码时抛出 SudoPasswordRequiredException。</summary>
    public string WrapSudo(string command)
    {
        if (IsRoot) return command;
        var pw = SudoPassword ?? throw new SudoPasswordRequiredException("需要 sudo 密码：请在界面中设置后重试。");
        return $"echo {UnixPath.ShellQuote(pw)} | sudo -S -p '' {command} 2>&1";
    }

    /// <summary>
    /// sudo 包装但**不合并 stderr**。用于二进制 stdout（如拖出时的 tar 流），
    /// 避免 WrapSudo 的 2&gt;&amp;1 把错误文本混进数据。
    /// </summary>
    public string WrapSudoRaw(string command)
    {
        if (IsRoot) return command;
        var pw = SudoPassword ?? throw new SudoPasswordRequiredException("需要 sudo 密码：请在界面中设置后重试。");
        return $"echo {UnixPath.ShellQuote(pw)} | sudo -S -p '' {command}";
    }

    /// <summary>
    /// sudo 包装**整个复合命令**（通过 sh -c），用于含 &amp;&amp; / if / 管道的命令。
    /// 直接拼在 sudo 后面时，shell 只会把第一段交给 sudo，后续段仍以普通用户执行。
    /// </summary>
    public string WrapSudoShell(string command)
    {
        if (IsRoot) return command;
        var pw = SudoPassword ?? throw new SudoPasswordRequiredException("需要 sudo 密码：请在界面中设置后重试。");
        return $"echo {UnixPath.ShellQuote(pw)} | sudo -S -p '' sh -c {UnixPath.ShellQuote(command)}";
    }

    // ---------------------------------------------------------------- SFTP 交互操作

    /// <summary>构造目录列表（由 <see cref="SftpAttemptAsync{T}"/> 在 SFTP 锁内调用）。</summary>
    private IReadOnlyList<RemoteFileInfo> BuildDirListing(string path)
    {
        var items = new List<RemoteFileInfo>();
        foreach (var f in _sftp.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            items.Add(new RemoteFileInfo
            {
                Name = f.Name,
                FullName = f.FullName,
                IsDirectory = f.IsDirectory && !f.IsSymbolicLink,
                IsSymbolicLink = f.IsSymbolicLink,
                Length = f.IsDirectory || f.IsSymbolicLink ? 0 : f.Length,
                ModifiedUtc = f.LastWriteTimeUtc,
                Permissions = UnixPath.PermissionsString(
                    f.IsDirectory, f.IsSymbolicLink,
                    f.OwnerCanRead, f.OwnerCanWrite, f.OwnerCanExecute,
                    f.GroupCanRead, f.GroupCanWrite, f.GroupCanExecute,
                    f.OthersCanRead, f.OthersCanWrite, f.OthersCanExecute),
            });
        }
        return items
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>探测路径属性（由 <see cref="SftpAttemptAsync{T}"/> 在 SFTP 锁内调用；不存在返回 null）。</summary>
    private RemoteFileInfo? BuildStat(string path)
    {
        if (!_sftp.Exists(path)) return null;
        var a = _sftp.GetAttributes(path);
        return new RemoteFileInfo
        {
            Name = UnixPath.FileName(path),
            FullName = path,
            IsDirectory = a.IsDirectory && !a.IsSymbolicLink,
            IsSymbolicLink = a.IsSymbolicLink,
            Length = a.IsDirectory ? 0 : a.Size,
            ModifiedUtc = a.LastWriteTimeUtc,
            Permissions = UnixPath.PermissionsString(
                a.IsDirectory, a.IsSymbolicLink,
                a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute,
                a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute,
                a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute),
        };
    }

    /// <summary>探测远程路径；预期内失败（权限不足 / 不存在 / 连接断开）通过 <see cref="StatResult"/> 返回，不抛异常。</summary>
    public async Task<StatResult> StatAsync(string path, CancellationToken ct = default)
    {
        var attempt = await SftpAttemptAsync(() => BuildStat(path), nameof(StatAsync), ct)
            .ConfigureAwait(false);
        return new StatResult(attempt.Value, attempt.PermissionDenied, attempt.Error);
    }

    /// <summary>
    /// 删除远程文件/目录。目录默认递归删除；符号链接只删除链接本身。
    /// 权限不足等常见失败不抛异常（返回 <see cref="DeleteResult"/>），
    /// 由调用方决定是否用 <see cref="DeleteViaSudoAsync"/> 重试；仅连接断开时抛
    /// <see cref="SessionDisconnectedException"/>。
    /// </summary>
    public Task<DeleteResult> DeleteAsync(string path, bool recursive, bool isSymlink, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                DeleteInternal(path, recursive, isSymlink);
                return DeleteResult.Success;
            }
            catch (SftpPermissionDeniedException)
            {
                return new DeleteResult(DeleteStatus.PermissionDenied, "当前用户对该文件/目录没有删除权限");
            }
            catch (SftpPathNotFoundException)
            {
                return new DeleteResult(DeleteStatus.NotFound, "远程路径不存在");
            }
            catch (Exception ex) when (IsConnectionDead(ex))
            {
                OnConnectionLost(ex.Message);
                throw new SessionDisconnectedException("连接已断开：" + ex.Message);
            }
            catch (Exception ex)
            {
                return new DeleteResult(DeleteStatus.Failed, ex.Message);
            }
        }, ct);

    /// <summary>
    /// 用 sudo rm -rf 删除（SFTP 删除权限不足时、用户确认后调用）。
    /// 一次可传多路径；非 root 且未保存 sudo 密码时抛 <see cref="SudoPasswordRequiredException"/>。
    /// </summary>
    public Task<CommandResult> DeleteViaSudoAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return Task.FromResult(new CommandResult(0, "", ""));
        var sb = new StringBuilder("rm -rf --");
        foreach (var p in paths)
            sb.Append(' ').Append(UnixPath.ShellQuote(p));
        return RunAsync(WrapSudo(sb.ToString()), TimeSpan.FromMinutes(10), ct);
    }

    private void DeleteInternal(string path, bool recursive, bool isSymlink)
    {
        if (isSymlink)
        {
            _sftp.Delete(path);
            return;
        }
        var a = _sftp.GetAttributes(path);
        if (!a.IsDirectory)
        {
            _sftp.Delete(path);
            return;
        }
        if (!recursive)
        {
            _sftp.DeleteDirectory(path);
            return;
        }
        foreach (var f in _sftp.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsSymbolicLink) _sftp.Delete(f.FullName);
            else if (f.IsDirectory) DeleteInternal(f.FullName, true, false);
            else _sftp.Delete(f.FullName);
        }
        _sftp.DeleteDirectory(path);
    }

    /// <summary>重命名；成功返回 null，失败返回用户可读的错误信息（预期内失败不抛异常）。</summary>
    public Task<string?> RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
        => SftpActionAttemptAsync(() =>
        {
            _sftp.RenameFile(oldPath, newPath);
            return null;
        }, nameof(RenameAsync), ct);

    /// <summary>新建目录；成功返回 null，失败返回用户可读的错误信息（预期内失败不抛异常）。</summary>
    public Task<string?> MkdirAsync(string path, CancellationToken ct = default)
        => SftpActionAttemptAsync(() =>
        {
            if (_sftp.Exists(path)) return "该名称已存在。";
            _sftp.CreateDirectory(path);
            return null;
        }, nameof(MkdirAsync), ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try { lock (_sftpLock) return _sftp.Exists(path); }
            catch (SftpPermissionDeniedException) { return false; }
            catch (SftpPathNotFoundException) { return false; }
            catch (Exception ex) when (IsConnectionDead(ex)) { OnConnectionLost(ex.Message); return false; }
            catch { return false; }
        }, ct);

    public Task TouchAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try { Run($"touch -- {UnixPath.ShellQuote(path)}"); }
            catch (SshOperationTimeoutException) { /* 预期内失败：不跨 Task.Run 委托抛出 */ }
        }, ct);

    // ---------------------------------------------------------------- 文件内容读写（编辑器）

    /// <summary>读取整个远程文件（由 <see cref="SftpAttemptAsync{T}"/> 在 SFTP 锁内调用）。</summary>
    private byte[] ReadAllBytes(string path)
    {
        using var ms = new MemoryStream();
        _sftp.DownloadFile(path, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 整体覆盖写入远程文件（保留原文件的属主与权限，mtime 更新为现在）。
    /// 权限不足等情况不抛异常（返回 <see cref="WriteResult"/>），
    /// 由调用方决定是否用 sudo 兜底；仅连接断开时抛 <see cref="SessionDisconnectedException"/>。
    /// </summary>
    public Task<WriteResult> WriteFileBytesAsync(string path, byte[] bytes, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                lock (_sftpLock)
                {
                    using var ms = new MemoryStream(bytes, 0, bytes.Length, writable: false);
                    _sftp.UploadFile(ms, path, true);
                }
                return WriteResult.Success;
            }
            catch (SftpPermissionDeniedException)
            {
                return new WriteResult(WriteStatus.PermissionDenied, "当前用户对该文件没有写入权限");
            }
            catch (SftpPathNotFoundException)
            {
                return new WriteResult(WriteStatus.NotFound, "远程文件不存在（可能已被删除或移动）");
            }
            catch (Exception ex) when (IsConnectionDead(ex))
            {
                OnConnectionLost(ex.Message);
                throw new SessionDisconnectedException("连接已断开：" + ex.Message);
            }
            catch (Exception ex)
            {
                return new WriteResult(WriteStatus.Failed, ex.Message);
            }
        }, ct);

    public Task<string> GetHomeAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                var home = Run("printf %s \"$HOME\"", TimeSpan.FromSeconds(10)).Output.Trim();
                return home.StartsWith('/') ? home : "/";
            }
            catch (SshOperationTimeoutException)
            {
                return "/";
            }
        }, ct);

    // ---------------------------------------------------------------- sudo 降级访问（文件管理）

    /// <summary>
    /// 列目录（SFTP 优先；遇权限不足且 allowSudo 时自动降级为 sudo find，返回 UsedSudo 标记）。
    /// 需要 sudo 密码而未设置时抛 <see cref="SudoPasswordRequiredException"/>（调用方弹窗后重试）。
    /// </summary>
    public async Task<DirListing> ListDirectoryExAsync(string path, bool allowSudo, CancellationToken ct = default)
    {
        var attempt = await SftpAttemptAsync(() => BuildDirListing(path), nameof(ListDirectoryExAsync), ct)
            .ConfigureAwait(false);

        if (!attempt.PermissionDenied)
        {
            return attempt.Error != null
                ? new DirListing(Array.Empty<RemoteFileInfo>(), false, attempt.Error)
                : new DirListing(attempt.Value!, false);
        }

        if (allowSudo)
        {
            var (items, error) = await ListDirectoryViaSudoAsync(path, ct).ConfigureAwait(false);
            return error != null
                ? new DirListing(Array.Empty<RemoteFileInfo>(), true, error)
                : new DirListing(items!, true);
        }

        // 未启用自动提权：通过 Error 返回用户可读提示，不在后台委托内抛异常
        return new DirListing(Array.Empty<RemoteFileInfo>(), false,
            $"当前用户无权访问 {path}。可在连接设置的“无权限时自动 sudo 提权”开启后自动提权浏览。");
    }

    /// <summary>sudo find 列目录：find -maxdepth 1 -printf '%y|%s|%T@|%m|%f\0'（\0 分隔记录，字段内 | 仅文件名段可能出现）。
    /// 注意：GNU find 不支持 “--” 选项结束符，路径本身以 / 开头不会与选项混淆。
    /// 返回 (items, error)：失败通过 error 返回，不在后台委托内抛异常。</summary>
    private async Task<(IReadOnlyList<RemoteFileInfo>? Items, string? Error)> ListDirectoryViaSudoAsync(
        string path, CancellationToken ct)
    {
        var cmd = WrapSudo(
            $"find {UnixPath.ShellQuote(path)} -maxdepth 1 -mindepth 1 -printf '%y|%s|%T@|%m|%f\\0'");
        var (code, raw, err) = await RunRawOutputAsync(cmd, ct).ConfigureAwait(false);
        if (code != 0)
        {
            var msg = Encoding.UTF8.GetString(raw).Trim();
            if (msg.Length == 0) msg = err.Trim();
            return (null, $"sudo 列目录失败：{(msg.Length > 0 ? msg : "退出码 " + code)}" +
                          "\n（请确认该用户可用 sudo，且已提供 sudo 密码）");
        }

        var list = new List<RemoteFileInfo>();
        var start = 0;
        while (start < raw.Length)
        {
            var end = Array.IndexOf(raw, (byte)0, start);
            if (end < 0) end = raw.Length;
            if (end > start)
            {
                var record = Encoding.UTF8.GetString(raw, start, end - start);
                var item = ParseFindRecord(record, path);
                if (item != null) list.Add(item);
            }
            start = end + 1;
        }
        return (list
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList(), null);
    }

    /// <summary>解析 find -printf 记录：y|s|T@|m|name。</summary>
    private static RemoteFileInfo? ParseFindRecord(string record, string dirPath)
    {
        // 前 4 个字段（类型/大小/mtime/权限）不含 '|'，文件名在最后，其中的 '|' 保留在名字里
        var parts = record.Split('|', 5);
        if (parts.Length < 5 || parts[0].Length != 1) return null;
        var y = parts[0][0];
        if (y is not ('f' or 'd' or 'l')) return null;
        if (!long.TryParse(parts[1], out var size)) size = 0;
        if (!double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var mtime))
            mtime = 0;
        var mtimeUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)(mtime * 1000)).UtcDateTime;
        var mode = 0;
        foreach (var c in parts[3])
        {
            if (c is >= '0' and <= '7') mode = mode * 8 + (c - '0');
            else { mode = 0; break; }
        }
        var name = parts[4];
        if (name.Length == 0) return null;
        return new RemoteFileInfo
        {
            Name = name,
            FullName = UnixPath.Combine(dirPath, name),
            IsDirectory = y == 'd',
            IsSymbolicLink = y == 'l',
            Length = y == 'f' ? size : 0,
            ModifiedUtc = mtimeUtc,
            Permissions = UnixPath.PermissionsFromMode(y, mode),
        };
    }

    /// <summary>
    /// 读取文件内容（SFTP 优先；遇权限不足且 allowSudo 时自动降级为 sudo cat，返回 UsedSudo 标记）。
    /// </summary>
    public async Task<FileReadResult> ReadFileBytesExAsync(string path, bool allowSudo, CancellationToken ct = default)
    {
        var attempt = await SftpAttemptAsync(() => ReadAllBytes(path), nameof(ReadFileBytesExAsync), ct)
            .ConfigureAwait(false);

        if (!attempt.PermissionDenied)
        {
            return attempt.Error != null
                ? new FileReadResult(Array.Empty<byte>(), false, attempt.Error)
                : new FileReadResult(attempt.Value!, false);
        }

        if (!allowSudo)
        {
            // 未启用自动提权：通过 Error 返回用户可读提示，不在后台委托内抛异常
            return new FileReadResult(Array.Empty<byte>(), false,
                $"当前用户无权读取 {path}。可在连接设置的“无权限时自动 sudo 提权”开启后自动提权读取。");
        }

        var (code, raw, err) = await RunRawOutputAsync(
            WrapSudo($"cat -- {UnixPath.ShellQuote(path)}"), ct).ConfigureAwait(false);
        if (code != 0)
        {
            var msg = Encoding.UTF8.GetString(raw).Trim();
            if (msg.Length == 0) msg = err.Trim();
            return new FileReadResult(Array.Empty<byte>(), true,
                $"sudo 读取失败：{(msg.Length > 0 ? msg : "退出码 " + code)}");
        }
        return new FileReadResult(raw, true);
    }

    /// <summary>执行命令并取原始 stdout 字节（不经过 UTF-8 字符串解码，\0 等二进制内容不损坏）。</summary>
    private async Task<(int ExitCode, byte[] Out, string Err)> RunRawOutputAsync(string command, CancellationToken ct)
    {
        if (!IsConnected) throw new SessionDisconnectedException("连接已断开，请重新连接。");
        SshCommand? cmd = null;
        try
        {
            cmd = _ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(2);
            var outStream = cmd.OutputStream;
            var tOut = Task.Run(() =>
            {
                using var ms = new MemoryStream();
                outStream.CopyTo(ms);
                return ms.ToArray();
            }, ct);
            try
            {
                await cmd.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnConnectionLost(ex.Message);
                throw new SessionDisconnectedException("连接已断开：" + ex.Message);
            }
            var bytes = await tOut.ConfigureAwait(false);
            return (cmd.ExitStatus ?? -1, bytes, cmd.Error ?? "");
        }
        finally
        {
            try { cmd?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 执行命令并把 stdout **二进制原样**写入本地文件（拖出 tar 流使用）。
    /// command 不能把 stderr 合并到 stdout（用 <see cref="WrapSudoRaw"/>，不要用 WrapSudo）。
    /// </summary>
    public async Task<(int ExitCode, string Error)> RunToFileAsync(
        string command, string localPath, CancellationToken ct = default)
    {
        if (!IsConnected) throw new SessionDisconnectedException("连接已断开，请重新连接。");
        SshCommand? cmd = null;
        try
        {
            cmd = _ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(30);
            var outStream = cmd.OutputStream;
            await using var fs = File.Create(localPath);
            var tOut = Task.Run(() =>
            {
                outStream.CopyTo(fs);
            }, ct);
            try
            {
                await cmd.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnConnectionLost(ex.Message);
                throw new SessionDisconnectedException("连接已断开：" + ex.Message);
            }
            await tOut.ConfigureAwait(false);
            return (cmd.ExitStatus ?? -1, cmd.Error ?? "");
        }
        finally
        {
            try { cmd?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// sudo cat 流式下载到流（SFTP 无权限时由传输队列降级调用）。
    /// onProgress 汇报累计字节数。ExitCode != 0 时抛异常（输出内容即错误文本）。
    /// </summary>
    public async Task<long> DownloadViaSudoAsync(string path, Stream dest, Action<long>? onProgress, CancellationToken ct = default)
    {
        if (!IsConnected) throw new SessionDisconnectedException("连接已断开，请重新连接。");
        var command = WrapSudo($"cat -- {UnixPath.ShellQuote(path)} 2>/dev/null");
        SshCommand? cmd = null;
        try
        {
            cmd = _ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(30);
            var outStream = cmd.OutputStream;
            var tPump = Task.Run(async () =>
            {
                var buf = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = await outStream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    total += n;
                    onProgress?.Invoke(total);
                }
                return total;
            }, ct);
            try
            {
                await cmd.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                OnConnectionLost(ex.Message);
                throw new SessionDisconnectedException("连接已断开：" + ex.Message);
            }
            var total = await tPump.ConfigureAwait(false);
            if (cmd.ExitStatus is not 0)
                throw new SshManagerException($"sudo 下载失败（退出码 {cmd.ExitStatus}），请确认文件存在且用户可用 sudo。");
            return total;
        }
        finally
        {
            try { cmd?.Dispose(); } catch { }
        }
    }

    /// <summary>sudo stat -Lc 获取单文件类型与大小（SFTP 无权限时的下载前探测）。</summary>
    public async Task<(bool IsDir, long Size)> StatViaSudoAsync(string path, CancellationToken ct = default)
    {
        var r = await RunAsync(
            WrapSudo($"stat -Lc '%F|%s' -- {UnixPath.ShellQuote(path)}"),
            TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        // "regular file|12345" / "directory|4096" / "symbolic link|7"
        var output = (r.Output ?? "").Trim();
        var sep = output.IndexOf('|');
        if (r.ExitCode != 0 || sep <= 0 || !long.TryParse(output[(sep + 1)..].Trim(), out var size))
            throw new SshManagerException("无法读取该路径（sudo stat 失败）：" +
                                          ((r.Output + " " + r.Error).Trim().Length > 0
                                              ? (r.Output + " " + r.Error).Trim()
                                              : "退出码 " + r.ExitCode));
        var isDir = output[..sep].Trim().Contains("directory", StringComparison.OrdinalIgnoreCase);
        return (isDir, size);
    }

    // ---------------------------------------------------------------- PTY 交互终端

    /// <summary>打开一个持久化伪终端会话（命令台“交互终端”模式）。</summary>
    public PtySession OpenPty(int cols, int rows)
    {
        if (!IsConnected) throw new SessionDisconnectedException("连接已断开，请重新连接。");
        return PtySession.Open(_ssh, cols, rows);
    }

    // ---------------------------------------------------------------- 基础设施

    /// <summary>后台 SFTP 操作结果：预期内失败（权限 / 不存在 / 断开）通过返回值表达，不跨 Task.Run 委托抛异常。</summary>
    private sealed record SftpAttempt<T>(T? Value, bool PermissionDenied, string? Error) where T : class;

    /// <summary>
    /// 在后台线程执行 SFTP 读取操作，并在委托内部捕获所有预期内异常。
    /// 异常绝不能跨 Task.Run 委托边界抛出，否则调试器会在“首次抛出”处中断（用户看到的“崩溃”）。
    /// </summary>
    private Task<SftpAttempt<T>> SftpAttemptAsync<T>(Func<T?> f, string op, CancellationToken ct)
        where T : class
        => Task.Run(() =>
        {
            try
            {
                lock (_sftpLock)
                {
                    return new SftpAttempt<T>(f(), false, null);
                }
            }
            catch (SftpPermissionDeniedException ex)
            {
                return new SftpAttempt<T>(null, true, $"当前用户没有权限（{op}）：{ex.Message}");
            }
            catch (SftpPathNotFoundException)
            {
                return new SftpAttempt<T>(null, false, "远程路径不存在。");
            }
            catch (Exception ex) when (IsConnectionDead(ex))
            {
                OnConnectionLost(ex.Message);
                return new SftpAttempt<T>(null, false, "连接已断开：" + ex.Message);
            }
            catch (Exception ex)
            {
                return new SftpAttempt<T>(null, false, ex.Message);
            }
        }, ct);

    /// <summary>
    /// 在后台线程执行 SFTP 写操作（重命名 / 新建目录等）；成功返回 null，失败返回错误信息。
    /// 同样保证异常不跨 Task.Run 委托边界抛出。
    /// </summary>
    private Task<string?> SftpActionAttemptAsync(Func<string?> f, string op, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                lock (_sftpLock)
                {
                    return f();
                }
            }
            catch (SftpPermissionDeniedException ex)
            {
                return $"当前用户没有权限（{op}）：{ex.Message}";
            }
            catch (SftpPathNotFoundException)
            {
                return "远程路径不存在。";
            }
            catch (Exception ex) when (IsConnectionDead(ex))
            {
                OnConnectionLost(ex.Message);
                return "连接已断开：" + ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }, ct);

    private static bool IsConnectionDead(Exception ex) =>
        ex is SshConnectionException or SocketException or ObjectDisposedException;

    private void OnConnectionLost(string reason)
    {
        if (Interlocked.Exchange(ref _lostReported, 1) == 1) return;
        try { ConnectionLost?.Invoke(reason); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ssh.Disconnect(); } catch { }
        try { _ssh.Dispose(); } catch { }
        try { _sftp.Disconnect(); } catch { }
        try { _sftp.Dispose(); } catch { }
    }
}
