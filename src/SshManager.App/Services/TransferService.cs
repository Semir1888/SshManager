using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Services;

namespace SshManager.App.Services;

/// <summary>
/// 上传/下载队列：严格按入队顺序执行。
/// 队列状态由内部列表（锁保护）维护，UI 的 Items 只做展示，
/// 杜绝“任务卡在排队中 / 进度不动”的竞态。
/// 大文件走独立 SFTP 连接，不阻塞文件管理器的交互操作。
/// </summary>
public sealed class TransferService
{
    private readonly object _lock = new();
    private readonly object _clientLock = new();
    private readonly List<TransferItem> _pending = new();
    private SshSession? _session;
    private SftpClient? _client;
    private DispatcherTimer? _timer;
    private bool _pumping;          // 泵循环是否在跑（仅 UI 线程读写）
    private TransferItem? _running; // 当前正在传输的项（仅 UI 线程读写）

    public ObservableCollection<TransferItem> Items { get; } = new();

    public void Bind(SshSession session)
    {
        _session = session;
        Items.Clear();
        lock (_lock) _pending.Clear();
        EnsureTimer();
    }

    /// <summary>断开/重置：取消所有未完成任务并释放传输连接。</summary>
    public void Reset()
    {
        _session = null;
        lock (_lock)
        {
            foreach (var t in _pending)
            {
                try { t.Cts.Cancel(); } catch { }
                if (t.StatusText == "排队中") t.SetStatus(TransferStatus.Cancelled);
            }
            _pending.Clear();
        }
        if (_running != null)
        {
            try { _running.Cts.Cancel(); } catch { }
            _running.SetStatus(TransferStatus.Cancelled);
            _running = null;
        }
        lock (_clientLock)
        {
            try { _client?.Dispose(); } catch { }
            _client = null;
        }
    }

