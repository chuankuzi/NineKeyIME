// 本文件职责：T9 键位字母与方向映射的唯一真源，决定每个数字键中心字与四方向槽。
// 数据流位置：KeyLayout.Keys → KeyboardWindow 初始化 KeyButton → 键面小字与槽位标注（Flick 已移除，槽位纯展示）。
// ⚠ 坑 1：三字母键点按等价于左滑，中心字与 Left 槽必须一致，否则点按与左滑结果不同（§13.17）。
// ⚠ 坑 2：四字母键 7/9 中心字不能滑出，因此 Center 只用于点按，不在 Slots 中重复（§13.17）。
// ⚠ 坑 3：CommitDirect=true 的键（1/0）直接上屏，不走候选流程，与拼音键逻辑完全不同。
// 相关规格：§2.1、§13.9、§13.17。

namespace NineKey.Keyboard.Input;

/// <summary>
/// 键位字母布局唯一真源（§2.1 + §13.9 + §13.17 方向映射修订）。
/// 方向分配（§13.17 解决"中心字滑不出"）：
///   三字母键（2/3/4/5/6/8）：左=中心字（首字母，点按=左滑等价），右=第2个，下=第3个，上=取消区。
///   四字母键（7/9）：中=第1，右=第2，下=第3，左=第4，上=取消区。
/// 未分配方向 = 取消区（滑到抬起等同回滑取消，不上屏，提示泡不显示）。
/// </summary>
public static class KeyLayout
{
    /// <summary>键定义：Digit 键号，Center 中心字，Right/Down/Left/Up 四方向槽（空串=未分配）。</summary>
    public sealed record KeyDef(string Digit, string Center, string Right, string Down, string Left, string Up, bool CommitDirect)
    {
        /// <summary>方向槽数组（recognizer 顺序：右/下/左/上）。</summary>
        public string[] Slots => [Right, Down, Left, Up];

        /// <summary>KeyButton.Options 格式（'|' 分隔 4 槽）。</summary>
        public string Options => string.Join('|', Slots);
    }

    /// <summary>0-9 全部键位定义，顺序对应键盘 1/2/3/.../0。</summary>
    public static readonly IReadOnlyList<KeyDef> Keys =
    [
        new("1", "1", "，", "。", "！", "？", true),
        new("2", "A", "B", "C", "A", "", false),
        new("3", "D", "E", "F", "D", "", false),
        new("4", "G", "H", "I", "G", "", false),
        new("5", "J", "K", "L", "J", "", false),
        new("6", "M", "N", "O", "M", "", false),
        new("7", "P", "Q", "R", "S", "", false),
        new("8", "T", "U", "V", "T", "", false),
        new("9", "W", "X", "Y", "Z", "", false),
        new("0", "空格", "", "", "", "", true),
    ];
}
