using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshManager.Core.Helpers;

namespace SshManager.App.ViewModels;

/// <summary>
/// 文件编辑器的一个标签页：远端文件内容 + 修改状态 + 行列统计。
/// 编码 / 换行 / BOM / 结尾换行等原文件特征在打开时锁定，保存时还原。
/// </summary>
public partial class EditorTabViewModel : ViewModelBase
{
    private readonly FileManagerViewModel _parent;
    private string _originalText = "";

    /// <summary>保存成功时记录的远端大小/mtime，用于检测“打开后被外部修改”。</summary>
    private long _savedSize;
    private DateTime _savedMtimeUtc = DateTime.MinValue;

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private string _caretText = "行 1，列 1";
    [ObservableProperty] private bool _useWrap;
    /// <summary>是否以 sudo 提权读取打开（无权限文件自动降级时为 true）。</summary>
    [ObservableProperty] private bool _wasReadWithSudo;

    /// <summary>是否为当前激活的顶部标签（由 MainWindowViewModel 维护）。</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>是否带 UTF-8 BOM（保存时还原）。</summary>
    internal bool HasBom { get; }

    /// <summary>原文件是否使用 CRLF（保存时还原；LF 文件统一保存为 LF）。</summary>
    internal bool UseCrlf { get; }

    /// <summary>原文件是否以换行结尾（保存时还原）。</summary>
    internal bool EndedWithNewline { get; }

    internal long SavedSize => _savedSize;
    internal DateTime SavedMtimeUtc => _savedMtimeUtc;

    public string Name => UnixPath.FileName(Path);
    public string Header => (IsModified ? "● " : "") + Name;
    public string EncodingText { get; }
    public string NewlineText { get; }

    internal EditorTabViewModel(
        FileManagerViewModel parent, string path, string text,
        bool hasBom, bool useCrlf, bool endedWithNewline,
        long sizeBytes, DateTime mtimeUtc, bool wasReadWithSudo = false)
    {
        _parent = parent;
        HasBom = hasBom;
        UseCrlf = useCrlf;
        EndedWithNewline = endedWithNewline;
        EncodingText = hasBom ? "UTF-8 (BOM)" : "UTF-8";
        NewlineText = useCrlf ? "CRLF" : "LF";

        _savedSize = sizeBytes;
        _savedMtimeUtc = mtimeUtc;
        Path = path;
        _originalText = text;
        WasReadWithSudo = wasReadWithSudo;
        Text = text; // 触发统计与 IsModified=false
    }

    partial void OnTextChanged(string value)
    {
        IsModified = value != _originalText;
        UpdateStats();
    }

    partial void OnIsModifiedChanged(bool value) => OnPropertyChanged(nameof(Header));

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Header));
    }

    // ---------------------------------------------------------------- 命令

    /// <summary>点击顶部标签：激活该编辑页。</summary>
    [RelayCommand]
    private void Activate() => _parent.ActivateTab(this);

    /// <summary>保存（Ctrl+S 同效）。</summary>
    [RelayCommand]
    private Task Save() => _parent.SaveTabAsync(this);

    /// <summary>放弃全部修改，恢复到上次保存的内容。</summary>
    [RelayCommand]
    private void Discard()
    {
        Text = _originalText;
        StatusText = "";
    }

    /// <summary>关闭标签页（有未保存修改时先询问）。</summary>
    [RelayCommand]
    private Task Close() => _parent.CloseTabAsync(this);

    /// <summary>关闭全部编辑标签页。</summary>
    [RelayCommand]
    private Task CloseAll() => _parent.CloseAllTabs();

    // ---------------------------------------------------------------- 状态更新

    /// <summary>编辑框光标变化时更新行列显示（EditorTextBox 调用）。</summary>
    internal void UpdateCaret(int index)
    {
        var t = Text ?? "";
        if (index < 0) index = 0;
        if (index > t.Length) index = t.Length;
        var line = 1;
        var col = 1;
        for (var i = 0; i < index; i++)
        {
            if (t[i] == '\n') { line++; col = 1; }
            else col++;
        }
        CaretText = $"行 {line}，列 {col}";
    }

    /// <summary>保存成功后由 FileManagerViewModel 调用。</summary>
    internal void MarkSaved(string savedSnapshot, long sizeBytes, DateTime mtimeUtc)
    {
        _originalText = savedSnapshot;
        _savedSize = sizeBytes;
        _savedMtimeUtc = mtimeUtc;
        IsModified = (Text ?? "") != savedSnapshot;
        StatusText = "";
        UpdateStats();
    }

    private void UpdateStats()
    {
        var t = Text ?? "";
        var lines = 1;
        foreach (var ch in t)
            if (ch == '\n') lines++;
        var bytes = Encoding.UTF8.GetByteCount(t);
        StatsText = $"{lines} 行 · {t.Length} 字符 · {UnixPath.HumanSize(bytes)} · {EncodingText} · {NewlineText}";
    }
}
