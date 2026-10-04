// 本文件职责：候选条手势判定纯逻辑（批5 滑动翻页 + 批7 长按菜单），不含任何 WPF 类型，便于单测。
// 数据流位置：CandidateBar 的 Preview 触摸/鼠标两条路径 → 本控制器 → 翻页事件 / 长按菜单定时器。
// ⚠ 坑 1：候选按钮会 CaptureMouse，Move/Up 只保证送到按钮自身；所以判定必须是"可被重复调用"的纯状态机，
//         由候选条与按钮**双通道**同时喂入，同一手势只能产出一次结论（用 _consumed 守住）。
// ⚠ 坑 2：长按容差必须等于滑动阈值（30px）——否则 10~29px 的抖动会既取消长按又不翻页，形成"死区"。

namespace NineKey.Keyboard.Views;

/// <summary>候选条手势判定：按下同时启动滑动跟踪与长按计时；滑动与长按互斥。</summary>
internal sealed class CandidateGestureController
{
    /// <summary>水平滑动触发翻页的位移阈值（像素）。</summary>
    public const double SwipeThresholdPx = 30;

    /// <summary>长按判定时长（毫秒）。</summary>
    public const int LongPressMs = 500;

    private bool _tracking;
    private double _startX;
    private double _startY;
    private double _maxDx;
    private double _maxDy;
    private bool _longPressArmed;
    private bool _longPressFired;
    private bool _consumed;

    /// <summary>是否正在跟踪一次手势。</summary>
    public bool IsTracking => _tracking;

    /// <summary>本次手势是否已经弹出过长按菜单。</summary>
    public bool LongPressFired => _longPressFired;

    /// <summary>按下：同时启动滑动跟踪与长按计时。</summary>
    public void Down(double x, double y)
    {
        _startX = x;
        _startY = y;
        _maxDx = 0;
        _maxDy = 0;
        _tracking = true;
        _longPressArmed = true;
        _longPressFired = false;
        _consumed = false;
    }

    /// <summary>移动：记录过程峰值位移；越过阈值即取消长按（滑动优先）。</summary>
    public void Move(double x, double y)
    {
        if (!_tracking)
        {
            return;
        }

        var dx = x - _startX;
        var dy = y - _startY;
        if (Math.Abs(dx) > Math.Abs(_maxDx))
        {
            _maxDx = dx;
        }

        if (Math.Abs(dy) > Math.Abs(_maxDy))
        {
            _maxDy = dy;
        }

        if (Math.Abs(_maxDx) > SwipeThresholdPx || Math.Abs(_maxDy) > SwipeThresholdPx)
        {
            _longPressArmed = false;
        }
    }

    /// <summary>抬起：返回 +1=右滑（上一页）、-1=左滑（下一页）、0=未构成滑动（交还原点击逻辑）。</summary>
    public int Up(double x, double y)
    {
        if (!_tracking)
        {
            return 0;
        }

        Move(x, y); // 抬起点也算一次采样
        _tracking = false;
        _longPressArmed = false;

        if (_consumed)
        {
            return 0; // 已由另一通道或长按产出结论
        }

        // 垂直分量占优 或 未过阈值 → 视为点击
        if (Math.Abs(_maxDy) >= Math.Abs(_maxDx) || Math.Abs(_maxDx) < SwipeThresholdPx)
        {
            return 0;
        }

        _consumed = true;
        return _maxDx < 0 ? -1 : 1;
    }

    /// <summary>定时器到点调用：位移未越阈值且尚未触发过 → 该弹长按菜单。</summary>
    public bool TryFireLongPress()
    {
        if (!_tracking || !_longPressArmed || _longPressFired)
        {
            return false;
        }

        _longPressFired = true;
        _longPressArmed = false;
        _consumed = true; // 菜单已占本次手势，抬起不再翻页
        return true;
    }

    /// <summary>取消长按计时（抬起、离开、滚动等）。</summary>
    public void CancelLongPress() => _longPressArmed = false;
}
