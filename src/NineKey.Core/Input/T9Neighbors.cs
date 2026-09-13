namespace NineKey.Core.Input;

// 本文件职责：定义 T9 电话键盘各数字键的物理四邻键，用于误触纠正概率计算。
// 数据流位置：触屏点按生成 TouchVariant 时 → 查询邻键概率 → KeyController / QueryEngine 做 Beam 扩展。
// ⚠ 坑 1：Map 按电话键盘物理排布，不是数字大小顺序；新增/改键位必须同步此处。
// ⚠ 坑 2：0 键只有上邻 8，没有下邻，算概率时不能假设四邻都不为空。
// ⚠ 坑 3：非 0-9 字符查询返回全空 Neighbors，调用方应把它视为无邻键而不是异常。
// 相关规格：§M8-1。

/// <summary>T9 电话键位四邻键映射，用于误触纠正概率计算。</summary>
public static class T9Neighbors
{
    /// <summary>某个键的左/上/右/下邻键，null 表示该方向无键。</summary>
    public readonly record struct Neighbors(char? Left, char? Up, char? Right, char? Down);

    // ⚠ 坑：键位坐标是物理排布，中心键 5 的四邻为 4/2/6/8，映射错误会直接影响误触纠正准确率。
    private static readonly Dictionary<char, Neighbors> Map = new()
    {
        ['1'] = new(null, null, '2', '4'),
        ['2'] = new('1', null, '3', '5'),
        ['3'] = new('2', null, null, '6'),
        ['4'] = new(null, '1', '5', '7'),
        ['5'] = new('4', '2', '6', '8'),
        ['6'] = new('5', '3', null, '9'),
        ['7'] = new(null, '4', '8', null),
        ['8'] = new('7', '5', '9', '0'),
        ['9'] = new('8', '6', null, '0'),
        ['0'] = new(null, '8', null, null),
    };

    /// <summary>获取指定数字键的四邻键；未知键返回全空。</summary>
    /// <param name="digit">电话键盘数字（'0'-'9'）。</param>
    public static Neighbors Get(char digit) => Map.TryGetValue(digit, out var n) ? n : new(null, null, null, null);
}
