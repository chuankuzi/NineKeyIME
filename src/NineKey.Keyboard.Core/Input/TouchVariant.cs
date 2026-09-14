// 本文件职责：定义误触纠正的最小数据单元（候选数字+概率）与一次按键的完整概率信息。
// 数据流位置：触摸命中计算生成 TouchVariant → 聚合成 KeyPressInfo → KeyController 决定是否走误触纠正路径。
// ⚠ 坑：Probability 必须严格大于 0 且不超过 1，否则 Beam 概率乘积会非法或截断（§M8-1）。
// ⚠ 坑 2：CommitDirect 决定路由（直投键上屏 vs 进输入串）。
// 相关规格：§M8-1。

namespace NineKey.Keyboard.Input;

/// <summary>误触纠正：一次触摸在某一位上的候选数字及其概率。</summary>
/// <param name="Digit">候选数字（0-9）。</param>
/// <param name="Probability">该数字的概率，范围 (0,1]。</param>
public readonly record struct TouchVariant(char Digit, double Probability);

/// <summary>误触纠正：一次按键/触摸的完整概率信息（含候选集合与路由标记）。</summary>
public sealed record KeyPressInfo(
    string Value,
    bool CommitDirect,
    IReadOnlyList<TouchVariant> Variants);
