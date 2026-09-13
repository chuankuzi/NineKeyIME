namespace NineKey.Core.Pinyin;

// 本文件职责：维护普通话合法拼音音节表，并提供音节切分与合法性校验。
// 数据流位置：DictBuilder 校验词库拼音 → Syllable.IsValid / Split；QueryEngine 显示拼音分段也依赖 Split。
// ⚠ 坑 1：ü 统一用 v 表示，任何把 u 当 ü 的映射都会在这里或 LetterKeyMap 层出错。
// ⚠ 坑 2：最长合法音节 6 字符（如 chuang），Split 的贪心上限必须 ≥6，否则切分失败。
// ⚠ 坑 3：脏数据 fallback 会吞掉非法字符，调用方应据此过滤，不能把单字符音节当正常结果参与组词。
// 相关规格：§2.2、§2.3、§13.15。

/// <summary>
/// 合法拼音音节表（约 410 个），按“声母 × 韵母”规则生成，数据内嵌。
/// 规则集覆盖普通话全部合法音节；DictBuilder 构建词库时会用词库实际数据再校验。
/// ü 统一用 v 表示（nv=女，lve=略）。
/// </summary>
public static class Syllable
{
    private static readonly HashSet<string> Valid = Build();

    /// <summary>判断给定字符串是否为合法音节（ü 用 v 表示）。</summary>
    public static bool IsValid(string syllable) => Valid.Contains(syllable);

    /// <summary>当前合法音节总数（约 410）。</summary>
    public static int Count => Valid.Count;

