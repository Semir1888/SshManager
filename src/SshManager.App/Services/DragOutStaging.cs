using System.Formats.Tar;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Services;

namespace SshManager.App.Services;

/// <summary>拖拽/剪贴板共用的远程条目信息。</summary>
public sealed record FileTransferItem(string FullName, string Name, bool IsDirectory);

/// <summary>内部拖拽的 JSON 载荷（自定义 DataFormat）。</summary>
public sealed record RemoteDragPayload(List<FileTransferItem> Items, bool SourceSudo);

/// <summary>
/// 拖出到 Windows 的临时 staging：拖出本程序窗口后，把远程选中项打包成 tar 流下载到本地临时目录，
/// 再用 System.Formats.Tar 解包成真实文件/文件夹，供 OLE CF_HDROP 拖放使用。
///
/// 构造时先创建同名占位文件/目录，便于调用方用平台的 StorageProvider 预解析成 IStorageItem；
/// 真正的下载在 <see cref="Start"/> 后于后台线程进行。失败时删除占位，避免复制空文件。
/// </summary>
public sealed class DragOutStaging
{
    private readonly SshSession _session;
    private readonly string _parentDir;
    private readonly IReadOnlyList<FileTransferItem> _items;
    private readonly bool _sourceSudo;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private volatile string? _error;

    /// <summary>本地临时 staging 根目录。</summary>
    public string Root { get; }

    public string? Error => _error;

    public DragOutStaging(SshSession session, string parentDir,
        IReadOnlyList<FileTransferItem> items, bool sourceSudo)
    {
        _session = session;
        _parentDir = parentDir;
        _items = items;
        _sourceSudo = sourceSudo;
        Root = Path.Combine(Path.GetTempPath(), "SshManager", "dragout", Guid.NewGuid().ToString("N"));
        CreatePlaceholders();
    }

    /// <summary>某个选中项在本地 staging 中的路径（构造后即存在占位）。</summary>
    public string GetStagedPath(int index) => Path.Combine(Root, _items[index].Name);

    /// <summary>开始后台下载/解包；重复调用无副作用。</summary>
    public void Start()
    {
        if (_task != null) return;
        _task = Task.Run(RunAsync);
    }

    /// <summary>
    /// 等待 staging 完成（Windows OLE 在 drop 时同步取文件数据）。
    /// staging 全程在后台线程完成，不依赖 UI 线程。
    /// </summary>
    public bool WaitForStaging()
    {
        Start();
        try
        {
            _task!.GetAwaiter().GetResult();
            return _error == null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return false;
        }
    }

    public void Cancel()
    {
        try { _cts.Cancel(); } catch { }
    }

    /// <summary>清理过期的拖出临时目录（程序启动时调用）。</summary>
    public static void CleanupOld(TimeSpan maxAge)
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "SshManager", "dragout");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > maxAge)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* 正在使用/无权限可忽略 */ }
            }
            foreach (var tar in Directory.GetFiles(root, "*.tar"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tar) > maxAge)
                        File.Delete(tar);
                }
                catch { }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    private void CreatePlaceholders()
    {
        try
        {
            Directory.CreateDirectory(Root);
            foreach (var item in _items)
            {
                var p = Path.Combine(Root, item.Name);
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(p);
                }
                else
                {
                    var dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (!File.Exists(p)) File.Create(p).Dispose();
                }
            }
        }
        catch { /* 占位失败只会影响拖出，内部拖拽不受影响 */ }
    }

    private async Task RunAsync()
    {
        // tar 放在 Root 同级，避免与远程文件名（例如 dragout.tar）冲突
        var tarPath = Root + ".tar";
        try
        {
            Directory.CreateDirectory(Root);

            var names = string.Join(" ", _items.Select(i => UnixPath.ShellQuote(i.Name)));
            var tarArgs = $"tar -C {UnixPath.ShellQuote(_parentDir)} -cf - -- {names}";
            // sudo 时不能用 WrapSudo（它带 2>&1，会把 stderr 混进 tar 流）
            var command = _sourceSudo ? _session.WrapSudoRaw(tarArgs) : tarArgs;

            var (code, err) = await _session.RunToFileAsync(command, tarPath, _cts.Token).ConfigureAwait(false);
            if (code != 0)
                throw new SshManagerException(
                    $"拖出失败（打包退出码 {code}）：{FirstLine(err)}");

            TarFile.ExtractToDirectory(tarPath, Root, overwriteFiles: true);
            try { File.Delete(tarPath); } catch { }

            foreach (var item in _items)
            {
                var p = Path.Combine(Root, item.Name);
                if (!File.Exists(p) && !Directory.Exists(p))
                    throw new SshManagerException($"拖出内容缺失：{item.Name}");
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            try { File.Delete(tarPath); } catch { }
            // 删除占位，避免资源管理器把空文件/空目录复制出去
            foreach (var item in _items)
            {
                try
                {
                    var p = Path.Combine(Root, item.Name);
                    if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                    else if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }
        }
    }

    private static string FirstLine(string? text)
    {
        var line = (text ?? "").Trim();
        var i = line.IndexOf('\n');
        return i >= 0 ? line[..i].Trim() : line;
    }
}
