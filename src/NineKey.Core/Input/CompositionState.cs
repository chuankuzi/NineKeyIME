// 本文件职责：记录一次查询输入串与引擎实际消费键数，支撑部分上屏 leftover 计算。
// 数据流位置：QueryEngine.Query 返回 → KeyController 据此决定提交/保留缓冲。
// ⚠ 坑 1：consumedKeys 必须 ≤ input.Length，否则 leftover 切片会越界。
// ⚠ 坑 2：前缀兜底时 consumed 会小于输入总长，调用方必须将 leftover 回显给用户（§M8-5）。
// 相关规格：§M8-5。

namespace NineKey.Core.Input;

/// <summary>当前组卷状态：输入串被引擎消费了多少键、剩余多少留给下一组。</summary>
public sealed class CompositionState
{
    public string Input { get; private set; } = string.Empty;

    /// <summary>本次查询消费掉的键数（前缀兜底时 &lt; 输入总长）。</summary>
    public int ConsumedKeys { get; private set; }

    public string Leftover => Input.Length > ConsumedKeys ? Input[ConsumedKeys..] : string.Empty;

    public bool HasLeftover => Leftover.Length > 0;

    /// <summary>设置输入串与已消费键数。consumedKeys 必须在 [0, input.Length] 范围内。</summary>
    public void Set(string input, int consumedKeys)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (consumedKeys < 0 || consumedKeys > input.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(consumedKeys));
        }

        Input = input;
        ConsumedKeys = consumedKeys;
    }

    /// <summary>清空输入与消费计数。</summary>
    public void Clear()
    {
        Input = string.Empty;
        ConsumedKeys = 0;
    }
}
