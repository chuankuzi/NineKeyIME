// 本文件职责：符号键盘页面内容的唯一真源，按中文/英文数学/单位常用分类分页。
// 数据流位置：SymbolLayout.Pages → KeyboardWindow 构建符号面板 → 用户点击 → CommitDirect 上屏。
// ⚠ 坑 1：1 键内嵌的四个标点是快捷入口，与符号页不互斥，修改时必须两边同步（§13.15）。
// ⚠ 坑 2：每页符号顺序即面板展示顺序，高频符号应靠前以减少翻页。
// 相关规格：§13.11、§13.15。

namespace NineKey.Keyboard.Input;

/// <summary>
/// 符号页内容唯一真源（§13.15 基线 + §13.11 补充条款：按类分页，至少两页）。
/// 1 键内嵌四标点（，。！？）为快捷入口，与本表不互斥。
/// </summary>
public static class SymbolLayout
{
    /// <summary>一页符号：页签标题 + 符号列表（按页签顺序展示）。</summary>
    public sealed record SymbolPage(string Title, IReadOnlyList<string> Symbols);

    /// <summary>全部符号页，按页签顺序展示。</summary>
    public static readonly IReadOnlyList<SymbolPage> Pages =
    [
        new("中文",
        [
            "。", "，", "、", "；", "：", "？", "！", "“",
            "”", "‘", "’", "（", "）", "《", "》", "…",
            "—", "·",
        ]),
        new("英文·数学",
        [
            ",", ".", ";", ":", "!", "?", "'", "\"",
            "(", ")", "[", "]", "<", ">", "＋", "－",
            "×", "÷", "＝", "±",
        ]),
        new("单位·常用",
        [
            "℃", "￥", "$", "%", "°", "@", "#", "&",
            "~", "^", "|", "\\", "/",
        ]),
    ];
}
