// 本文件职责：把数字/拼音签名转成排序后的中文候选，支持精确、简拼、前缀兜底与误触纠正。
// 数据流位置：KeyController 组装查询串 → QueryEngine 查内存索引 → 返回 Candidate 列表。
// ⚠ 坑 1：模糊音会让签名变体数爆炸，必须硬顶总查询数 ≤16，否则延迟飙升（§M8-1）。
// ⚠ 坑 2：用户词学习次数要映射到系统词频量级，否则永远排不进候选前列（§2.3-8）。
// ⚠ 坑 3：前缀兜底只取最长可匹配前缀，leftover 必须回显，否则用户会感觉候选"缺字"。
// 相关规格：§2.3、§8 M7、§13.30、§M8-1。

using NineKey.Core.Dictionary;
using NineKey.Core.Pinyin;

namespace NineKey.Core.Engine;

/// <summary>一次查询的结果。</summary>
public sealed record QueryResult(
    IReadOnlyList<Candidate> Candidates,
    int ConsumedKeys,
    string Leftover,
    string? TopPinyin = null);

/// <summary>
/// T9 查询引擎：精确匹配 → 简拼匹配 → 前缀兜底（逐级缩短尾键）。
/// 对应规格文档 §2.3。构造时把用户学习词合并进索引（§2.3-8 启动加载合并）。
/// </summary>
public sealed class QueryEngine
{
    // ⚠ 坑：简拼候选权重必须显著低于全拼，否则"dg"会把"大哥"顶到"德国"前面。
    public const double JianpinWeightFactor = 0.3;

    /// <summary>用户词每次学习的权重当量：学习 1 次 ≈ 系统词库顶档词频(1000)，3 次后稳定进候选前列（M4 验收基线）。</summary>
    public const int UserLearnWeight = 1000;

    private readonly InMemoryLexicon _lexicon;
    private readonly Ranker _ranker;
    private readonly UserDictionary _userDict;

    public QueryEngine(InMemoryLexicon lexicon, Ranker ranker, UserDictionary userDict)
        : this(lexicon, ranker, userDict, FuzzyProfile.AllOff)
    {
    }

    public QueryEngine(InMemoryLexicon lexicon, Ranker ranker, UserDictionary userDict, FuzzyProfile fuzzyProfile)
    {
        _lexicon = lexicon ?? throw new ArgumentNullException(nameof(lexicon));
        _ranker = ranker ?? throw new ArgumentNullException(nameof(ranker));
        _userDict = userDict ?? throw new ArgumentNullException(nameof(userDict));
        FuzzyProfile = fuzzyProfile ?? throw new ArgumentNullException(nameof(fuzzyProfile));
        MergeUserEntries();
    }

    /// <summary>当前模糊音配置（§8 M7）：运行时切换后会立即生效。</summary>
    public FuzzyProfile FuzzyProfile { get; set; }

    /// <summary>候选上屏后的学习入口：更新用户词典并把新词频即时合并进索引（下次查询生效）。</summary>
    public void LearnFromCommit(string word, string? pinyin)
    {
        var count = _userDict.Learn(word, pinyin);
        var py = pinyin ?? _userDict.GetPinyin(word);
        if (!string.IsNullOrEmpty(py))
        {
            _lexicon.Upsert(new LexEntry(word, py, ScaleUserFreq(count), LexiconSource.User));
        }
    }

    /// <summary>
    /// 查询入口。input 为字母串或数字串（两种视图等价，内部转签名）。
    /// 返回排序后的候选、消费的键数、剩余键（前缀兜底时非空）。
    /// </summary>
    public QueryResult Query(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0)
        {
            return new QueryResult([], 0, string.Empty);
        }

        var sig = Signature.Of(input);

        // 1) 全拼精确 + 简拼精确（含模糊音变体）
        var fuzzySigs = FuzzyProfile.EnabledGroups.Count > 0
            ? FuzzySignature.Variants(sig, FuzzyProfile)
            : [sig];

