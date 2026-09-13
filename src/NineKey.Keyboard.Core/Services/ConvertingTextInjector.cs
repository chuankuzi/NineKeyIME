// 本文件职责：ITextInjector 的装饰器，在"上屏边界"对文本做可选转换（简繁输出等）。
// 数据流位置：KeyController → 本装饰器（Converter 非空时转换）→ 真实注入器（TextInjector）。
// ⚠ 坑：学习链在 KeyController 内、发生在调用本装饰器之前，故用户词典始终记录简体原词，切换繁简不污染词库。
// 相关规格：§13.31（简繁输出，2026-09-13 立项）。

namespace NineKey.Keyboard.Services;

/// <summary>上屏文本转换装饰器：Converter 为 null 时原样直通（默认简体输出）。</summary>
public sealed class ConvertingTextInjector : ITextInjector
{
    private readonly ITextInjector _inner;

    /// <summary>文本转换函数（如 OpenCC 简体→繁体）；null = 不转换。</summary>
    public Func<string, string>? Converter { get; set; }

    public ConvertingTextInjector(ITextInjector inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void InjectText(string text) => _inner.InjectText(Converter?.Invoke(text) ?? text);

    public void InjectBackspace() => _inner.InjectBackspace();

    public void InjectEnter() => _inner.InjectEnter();

    public void InjectSpace() => _inner.InjectSpace();
}
