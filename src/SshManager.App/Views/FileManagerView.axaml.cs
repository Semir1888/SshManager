using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SshManager.App.ViewModels;

namespace SshManager.App.Views;

public partial class FileManagerView : UserControl
{
    private FileManagerViewModel? _hookedVm;

    private FileManagerViewModel? Vm => DataContext as FileManagerViewModel;

    public FileManagerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void HookViewModel()
    {
        if (_hookedVm != null)
            _hookedVm.RequestDownloadDialog -= OnRequestDownload;
        _hookedVm = Vm;
        if (_hookedVm != null)
            _hookedVm.RequestDownloadDialog += OnRequestDownload;
    }

    // ------------------------------------------------------------ 键盘 / 选择

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Vm?.SetSelected(FilesGrid.SelectedItems.OfType<FileEntryViewModel>().ToList());
    }

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (Vm != null && FilesGrid.SelectedItem is FileEntryViewModel entry)
                await Vm.OpenEntryAsync(entry);
        }
        catch { /* 打开/下载失败已在 VM 内提示 */ }
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

    // ------------------------------------------------------------ 拖放上传

#pragma warning disable CS0618 // DataFormats.Files 在 11.x 仍可用
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).ToList();
        if (paths is { Count: > 0 })
        {
            Vm?.UploadLocalPaths(paths);
            e.Handled = true;
        }
    }
#pragma warning restore CS0618
}
