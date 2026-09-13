using NineKey.Core.Pinyin;

// 本文件职责：内存中的词库倒排索引，提供全拼/简拼精确查询、存在性判断及用户词更新。
// 数据流位置：LexiconLoader 构建 → QueryEngine 查询；UserDictionary 通过 Upsert 合并学习结果。
// ⚠ 坑 1：Upsert 不加锁，依赖规格 §4.4 引擎只在 UI 线程使用，跨线程调用会损坏索引。
// ⚠ 坑 2：简拼签名长度若与全拼相同（如单音节），则无需重复建索引。
// ⚠ 坑 3：Build 对脏数据做运行期兜底，但不能替代 DictBuilder 构建期校验。
// 相关规格：§2.3、§2.3-8、§4.4、§M8-5。

namespace NineKey.Core.Dictionary;

/// <summary>
/// 内存倒排索引：数字签名 → 条目列表（构建期生成，运行期 O(1) 查询）。
/// 同时维护简拼（首字母）索引，权重由查询侧打折。
/// </summary>
public sealed class InMemoryLexicon
{
    private readonly Dictionary<string, List<LexEntry>> _fullIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LexEntry>> _jianpinIndex = new(StringComparer.Ordinal);

    public int EntryCount { get; private set; }

    /// <summary>从词条枚举构建索引，自动去重并丢弃空/无效条目。</summary>
    public static InMemoryLexicon Build(IEnumerable<LexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var lexicon = new InMemoryLexicon();
        var seen = new HashSet<(string Word, string Pinyin)>();
        foreach (var entry in entries)
        {
            // ⚠ 坑：DictBuilder 已在构建期校验，但运行时仍可能遇到脏数据，此处兜底跳过避免索引异常。
            if (entry.Word.Length == 0 || entry.Pinyin.Length == 0 || entry.Frequency <= 0)
            {
                continue;
            }

            // ⚠ 坑：同 (词,拼音) 重复条目只保留一条，避免候选列表出现重复项。
            if (!seen.Add((entry.Word, entry.Pinyin)))
            {
                continue;
            }

            lexicon.Add(entry);
        }

        return lexicon;
    }

    private void Add(LexEntry entry)
    {
        var sig = Signature.Of(entry.Pinyin);
        AddTo(_fullIndex, sig, entry);

        var jianpinSig = ComputeJianpinSignature(entry.Pinyin);

        // ⚠ 坑：简拼与全拼长度相同时二者索引重合，重复添加会导致候选翻倍。
        if (jianpinSig.Length != sig.Length)
        {
            AddTo(_jianpinIndex, jianpinSig, entry);
        }

        EntryCount++;
    }

    /// <summary>
    /// 运行时合并/更新条目（用户词学习用）：同 (词,拼音) 的旧条目先从两个索引移除再按新词频加入。
    /// 按规格 §4.4 引擎只在 UI 线程使用，本方法不加锁。
    /// </summary>
    public void Upsert(LexEntry entry)
    {
        if (entry.Word.Length == 0 || entry.Pinyin.Length == 0 || entry.Frequency <= 0)
        {
            return;
        }

        Remove(entry.Word, entry.Pinyin);
        Add(entry);
    }

    private void Remove(string word, string pinyin)
    {
        var sig = Signature.Of(pinyin);
        var removed = RemoveFrom(_fullIndex, sig, word, pinyin);
        var jianpinSig = ComputeJianpinSignature(pinyin);
        if (jianpinSig.Length != sig.Length)
        {
            RemoveFrom(_jianpinIndex, jianpinSig, word, pinyin);
        }

        if (removed)
        {
            EntryCount--;
        }
    }

    private static bool RemoveFrom(Dictionary<string, List<LexEntry>> index, string key, string word, string pinyin)
    {
        if (!index.TryGetValue(key, out var list))
        {
            return false;
        }

        var removed = list.RemoveAll(e => e.Word == word && e.Pinyin == pinyin) > 0;
        if (list.Count == 0)
        {
            index.Remove(key);
        }

        return removed;
    }

    /// <summary>按全拼数字签名精确匹配。signature 为 QueryEngine 转好的数字签名。</summary>
    public IReadOnlyList<LexEntry> QueryExact(string signature) =>
        _fullIndex.TryGetValue(signature, out var list) ? list : [];

    /// <summary>按简拼（首字母）数字签名精确匹配。signature 为 QueryEngine 转好的数字签名。</summary>
    public IReadOnlyList<LexEntry> QueryJianpin(string signature) =>
        _jianpinIndex.TryGetValue(signature, out var list) ? list : [];

    /// <summary>是否存在该全拼签名的条目，前缀兜底时用于快速判断最长可匹配前缀。</summary>
    public bool HasExact(string signature) => _fullIndex.ContainsKey(signature);

    private static void AddTo(Dictionary<string, List<LexEntry>> index, string key, LexEntry entry)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(entry);
    }

    private static string ComputeJianpinSignature(string pinyin)
    {
        var syllables = Syllable.Split(pinyin);
        return string.Concat(syllables.Select(s => LetterKeyMap.LetterToDigit(s[0])));
    }
}
