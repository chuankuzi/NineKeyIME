// 本文件职责：定义查询引擎返回的候选对象及来源枚举，供排序、显示与用户词学习使用。
// 数据流位置：QueryEngine 生成 Candidate → Ranker 排序 → KeyController 上屏/学习。
// ⚠ 坑 1：ConsumedKeys 为 0 时调用方默认消费全部缓冲，部分上屏时必须显式赋值（§M8-5）。
// ⚠ 坑 2：CandidateSource 的数值顺序由 Ranker 直接决定，新增来源必须放对层级。
// ⚠ 坑 3：Pinyin 可为 null，但用户词学习需要拼音，上屏前要做空值兜底。
// 相关规格：§2.3、§M8-5。

namespace NineKey.Core.Engine;

/// <summary>候选来源（决定排序权重层级）。</summary>
public enum CandidateSource
{
    /// <summary>标准签名全拼精确匹配。</summary>
    FullMatch,

    /// <summary>标准签名首字母简拼匹配（权重 ×0.3）。</summary>
    JianpinMatch,

    /// <summary>模糊签名全拼精确匹配（§13.30：降权后仍低于标准签名）。</summary>
    FuzzyFullMatch,

    /// <summary>模糊签名首字母简拼匹配。</summary>
    FuzzyJianpinMatch,

    /// <summary>拼音组合引导项（§letter-pin：KeyController 在 Ranker 之后前置插入，不参与排序；点击=锁定/撤销组合）。</summary>
    PinyinGuide,

    /// <summary>句子记忆候选（批11）：由 KeyController 在第 1 页末位插入，**不参与排序**；若误入 Ranker 则落 `_ => 4` 兜底档。</summary>
    SentenceMemory,
}

/// <summary>
/// 一个候选：(文本, 来源, 权重, 拼音)。
/// 拼音供用户词学习与状态栏显示；ConsumedKeys 表示该候选覆盖的输入位数（§M8-5 部分上屏）。
/// </summary>
public sealed record Candidate(string Text, CandidateSource Source, double Weight, string? Pinyin = null)
{
    /// <summary>该候选覆盖的输入键数（从当前查询串开头计）。0 表示未计算（默认消费全部）。</summary>
    public int ConsumedKeys { get; init; }
}
