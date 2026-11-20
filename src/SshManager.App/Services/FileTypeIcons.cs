using Avalonia;
using Avalonia.Media;
using SshManager.Core.Models;

namespace SshManager.App.Services;

/// <summary>根据远程文件类型挑选图标与颜色。</summary>
public static class FileTypeIcons
{
    // 与 Assets/Icons.axaml 中的同款图形保持一致（代码端直接解析，避免资源查找 API 差异）
    private const string PathFolderIcon = "M3 7c0-1.1.9-2 2-2h4.2c.5 0 1 .2 1.4.6L12 7h7c1.1 0 2 .9 2 2v8c0 1.1-.9 2-2 2H5c-1.1 0-2-.9-2-2V7z";
    private const string PathFileIcon = "M7 3h7l4.5 4.5V20c0 .6-.4 1-1 1H7c-.6 0-1-.4-1-1V4c0-.6.4-1 1-1z M14 3v5h4.5";
    private const string PathLinkIcon = "M9 15l6-6 M7 17a4.5 4.5 0 0 1 0-6.4l1.8-1.8 M17 7a4.5 4.5 0 0 1 0 6.4l-1.8 1.8";
    private const string PathImageIcon = "M4 5h16v14H4z M8.5 10a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z M4 16l5-4 3 2.5 3.5-3L20 15";
    private const string PathArchiveIcon = "M6 3h12v18H6z M12 3v6 M12 12h.01 M12 15.5h.01";
    private const string PathBoltIcon = "M13 2L4.5 13.5H11L9.5 22 19 10h-6.5L13 2z";

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    private static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#eab308"));
    private static readonly IBrush FileBrush = new SolidColorBrush(Color.Parse("#8b93a5"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#2dd4bf"));
    private static readonly IBrush ImageBrush = new SolidColorBrush(Color.Parse("#34d399"));
    private static readonly IBrush ArchiveBrush = new SolidColorBrush(Color.Parse("#fb923c"));
    private static readonly IBrush ExecBrush = new SolidColorBrush(Color.Parse("#f472b6"));

    public static (Geometry Icon, IBrush Brush) Resolve(RemoteFileInfo f)
    {
        if (f.IsSymbolicLink) return (Geom(PathLinkIcon), LinkBrush);
        if (f.IsDirectory) return (Geom(PathFolderIcon), FolderBrush);

        var ext = System.IO.Path.GetExtension(f.Name).ToLowerInvariant();
        switch (ext)
        {
            case ".png": case ".jpg": case ".jpeg": case ".gif": case ".svg":
            case ".webp": case ".bmp": case ".ico": case ".heic":
                return (Geom(PathImageIcon), ImageBrush);
            case ".zip": case ".tar": case ".gz": case ".bz2": case ".xz":
            case ".7z": case ".rar": case ".tgz":
                return (Geom(PathArchiveIcon), ArchiveBrush);
        }

        if (f.Permissions.Length == 10 &&
            (f.Permissions[3] == 'x' || f.Permissions[6] == 'x' || f.Permissions[9] == 'x'))
            return (Geom(PathBoltIcon), ExecBrush);

        return (Geom(PathFileIcon), FileBrush);
    }

    private static Geometry Geom(string path)
    {
        if (Cache.TryGetValue(path, out var g)) return g;
        g = Geometry.Parse(path);
        Cache[path] = g;
        return g;
    }
}
