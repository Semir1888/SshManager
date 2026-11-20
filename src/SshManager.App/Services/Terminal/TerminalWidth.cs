namespace SshManager.App.Services.Terminal;

/// <summary>
/// East Asian Width（常用区间）判断：CJK 等全角字符在终端中占 2 列。
/// 覆盖 BMP 内的中文 / 日文 / 韩文及全角标点；代理对（emoji、CJK 扩展 B）暂不处理。
/// </summary>
public static class TerminalWidth
{
    /// <summary>字符占用的终端列数：1 或 2。</summary>
    public static int Of(char ch) => IsWide(ch) ? 2 : 1;

    public static bool IsWide(char ch) =>
        ch is >= 'ᄀ' and <= 'ᅟ'   // Hangul Jamo
            or >= '⺀' and <= '〾'  // CJK 部首 / 康熙字典 / CJK 标点
            or >= 'ぁ' and <= '㏿'  // 平假名 / 片假名 / 注音 / 韩文兼容 / CJK 兼容
            or >= '㐀' and <= '䶿'  // CJK 扩展 A
            or >= '一' and <= '鿿'  // CJK 统一表意文字
            or >= 'ꀀ' and <= '꓏'  // 彝文
            or >= 'ꥠ' and <= '꥿'  // Hangul Jamo 扩展 A
            or >= '가' and <= '힣'  // 韩文音节
            or >= '豈' and <= '﫿'  // CJK 兼容表意文字
            or >= '︐' and <= '︙'  // 竖排标点
            or >= '︰' and <= '﹯'  // CJK 兼容形式 / 小写变体
            or >= '＀' and <= '｠'  // 全角 ASCII / 全角标点
            or >= '￠' and <= '￦'; // 全角符号
}
