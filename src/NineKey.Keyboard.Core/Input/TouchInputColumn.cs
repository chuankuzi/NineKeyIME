// 本文件职责：承载一次按键在一位上的候选数字集合，供误触纠正 Beam 搜索展开。
// 数据流位置：KeyButton/TouchGhostFilter 产生候选 → TouchInputColumn 按位收集 → KeyController BeamExpand → QueryEngine。
// ⚠ 坑：首项必须为本键且概率最高，否则 Beam 会优先偏向错误路径（§M8-1）。
// 相关规格：§M8-1。

namespace NineKey.Keyboard.Input;

/// <summary>§M8-1：一个输入位上的候选数字集合（误触纠正）。</summary>
public sealed class TouchInputColumn
{
    /// <summary>该位所有候选数字及其概率；首项通常为本键（概率 1）。</summary>
    public List<TouchVariant> Variants { get; set; } = [];
}
