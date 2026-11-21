using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.App.Services;
using SshManager.Core;
using SshManager.Core.Helpers;
using SshManager.Core.Models;
using SshManager.Core.Services;

namespace SshManager.App.ViewModels;

public partial class FileManagerViewModel : ViewModelBase
{
    private const long MaxEditBytes = 2 * 1024 * 1024; // 2MB 编辑上限

    private readonly SshSession _session;
    private readonly TransferService _transfers;
    private readonly MainWindowViewModel _owner;
    private string? _home;
    private string _actualPath = "/";

    [ObservableProperty] private string _pathText = "/";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private string _infoText = "";
    [ObservableProperty] private string _errorText = "";

    /// <summary>当前目录是否为 sudo 提权访问（界面显示徽章提醒）。</summary>
    [ObservableProperty] private bool _isSudoDir;

    /// <summary>该连接是否允许无权限时自动 sudo 降级（连接对话框可配置）。</summary>
    public bool AutoSudo => _session.Profile.AutoSudo;

    /// <summary>全部条目（服务端顺序），Entries 为过滤后视图。</summary>
    public ObservableCollection<FileEntryViewModel> All { get; } = new();

    public ObservableCollection<FileEntryViewModel> Entries { get; } = new();

    public List<FileEntryViewModel> Selected { get; private set; } = new();

    /// <summary>当前远程目录（拖放/粘贴的目标目录）。</summary>
    public string CurrentPath => _actualPath;

    // ------------------------------------------------------------------ 内部剪贴板（复制/剪切/粘贴）

    private readonly List<FileTransferItem> _clipboard = new();
    private bool _clipboardCut;
    private bool _clipboardSudo; // 复制/剪切来源目录是否为 sudo 提权访问

    public bool CanPaste => _clipboard.Count > 0;

    internal IReadOnlyList<FileEntryViewModel> SelectionOr(FileEntryViewModel entry)
        => Selected.Contains(entry) ? Selected : new[] { entry };

    // ------------------------------------------------------------------ 编辑器

    /// <summary>文件管理模块内部的编辑标签集合（主Tab=文件浏览，永远存在）。</summary>
    public ObservableCollection<EditorTabViewModel> EditorTabs { get; } = new();

    /// <summary>当前是否显示文件浏览主Tab（false = 显示某个编辑标签）。</summary>
    [ObservableProperty] private bool _isBrowserTab = true;

    /// <summary>是否存在编辑标签（决定是否显示内部标签条）。</summary>
    public bool HasEditorTabs => EditorTabs.Count > 0;

    /// <summary>是否存在未保存的编辑内容（断开连接前提醒用）。</summary>
    public bool HasUnsavedTabs
    {
        get
        {
            foreach (var t in EditorTabs)
                if (t.IsModified) return true;
            return false;
        }
    }

    /// <summary>视图订阅：弹出“选择下载位置”对话框。</summary>
    public event Action<IReadOnlyList<FileEntryViewModel>>? RequestDownloadDialog;

