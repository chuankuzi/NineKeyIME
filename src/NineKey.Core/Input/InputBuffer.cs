using NineKey.Core.Pinyin;

// 本文件职责：维护用户原始输入的字母视图与 T9 数字签名视图，支持追加、退格、前缀移除。
// 数据流位置：KeyController 按键事件 → InputBuffer → QueryEngine 用 Letters/DigitView 查词。
// ⚠ 坑 1：MaxLength 硬顶 32，防止超长串无限增长导致查询延迟爆炸。
// ⚠ 坑 2：所有字母强制小写，保证 T9 签名与词库拼音大小写一致。
// ⚠ 坑 3：RemovePrefix 用于部分上屏，长度必须与 QueryEngine 消费的键数对齐（§M8-5）。
// 相关规格：§2.3、§M8-5。

namespace NineKey.Core.Input;

/// <summary>输入串状态：同时维护字母视图与数字（T9）视图。</summary>
public sealed class InputBuffer
{
    private readonly System.Text.StringBuilder _letters = new();

    public string Letters => _letters.ToString();

    public string DigitView => Signature.Of(_letters.ToString());

    public int Length => _letters.Length;

    public bool IsEmpty => _letters.Length == 0;

    // ⚠ 坑：手机输入法惯例：输入串过长直接封顶，防止无效串无限增长拖累查询。
    public const int MaxLength = 32;

    /// <summary>追加一个字符；超过 MaxLength 时丢弃，并强制转小写。</summary>
    public void Append(char c)
    {
        if (_letters.Length < MaxLength)
        {
            // 统一转小写，避免大小写混合导致 T9 签名与词库不一致。
            _letters.Append(char.ToLowerInvariant(c));
        }
    }

    /// <summary>删除末尾一个字符；缓冲为空时不操作。</summary>
    public void Backspace()
    {
        if (_letters.Length > 0)
        {
            _letters.Remove(_letters.Length - 1, 1);
        }
    }

    /// <summary>§M8-5：从缓冲开头移除指定长度的字符（部分上屏消费前缀）。</summary>
    public void RemovePrefix(int length)
    {
        if (length <= 0)
        {
            return;
        }

        if (length >= _letters.Length)
        {
            _letters.Clear();
            return;
        }

        _letters.Remove(0, length);
    }

    /// <summary>清空缓冲。</summary>
    public void Clear() => _letters.Clear();
}
