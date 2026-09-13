// 本文件职责：主题色解析的 WPF 门面——公共 API（Color 签名）与历史完全一致，内部委托纯库 ThemeMath 计算。
// 数据流位置：KeyboardWindow.ApplyTheme 调用 Resolve → 返回 Palette → 涂刷键盘/候选栏/按键。
// ⚠ 坑 1：自定义色无效时必须静默回退主题默认，不能抛异常导致主题系统崩溃。
// ⚠ 坑 3：调用方按自身透明度设置再叠加 alpha，本类返回的色值均为不透明基色。
// ⚠ 改动物理：自路线 B 起本类为薄壳，数学实现在 NineKey.Keyboard.Core 的 ThemeMath；两侧常数必须同步（§13.15、§13.18-2、§13.18-3）。

using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace NineKey.Keyboard.Views;

/// <summary>
/// 主题色解析（§13.18-3 遍历式对比度回归 + §13.18-2 主题自定义）。
/// 自定义色无效时回退主题默认；前景色在任何背景下自动翻转黑/白，保证对比度 ≥4.5:1（§13.15）。
/// 自路线 B 起为 ThemeMath 的 WPF 门面，行为与历史版本等价。
/// </summary>
public static class ThemeColors
{
    /// <summary>WCAG AA 正常文本最小对比度阈值 4.5:1。</summary>
    public const double MinContrast = ThemeMath.MinContrast;

    /// <summary>一组角色色（alpha 叠加由调用方按自身透明度设置另行处理）。</summary>
    public sealed record Palette(
        Color Background,
        Color KeyBackground,
        Color KeyPressed,
        Color KeyBorder,
        Color Foreground,
        Color SubForeground,
        Color BarBackground);

    /// <summary>解析当前主题 + 自定义覆盖。</summary>
    /// <param name="light">true=浅色主题，false=深色主题。</param>
    /// <param name="customBg">键盘背景色 #RRGGBB，null 表示跟随主题。</param>
    /// <param name="customKeyBg">按键背景色 #RRGGBB，null 表示跟随主题。</param>
    public static Palette Resolve(bool light, string? customBg, string? customKeyBg)
    {
        var p = ThemeMath.Resolve(light, customBg, customKeyBg);
        return new Palette(
            Background: ToColor(p.Background),
            KeyBackground: ToColor(p.KeyBackground),
            KeyPressed: ToColor(p.KeyPressed),
            KeyBorder: ToColor(p.KeyBorder),
            Foreground: ToColor(p.Foreground),
            SubForeground: ToColor(p.SubForeground),
            BarBackground: ToColor(p.BarBackground));
    }

    /// <summary>返回在指定背景下对比度更高的前景色（黑或白）。</summary>
    public static Color BestForeground(Color background) =>
        ToColor(ThemeMath.BestForeground(FromColor(background)));

    /// <summary>计算 WCAG 2.x 对比度（1~21）。</summary>
    public static double ContrastRatio(Color a, Color b) =>
        ThemeMath.ContrastRatio(FromColor(a), FromColor(b));

    /// <summary>解析 #RRGGBB 或 RRGGBB；无效输入返回 false。</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        var ok = ThemeMath.TryParse(hex, out var c);
        color = ok ? ToColor(c) : default;
        return ok;
    }

    /// <summary>给基色附加 alpha 通道。</summary>
    public static Color WithAlpha(Color c, byte alpha) =>
        ToColor(ThemeMath.WithAlpha(FromColor(c), alpha));

    private static Color ToColor(RgbaColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static RgbaColor FromColor(Color c) => new(c.A, c.R, c.G, c.B);
}
