// 本文件职责：定义文字上屏抽象接口，KeyController 只依赖本接口，便于测试替身替换。
// 数据流位置：KeyController 调用接口方法 → TextInjector（真实）或 StubTextInjector（测试）执行。
// ⚠ 坑：接口只暴露最常用的文本/退格/回车/空格，不暴露 InjectKey/InjectShortcut，那是 TextInjector 的扩展能力（M7-7）。
// 相关规格：§2.8、M7-7。

namespace NineKey.Keyboard.Services;

/// <summary>文字上屏抽象（SendInput 封装的可测替身口，KeyController 只依赖本接口）。</summary>
public interface ITextInjector
{
    /// <summary>Unicode 直送文本（&gt;U+FFFF 字符按 UTF-16 代理对拆两个字发送）。</summary>
    void InjectText(string text);

    /// <summary>发送退格键。</summary>
    void InjectBackspace();

    /// <summary>发送回车键。</summary>
    void InjectEnter();

    /// <summary>发送 VK_SPACE 空格（§13.5：空格键禁止输出字符 '0'）。</summary>
    void InjectSpace();
}
