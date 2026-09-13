// 本文件职责：对候选列表按匹配类型、词频、长度进行综合排序。
// 数据流位置：QueryEngine 查词后 → Ranker.Rank → KeyController 取候选页。
// ⚠ 坑 1：排序键顺序决定“全拼>简拼>模糊”的层级，ThenBy/ThenByDescending 不能写反。
// ⚠ 坑 2：短输入单字优先会改变多字候选位置，需与 UI 翻页逻辑配套验收。
// 相关规格：§2.3。

namespace NineKey.Core.Engine;

/// <summary>
/// 候选排序：全拼 > 简拼 > 模糊；词频降序（主）；词长度越接近输入串越优先（次）。
/// 输入 ≤2 键时可启用单字优先（手机输入法惯例）。
/// </summary>
public sealed class Ranker
{
    private readonly bool _singleCharPriorityForShortInput;

    /// <summary>初始化 Ranker。singleCharPriorityForShortInput 为 true 时，输入 ≤2 键优先排单字。</summary>
    public Ranker(bool singleCharPriorityForShortInput = true)
    {
        _singleCharPriorityForShortInput = singleCharPriorityForShortInput;
    }

    /// <summary>按来源层级、词频、长度对候选排序。inputKeyCount 用于短输入单字优先与长度接近度计算。</summary>
    public IReadOnlyList<Candidate> Rank(IEnumerable<Candidate> candidates, int inputKeyCount)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var singleCharFirst = _singleCharPriorityForShortInput && inputKeyCount <= 2;
        return candidates
            .OrderBy(c => singleCharFirst && c.Text.Length > 1 ? 1 : 0)
            // ⚠ 坑：返回值越小越靠前，顺序必须与 CandidateSource 的层级语义保持一致。
            .ThenBy(c => c.Source switch
            {
                CandidateSource.FullMatch => 0,
                CandidateSource.JianpinMatch => 1,
                CandidateSource.FuzzyFullMatch => 2,
                CandidateSource.FuzzyJianpinMatch => 3,
                _ => 4,
            })
            .ThenByDescending(c => c.Weight)
            .ThenBy(c => Math.Abs(c.Text.Length - inputKeyCount))
            .ToList();
    }
}
