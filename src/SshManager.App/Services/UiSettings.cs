using System.Text.Json;

namespace SshManager.App.Services;

/// <summary>
/// 界面偏好持久化（%APPDATA%/SshManager/ui.json）：
/// 侧栏收起状态、主题等，启动时恢复上次的选择。
/// </summary>
public sealed class UiSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public bool SidebarCollapsed { get; set; }
    public bool DarkTheme { get; set; } = true;
    public int TransfersDrawerHeight { get; set; } = 240;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshManager");

    private static string FilePath => Path.Combine(Dir, "ui.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath));
                if (s != null) return s;
            }
        }
        catch { /* 损坏则使用默认值 */ }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 保存失败不影响使用 */ }
    }
}