    /// <summary>
    /// 把连续拼音串切分为音节序列，用于显示分段与简拼首字母提取。
    /// 优先最长匹配并带回溯；无法全程合法时退化为贪心吞咽非法字符。
    /// </summary>
    /// <param name="pinyin">小写拼音串，ü 用 v 表示。</param>
    public static IReadOnlyList<string> Split(string pinyin)
    {
        ArgumentNullException.ThrowIfNull(pinyin);
        var memo = new Dictionary<int, string[]?>();
        var split = SplitFrom(pinyin, 0, memo);
        if (split is not null)
        {
            return split;
        }

        // ⚠ 坑：脏数据无法完全合法切分，吞单字符是“可检出”策略，不是正确音节。
        var fallback = new List<string>();
        var i = 0;
        while (i < pinyin.Length)
        {
            var matched = false;
            // ⚠ 坑：最长音节 6 字符（chuang），上限不能小于实际最长音节，否则合法切分被漏掉。
            for (var len = Math.Min(6, pinyin.Length - i); len >= 1; len--)
            {
                if (Valid.Contains(pinyin.Substring(i, len)))
                {
                    fallback.Add(pinyin.Substring(i, len));
                    i += len;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                fallback.Add(pinyin[i].ToString());
                i++;
            }
        }

        return fallback;
    }

    /// <summary>从 pos 起的最长优先合法切分；不存在时返回 null（记忆化避免指数爆炸）。</summary>
    private static string[]? SplitFrom(string pinyin, int pos, Dictionary<int, string[]?> memo)
    {
        if (pos == pinyin.Length)
        {
            return [];
        }

        if (memo.TryGetValue(pos, out var cached))
        {
            return cached;
        }

        for (var len = Math.Min(6, pinyin.Length - pos); len >= 1; len--)
        {
            var piece = pinyin.Substring(pos, len);
            if (!Valid.Contains(piece))
            {
                continue;
            }

            var rest = SplitFrom(pinyin, pos + len, memo);
            if (rest is not null)
            {
                var result = new string[rest.Length + 1];
                result[0] = piece;
                rest.CopyTo(result, 1);
                return memo[pos] = result;
            }
        }

        return memo[pos] = null;
    }

    private static HashSet<string> Build()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        // 韵母分组
        string[] aGroup = ["a", "ai", "an", "ang", "ao"];
        string[] eGroup = ["e", "ei", "en", "eng"];
        string[] oGroup = ["o", "ou"];
        string[] uFull = ["u", "ua", "uai", "uan", "uang", "ui", "un", "uo"];
        string[] iBasic = ["i", "ian", "iao", "ie", "in", "ing"];
        string[] iFull = ["i", "ia", "ian", "iang", "iao", "ie", "in", "ing", "iong", "iu"];

        void Add(string initial, IEnumerable<string> finals)
        {
            foreach (var f in finals)
            {
                set.Add(initial + f);
            }
        }

        // ⚠ 坑：零声母不列齐齿呼（ia/ian/iang 等），这些音由 y 段覆盖，避免重复。
        Add("", aGroup);
        Add("", eGroup);
        Add("", oGroup);
        Add("", ["er", "i"]);
        Add("", uFull);

        // b：只配 o（bo），不配 ou（bou 非法）；合口只有单韵母 u（bu）
        Add("b", aGroup);
        Add("b", eGroup);
        Add("b", ["o", "u"]);
        Add("b", iBasic);

        // p m：o + ou（po/mo/pou/mou）+ 单韵母 u（pu/mu）
        foreach (var s in new[] { "p", "m" })
        {
            Add(s, aGroup);
            Add(s, eGroup);
            Add(s, oGroup);
            Add(s, ["u"]);
            Add(s, iBasic);
        }

        // f：无齐齿呼；合口只有单韵母 u（fu）
        Add("f", aGroup);
        Add("f", eGroup);
        Add("f", oGroup);
        Add("f", ["u"]);

        // d t：完整 u 组（无 ua/uai/uang）+ 齐齿 + ong + ou（do/to 非法，只配 ou）
        string[] uDT = ["u", "uan", "ui", "un", "uo"];
        foreach (var s in new[] { "d", "t" })
        {
            Add(s, aGroup);
            Add(s, eGroup);
            Add(s, ["ou"]);
            Add(s, ["ong"]);
            Add(s, iBasic);
            Add(s, uDT);
        }

        // d 独有 diu（tiu 非法）
        set.Add("diu");

        // ⚠ 坑：n/l 的撮口呼用 v/ve 表示 ü/üe，必须和 LetterKeyMap 中 v→8 的映射保持一致。
        foreach (var s in new[] { "n", "l" })
        {
            Add(s, aGroup);
            Add(s, eGroup);
            Add(s, ["ou"]);
            Add(s, ["ong"]);
            Add(s, iBasic);
            Add(s, ["iang", "iu"]);
            Add(s, uDT);
            Add(s, ["v", "ve"]);
        }

        // g k h：合口呼完整 + ong，无齐齿
        foreach (var s in new[] { "g", "k", "h" })
        {
            Add(s, aGroup);
            Add(s, eGroup);
            Add(s, oGroup);
            Add(s, ["ong"]);
            Add(s, uFull);
        }

        // j q x：齐齿呼全组 + 撮口呼（以 u 形表示 ü：ju/jue/juan/jun）
        string[] juetuan = ["u", "ue", "uan", "un"];
        foreach (var s in new[] { "j", "q", "x" })
        {
            Add(s, iFull);
            Add(s, juetuan);
        }

        // zh ch sh r：合口完整 + ong + 特殊 i（知/吃/诗/日 的舌尖元音）
        foreach (var s in new[] { "zh", "ch", "sh", "r" })
        {
            Add(s, aGroup);
            Add(s, eGroup);
            Add(s, oGroup);
            Add(s, ["ong"]);
            Add(s, uFull);
            Add(s, ["i"]);
        }

        // z c s：合口无 ua/uai/uang + ong + i（资/疵/思）
        string[] uZS = ["u", "uan", "ui", "un", "uo"];
        foreach (var s in new[] { "z", "c", "s" })
        {
            Add(s, aGroup);
            Add(s, eGroup);
            Add(s, oGroup);
            Add(s, ["ong"]);
            Add(s, uZS);
            Add(s, ["i"]);
        }

        // y：yi + a 组 + ye/yin/ying + you(y+ou) + yong(y+ong) + 撮口(yu/yue/yuan/yun)
        Add("y", aGroup);
        Add("y", ["i", "e", "in", "ing", "ou", "ong", "u", "ue", "uan", "un"]);

        // w：wa/wo/wu 系列 + wei/wen/weng
        Add("w", aGroup);
        Add("w", ["ei", "en", "eng", "o", "u"]);

        // 特殊音节：叹词/语气词 呒(m)、嗯/唔(n、ng)，及 kMandarin 收录的口语音节（哟 yo、咯 lo、噷 hm、哼 hng、覅 fiao）
        set.Add("m");
        set.Add("n");
        set.Add("ng");
        set.Add("yo");
        set.Add("lo");
        set.Add("hm");
        set.Add("hng");
        set.Add("fiao");

        return set;
    }
}
