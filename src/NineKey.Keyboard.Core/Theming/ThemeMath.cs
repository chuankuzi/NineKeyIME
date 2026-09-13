// 本文件职责：主题色解析的纯数学（WCAG 对比度、相对亮度、明度微调、#RRGGBB 解析），与壳侧 ThemeColors 逐行同构。
// 数据流位置：壳侧 ThemeColors 委托本类完成全部计算，再做 RgbaColor↔Color 转换后涂刷。
// ⚠ 坑：0.03928/2.4/1.055/12.92 是 sRGB 到线性光的 IEC 61966-2-1 标准常数，不能改动（§13.15、§13.18-2、§13.18-3，与壳侧同规，改动必须双侧同步）。

namespace NineKey.Keyboard.Views;

/// <summary>平台无关的 RGBA 颜色（纯数据，替代 System.Windows.Media.Color）。对比度等数学只使用 R/G/B。</summary>
public readonly record struct RgbaColor(byte A, byte R, byte G, byte B)
{
    /// <summary>不透明基色快捷构造。</summary>
    public static RgbaColor FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);
}

/// <summary>
/// 主题色解析纯逻辑（§13.18-3 遍历式对比度回归 + §13.18-2 主题自定义）。
/// 自定义色无效时回退主题默认；前景色自动翻转黑/白，保证对比度 ≥4.5:1（§13.15）。
/// </summary>
public static class ThemeMath
{
    /// <summary>WCAG AA 正常文本最小对比度阈值 4.5:1。</summary>
    public const double MinContrast = 4.5;

    /// <summary>一组角色色（alpha 叠加由调用方按自身透明度设置另行处理）。</summary>
    public sealed record Palette(
        RgbaColor Background,
        RgbaColor KeyBackground,
        RgbaColor KeyPressed,
        RgbaColor KeyBorder,
        RgbaColor Foreground,
        RgbaColor SubForeground,
        RgbaColor BarBackground);

    private static readonly RgbaColor Black = RgbaColor.FromRgb(0, 0, 0);
    private static readonly RgbaColor White = RgbaColor.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>解析当前主题 + 自定义覆盖（语义与壳侧 ThemeColors.Resolve 完全一致）。</summary>
    /// <param name="light">true=浅色主题，false=深色主题。</param>
    /// <param name="customBg">键盘背景色 #RRGGBB，null 表示跟随主题。</param>
    /// <param name="customKeyBg">按键背景色 #RRGGBB，null 表示跟随主题。</param>
    public static Palette Resolve(bool light, string? customBg, string? customKeyBg)
    {
        var bg = light ? RgbaColor.FromRgb(0xF3, 0xF3, 0xF6) : RgbaColor.FromRgb(0x2A, 0x2A, 0x2E);
        var keyBg = light ? RgbaColor.FromRgb(0xFF, 0xFF, 0xFF) : RgbaColor.FromRgb(0x3A, 0x3A, 0x3D);
        if (TryParse(customBg, out var cb))
        {
            bg = cb;
        }

        if (TryParse(customKeyBg, out var ck))
        {
            keyBg = ck;
        }

        // 前景色自动翻转：任何背景（含自定义极端色）下黑/白取高对比者，恒 ≥4.5:1
        var fg = BestForeground(keyBg);
        return new Palette(
            Background: bg,
            KeyBackground: keyBg,
            KeyPressed: Adjust(keyBg, light ? -0.22 : 0.24),
            KeyBorder: light ? new RgbaColor(0x66, 0, 0, 0) : new RgbaColor(0x66, 0xFF, 0xFF, 0xFF),
            Foreground: fg,
            SubForeground: new RgbaColor(0x99, fg.R, fg.G, fg.B),
            BarBackground: Adjust(keyBg, light ? -0.08 : -0.04));
    }

    /// <summary>返回在指定背景下对比度更高的前景色（黑或白）。</summary>
    public static RgbaColor BestForeground(RgbaColor background) =>
        ContrastRatio(Black, background) >= ContrastRatio(White, background) ? Black : White;

    /// <summary>计算 WCAG 2.x 对比度（1~21）。</summary>
    public static double ContrastRatio(RgbaColor a, RgbaColor b)
    {
        var l1 = Luminance(a);
        var l2 = Luminance(b);
        var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>解析 #RRGGBB 或 RRGGBB；无效输入返回 false（A 恒为 0xFF）。</summary>
    public static bool TryParse(string? hex, out RgbaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            return false;
        }

        color = RgbaColor.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>给基色附加 alpha 通道。</summary>
    public static RgbaColor WithAlpha(RgbaColor c, byte alpha) => new(alpha, c.R, c.G, c.B);

    private static RgbaColor Adjust(RgbaColor c, double factor)
    {
        byte F(byte v) => (byte)Math.Clamp((int)(v + 255 * factor), 0, 255);
        return RgbaColor.FromRgb(F(c.R), F(c.G), F(c.B));
    }

    private static double Luminance(RgbaColor c)
    {
        // ⚠ 坑：0.03928、2.4、1.055、12.92 是 sRGB 到线性光的 IEC 61966-2-1 标准常数，不能改动。
        static double F(double v)
        {
            v /= 255;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }
}
