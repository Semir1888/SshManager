using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SshManager.App.Services;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class FileManagerView : UserControl
{
    /// <summary>
    /// 内部远程条目的拖拽格式（JSON 载荷）。
    /// 用 bytes 格式：Windows OLE 在 drop 侧会把未知格式还原成 byte[] 应用格式，
    /// string 应用格式在往返后类型不一致，内部拖拽会识别不到。
    /// </summary>
    private static readonly DataFormat<byte[]> RemoteFilesFormat =
        DataFormat.CreateBytesApplicationFormat("sshmanager.remote-files");

    private FileManagerViewModel? _hookedVm;

    private FileManagerViewModel? Vm => DataContext as FileManagerViewModel;

    // 内部拖拽候选 / 拖出 staging
    private Point _dragStart;
    private FileEntryViewModel? _dragEntry;
    private FileManagerViewModel? _dragSourceVm;
    private DragOutStaging? _dragStaging;
    private FileEntryViewModel? _dropRow;

    public FileManagerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 空白区域右键菜单不在可视树中，显式把 DataContext 指到当前 VM
        if (FilesGrid.ContextMenu != null)
        {
            FilesGrid.ContextMenu.Opening += (_, _) =>
                FilesGrid.ContextMenu.DataContext = Vm;
        }

        // Tunnel：先于 DataGrid 自身的选取处理，记录拖拽候选
        FilesGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        FilesGrid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);
        // Tunnel：复制/剪切/粘贴优先于 DataGrid 自身的单元格复制
        FilesGrid.AddHandler(KeyDownEvent, OnGridShortcutKeyDown, RoutingStrategies.Tunnel);
    }

    private void HookViewModel()
    {
        if (_hookedVm != null)
            _hookedVm.RequestDownloadDialog -= OnRequestDownload;
        _hookedVm = Vm;
        if (_hookedVm != null)
            _hookedVm.RequestDownloadDialog += OnRequestDownload;
        if (FilesGrid.ContextMenu != null)
            FilesGrid.ContextMenu.DataContext = Vm;
    }

    // ------------------------------------------------------------ 键盘 / 选择

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Vm?.SetSelected(FilesGrid.SelectedItems.OfType<FileEntryViewModel>().ToList());
    }

    private List<FileEntryViewModel> SelectedEntries()
        => FilesGrid.SelectedItems.OfType<FileEntryViewModel>().ToList();

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (Vm != null && FilesGrid.SelectedItem is FileEntryViewModel entry)
                await Vm.OpenEntryAsync(entry);
        }
        catch { /* 打开/下载失败已在 VM 内提示 */ }
    }

    private void OnGridShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.C:
                    Vm.CopyEntries(SelectedEntries());
                    e.Handled = true;
                    return;
                case Key.X:
                    Vm.CutEntries(SelectedEntries());
                    e.Handled = true;
                    return;
                case Key.V:
                    Vm.PasteCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }
        if (e.Key == Key.Escape)
        {
            Vm.ClearClipboard();
            e.Handled = true;
        }
    }

    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        try
        {
            switch (e.Key)
            {
                case Key.Delete when FilesGrid.SelectedItems.Count > 0:
                    await Vm.DeleteEntriesAsync(FilesGrid.SelectedItems.OfType<FileEntryViewModel>().ToList());
                    e.Handled = true;
                    break;
                case Key.F5:
                    Vm.RefreshCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Back when !e.Handled:
                    Vm.GoUpCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
        catch { /* 异常已在 VM 内处理 */ }
    }

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm != null)
        {
            Vm.GoCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnToggleHidden(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.ShowHidden = !Vm.ShowHidden;
        HiddenText.Text = Vm.ShowHidden ? "隐藏隐藏" : "显示隐藏";
    }

    // ------------------------------------------------------------ 选择器

    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    private async void OnUploadFiles(object? sender, RoutedEventArgs e)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = true,
        });
        if (files.Count > 0)
            Vm?.UploadLocalPaths(files.Select(f => f.Path.LocalPath).ToList());
    }

    private async void OnUploadFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要上传的文件夹（递归上传）",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            Vm?.UploadLocalPaths(new[] { folders[0].Path.LocalPath });
    }

    private void OnDownload(object? sender, RoutedEventArgs e) => Vm?.RaiseDownloadSelected();

    private async void OnRequestDownload(IReadOnlyList<FileEntryViewModel> items)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"选择保存位置（{items.Count} 项）",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            Vm?.DownloadEntriesTo(items, folders[0].Path.LocalPath);
    }

    // ------------------------------------------------------------ 内部拖拽（移动 / 复制 / 拖出到 Windows）

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FilesGrid).Properties.IsLeftButtonPressed)
        {
            _dragEntry = null;
            return;
        }
        _dragStart = e.GetPosition(FilesGrid);
        _dragEntry = FindRowEntry(e.Source);
    }

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragEntry == null || Vm == null) return;
        if (!e.GetCurrentPoint(FilesGrid).Properties.IsLeftButtonPressed)
        {
            _dragEntry = null;
            return;
        }

        var p = e.GetPosition(FilesGrid);
        if (Math.Abs(p.X - _dragStart.X) < 5 && Math.Abs(p.Y - _dragStart.Y) < 5) return;

        var candidate = _dragEntry;
        _dragEntry = null;

        var selected = SelectedEntries();
        var items = selected.Contains(candidate) ? selected : new List<FileEntryViewModel> { candidate };
        if (items.Count == 0) return;

        DragOutStaging? staging = null;
        try
        {
            var payload = new RemoteDragPayload(
                items.Select(i => new FileTransferItem(i.FullName, i.Name, i.IsDirectory)).ToList(),
                Vm.IsSudoDir);
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(RemoteFilesFormat, json));

            // 拖出到 Windows：staging 先创建本地占位，再用平台 StorageProvider 预解析 IStorageItem。
            // 真正下载只在拖出窗口后开始；drop 时 getter 会等待下载完成。
            staging = Vm.CreateDragOutStaging(items);
            _dragStaging = staging;
            _dragSourceVm = Vm;

            var localItems = new IStorageItem?[items.Count];
            var canDragOut = true;
            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var path = staging.GetStagedPath(i);
                    localItems[i] = items[i].IsDirectory
                        ? await Storage.TryGetFolderFromPathAsync(path)
                        : await Storage.TryGetFileFromPathAsync(path);
                    if (localItems[i] == null)
                    {
                        canDragOut = false;
                        break;
                    }
                }
            }
            catch { canDragOut = false; }

            if (canDragOut)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var index = i;
                    transfer.Add(DataTransferItem.Create(DataFormat.File, () =>
                    {
                        // 失败时返回 null：Avalonia 会跳过该项，不会把空/不存在的路径交给资源管理器
                        var ok = staging.WaitForStaging();
                        return ok ? localItems[index] : null!;
                    }));
                }
            }

            e.Handled = true;
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch { /* 拖拽取消/平台不支持时忽略 */ }
        finally
        {
            SetDropHighlight(null);
            if (staging?.Error is { } err) Vm?.ReportDragOutError(err);
            _dragStaging = null;
            _dragSourceVm = null;
        }
    }

    private static FileEntryViewModel? FindRowEntry(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is DataGridRow row)
                return row.DataContext as FileEntryViewModel;
        return null;
    }

    private readonly record struct DropTarget(string Dir, FileEntryViewModel? Row);

    private DropTarget? FindDropTarget(DragEventArgs e)
    {
        if (Vm == null) return null;
        var p = e.GetPosition(FilesGrid);
        if (p.X < 0 || p.Y < 0 || p.X > FilesGrid.Bounds.Width || p.Y > FilesGrid.Bounds.Height)
            return null;

        // 个别平台把 drag 事件的 Source 设为 UserControl/DataGrid，这里用坐标再做一次命中测试
        var row = FindRowEntry(e.Source);
        if (row == null)
        {
            var hit = FilesGrid.GetVisualAt(p);
            if (hit != null) row = FindRowEntry(hit);
        }

        if (row is { IsDirectory: true })
            return new DropTarget(row.FullName, row);
        return new DropTarget(Vm.CurrentPath, null);
    }

    private void SetDropHighlight(FileEntryViewModel? row)
    {
        if (ReferenceEquals(_dropRow, row)) return;
        if (_dropRow != null) _dropRow.IsDropTarget = false;
        _dropRow = row;
        if (row != null) row.IsDropTarget = true;
    }

    private static RemoteDragPayload? ParsePayload(DragEventArgs e)
    {
        try
        {
            var raw = e.DataTransfer.TryGetValue(RemoteFilesFormat);
            if (raw is not { Length: > 0 }) return null;
            var json = Encoding.UTF8.GetString(raw);
            return JsonSerializer.Deserialize<RemoteDragPayload>(json);
        }
        catch { return null; }
    }

    private static bool CanDrop(RemoteDragPayload payload, string targetDir)
    {
        foreach (var item in payload.Items)
        {
            if (!item.IsDirectory) continue;
            var src = item.FullName.TrimEnd('/');
            if (targetDir == item.FullName
                || targetDir.StartsWith(src + "/", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------ 拖放（内部 + Windows 本地文件上传）

#pragma warning disable CS0618 // DataFormats.Files 在 11.x 仍可用
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(RemoteFilesFormat))
        {
            var payload = ParsePayload(e);
            var target = FindDropTarget(e);
            if (payload == null || target == null || !CanDrop(payload, target.Value.Dir))
            {
                SetDropHighlight(null);
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            SetDropHighlight(target.Value.Row);
            var copy = (e.KeyModifiers & KeyModifiers.Control) != 0;
            e.DragEffects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.Contains(DataFormats.Files))
        {
            var target = FindDropTarget(e);
            SetDropHighlight(target?.Row);
            e.DragEffects = target != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        SetDropHighlight(null);

        // 内部拖拽离开整个窗口：开始后台下载/解包（供拖出到 Windows 使用）
        if (_dragStaging == null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top != null)
        {
            var p = e.GetPosition(top);
            if (p.X >= 0 && p.Y >= 0 && p.X <= top.Bounds.Width && p.Y <= top.Bounds.Height)
                return; // 仍在程序窗口内
        }
        _dragStaging.Start();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        var vm = Vm;
        if (vm == null) return;
        var target = FindDropTarget(e);
        if (target == null) return;

        if (e.DataTransfer.Contains(RemoteFilesFormat))
        {
            var payload = ParsePayload(e);
            if (payload != null && ReferenceEquals(_dragSourceVm, vm))
            {
                var copy = (e.KeyModifiers & KeyModifiers.Control) != 0;
                e.Handled = true;
                await vm.MoveCopyAsync(payload.Items, target.Value.Dir, copy, payload.SourceSudo);
            }
            return;
        }

        var paths = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).ToList();
        if (paths is { Count: > 0 })
        {
            vm.UploadLocalPaths(paths, target.Value.Dir);
            e.Handled = true;
        }
    }
#pragma warning restore CS0618
}
