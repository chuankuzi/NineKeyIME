// 本文件职责：过滤触屏硬件在同一点附近的重复/幽灵触点报告，避免一个真实触摸被拆成多个手势。
// 数据流位置：RawTouchWatcher/KeyButton 收到原始触点 → TouchGhostFilter 判定 → 有效触点进入 KeyButton 点按状态机。
// ⚠ 坑 1：判定必须同时满足时间与距离，只满足其一容易误杀快速滑动或不同位置的正常连击（§13.20-2）。
// ⚠ 坑 2：抬起后短时间内同位置再次按下也视为幽灵，否则抬手抖动会被当成新一次点击。
// 相关规格：§13.20-2。

namespace NineKey.Keyboard.Input;

/// <summary>
/// 触屏幽灵触点过滤（§13.20-2）：30ms 内且 10px 内的第二触点视为同一触点的重复报告，
/// 合并为同一触点（即不开启新手势）。同时统计原始触点/已采纳触点/幽灵触点数量供日志。
/// </summary>
public sealed class TouchGhostFilter
{
    public const int MaxGhostMs = 30;
    public const double MaxGhostPx = 10.0;

    private long _rawCount;
    private long _acceptedCount;
    private long _ghostCount;

    private (long T, TouchPoint2D P)? _activeStart;
    private (long T, TouchPoint2D P)? _lastUp;

    /// <summary>原始/已采纳/幽灵触点计数（供日志与调试）。</summary>
    public (long Raw, long Accepted, long Ghost) Counts => (_rawCount, _acceptedCount, _ghostCount);

    /// <summary>记录一次原始触点事件（无论是否采纳）。</summary>
    public void RecordRawTouch()
    {
        _rawCount++;
    }

    /// <summary>
    /// 判定新触点是否为幽灵触点：若当前已有活动触点或刚抬起，且在 30ms/10px 内则合并。
    /// 返回 true 表示采纳为新手势起点，false 表示忽略并合并，reason 输出原因。
    /// </summary>
    public bool TryAcceptTouch(long timestamp, TouchPoint2D position, bool hasActiveTouch, out string reason)
    {
        if (hasActiveTouch && _activeStart is { } active)
        {
            var dt = timestamp - active.T;
            var d = Distance(position, active.P);
            if (dt <= MaxGhostMs && d <= MaxGhostPx)
            {
                _ghostCount++;
                reason = $"ghost-active dt={dt}ms dist={d:F1}px";
                return false;
            }
        }

        if (_lastUp is { } up)
        {
            var dt = timestamp - up.T;
            var d = Distance(position, up.P);
            if (dt <= MaxGhostMs && d <= MaxGhostPx)
            {
                _ghostCount++;
                reason = $"ghost-up dt={dt}ms dist={d:F1}px";
                return false;
            }
        }

        _acceptedCount++;
        reason = "accepted";
        return true;
    }

    /// <summary>记录被采纳的触点起点。</summary>
    public void RecordAcceptedTouch(long timestamp, TouchPoint2D position)
    {
        _activeStart = (timestamp, position);
    }

    /// <summary>触点抬起：释放当前活动触点并记录为最近抬起。</summary>
    public void RecordTouchUp(long timestamp, TouchPoint2D position)
    {
        _activeStart = null;
        _lastUp = (timestamp, position);
    }

    private static double Distance(TouchPoint2D a, TouchPoint2D b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