    public FileManagerViewModel(SshSession session, TransferService transfers, MainWindowViewModel owner)
    {
        _session = session;
        _transfers = transfers;
        _owner = owner;
        EditorTabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEditorTabs));
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try { _home = await _session.GetHomeAsync(); }
        catch { _home = "/"; }
        await NavigateAsync(_home!);
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = (FilterText ?? "").Trim();
        Entries.Clear();
        foreach (var e in All)
        {
            if (!ShowHidden && e.Name.StartsWith('.')) continue;
            if (f.Length > 0 && !e.Name.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            Entries.Add(e);
        }
    }

    public void SetSelected(IReadOnlyList<FileEntryViewModel> items)
    {
        Selected = items.ToList();
        InfoText = Selected.Count > 0 ? $"已选 {Selected.Count} 项" : $"{All.Count} 项";
    }

    // ------------------------------------------------------------------ 导航

    private async Task NavigateAsync(string path)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorText = "";
        try
        {
            // SFTP 优先；权限不足且允许时自动 sudo find 降级（需要密码则弹窗后重试）
            DirListing listing;
            try
            {
                listing = await _session.ListDirectoryExAsync(path, AutoSudo);
            }
            catch (SudoPasswordRequiredException)
            {
                var pw = await PromptSudoPasswordAsync($"用户 {_session.Profile.Username} 无权访问 {path}，提权浏览需要提供 sudo 密码");
                if (pw == null)
                {
                    ErrorText = "已取消：未提供 sudo 密码，无法浏览该目录。";
                    return;
                }
                _session.SudoPassword = pw;
                listing = await _session.ListDirectoryExAsync(path, AutoSudo);
            }

            // 预期内失败（权限不足等）通过结果返回，这里只需显示错误文本，不再让异常穿透
            if (listing.Error != null)
            {
                ErrorText = listing.Error;
                return;
            }

            _actualPath = UnixPath.Normalize(path);
            PathText = _actualPath;
            IsSudoDir = listing.UsedSudo;
            All.Clear();
            foreach (var i in listing.Items) All.Add(new FileEntryViewModel(i, this));
            ApplyClipboardMarks();
            ApplyFilter();
            InfoText = $"{All.Count} 项";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>弹窗获取 sudo 密码；用户取消返回 null。</summary>
    private async Task<string?> PromptSudoPasswordAsync(string purpose)
    {
        var (ok, pw) = await Dialogs.InputAsync("sudo 密码", purpose + "：", "", password: true);
        return ok && pw.Length > 0 ? pw : null;
    }

    // ------------------------------------------------------------------ 复制 / 剪切 / 粘贴 / 跨目录移动

    private void ApplyClipboardMarks()
    {
        if (!_clipboardCut || _clipboard.Count == 0) return;
        var paths = _clipboard.Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var e in All)
            e.IsCut = paths.Contains(e.FullName);
    }

    public void CopyEntries(IReadOnlyList<FileEntryViewModel> items) => SetClipboard(items, cut: false);

    public void CutEntries(IReadOnlyList<FileEntryViewModel> items) => SetClipboard(items, cut: true);

    private void SetClipboard(IReadOnlyList<FileEntryViewModel> items, bool cut)
    {
        if (items.Count == 0) return;
        foreach (var e in All) e.IsCut = false;
        _clipboard.Clear();
        foreach (var e in items)
            _clipboard.Add(new FileTransferItem(e.FullName, e.Name, e.IsDirectory));
        _clipboardCut = cut;
        _clipboardSudo = IsSudoDir;

        if (cut)
        {
            var paths = _clipboard.Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var e in All)
                e.IsCut = paths.Contains(e.FullName);
        }
        OnPropertyChanged(nameof(CanPaste));
        _owner.SetStatus(cut
            ? $"已剪切 {items.Count} 项，可到目标目录粘贴"
            : $"已复制 {items.Count} 项，可到目标目录粘贴");
    }

    /// <summary>清除剪贴板与剪切标记（Esc / 新会话）。</summary>
    public void ClearClipboard()
    {
        if (_clipboard.Count == 0 && All.All(e => !e.IsCut)) return;
        ResetClipboard();
        _owner.SetStatus("已清空剪贴板");
    }

    private void ResetClipboard()
    {
        _clipboard.Clear();
        _clipboardCut = false;
        _clipboardSudo = false;
        foreach (var e in All) e.IsCut = false;
        OnPropertyChanged(nameof(CanPaste));
    }

    [RelayCommand]
    private async Task Paste()
    {
        if (_clipboard.Count > 0)
            await PasteIntoAsync(_actualPath);
    }

    public async Task PasteIntoAsync(string targetDir)
    {
        if (_clipboard.Count == 0) return;
        var items = _clipboard.ToList();
        var cut = _clipboardCut;
        var sourceSudo = _clipboardSudo;
        var result = await MoveCopyAsync(items, targetDir, copy: !cut, sourceSudo);

        if (!cut) return;
        if (result.Ok)
        {
            ResetClipboard();
            return;
        }
        // 部分失败/跳过：剪贴板仅保留未完成的项，便于重试
        var failed = result.FailedSources.ToHashSet(StringComparer.Ordinal);
        _clipboard.RemoveAll(c => !failed.Contains(c.FullName));
        OnPropertyChanged(nameof(CanPaste));
        ApplyClipboardMarks();
    }

    public sealed record MoveCopyResult(bool Ok, int Succeeded, IReadOnlyList<string> FailedSources, string? Error);

    /// <summary>
    /// 在远程目录间移动/复制（拖拽与剪贴板共用）。预期失败全部转成结果/错误文本，不向 UI 抛异常。
    /// </summary>
    public async Task<MoveCopyResult> MoveCopyAsync(
        IReadOnlyList<FileTransferItem> items, string targetDir, bool copy, bool sourceSudo)
    {
        static List<string> AllPaths(IReadOnlyList<FileTransferItem> list)
            => list.Select(i => i.FullName).ToList();

        if (items.Count == 0) return new(true, 0, Array.Empty<string>(), null);
        if (IsLoading)
        {
            ErrorText = "正在执行其他文件操作，请稍候。";
            return new(false, 0, AllPaths(items), "正在执行其他文件操作，请稍候。");
        }

        targetDir = UnixPath.Normalize(targetDir);
        IsLoading = true;
        var failed = new List<string>();
        var messages = new List<string>();
        var succeeded = 0;
        try
        {
            ErrorText = "";

            // 目标目录列表（冲突检测）
            var targetNames = new HashSet<string>(StringComparer.Ordinal);
            if (targetDir == _actualPath)
            {
                foreach (var e in All) targetNames.Add(e.Name);
            }
            else
            {
                DirListing listing;
                try
                {
                    listing = await _session.ListDirectoryExAsync(targetDir, AutoSudo || IsSudoDir);
                }
                catch (SudoPasswordRequiredException)
                {
                    var pw = await PromptSudoPasswordAsync($"需要 sudo 权限才能访问目标目录 {targetDir}");
                    if (pw == null)
                        return new(false, 0, AllPaths(items), "已取消：未提供 sudo 密码。");
                    _session.SudoPassword = pw;
                    listing = await _session.ListDirectoryExAsync(targetDir, true);
                }
                if (listing.Error != null)
                    return new(false, 0, AllPaths(items), listing.Error);
                foreach (var e in listing.Items) targetNames.Add(e.Name);
            }

            // 逐项校验 + 收集冲突
            var plan = new List<(FileTransferItem Item, string Src, string Dest, bool Overwrite)>();
            var conflicts = new List<FileTransferItem>();
            foreach (var item in items)
            {
                var src = UnixPath.Normalize(item.FullName);
                var dest = UnixPath.Combine(targetDir, item.Name);
                if (dest == src || (!copy && UnixPath.Parent(src) == targetDir))
                    continue; // 已在目标位置 / 同目录移动：无需操作
                if (item.IsDirectory &&
                    (targetDir == src || targetDir.StartsWith(src.TrimEnd('/') + "/", StringComparison.Ordinal)))
                {
                    failed.Add(item.FullName);
                    messages.Add($"{item.Name}：不能把文件夹放入自身或子目录");
                    continue;
                }
                var overwrite = targetNames.Contains(item.Name);
                plan.Add((item, src, dest, overwrite));
                if (overwrite) conflicts.Add(item);
            }

            // 冲突：覆盖 / 跳过 / 取消（一次应用于全部）
            if (conflicts.Count > 0)
            {
                var preview = string.Join("、", conflicts.Take(4).Select(c => $"“{c.Name}”"));
                if (conflicts.Count > 4) preview += $" 等共 {conflicts.Count} 项";
                var choice = await Dialogs.ChoiceAsync(
                    "目标已存在",
                    $"{preview} 在目标目录中已存在。\n覆盖将替换目标内容，此操作不可恢复。",
                    okText: "覆盖", discardText: "跳过");
                if (choice == 2)
                    return new(false, 0, AllPaths(items), "已取消。");
                if (choice == 1)
                {
                    var skip = conflicts.Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);
                    foreach (var c in conflicts)
                    {
                        failed.Add(c.FullName);
                        messages.Add($"{c.Name}：已跳过（目标已存在）");
                    }
                    plan.RemoveAll(p => skip.Contains(p.Item.FullName));
                }
            }

            // 执行
            var preferSudo = sourceSudo || IsSudoDir;
            foreach (var (item, src, dest, overwrite) in plan)
            {
                var op = copy ? "复制" : "移动";
                var baseCmd = copy
                    ? $"cp -a -- {UnixPath.ShellQuote(src)} {UnixPath.ShellQuote(targetDir)}/"
                    : $"mv -- {UnixPath.ShellQuote(src)} {UnixPath.ShellQuote(targetDir)}/";
                var cmd = overwrite
                    ? $"rm -rf -- {UnixPath.ShellQuote(dest)} && {baseCmd}"
                    : baseCmd;

                string? error;
                if (preferSudo)
                {
                    error = await RunSudoOnceAsync(cmd, op + "文件", TimeSpan.FromMinutes(10), shell: true);
                }
                else
                {
                    var r = await _session.RunAsync(cmd, TimeSpan.FromMinutes(10));
                    error = r.ExitCode == 0 ? null : (r.Output + " " + r.Error).Trim();

                    if (error != null && LooksLikePermissionError(error) &&
                        await Dialogs.ConfirmAsync("权限不足",
                            $"{item.Name} {op}失败：权限不足。\n是否使用 sudo 重试？",
                            danger: true, okText: "sudo 重试"))
                    {
                        // 复制可能留下半成品：sudo 重试前先清理目标再操作
                        var retry = $"rm -rf -- {UnixPath.ShellQuote(dest)} && {baseCmd}";
                        error = await RunSudoOnceAsync(retry, op + "文件", TimeSpan.FromMinutes(10), shell: true);
                    }
                }

                if (error == null) succeeded++;
                else
                {
                    failed.Add(item.FullName);
                    messages.Add($"{item.Name}：{FirstLine(error)}");
                }
            }

            if (messages.Count > 0) ErrorText = "部分操作未完成：" + string.Join("；", messages);
            if (succeeded > 0)
                _owner.SetStatus($"已{(copy ? "复制" : "移动")} {succeeded} 项到 {targetDir}");
            else if (messages.Count == 0)
                _owner.SetStatus("没有需要执行的项（已在目标目录）。");
        }
        catch (Exception ex)
        {
            messages.Add(ex.Message);
            foreach (var p in AllPaths(items))
                if (!failed.Contains(p)) failed.Add(p);
            ErrorText = "操作失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        await NavigateAsync(_actualPath);
        var ok = failed.Count == 0;
        return new(ok, succeeded, failed, messages.Count > 0 ? string.Join("；", messages) : null);
    }

    private static bool LooksLikePermissionError(string text)
        => text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || text.Contains("权限不足", StringComparison.Ordinal)
           || text.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string? text)
    {
        var line = (text ?? "").Trim();
        var i = line.IndexOf('\n');
        return i >= 0 ? line[..i].Trim() : line;
    }

    // ------------------------------------------------------------------ 解压压缩包

    public async Task ExtractAsync(FileEntryViewModel entry, bool toOwnFolder)
    {
        if (IsLoading) return;
        if (!ArchiveFormats.IsSupported(entry.Name))
        {
            ErrorText = "不支持的压缩格式。";
            return;
        }

        var targetDir = toOwnFolder
            ? UnixPath.Combine(_actualPath, ArchiveFormats.StripSuffix(entry.Name))
            : _actualPath;

        if (!await Dialogs.ConfirmAsync(
                "解压",
                $"将 “{entry.Name}” 解压到：\n{targetDir}\n\n同名文件可能被覆盖。",
                okText: "解压"))
            return;

        IsLoading = true;
        try
        {
            ErrorText = "";
            var extract = ArchiveFormats.BuildExtractCommand(entry.FullName, targetDir);
            if (extract.Length == 0)
            {
                ErrorText = "不支持的压缩格式。";
                return;
            }
            var cmd = toOwnFolder
                ? $"mkdir -p -- {UnixPath.ShellQuote(targetDir)} && {extract}"
                : extract;

            string? error;
            if (IsSudoDir)
            {
                error = await RunSudoOnceAsync(cmd, "解压文件", TimeSpan.FromMinutes(10), shell: true);
            }
            else
            {
                var r = await _session.RunAsync(cmd, TimeSpan.FromMinutes(10));
                error = r.ExitCode == 0 ? null : (r.Output + " " + r.Error).Trim();
                if (error != null && LooksLikePermissionError(error) &&
                    await Dialogs.ConfirmAsync("权限不足",
                        "解压需要更高权限，是否使用 sudo 重试？",
                        danger: true, okText: "sudo 重试"))
                {
                    error = await RunSudoOnceAsync(cmd, "解压文件", TimeSpan.FromMinutes(10), shell: true);
                }
            }

            if (error == null)
                _owner.SetStatus($"已解压到 {targetDir}");
            else
                ErrorText = "解压失败：" + DescribeExtractError(error);
        }
        catch (Exception ex)
        {
            ErrorText = "解压失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        await NavigateAsync(_actualPath);
    }

    private static string DescribeExtractError(string error)
    {
        if (error.Contains("__NO_ZIP_EXTRACTOR__", StringComparison.Ordinal)
            || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("command not found", StringComparison.OrdinalIgnoreCase))
        {
            return error +
                   "\n请安装对应工具：unzip（sudo apt install unzip）、p7zip-full、或 tar / xz-utils。";
        }
        return error;
    }

    // ------------------------------------------------------------------ 拖出到 Windows

    /// <summary>为一次内部拖拽创建拖出 staging（只有拖出窗口时才会真正下载）。</summary>
    public DragOutStaging CreateDragOutStaging(IReadOnlyList<FileEntryViewModel> items)
        => new(_session, _actualPath,
            items.Select(e => new FileTransferItem(e.FullName, e.Name, e.IsDirectory)).ToList(),
            IsSudoDir);

    /// <summary>拖出到 Windows 失败时显示原因（由拖拽结束后调用）。</summary>
    public void ReportDragOutError(string message)
    {
        ErrorText = "拖出到 Windows 失败：" + message;
        _owner.SetStatus("拖出到 Windows 失败：" + message, error: true);
    }

    [RelayCommand]
    private Task Refresh() => NavigateAsync(_actualPath);

    [RelayCommand]
    private Task GoUp() => NavigateAsync(UnixPath.Parent(_actualPath));

    [RelayCommand]
    private Task GoHome() => NavigateAsync(_home ?? "/");

    [RelayCommand]
    private async Task Go()
    {
        var p = UnixPath.Normalize(PathText);
        try
        {
            var st = await _session.StatAsync(p);
            if (st.Error != null) ErrorText = st.Error;
            else if (st.PermissionDenied) await NavigateAsync(p); // 无权限 stat（如 /root）：交给导航的 sudo 降级
            else if (st.Info == null) ErrorText = "路径不存在。";
            else if (st.Info.IsDirectory) await NavigateAsync(p);
            else ErrorText = "这是一个文件，不是目录。";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    // ------------------------------------------------------------------ 文件操作

    [RelayCommand]
    private async Task NewFolder()
    {
        var (ok, name) = await Dialogs.InputAsync("新建文件夹", $"在 {_actualPath} 下创建：", "");
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var err = await _session.MkdirAsync(UnixPath.Combine(_actualPath, name.Trim()));
            if (err != null)
            {
                ErrorText = err;
                return;
            }
            await NavigateAsync(_actualPath);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    [RelayCommand]
    private async Task NewFile()
    {
        var (ok, name) = await Dialogs.InputAsync("新建文件", $"在 {_actualPath} 下创建：", "");
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _session.TouchAsync(UnixPath.Combine(_actualPath, name.Trim()));
            await NavigateAsync(_actualPath);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    [RelayCommand]
    private Task DeleteSelected() => DeleteEntriesAsync(Selected);

    [RelayCommand]
    private Task RenameSelected() => Selected.Count > 0 ? RenameEntryAsync(Selected[0]) : Task.CompletedTask;

    /// <summary>工具栏“编辑”：编辑第一个选中项（也可双击文件）。</summary>
    [RelayCommand]
    private Task EditSelected() => Selected.Count > 0 ? OpenInEditorAsync(Selected[0]) : Task.CompletedTask;

    [RelayCommand]
    private Task PropertiesSelected() => Selected.Count > 0 ? ShowPropertiesAsync(Selected[0]) : Task.CompletedTask;

    public async Task DeleteEntriesAsync(IReadOnlyList<FileEntryViewModel> items)
    {
        if (items.Count == 0 || IsLoading) return;
        try
        {
            var msg = items.Count == 1
                ? (items[0].IsDirectory
                    ? $"删除文件夹 “{items[0].Name}” 及其中所有内容？\n此操作不可恢复。"
                    : $"删除 “{items[0].Name}”？")
                : $"删除选中的 {items.Count} 项？\n此操作不可恢复。";
            if (!await Dialogs.ConfirmAsync("删除确认", msg, danger: true, okText: "删除")) return;

            var errors = new List<string>();
            var needSudo = new List<FileEntryViewModel>();
            var deletedPaths = new List<string>();
            var okCount = 0;

            foreach (var i in items)
            {
                try
                {
                    var r = await _session.DeleteAsync(i.FullName, recursive: true, i.IsSymlink);
                    switch (r.Status)
                    {
                        case DeleteStatus.Ok:
                            okCount++;
                            deletedPaths.Add(i.FullName);
                            break;
                        case DeleteStatus.PermissionDenied:
                            needSudo.Add(i);
                            break;
                        default:
                            errors.Add($"{i.Name}：{r.Describe()}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{i.Name}：{ex.Message}");
                }
            }

            // 权限不足的项：询问是否用 sudo 强制删除
            if (needSudo.Count > 0)
            {
                var preview = string.Join("、", needSudo.Take(4).Select(x => $"“{x.Name}”"));
                if (needSudo.Count > 4) preview += $" 等共 {needSudo.Count} 项";
                var useSudo = await Dialogs.ConfirmAsync(
                    "权限不足",
                    $"{preview} 因权限不足无法直接删除。\n是否使用 sudo 强制删除？\n此操作同样不可恢复。",
                    danger: true, okText: "sudo 删除");
                if (useSudo)
                {
                    var sudoError = await DeleteViaSudoAsync(needSudo);
                    if (sudoError == null)
                    {
                        okCount += needSudo.Count;
                        deletedPaths.AddRange(needSudo.Select(x => x.FullName));
                    }
                    else errors.Add(sudoError);
                }
                else
                {
                    errors.Add($"已跳过 {needSudo.Count} 项权限不足的文件（未使用 sudo）");
                }
            }

            // 删除成功的文件：直接关掉对应编辑器标签（不询问）
            for (var i = EditorTabs.Count - 1; i >= 0; i--)
                if (deletedPaths.Contains(EditorTabs[i].Path))
                    RemoveTab(EditorTabs[i]);

            ErrorText = errors.Count > 0 ? "部分删除失败：" + string.Join("；", errors) : "";
            if (okCount > 0)
                _owner.SetStatus($"已删除 {okCount} 项" + (errors.Count > 0 ? $"，{errors.Count} 项未成功" : ""),
                    errors.Count > 0);
            await NavigateAsync(_actualPath);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>sudo 批量删除；成功返回 null，失败返回错误信息。</summary>
    private async Task<string?> DeleteViaSudoAsync(IReadOnlyList<FileEntryViewModel> items)
    {
        var paths = items.Select(x => x.FullName).ToList();
        try
        {
            return await RunSudoDeleteAsync(paths);
        }
        catch (SudoPasswordRequiredException)
        {
            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要提权删除这些文件，请输入其密码：",
                "", password: true);
            if (!ok || pw.Length == 0)
                return "已取消：未提供 sudo 密码。";
            _session.SudoPassword = pw;
            return await RunSudoDeleteAsync(paths);
        }
        catch (Exception ex)
        {
            return "sudo 删除失败：" + ex.Message;
        }
    }

    private async Task<string?> RunSudoDeleteAsync(List<string> paths)
    {
        var r = await _session.DeleteViaSudoAsync(paths);
        return r.ExitCode == 0
            ? null
            : "sudo 删除失败（退出码 " + r.ExitCode + "）：" + (r.Output + " " + r.Error).Trim();
    }

    public async Task RenameEntryAsync(FileEntryViewModel e)
    {
        var (ok, name) = await Dialogs.InputAsync("重命名", "新名称：", e.Name);
        if (!ok || string.IsNullOrWhiteSpace(name) || name == e.Name) return;
        var newPath = UnixPath.Combine(UnixPath.Parent(e.FullName), name.Trim());
        try
        {
            var err = await _session.RenameAsync(e.FullName, newPath);
            if (err != null)
            {
                ErrorText = err;
                return;
            }
            // 同步已打开的编辑器标签路径
            var openTab = EditorTabs.FirstOrDefault(t => t.Path == e.FullName);
            if (openTab != null) openTab.Path = newPath;
            await NavigateAsync(_actualPath);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    public async Task ShowPropertiesAsync(FileEntryViewModel e)
    {
        var rows = new List<DialogRow>
        {
            new("名称", e.Name),
            new("路径", e.FullName),
            new("类型", e.IsDirectory ? "目录" : e.IsSymlink ? "符号链接" : "文件"),
            new("大小", e.SizeText),
            new("权限", e.PermsText),
            new("修改时间", e.DateText),
        };
        await Dialogs.InfoAsync("属性", rows);
    }

    public async Task OpenEntryAsync(FileEntryViewModel e)
    {
        if (e.IsDirectory) await NavigateAsync(e.FullName);
        else await OpenInEditorAsync(e);
    }

    /// <summary>单个条目下载：交给视图弹保存目录对话框。</summary>
    public Task RequestEntryDownloadAsync(FileEntryViewModel e)
    {
        RequestDownloadDialog?.Invoke(new[] { e });
        return Task.CompletedTask;
    }

    /// <summary>工具栏“下载”：把当前选中项交给视图弹目录对话框。</summary>
    public void RaiseDownloadSelected()
    {
        if (Selected.Count > 0)
            RequestDownloadDialog?.Invoke(Selected);
    }

    // ------------------------------------------------------------------ 编辑器

    /// <summary>切回文件浏览主Tab。</summary>
    [RelayCommand]
    private void ShowBrowser()
    {
        IsBrowserTab = true;
        foreach (var t in EditorTabs) t.IsSelected = false;
    }

    /// <summary>点击内部编辑标签：切换到该编辑页。</summary>
    internal void ActivateTab(EditorTabViewModel tab)
    {
        IsBrowserTab = false;
        foreach (var t in EditorTabs) t.IsSelected = ReferenceEquals(t, tab);
    }

    /// <summary>供其他页面跳转：导航到指定目录。</summary>
    public Task NavigateToAsync(string path) => NavigateAsync(path);

    /// <summary>供其他页面跳转：定位并打开指定远程文件（如 compose 配置）。</summary>
    public async Task OpenRemotePathAsync(string path)
    {
        var full = UnixPath.Normalize(path);
        await NavigateAsync(UnixPath.Parent(full));

        var entry = All.FirstOrDefault(e => !e.IsDirectory && e.FullName == full);
        if (entry == null)
        {
            var st = await _session.StatAsync(full);
            if (st.Info is { IsDirectory: false })
                entry = new FileEntryViewModel(st.Info, this);
        }

        if (entry == null)
        {
            ErrorText = $"未找到文件：{full}";
            return;
        }

        await OpenInEditorAsync(entry);
    }

    /// <summary>双击/右键“编辑”：把文件读入编辑器标签页（二进制、非 UTF-8、超 2MB 拒绝）。</summary>
    public async Task OpenInEditorAsync(FileEntryViewModel e)
    {
        if (e.IsDirectory)
        {
            await NavigateAsync(e.FullName);
            return;
        }

        // 已打开则直接激活
        var exist = EditorTabs.FirstOrDefault(t => t.Path == e.FullName);
        if (exist != null)
        {
            ActivateTab(exist);
            return;
        }

        // 符号链接的列表大小恒为 0，需按目标实测（-L 跟随链接）
        var sizeBytes = e.SizeBytes;
        if (e.IsSymlink)
        {
            CommandResult target;
            try
            {
                target = await _session.RunAsync(
                    $"stat -Lc %s -- {UnixPath.ShellQuote(e.FullName)}", TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                ErrorText = $"读取 “{e.Name}” 失败：{ex.Message}";
                return;
            }
            if (target.ExitCode != 0 || !long.TryParse(target.Output.Trim(), out sizeBytes))
            {
                ErrorText = $"“{e.Name}”是无法访问的符号链接（目标不存在？）。";
                return;
            }
        }

        if (sizeBytes > MaxEditBytes)
        {
            ErrorText = $"“{e.Name}”超过 2MB，为避免界面卡顿，请下载到本地编辑。";
            return;
        }

        FileReadResult read;
        try
        {
            try
            {
                read = await _session.ReadFileBytesExAsync(e.FullName, AutoSudo);
            }
            catch (SudoPasswordRequiredException)
            {
                var pw = await PromptSudoPasswordAsync($"用户 {_session.Profile.Username} 无权读取 “{e.Name}”，提权打开需要提供 sudo 密码");
                if (pw == null)
                {
                    ErrorText = "已取消：未提供 sudo 密码，无法打开该文件。";
                    return;
                }
                _session.SudoPassword = pw;
                read = await _session.ReadFileBytesExAsync(e.FullName, AutoSudo);
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"读取 “{e.Name}” 失败：{ex.Message}";
            return;
        }

        if (read.Error != null)
        {
            ErrorText = $"读取 “{e.Name}” 失败：{read.Error}";
            return;
        }

        var bytes = read.Bytes;

        // BOM / 编码 / 二进制检测
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (!hasBom && bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            ErrorText = $"“{e.Name}”是 UTF-16 编码，暂不支持编辑，请用终端处理。";
            return;
        }
        var offset = hasBom ? 3 : 0;
        if (Array.IndexOf(bytes, (byte)0, offset) >= 0)
        {
            ErrorText = $"“{e.Name}”是二进制文件，无法作为文本编辑。";
            return;
        }

        string text;
        try
        {
            var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            text = strictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            ErrorText = $"“{e.Name}”不是有效的 UTF-8 文本（可能是 GBK 等其他编码），为避免乱码不做编辑。";
            return;
        }

        var useCrlf = text.Contains("\r\n");
        var endedWithNewline = text.EndsWith('\n');
        var tab = new EditorTabViewModel(this, e.FullName, text, hasBom, useCrlf, endedWithNewline,
            bytes.Length, e.Model.ModifiedUtc, read.UsedSudo);
        EditorTabs.Add(tab);
        ActivateTab(tab);
        ErrorText = "";
    }

    /// <summary>保存标签页（Ctrl+S / 保存按钮）。权限不足时可选 sudo 兜底（临时文件 + sudo cp）。</summary>
    public async Task SaveTabAsync(EditorTabViewModel tab)
    {
        if (tab.IsSaving) return;
        tab.IsSaving = true;
        try
        {
            var snapshot = tab.Text ?? "";
            var bytes = BuildSaveBytes(tab, snapshot);

            // 检测“打开后远端被外部修改”（sudo 读取的文件无权 stat 时跳过检测）
            var stat = await _session.StatAsync(tab.Path);
            var st = stat.Info;
            var statDenied = stat.PermissionDenied;
            if (!statDenied && st == null)
            {
                if (!await Dialogs.ConfirmAsync(
                        "文件已不存在",
                        $"“{tab.Name}”在服务器上已不存在（可能被删除或移动）。\n继续保存将重新创建该文件。",
                        okText: "重新创建并保存"))
                {
                    tab.StatusText = "已取消：远端文件已不存在。";
                    return;
                }
            }
            else if (st != null && (st.Length != tab.SavedSize || st.ModifiedUtc != tab.SavedMtimeUtc))
            {
                if (!await Dialogs.ConfirmAsync(
                        "文件已被修改",
                        $"“{tab.Name}”在打开后于服务器上发生了变化（可能被其他程序修改）。\n是否覆盖保存？",
                        danger: true, okText: "覆盖保存"))
                {
                    tab.StatusText = "已取消：远端文件已变化，未覆盖。";
                    return;
                }
            }

            var r = await _session.WriteFileBytesAsync(tab.Path, bytes);
            switch (r.Status)
            {
                case WriteStatus.Ok:
                    await MarkTabSavedAsync(tab, snapshot, bytes.Length);
                    _owner.SetStatus($"已保存 {tab.Path}");
                    break;
                case WriteStatus.PermissionDenied:
                    await SaveTabViaSudoAsync(tab, snapshot, bytes);
                    break;
                default:
                    tab.StatusText = r.Describe();
                    break;
            }
        }
        catch (SessionDisconnectedException ex)
        {
            tab.StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            tab.StatusText = "保存失败：" + ex.Message;
        }
        finally
        {
            tab.IsSaving = false;
        }
    }

    /// <summary>把编辑器内容按原文件特征（BOM / 换行符 / 结尾换行）序列化为字节数组。</summary>
    private static byte[] BuildSaveBytes(EditorTabViewModel tab, string text)
    {
        var norm = (text ?? "").Replace("\r\n", "\n");
        if (tab.UseCrlf)
            norm = norm.Replace("\n", "\r\n");
        if (tab.EndedWithNewline && norm.Length > 0 && !norm.EndsWith(tab.UseCrlf ? "\r\n" : "\n", StringComparison.Ordinal))
            norm += tab.UseCrlf ? "\r\n" : "\n";

        var body = Encoding.UTF8.GetBytes(norm);
        if (!tab.HasBom) return body;
        var withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        body.CopyTo(withBom, 3);
        return withBom;
    }

    /// <summary>保存成功后回写标签页状态（重新 stat 拿最新 mtime/大小）。</summary>
    private async Task MarkTabSavedAsync(EditorTabViewModel tab, string snapshot, int bytesLength)
    {
        RemoteFileInfo? st = null;
        try { st = (await _session.StatAsync(tab.Path)).Info; }
        catch { /* 不影响保存结果 */ }
        tab.MarkSaved(snapshot, st?.Length ?? bytesLength, st?.ModifiedUtc ?? DateTime.UtcNow);
    }

    /// <summary>sudo 兜底保存：先 SFTP 写 /tmp 临时文件，再 sudo cp 覆盖（保留原属主与权限）。</summary>
    private async Task SaveTabViaSudoAsync(EditorTabViewModel tab, string snapshot, byte[] bytes)
    {
        var ok = await Dialogs.ConfirmAsync(
            "权限不足",
            $"“{tab.Name}”需要管理员权限才能写入。\n是否使用 sudo 保存？（内容不变，保留原属主与权限）",
            danger: true, okText: "sudo 保存");
        if (!ok)
        {
            tab.StatusText = "已取消：权限不足，未保存。";
            return;
        }

        var temp = "/tmp/.sshmanager-edit-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var w = await _session.WriteFileBytesAsync(temp, bytes);
            if (w.Status != WriteStatus.Ok)
            {
                tab.StatusText = "sudo 保存失败（写临时文件失败）：" + w.Describe();
                return;
            }
            var cp = await RunSudoOnceAsync(
                $"cp -- {UnixPath.ShellQuote(temp)} {UnixPath.ShellQuote(tab.Path)}",
                "提权保存文件");
            if (cp != null)
            {
                tab.StatusText = "sudo 保存失败：" + cp;
                return;
            }
            await MarkTabSavedAsync(tab, snapshot, bytes.Length);
            _owner.SetStatus($"已用 sudo 保存 {tab.Path}");
        }
        finally
        {
            try { await _session.RunAsync("rm -f -- " + UnixPath.ShellQuote(temp), TimeSpan.FromSeconds(10)); }
            catch { /* 临时文件清理失败可忽略 */ }
        }
    }

    /// <summary>sudo 执行；未保存密码时弹窗补齐后重试。成功返回 null，失败返回错误信息。</summary>
    /// <param name="shell">复合命令（含 &amp;&amp; / if）必须为 true，经 sh -c 整体提权。</param>
    private async Task<string?> RunSudoOnceAsync(string command, string purpose,
        TimeSpan? timeout = null, bool shell = false)
    {
        try
        {
            return await RunSudoRawAsync(command, timeout, shell);
        }
        catch (SudoPasswordRequiredException)
        {
            var (ok, pw) = await Dialogs.InputAsync(
                "sudo 密码",
                $"用户 {_session.Profile.Username} 需要{purpose}，请输入其密码：",
                "", password: true);
            if (!ok || pw.Length == 0)
                return "已取消：未提供 sudo 密码。";
            _session.SudoPassword = pw;
            return await RunSudoRawAsync(command, timeout, shell);
        }
    }

    private async Task<string?> RunSudoRawAsync(string command, TimeSpan? timeout = null, bool shell = false)
    {
        var wrapped = shell ? _session.WrapSudoShell(command) : _session.WrapSudo(command);
        var r = await _session.RunAsync(wrapped, timeout ?? TimeSpan.FromSeconds(60));
        return r.ExitCode == 0 ? null : (r.Output + " " + r.Error).Trim();
    }

    /// <summary>关闭标签页；有未保存修改时询问（保存 / 不保存 / 取消）。返回是否真正关闭。</summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        if (tab.IsModified)
        {
            var choice = await Dialogs.ChoiceAsync(
                "未保存的修改",
                $"“{tab.Name}”有未保存的修改。",
                okText: "保存并关闭", discardText: "不保存");
            if (choice == 2) return false; // 取消
            if (choice == 0)
            {
                await SaveTabAsync(tab);
                if (tab.IsModified) return false; // 保存失败（错误显示在标签页状态栏），不关闭
            }
        }
        RemoveTab(tab);
        return true;
    }

    private void RemoveTab(EditorTabViewModel tab)
    {
        var wasCurrent = tab.IsSelected;
        EditorTabs.Remove(tab);
        // 关闭的是当前显示页 → 回到文件浏览主Tab
        if (wasCurrent) ShowBrowser();
    }

    /// <summary>关闭全部编辑标签页（用户取消某个后停止后续关闭）。</summary>
    public async Task CloseAllTabs()
    {
        for (var i = EditorTabs.Count - 1; i >= 0; i--)
            if (!await CloseTabAsync(EditorTabs[i]))
                break; // 用户取消后停止后续关闭
    }

    // ------------------------------------------------------------------ 传输

    /// <summary>上传本地文件/目录（视图选择后回调）；targetDir 为空时上传到当前目录。</summary>
    public void UploadLocalPaths(IEnumerable<string> paths) => UploadLocalPaths(paths, null);

    public void UploadLocalPaths(IEnumerable<string> paths, string? targetDir)
    {
        var remoteDir = string.IsNullOrWhiteSpace(targetDir) ? _actualPath : UnixPath.Normalize(targetDir);
        var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (list.Count == 0) return;
        foreach (var p in list) _transfers.EnqueueUpload(p, remoteDir);
        _owner.TransfersOpen = true;
        _owner.SetStatus($"已加入上传队列（{list.Count} 项）→ {remoteDir}");
    }

    /// <summary>下载到指定本地目录（视图选择后回调）。</summary>
    public void DownloadEntriesTo(IReadOnlyList<FileEntryViewModel> items, string localDir)
    {
        if (items.Count == 0) return;
        foreach (var i in items)
            _ = _transfers.EnqueueDownloadAsync(i.FullName, localDir);
        _owner.TransfersOpen = true;
        _owner.SetStatus($"已加入下载队列（{items.Count} 项）");
    }
}