    /// <summary>排队中 + 传输中的任务数（状态栏“传输 (N)”）。</summary>
    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                var n = _pending.Count(i => i.StatusText is "排队中" or "传输中");
                if (_running != null && _running.StatusText == "传输中") n++;
                return n;
            }
        }
    }

    public void Cancel(TransferItem t)
    {
        try { t.Cts.Cancel(); } catch { }
        // 还在排队的直接标记为已取消（泵循环会跳过它）
        if (t.StatusText == "排队中") t.SetStatus(TransferStatus.Cancelled);
    }

    public void ClearFinished()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].StatusText is not ("排队中" or "传输中"))
                Items.RemoveAt(i);
        }
    }

    // ------------------------------------------------------------------ 入队

    /// <summary>上传本地文件或整个目录（目录递归展开为多个文件任务，个别目录无权限时跳过不中断）。</summary>
    public void EnqueueUpload(string localPath, string remoteDir)
    {
        if (_session == null) return;
        var batch = new List<TransferItem>();
        try
        {
            if (File.Exists(localPath))
            {
                AddLocalFileToBatch(batch, localPath, UnixPath.Combine(remoteDir, Path.GetFileName(localPath)));
            }
            else if (Directory.Exists(localPath))
            {
                var baseName = Path.GetFileName(localPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (var f in CollectLocalFiles(localPath))
                {
                    var rel = Path.GetRelativePath(localPath, f).Replace('\\', '/');
                    AddLocalFileToBatch(batch, f,
                        UnixPath.Combine(UnixPath.Combine(remoteDir, baseName), rel));
                }
            }
            else
            {
                batch.Add(new TransferItem(TransferDirection.Upload, localPath, remoteDir, 0, Cancel)
                    .MarkFailed("本地路径不存在"));
            }
        }
        catch (Exception ex)
        {
            batch.Add(new TransferItem(TransferDirection.Upload, localPath, remoteDir, 0, Cancel)
                .MarkFailed("读取本地路径失败：" + ex.Message));
        }
        AddRange(batch);
    }

    private void AddLocalFileToBatch(List<TransferItem> batch, string localFile, string remotePath)
    {
        try
        {
            batch.Add(new TransferItem(TransferDirection.Upload, localFile, remotePath,
                new FileInfo(localFile).Length, Cancel));
        }
        catch (Exception ex)
        {
            batch.Add(new TransferItem(TransferDirection.Upload, localFile, remotePath, 0, Cancel)
                .MarkFailed("读取本地文件失败：" + ex.Message));
        }
    }

    /// <summary>下载远程文件或目录到本地目录（目录递归收集，个别子目录读取失败时记为失败项继续）。</summary>
    public async Task EnqueueDownloadAsync(string remotePath, string localDir)
    {
        if (_session == null) return;
        var batch = new List<TransferItem>();
        try
        {
            Core.Models.RemoteFileInfo? stat = null;
            var statResult = await _session.StatAsync(remotePath);
            stat = statResult.Info;
            if (statResult.PermissionDenied)
            {
                // 无权限（如 /root 下路径）：sudo stat 探测类型与大小，再分类处理
                var (isDir, size) = await RunSudoAsync(
                    s => s.StatViaSudoAsync(remotePath),
                    "读取无权限路径");
                if (isDir)
                {
                    await CollectDirAsync(remotePath, Path.Combine(localDir, UnixPath.FileName(remotePath)), batch);
                }
                else
                {
                    batch.Add(new TransferItem(TransferDirection.Download,
                        Path.Combine(localDir, UnixPath.FileName(remotePath)), remotePath, size, Cancel)
                    { FromSudoDir = true });
                }
                stat = null; // 已在上方入队，跳过下方分支
            }
            else if (statResult.Error != null)
            {
                throw new SshManagerException(statResult.Error);
            }
            if (stat == null) { }
            else if (stat.IsDirectory)
            {
                await CollectDirAsync(remotePath, Path.Combine(localDir, UnixPath.FileName(remotePath)), batch);
            }
            else
            {
                batch.Add(new TransferItem(TransferDirection.Download,
                    Path.Combine(localDir, stat.Name), remotePath, stat.Length, Cancel));
            }
        }
        catch (SudoPasswordRequiredException)
        {
            batch.Add(new TransferItem(TransferDirection.Download,
                Path.Combine(localDir, UnixPath.FileName(remotePath)), remotePath, 0, Cancel)
                .MarkFailed("已取消：未提供 sudo 密码，无法访问该路径"));
        }
        catch (Exception ex)
        {
            batch.Add(new TransferItem(TransferDirection.Download,
                Path.Combine(localDir, UnixPath.FileName(remotePath)), remotePath, 0, Cancel)
                .MarkFailed("读取远程路径失败：" + ex.Message));
        }
        AddRange(batch);
    }

    private async Task CollectDirAsync(string rdir, string ldir, List<TransferItem> batch)
    {
        IReadOnlyList<Core.Models.RemoteFileInfo> entries;
        var usedSudo = false;
        try
        {
            // SFTP 优先；无权限时 sudo find 降级（需密码则弹窗重试一次）
            try
            {
                var listing = await _session!.ListDirectoryExAsync(rdir, _session.Profile.AutoSudo);
                if (listing.Error != null) throw new SshManagerException(listing.Error);
                entries = listing.Items;
                usedSudo = listing.UsedSudo;
            }
            catch (SudoPasswordRequiredException)
            {
                var pw = await PromptSudoPasswordAsync($"用户 {_session!.Profile.Username} 无权访问 {rdir}，下载需要 sudo 密码");
                if (pw == null) throw new SshManagerException("已取消：未提供 sudo 密码。");
                _session.SudoPassword = pw;
                var listing = await _session.ListDirectoryExAsync(rdir, true);
                if (listing.Error != null) throw new SshManagerException(listing.Error);
                entries = listing.Items;
                usedSudo = true;
            }
        }
        catch (Exception ex)
        {
            batch.Add(new TransferItem(TransferDirection.Download,
                Path.Combine(ldir, UnixPath.FileName(rdir)), rdir, 0, Cancel)
                .MarkFailed("无法读取远程目录：" + ex.Message));
            return;
        }
        foreach (var e in entries)
        {
            if (e.IsSymbolicLink) continue; // 跳过符号链接，避免循环
            if (e.IsDirectory) await CollectDirAsync(e.FullName, Path.Combine(ldir, e.Name), batch);
            else batch.Add(new TransferItem(TransferDirection.Download,
                Path.Combine(ldir, e.Name), e.FullName, e.Length, Cancel)
            { FromSudoDir = usedSudo });
        }
    }

    /// <summary>弹窗获取 sudo 密码；取消返回 null。</summary>
    private static async Task<string?> PromptSudoPasswordAsync(string purpose)
    {
        var (ok, pw) = await Dialogs.InputAsync("sudo 密码", purpose + "：", "", password: true);
        return ok && pw.Length > 0 ? pw : null;
    }

    /// <summary>执行一次需要 sudo 密码的操作（缺密码时弹窗重试一次）。</summary>
    private async Task<T> RunSudoAsync<T>(Func<SshSession, Task<T>> op, string purpose)
    {
        var session = _session ?? throw new InvalidOperationException("会话已断开");
        try
        {
            return await op(session);
        }
        catch (SudoPasswordRequiredException)
        {
            var pw = await PromptSudoPasswordAsync($"用户 {session.Profile.Username} 需要{purpose}，请输入其 sudo 密码");
            if (pw == null) throw new SudoPasswordRequiredException("已取消：未提供 sudo 密码。");
            session.SudoPassword = pw;
            return await op(session);
        }
    }

    /// <summary>安全递归收集本地文件：无权限的子目录直接跳过，不影响其余文件。</summary>
    private static List<string> CollectLocalFiles(string root)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }
            string[] fs;
            try { fs = Directory.GetFiles(dir); }
            catch { fs = Array.Empty<string>(); }
            files.AddRange(fs);
            foreach (var s in subDirs) stack.Push(s);
        }
        return files;
    }

    // ------------------------------------------------------------------ 调度

    /// <summary>批量入队：内部列表立即登记，UI 集合经调度器插入后再触发泵。</summary>
    private void AddRange(IReadOnlyList<TransferItem> items)
    {
        if (items.Count == 0) return;
        lock (_lock) _pending.AddRange(items);
        Dispatcher.UIThread.Post(() =>
        {
            for (var i = items.Count - 1; i >= 0; i--)
                Items.Insert(0, items[i]); // 与列表“最新在上”一致
            Pump();
        });
    }

    /// <summary>启动泵循环（仅 UI 线程调用）。</summary>
    private void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        _ = PumpLoopAsync();
    }

    /// <summary>逐个执行排队任务；全程在 UI 线程上做状态更新，重活交给 Task.Run。</summary>
    private async Task PumpLoopAsync()
    {
        try
        {
            while (true)
            {
                if (_session == null) return;
                TransferItem? item;
                lock (_lock)
                {
                    item = _pending.FirstOrDefault(i => i.StatusText == "排队中");
                    if (item != null) _pending.Remove(item);
                }
                if (item == null) return;
                _running = item;
                try { await RunItemAsync(item); }
                finally { _running = null; }
            }
        }
        finally
        {
            _pumping = false;
            // 收尾期间又有新任务进来则补一次泵（正常情况下 AddRange 的 Post 会触发）
            bool more;
            lock (_lock) more = _pending.Any(i => i.StatusText == "排队中");
            if (more && _session != null)
                Dispatcher.UIThread.Post(Pump);
        }
    }

    private async Task RunItemAsync(TransferItem t)
    {
        t.SetStatus(TransferStatus.Running);
        t.LastSampleAt = DateTime.UtcNow;
        t.LastSampleBytes = 0;
        t.SpeedBps = 0;
        t.ProgressBytes = 0;
        t.Percent = 0;
        t.ProgressText = t.TotalBytes > 0 ? "0 / " + UnixPath.HumanSize(t.TotalBytes) : "";
        try
        {
            var session = _session ?? throw new InvalidOperationException("会话已断开");
            if (t.IsUpload)
                await RunUploadAsync(session, t);
            else
                await RunDownloadAsync(session, t);
            t.ProgressBytes = t.TotalBytes;
            t.SetStatus(TransferStatus.Done);
            t.Percent = 100;
            t.ProgressText = t.TotalBytes > 0
                ? $"{UnixPath.HumanSize(t.TotalBytes)} · 已完成"
                : "已完成";
        }
        catch (OperationCanceledException)
        {
            t.SetStatus(TransferStatus.Cancelled);
            t.Percent = FinalPercent(t);
            t.ProgressText = "已取消";
            try { if (!t.IsUpload) File.Delete(t.LocalPath); } catch { }
        }
        catch (Exception ex)
        {
            t.SetStatus(TransferStatus.Failed);
            t.Error = ex.Message;
            t.Percent = FinalPercent(t);
            t.ProgressText = "失败";
        }
    }

    /// <summary>
    /// 在后台线程执行传输操作，异常在委托内部捕获、回到异步方法后再抛出。
    /// 目的：避免调试器在 Task.Run 委托内部的“首次抛出”处中断（权限不足等预期内失败）。
    /// </summary>
    private static async Task RunTransferAsync(Action action, CancellationToken ct)
    {
        Exception? error = null;
        await Task.Run(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        }, ct);
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>上传：SFTP 直传；目标无权限时降级为 /tmp 中转 + sudo cp（保留目标已存在时的属主权限）。</summary>
    private async Task RunUploadAsync(SshSession session, TransferItem t)
    {
        await EnsureRemoteDirAsync(UnixPath.Parent(t.RemotePath));
        var client = GetClient(session);
        try
        {
            await RunTransferAsync(() =>
            {
                using var fs = File.OpenRead(t.LocalPath);
                // 回调里响应取消：ThrowIfCancellationRequested 会中断传输
                client.UploadFile(fs, t.RemotePath, true, b =>
                {
                    t.Cts.Token.ThrowIfCancellationRequested();
                    t.ProgressBytes = (long)b;
                });
            }, t.Cts.Token);
            return; // 直传成功
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsPermissionError(ex))
        {
            // 目标无权限：下方降级为 /tmp 中转 + sudo cp
        }
        catch (SshConnectionException) { throw; }
        catch (Exception ex)
        {
            // SFTP 通道异常（含临时网络问题）不冒然 sudo 重传，直接失败
            throw new SshManagerException(ex.Message);
        }

        // sudo 降级：先 SFTP 传到 /tmp（有进度），再 sudo mkdir -p + cp 覆盖
        var temp = "/tmp/.sshmanager-up-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await RunTransferAsync(() =>
            {
                using var fs = File.OpenRead(t.LocalPath);
                client.UploadFile(fs, temp, true, b =>
                {
                    t.Cts.Token.ThrowIfCancellationRequested();
                    t.ProgressBytes = (long)b;
                });
            }, t.Cts.Token);
            var target = UnixPath.ShellQuote(t.RemotePath);
            var parent = UnixPath.ShellQuote(UnixPath.Parent(t.RemotePath));
            var r = await RunSudoAsync(
                s => s.RunAsync(s.WrapSudoShell($"mkdir -p -- {parent} && cp -- {UnixPath.ShellQuote(temp)} {target}"),
                    TimeSpan.FromMinutes(10)),
                "上传到无权限目录");
            if (r.ExitCode != 0)
                throw new SshManagerException("sudo 上传失败：" + (r.Output + " " + r.Error).Trim());
        }
        finally
        {
            try { await session.RunAsync("rm -f -- " + UnixPath.ShellQuote(temp), TimeSpan.FromSeconds(10)); }
            catch { }
        }
    }

    /// <summary>下载：SFTP 直传；无权限时降级为 sudo cat 流式（进度照常显示）。</summary>
    private async Task RunDownloadAsync(SshSession session, TransferItem t)
    {
        var dir = Path.GetDirectoryName(t.LocalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var needSudo = t.FromSudoDir;
        if (!needSudo)
        {
            var client = GetClient(session);
            try
            {
                await RunTransferAsync(() =>
                {
                    using var fs = File.Create(t.LocalPath);
                    client.DownloadFile(t.RemotePath, fs, b =>
                    {
                        t.Cts.Token.ThrowIfCancellationRequested();
                        t.ProgressBytes = (long)b;
                    });
                }, t.Cts.Token);
                return; // 直传成功
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsPermissionError(ex))
            {
                needSudo = true;
            }
            catch (SshConnectionException) { throw; }
        }

        // sudo cat 流式下载（异常时删除半成品文件）
        try
        {
            using var fs = File.Create(t.LocalPath);
            t.ProgressBytes = 0;
            await RunSudoAsync(
                s => s.DownloadViaSudoAsync(t.RemotePath, fs, n => t.ProgressBytes = n, t.Cts.Token),
                "下载无权限文件");
            // sudo 路径拿不到精确总大小时以实际字节数为准
            if (t.TotalBytes <= 0 && t.ProgressBytes > 0)
                t.TotalBytes = t.ProgressBytes;
        }
        catch
        {
            try { File.Delete(t.LocalPath); } catch { }
            throw;
        }
    }

    private static double FinalPercent(TransferItem t) =>
        t.TotalBytes > 0 ? Math.Clamp(t.ProgressBytes * 100.0 / t.TotalBytes, 0, 100) : 0;

    /// <summary>SFTP 权限类异常判定（类型或消息，兼容不同 SSH.NET 版本的服务端拒绝方式）。</summary>
    private static bool IsPermissionError(Exception ex)
        => ex is SftpPermissionDeniedException
           || ex is PermissionDeniedException
           || ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("权限不足");

    private SftpClient GetClient(SshSession session)
    {
        lock (_clientLock)
        {
            if (_client != null && _client.IsConnected) return _client;
            try { _client?.Dispose(); } catch { }
            _client = session.CreateSftpClient();
            return _client;
        }
    }

    private async Task EnsureRemoteDirAsync(string remoteDir)
    {
        var session = _session ?? throw new InvalidOperationException("会话已断开");
        var denied = false;
        await Task.Run(() =>
        {
            var client = GetClient(session);
            var parts = remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cur = "";
            foreach (var p in parts)
            {
                cur += "/" + p;
                bool exists;
                try { exists = client.Exists(cur); }
                catch
                {
                    // 父目录无权限：stat 抛异常，直接交给 sudo mkdir -p 处理
                    denied = true;
                    return;
                }
                if (exists) continue;
                try { client.CreateDirectory(cur); }
                catch (SftpPermissionDeniedException) { denied = true; }
                catch { }
            }
        });
        if (denied)
        {
            // 中间目录无权限（如上传到 /root/xx）：sudo mkdir -p 兼容处理
            try
            {
                var r = await RunSudoAsync(
                    s => s.RunAsync(s.WrapSudo($"mkdir -p -- {UnixPath.ShellQuote(remoteDir)}"), TimeSpan.FromMinutes(2)),
                    "创建无权限目录");
                if (r.ExitCode != 0)
                    throw new SshManagerException("无权创建远程目录：" + (r.Output + " " + r.Error).Trim());
            }
            catch (SudoPasswordRequiredException)
            {
                throw new SshManagerException("无权创建远程目录（未提供 sudo 密码）。");
            }
        }
    }

    // ------------------------------------------------------------------ 进度刷新

    private void EnsureTimer()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshUi();
        _timer.Start();
    }

    private void RefreshUi()
    {
        foreach (var t in Items)
        {
            if (t.StatusText != "传输中") continue;
            var now = DateTime.UtcNow;
            if ((now - t.LastSampleAt).TotalMilliseconds >= 500)
            {
                var db = t.ProgressBytes - t.LastSampleBytes;
                var dt = (now - t.LastSampleAt).TotalSeconds;
                if (dt > 0 && db >= 0)
                {
                    var cur = db / dt;
                    t.SpeedBps = t.SpeedBps <= 0 ? cur : t.SpeedBps * 0.6 + cur * 0.4;
                }
                t.LastSampleBytes = t.ProgressBytes;
                t.LastSampleAt = now;
            }
            t.Percent = t.TotalBytes > 0
                ? Math.Clamp(t.ProgressBytes * 100.0 / t.TotalBytes, 0, 100)
                : 100;
            t.ProgressText = t.TotalBytes > 0
                ? $"{UnixPath.HumanSize(t.ProgressBytes)} / {UnixPath.HumanSize(t.TotalBytes)} · {UnixPath.HumanSize((long)t.SpeedBps)}/s"
                : "";
        }
    }
}

internal static class TransferItemExtensions
{
    public static TransferItem MarkFailed(this TransferItem t, string error)
    {
        t.SetStatus(TransferStatus.Failed);
        t.Error = error;
        t.ProgressText = "失败";
        return t;
    }
}
