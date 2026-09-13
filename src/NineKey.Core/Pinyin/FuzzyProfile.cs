namespace NineKey.Core.Pinyin;

// 本文件职责：定义 9 键 T9 输入的 9 组模糊音开关及对应的替换规则。
// 数据流位置：AppSettings 持有用户开关 → FuzzyProfile 把开关转成规则组 → FuzzySignature 用规则扩展签名。
// ⚠ 坑 1：同一组内字符规则（如 n↔l）与子串规则（如 9↔94）可能同时生效，组合爆炸靠 FuzzySignature 的 16 上限兜住。
// ⚠ 坑 2：每组规则是“双向”的（From↔To / Short↔Long），不能只填单向，否则用户习惯的一半发音匹配不到。
// ⚠ 坑 3：新增模糊音组时，必须同步在 AppSettings、托盘菜单、EnabledGroups 三处出现，否则开关不生效。
// 相关规格：§8 M7。

/// <summary>模糊音配置：9 组开关（§8 M7），默认全部关闭。</summary>
public sealed class FuzzyProfile
{
    /// <summary>单字母/单数字替换规则：原数字 → 别名数字。</summary>
    public sealed record CharRule(char From, char To);

    /// <summary>子串替换规则：短形式 ↔ 长形式（如 z=9 ↔ zh=94）。</summary>
    public sealed record SubstringRule(string Short, string Long);

    /// <summary>一组模糊音，可同时包含字符替换与子串替换。</summary>
    public sealed record Group(string Name, IReadOnlyList<CharRule> CharRules, IReadOnlyList<SubstringRule> SubstringRules);

    public bool ZhiZu { get; set; }
    public bool ChiCu { get; set; }
    public bool ShiSu { get; set; }
    public bool NiLi { get; set; }
    public bool RiLi { get; set; }
    public bool FuHu { get; set; }
    public bool AnAng { get; set; }
    public bool EnEng { get; set; }
    public bool InIng { get; set; }

    /// <summary>默认全关的模糊音配置。</summary>
    public static FuzzyProfile AllOff => new();

    /// <summary>全部开启的模糊音配置（测试与快速验收用）。</summary>
    public static FuzzyProfile AllOn => new()
    {
        ZhiZu = true,
        ChiCu = true,
        ShiSu = true,
        NiLi = true,
        RiLi = true,
        FuHu = true,
        AnAng = true,
        EnEng = true,
        InIng = true,
    };

    /// <summary>返回当前开启的模糊音组定义（含双向规则）。</summary>
    public IReadOnlyList<Group> EnabledGroups
    {
        get
        {
            // ⚠ 坑：每次访问都重新构造 Group 列表，频繁调用会分配内存；目前调用点有限，暂不缓存。
            var list = new List<Group>();
            if (ZhiZu) list.Add(MakeZhiZu());
            if (ChiCu) list.Add(MakeChiCu());
            if (ShiSu) list.Add(MakeShiSu());
            if (NiLi) list.Add(MakeNiLi());
            if (RiLi) list.Add(MakeRiLi());
            if (FuHu) list.Add(MakeFuHu());
            if (AnAng) list.Add(MakeAnAng());
            if (EnEng) list.Add(MakeEnEng());
            if (InIng) list.Add(MakeInIng());
            return list;
        }
    }

    // ⚠ 坑：子串规则只描述短/长形式，FuzzySignature 会双向替换，这里不要写成两个规则。
    private static Group MakeZhiZu() => new("z/zh",
        [],
        [new SubstringRule("9", "94")]); // z=9, zh=94

    private static Group MakeChiCu() => new("c/ch",
        [],
        [new SubstringRule("2", "24")]); // c=2, ch=24

    private static Group MakeShiSu() => new("s/sh",
        [],
        [new SubstringRule("7", "74")]); // s=7, sh=74

    private static Group MakeNiLi() => new("n/l",
        [new CharRule('6', '5'), new CharRule('5', '6')],
        []);

    private static Group MakeRiLi() => new("r/l",
        [new CharRule('7', '5'), new CharRule('5', '7')],
        []);

    private static Group MakeFuHu() => new("f/h",
        [new CharRule('3', '4'), new CharRule('4', '3')],
        []);

    private static Group MakeAnAng() => new("an/ang",
        [],
        [new SubstringRule("26", "264")]); // an=26, ang=264

    private static Group MakeEnEng() => new("en/eng",
        [],
        [new SubstringRule("36", "364")]); // en=36, eng=364

    private static Group MakeInIng() => new("in/ing",
        [],
        [new SubstringRule("46", "464")]); // in=46, ing=464
}