        var candidates = new List<Candidate>();
        for (var i = 0; i < fuzzySigs.Count; i++)
        {
            // §13.30：原始签名（i==0）为标准来源；其余为模糊来源，×0.5 降权且排序层级更低
            var isOriginal = i == 0;
            var weightFactor = isOriginal ? 1.0 : 0.5;
            var fuzzySig = fuzzySigs[i];
            var fullSource = isOriginal ? CandidateSource.FullMatch : CandidateSource.FuzzyFullMatch;
            var jianpinSource = isOriginal ? CandidateSource.JianpinMatch : CandidateSource.FuzzyJianpinMatch;
            candidates.AddRange(FromEntries(_lexicon.QueryExact(fuzzySig), fullSource, sig.Length, weightFactor));
            candidates.AddRange(FromEntries(_lexicon.QueryJianpin(fuzzySig), jianpinSource, sig.Length, weightFactor));
        }

        // 同一候选（词+拼音）保留最高权重来源
        var deduped = new Dictionary<(string Word, string? Pinyin), Candidate>();
        foreach (var c in candidates)
        {
            var key = (c.Text, c.Pinyin);
            if (!deduped.TryGetValue(key, out var existing) || c.Weight > existing.Weight)
            {
                deduped[key] = c;
            }
        }

        var consumed = sig.Length;
        var leftover = string.Empty;

        // 2) 前缀兜底：逐级缩短尾键，取最长可匹配前缀（仅对原始签名）
        if (deduped.Count == 0)
        {
            for (var cut = sig.Length - 1; cut >= 1; cut--)
            {
                var prefix = sig[..cut];
                if (!_lexicon.HasExact(prefix))
                {
                    continue;
                }

                foreach (var entry in _lexicon.QueryExact(prefix))
                {
                    var c = new Candidate(entry.Word, CandidateSource.FullMatch, entry.Frequency, entry.Pinyin)
                    {
                        ConsumedKeys = prefix.Length,
                    };
                    var key = (c.Text, c.Pinyin);
                    if (!deduped.TryGetValue(key, out var existing) || c.Weight > existing.Weight)
                    {
                        deduped[key] = c;
                    }
                }

                consumed = cut;
                leftover = sig[cut..];
                break;
            }
        }

        var ranked = _ranker.Rank(deduped.Values, sig.Length);
        var topPinyin = ranked.Count > 0 && ranked[0].Source is CandidateSource.FullMatch or CandidateSource.FuzzyFullMatch
            ? ranked[0].Pinyin
            : null;

