// 本文件职责：宏键数据模型，定义宏名称、动作类型与内容。
// 数据流位置：AppSettings.Macros → 宏键行渲染 → MacroExecutor 解析执行（SendInput 文本或快捷键）。
// ⚠ 坑：快捷键字符串目前未做运行时语法校验，非法组合会在执行时静默失败，需在宏编辑器侧限制输入（§13.28）。
// 相关规格：§13.28。

namespace NineKey.Keyboard.Settings;

/// <summary>自定义宏（§13.28）：名称≤4字，动作仅限文本上屏或发送按键组合。</summary>
public sealed class Macro
{
    /// <summary>宏名称，≤4 字，显示在宏键行按钮上。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>动作类型：文本上屏 或 按键组合。</summary>
    public MacroActionType ActionType { get; set; } = MacroActionType.Text;

    /// <summary>动作内容：文本 或 按键组合字符串（如 "Ctrl+S"）。</summary>
    public string Content { get; set; } = string.Empty;
}

public enum MacroActionType
{
    /// <summary>通过 SendInput 直接上屏文本。</summary>
    Text,

    /// <summary>发送按键组合（如 Ctrl+S）。</summary>
    Shortcut,
}
