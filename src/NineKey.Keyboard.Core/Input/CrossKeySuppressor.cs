// 本文件职责：按键时间窗抑制，过滤触屏/鼠标双发与邻近键幽灵触点。
// 数据流位置：KeyButton 等物理/触屏按键源 → CrossKeySuppressor 过滤 → KeyController 处理有效事件。
// ⚠ 坑 1：同键去重窗必须 ≥ 触屏+鼠标提升间隔，否则同一真实按下会被当成两次（§13.37）。
// ⚠ 坑 2：跨键抑制窗过大会误杀正常快速连击，过小则挡不住 Deck 厘米级漂移（§13.37）。
// 相关规格：§13.37。

namespace NineKey.Keyboard.Input;

/// <summary>
/// §13.37：按键时间窗抑制。同键 100ms 内去重（防触屏+鼠标提升双事件）；
/// 跨键 50ms 内抑制（防 Deck 厘米级幽灵触点）。
/// 被抑制事件不计入正常输入流程。
/// </summary>
public static class CrossKeySuppressor
{
    public const int SameKeyWindowMs = 100;
    public const int CrossKeyWindowMs = 50;

    private static long _lastPressTime;
    private static object? _lastSource;
    private static int _suppressedCount;

    /// <summary>自启动以来被抑制的事件总数（调试用）。</summary>
    public static int SuppressedCount => _suppressedCount;

    /// <summary>最近一次被接受按键的时间戳（调试用）。</summary>
    public static long LastPressTime => _lastPressTime;

    /// <summary>尝试接受一次按键；timestamp 与 source 决定是否被时间窗抑制，reason 输出原因。</summary>
    public static bool TryAccept(long timestamp, object source, out string reason)
    {
        if (_lastSource is not null && ReferenceEquals(_lastSource, source) && timestamp - _lastPressTime < SameKeyWindowMs)
        {
            _suppressedCount++;
            reason = "same-key";
            return false;
        }

        if (_lastSource is not null && !ReferenceEquals(_lastSource, source) && timestamp - _lastPressTime < CrossKeyWindowMs)
        {
            _suppressedCount++;
            reason = "cross-key";
            return false;
        }

        _lastPressTime = timestamp;
        _lastSource = source;
        reason = "accepted";
        return true;
    }

    /// <summary>测试钩子：清空统计与上一次按键记录。</summary>
    public static void Reset()
    {
        _lastPressTime = 0;
        _lastSource = null;
        _suppressedCount = 0;
    }
}
