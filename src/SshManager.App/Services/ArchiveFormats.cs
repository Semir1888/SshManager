using SshManager.Core.Helpers;

namespace SshManager.App.Services;

/// <summary>压缩包格式识别与服务器端解压命令构建（tar / unzip / 7z）。</summary>
public static class ArchiveFormats
{
    private static readonly string[] Suffixes =
    {
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz", ".tar", ".zip", ".7z",
    };

    public static bool IsSupported(string name)
        => Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>去掉压缩后缀，得到“同名文件夹”名称。</summary>
    public static string StripSuffix(string name)
    {
        foreach (var s in Suffixes)
        {
            if (!name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = name[..^s.Length].Trim();
            return baseName.Length > 0 ? baseName : "extracted";
        }
        return name;
    }

    /// <summary>返回解压命令（不包含 mkdir / sudo 包装）；不支持的格式返回空串。</summary>
    public static string BuildExtractCommand(string archivePath, string targetDir)
    {
        var qa = UnixPath.ShellQuote(archivePath);
        var qt = UnixPath.ShellQuote(targetDir);
        var lower = archivePath.ToLowerInvariant();

        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
            return $"tar -xzf {qa} -C {qt}";
        if (lower.EndsWith(".tar.bz2") || lower.EndsWith(".tbz2"))
            return $"tar -xjf {qa} -C {qt}";
        if (lower.EndsWith(".tar.xz") || lower.EndsWith(".txz"))
            return $"tar -xJf {qa} -C {qt}";
        if (lower.EndsWith(".tar"))
            return $"tar -xf {qa} -C {qt}";
        if (lower.EndsWith(".zip"))
            return "if command -v unzip >/dev/null 2>&1; then unzip -o -q " + qa + " -d " + qt +
                   "; elif command -v 7z >/dev/null 2>&1; then 7z x -y -o" + qt + " " + qa +
                   "; else echo __NO_ZIP_EXTRACTOR__; exit 127; fi";
        if (lower.EndsWith(".7z"))
            return "7z x -y -o" + qt + " " + qa;
        return "";
    }
}
