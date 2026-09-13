namespace NineKey.Core.Pinyin;

// 本文件职责：把 a-z 字母映射到 T9 数字键位，是签名生成的基础。
// 数据流位置：Signature.Of / KeyController 输入处理 → LetterKeyMap.LetterToDigit → 得到数字签名。
// ⚠ 坑 1：v 映射到 8 键，与 Syllable 中 ü 用 v 表示的规则联动，改一边必须同步另一边。
// ⚠ 坑 2：非 a-z 字符原样返回，调用方若混入大写/标点需先 ToLowerInvariant 或自行过滤。
// ⚠ 坑 3：Map 数组只开到 128，输入字符必须是 ASCII；遇到扩展字符会越界。
// 相关规格：§2.2、§8 M7。

/// <summary>
/// T9 字母→数字映射：abc→2, def→3, ghi→4, jkl→5, mno→6, pqrs→7, tuv→8, wxyz→9。
/// 拼音中以 v 代替 ü，v 归 8 键。其他字符（数字、标点）原样保留。
/// </summary>
public static class LetterKeyMap
{
    private static readonly int?[] Map = BuildMap();

    private static int?[] BuildMap()
    {
        // ⚠ 坑：数组只覆盖 0-127，输入字符若超出 ASCII 会 IndexOutOfRange；调用方 LetterToDigit 已用范围校验。
        var m = new int?[128];
        foreach (var pair in new (string Letters, int Digit)[]
        {
            ("abc", 2), ("def", 3), ("ghi", 4), ("jkl", 5),
            ("mno", 6), ("pqrs", 7), ("tuv", 8), ("wxyz", 9),
        })
        {
            foreach (var c in pair.Letters)
            {
                m[c] = pair.Digit;
            }
        }

        return m;
    }

    /// <summary>字母转 T9 数字；非 a-z 字符原样返回。</summary>
    public static char LetterToDigit(char c)
    {
        // ⚠ 坑：大写字母不会落到这里，Signature.Of 会先 ToLowerInvariant 压平。
        if (c >= 'a' && c <= 'z')
        {
            return (char)('0' + Map[c]!.Value);
        }

        return c;
    }
}
