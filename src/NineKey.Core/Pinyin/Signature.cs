namespace NineKey.Core.Pinyin;

// 本文件职责：把任意字母/数字串转成等长的 T9 数字签名，作为倒排索引的键。
// 数据流位置：用户输入 / 词库拼音 → Signature.Of → 数字签名 → InMemoryLexicon 查询。
// ⚠ 坑 1：只转小写字母，大写会先被 ToLowerInvariant 压平；数字/标点原样占位。
// ⚠ 坑 2：输出长度与输入严格等长，倒排索引构建时必须用同一函数，否则键对不上。
// ⚠ 坑 3：签名里允许非数字字符（如标点），查询侧要决定是保留还是过滤。
// 相关规格：§2.2、§2.3。

/// <summary>输入串 → 数字签名（倒排索引的键）。</summary>
public static class Signature
{
    /// <summary>
    /// 将输入串转为 T9 数字签名，大写字母会先转小写，非字母字符原样保留。
    /// </summary>
    /// <param name="letters">原始输入串，允许字母、数字、标点。</param>
    public static string Of(string letters)
    {
        ArgumentNullException.ThrowIfNull(letters);
        return string.Create(letters.Length, letters, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                // ⚠ 坑：非字母字符直接复制，签名与输入长度严格一致；调用方不能假设签名纯数字。
                span[i] = LetterKeyMap.LetterToDigit(char.ToLowerInvariant(src[i]));
            }
        });
    }
}