        return new QueryResult(ranked, consumed, leftover, topPinyin);
    }

    /// <summary>
    /// §M8-1：误触纠正多路查询。接收数字串变体及其概率，合并所有变体的候选。
    /// 总查询数（变体数 × 每条变体的模糊音签名数）硬顶 16；先缩 Beam（≤8），再缩别名。
    /// 本键全中（originalDigits）命中的候选额外 ×1.5 保底加成。
    /// </summary>
    public QueryResult QueryVariants(IReadOnlyList<(string Digits, double Probability)> variants, string originalDigits)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(originalDigits);

        if (variants.Count == 0 || originalDigits.Length == 0)
        {
            return new QueryResult([], 0, string.Empty);
        }

        var cappedVariants = CapVariants(variants, originalDigits);
        var merged = new Dictionary<(string Word, string? Pinyin), Candidate>();

        foreach (var (digits, prob) in cappedVariants)
        {
            var sig = Signature.Of(digits);
            var fuzzySigs = FuzzyProfile.EnabledGroups.Count > 0
                ? FuzzySignature.Variants(sig, FuzzyProfile)
                : [sig];

            // ⚠ 坑：二次硬顶总查询数 ≤16，模糊音 + Beam 组合会指数级膨胀。
            if (cappedVariants.Count * fuzzySigs.Count > 16)
            {
                var maxFuzzy = Math.Max(1, 16 / cappedVariants.Count);
                fuzzySigs = fuzzySigs.Take(maxFuzzy).ToList();
            }

            for (var i = 0; i < fuzzySigs.Count; i++)
            {
                var isOriginal = i == 0;
                var weightFactor = isOriginal ? 1.0 : 0.5;
                var fullSource = isOriginal ? CandidateSource.FullMatch : CandidateSource.FuzzyFullMatch;
                var jianpinSource = isOriginal ? CandidateSource.JianpinMatch : CandidateSource.FuzzyJianpinMatch;

                foreach (var c in FromEntries(_lexicon.QueryExact(fuzzySigs[i]), fullSource, originalDigits.Length, weightFactor))
                {
                    MergeCandidate(merged, c, prob);
                }

                foreach (var c in FromEntries(_lexicon.QueryJianpin(fuzzySigs[i]), jianpinSource, originalDigits.Length, weightFactor))
                {
                    MergeCandidate(merged, c, prob);
                }
            }
        }

        // ⚠ 坑：本键全中保底 ×1.5，防止误触纠正把正确输入压到后面。
        var originalSig = Signature.Of(originalDigits);
        var originalKeys = new HashSet<(string Word, string? Pinyin)>();
        foreach (var entry in _lexicon.QueryExact(originalSig))
        {
            _ = originalKeys.Add((entry.Word, entry.Pinyin));
        }

        foreach (var entry in _lexicon.QueryJianpin(originalSig))
        {
            _ = originalKeys.Add((entry.Word, entry.Pinyin));
        }

        foreach (var key in originalKeys)
        {
            if (merged.TryGetValue(key, out var c))
            {
                merged[key] = c with { Weight = c.Weight * 1.5 };
            }
        }

        var ranked = _ranker.Rank(merged.Values, originalDigits.Length);
        var topPinyin = ranked.Count > 0 && ranked[0].Source is CandidateSource.FullMatch or CandidateSource.FuzzyFullMatch
            ? ranked[0].Pinyin
            : null;

        return new QueryResult(ranked, originalDigits.Length, string.Empty, topPinyin);
    }

    private static List<(string Digits, double Probability)> CapVariants(
        IReadOnlyList<(string Digits, double Probability)> variants, string originalDigits)
    {
        var ordered = variants
            .OrderByDescending(v => v.Digits == originalDigits ? 1.0 : 0.0)
            .ThenByDescending(v => v.Probability)
            .ToList();

        return ordered.Take(8).ToList();
    }

    private static void MergeCandidate(
        Dictionary<(string Word, string? Pinyin), Candidate> merged, Candidate candidate, double variantProb)
    {
        var key = (candidate.Text, candidate.Pinyin);
        var weight = candidate.Weight * variantProb;
        if (!merged.TryGetValue(key, out var existing) || weight > existing.Weight)
        {
            merged[key] = candidate with { Weight = weight };
        }
    }

    private void MergeUserEntries()
    {
        foreach (var entry in _userDict.ToEntries())
        {
            _lexicon.Upsert(entry with { Frequency = ScaleUserFreq(entry.Frequency) });
        }
    }

    // ⚠ 坑：学习次数 × UserLearnWeight 可能溢出，必须钳位到 int.MaxValue/2。
    private static int ScaleUserFreq(long learnCount) =>
        learnCount >= int.MaxValue / 2 / UserLearnWeight
            ? int.MaxValue / 2
            : (int)learnCount * UserLearnWeight;

    private static IEnumerable<Candidate> FromEntries(
        IReadOnlyList<LexEntry> entries, CandidateSource source, int inputLength, double weightFactor = 1.0)
    {
        foreach (var entry in entries)
        {
            var weight = (double)entry.Frequency * weightFactor;
            if (source == CandidateSource.JianpinMatch)
            {
                weight *= JianpinWeightFactor;
            }

            // ⚠ 坑：§M8-5 部分上屏必须知道候选覆盖的输入位数。
            // 全拼候选默认消费其拼音长度（不超过输入长度）；简拼候选消费整个输入串。
            var consumed = source is CandidateSource.FullMatch or CandidateSource.FuzzyFullMatch
                ? Math.Min(entry.Pinyin?.Length ?? inputLength, inputLength)
                : inputLength;

            yield return new Candidate(entry.Word, source, weight, entry.Pinyin) { ConsumedKeys = consumed };
        }
    }
}
