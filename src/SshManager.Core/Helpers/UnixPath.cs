using System.Text;

namespace SshManager.Core.Helpers;

/// <summary>远程路径与格式化工具。</summary>
public static class UnixPath
{
    public static string Combine(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Normalize(dir);
        var d = (dir ?? "").TrimEnd('/');
        return string.IsNullOrEmpty(d) ? "/" + name.TrimStart('/') : d + "/" + name.TrimStart('/');
    }

    public static string Parent(string path)
    {
        var t = (path ?? "/").TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i <= 0 ? "/" : t[..i];
    }

    public static string FileName(string path)
    {
        var t = (path ?? "").TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i < 0 ? t : t[(i + 1)..];
    }

    public static string Normalize(string path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0) return "/";
        p = "/" + p.Trim('/');
        return p.Length == 0 ? "/" : p;
    }

    /// <summary>根据权限布尔组生成 rwx 字符串（SSH.NET 新版 API 不再暴露原始 mode）。</summary>
    public static string PermissionsString(
        bool isDirectory, bool isSymbolicLink,
        bool userRead, bool userWrite, bool userExec,
        bool groupRead, bool groupWrite, bool groupExec,
        bool otherRead, bool otherWrite, bool otherExec)
    {
        var t = isSymbolicLink ? 'l' : isDirectory ? 'd' : '-';
        char B(bool b, char c) => b ? c : '-';
        return new StringBuilder(10)
            .Append(t)
            .Append(B(userRead, 'r')).Append(B(userWrite, 'w')).Append(B(userExec, 'x'))
            .Append(B(groupRead, 'r')).Append(B(groupWrite, 'w')).Append(B(groupExec, 'x'))
            .Append(B(otherRead, 'r')).Append(B(otherWrite, 'w')).Append(B(otherExec, 'x'))
            .ToString();
    }

    /// <summary>由 find -printf %m 的八进制 mode（如 644）生成 rwx 字符串。</summary>
    public static string PermissionsFromMode(char typeChar, int mode)
    {
        var t = typeChar switch { 'd' => 'd', 'l' => 'l', _ => '-' };
        mode &= 0x1FF; // 0777
        char B(int bit, char c) => (mode & bit) != 0 ? c : '-';
        return new StringBuilder(10)
            .Append(t)
            .Append(B(0x100, 'r')).Append(B(0x80, 'w')).Append(B(0x40, 'x'))
            .Append(B(0x20, 'r')).Append(B(0x10, 'w')).Append(B(0x08, 'x'))
            .Append(B(0x04, 'r')).Append(B(0x02, 'w')).Append(B(0x01, 'x'))
            .ToString();
    }

    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string HumanSize(double bytes)
    {
        if (bytes < 0) return "—";
        var u = 0;
        while (bytes >= 1024 && u < SizeUnits.Length - 1) { bytes /= 1024; u++; }
        return u == 0 ? $"{bytes:0} B" : $"{bytes:0.#} {SizeUnits[u]}";
    }

    /// <summary>shell 单引号转义，用于把字符串安全地包进 '...'。</summary>
    public static string ShellQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";
}
