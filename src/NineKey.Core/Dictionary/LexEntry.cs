// 本文件职责：词库最小条目模型，区分系统/用户来源并暴露字数。
// 数据流位置：DictBuilder/LexiconLoader 生成 → InMemoryLexicon 索引 → QueryEngine 候选生成。
// ⚠ 坑 1：Pinyin 必须无声调且 ü=v，否则 Signature.Of 会生成错误数字签名。
// ⚠ 坑 2：作为 record 的字段被用作字典 key，Word 与 Pinyin 不能为 null，运行期仍要兜底。
// 相关规格：§2.3、§2.3-8。

namespace NineKey.Core.Dictionary;

/// <summary>词库条目来源。</summary>
public enum LexiconSource
{
    /// <summary>系统词库（DictBuilder 构建，pinyin-data 字表 + 词频表）。</summary>
    System,

    /// <summary>用户学习词库。</summary>
    User,
}

/// <summary>词库条目：(汉字词, 拼音串, 词频, 来源)。拼音无声调，ü 用 v。</summary>
public sealed record LexEntry(string Word, string Pinyin, int Frequency, LexiconSource Source)
{
    public int CharacterCount => Word.Length;
}
