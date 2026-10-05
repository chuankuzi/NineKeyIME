// 本文件职责：T9 按键控件，处理点按输入、误触概率计算与鼠标/触屏双通道输入。
// 数据流位置：KeyboardWindow 配置键定义 → KeyButton 识别点按 → 触发 DigitPressed → KeyController 处理输入。
// ⚠ 坑 1：WPF 触屏会提升为鼠标事件，必须用 _touchActive 和 500ms 抑制避免双发。
// ⚠ 坑 2：丢失 MouseUp/TouchUp 时状态会卡住，Press 入口和 LostCapture 都要做防御复位。
// 相关规格：§2.1、§4.9、§13.37、§M8-1。
// 修订（2026-09-08 用户裁定）：Flick 滑动选字与提示泡已移除，九键回归纯点按模型。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NineKey.Core.Input;
using NineKey.Keyboard.Input;
using NineKey.Keyboard.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace NineKey.Keyboard.Views;

/// <summary>
/// T9 按键（§2.1）：点按 = 中心值，进输入串或直投上屏。
/// 鼠标与触屏统一适配到同一按下/抬起状态机（§4.9）。
/// </summary>
public class KeyButton : Control
{
    static KeyButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KeyButton), new FrameworkPropertyMetadata(typeof(KeyButton)));
    }

    /// <summary>创建一个 T9 按键，注册鼠标与触屏事件。</summary>
    public KeyButton()
    {
        MouseLeftButtonDown += OnMousePress;
        MouseLeftButtonUp += OnMouseRelease;
        LostMouseCapture += OnCaptureLost;
    }

    /// <summary>§M8-1：带误触概率的按键事件。点按提供多位候选概率。</summary>
    public event Action<KeyPressInfo>? DigitPressed;

    /// <summary>§M8-1：误触纠正高斯 σ（px），由键盘窗口根据设置注入。</summary>
    public double TouchCorrectionSigma { get; set; } = 10.0;

    public string Digit
    {
        get => (string)GetValue(DigitProperty);
        set => SetValue(DigitProperty, value);
    }

    public static readonly DependencyProperty DigitProperty =
        DependencyProperty.Register(nameof(Digit), typeof(string), typeof(KeyButton), new PropertyMetadata(string.Empty));

    /// <summary>中心显示字（键面小字首字母），如 "A"。</summary>
    public string Center
    {
        get => (string)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public static readonly DependencyProperty CenterProperty =
        DependencyProperty.Register(nameof(Center), typeof(string), typeof(KeyButton),
            new PropertyMetadata(string.Empty, (d, _) => ((KeyButton)d).UpdateOptionsLabel()));

    /// <summary>方向槽，'|' 分隔，固定 4 槽（右|下|左|上）。Flick 已移除，仅用于键面小字显示。</summary>
    public string Options
    {
        get => (string)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public static readonly DependencyProperty OptionsProperty =
        DependencyProperty.Register(nameof(Options), typeof(string), typeof(KeyButton),
            new PropertyMetadata(string.Empty, (d, _) => ((KeyButton)d).UpdateOptionsLabel()));

    /// <summary>键面小字（由 Center + 非空槽自动生成）。</summary>
    public string OptionsLabel
    {
        get => (string)GetValue(OptionsLabelProperty);
        private set => SetValue(OptionsLabelPropertyKey, value);
    }

    public static readonly DependencyPropertyKey OptionsLabelPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(OptionsLabel), typeof(string), typeof(KeyButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty OptionsLabelProperty = OptionsLabelPropertyKey.DependencyProperty;

    /// <summary>true：点按结果直接上屏（标点/空格）；false：进入输入串（字母/数字）。</summary>
    public bool CommitDirect
    {
        get => (bool)GetValue(CommitDirectProperty);
        set => SetValue(CommitDirectProperty, value);
    }

    public static readonly DependencyProperty CommitDirectProperty =
        DependencyProperty.Register(nameof(CommitDirect), typeof(bool), typeof(KeyButton), new PropertyMetadata(false));

    public bool IsPressedVisual
    {
        get => (bool)GetValue(IsPressedVisualProperty);
        private set => SetValue(IsPressedVisualPropertyKey, value);
    }

    public static readonly DependencyPropertyKey IsPressedVisualPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsPressedVisual), typeof(bool), typeof(KeyButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsPressedVisualProperty = IsPressedVisualPropertyKey.DependencyProperty;

    /// <summary>按键常态背景色。</summary>
    public Brush KeyBackground
    {
        get => (Brush)GetValue(KeyBackgroundProperty);
        set => SetValue(KeyBackgroundProperty, value);
    }

    public static readonly DependencyProperty KeyBackgroundProperty =
        DependencyProperty.Register(nameof(KeyBackground), typeof(Brush), typeof(KeyButton),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3D))));

    /// <summary>按键按下态背景色。</summary>
    public Brush KeyPressedBackground
    {
        get => (Brush)GetValue(KeyPressedBackgroundProperty);
        set => SetValue(KeyPressedBackgroundProperty, value);
    }

    public static readonly DependencyProperty KeyPressedBackgroundProperty =
        DependencyProperty.Register(nameof(KeyPressedBackground), typeof(Brush), typeof(KeyButton),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x59))));

    /// <summary>按键边框颜色。</summary>
    public Brush KeyBorderBrush
    {
        get => (Brush)GetValue(KeyBorderBrushProperty);
        set => SetValue(KeyBorderBrushProperty, value);
    }

    public static readonly DependencyProperty KeyBorderBrushProperty =
        DependencyProperty.Register(nameof(KeyBorderBrush), typeof(Brush), typeof(KeyButton),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))));

    /// <summary>按键边框宽度（§13.18-2，0~4px 可调）。</summary>
    public Thickness KeyBorderThickness
    {
        get => (Thickness)GetValue(KeyBorderThicknessProperty);
        set => SetValue(KeyBorderThicknessProperty, value);
    }

    public static readonly DependencyProperty KeyBorderThicknessProperty =
        DependencyProperty.Register(nameof(KeyBorderThickness), typeof(Thickness), typeof(KeyButton),
            new PropertyMetadata(new Thickness(1)));

    /// <summary>小字/副标题前景色。</summary>
    public Brush SubForeground
    {
        get => (Brush)GetValue(SubForegroundProperty);
        set => SetValue(SubForegroundProperty, value);
    }

    public static readonly DependencyProperty SubForegroundProperty =
        DependencyProperty.Register(nameof(SubForeground), typeof(Brush), typeof(KeyButton),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))));

    private bool _touchActive;
    private long _lastTouchUp;
    private bool _isTouchPress;
    private readonly TouchGhostFilter _touchFilter = new();
    private IReadOnlyList<TouchVariant> _lastVariants = [new('0', 1.0)];
    private TouchDevice? _activeTouch;
    private bool _pressPending;

    private void UpdateOptionsLabel()
    {
        var parts = (Options ?? string.Empty).Split('|');
        var slots = new string[4];
        for (var i = 0; i < Math.Min(4, parts.Length); i++)
        {
            slots[i] = parts[i];
        }

        OptionsLabel = (Center ?? string.Empty) + string.Concat(slots);
    }

    // ---- 点按状态机 ----

    private void Press(Point pos, bool isTouch)
    {
        if (IsPressedVisual)
        {
            // 防御：合成输入或异常路径可能丢失抬起事件导致状态卡住，新按下前先复位
            ResetPressState();
        }

        _pressPending = true;
        IsPressedVisual = true;
        _isTouchPress = isTouch;
        _lastVariants = ComputeTouchVariants(pos);
    }

    private void Release(Point pos)
    {
        if (!_pressPending)
        {
            return;
        }

        // 滑出键矩形后抬起 = 取消（防误触）；键内抬起才提交点按
        var inBounds = pos.X >= 0 && pos.X <= ActualWidth && pos.Y >= 0 && pos.Y <= ActualHeight;
        _pressPending = false;
        ResetPressState();
        if (!inBounds)
        {
            // 批 2026-10-05（冻结区 A 方案：**只加观测，判定一行未改**，用户点名批准）：
            // "滑出即取消"是有意的防误触语义，但它**静默**——按下有高亮、抬手没结果，最易被感知成"这次没呼出/少打一个字"。
            // 这里记下越界量（DIP），Deck 复现一次即可判定"1 键呼不出"是否由手指在键框外抬起造成，以及容差该取多大。
            var dx = pos.X < 0 ? pos.X : (pos.X > ActualWidth ? pos.X - ActualWidth : 0);
            var dy = pos.Y < 0 ? pos.Y : (pos.Y > ActualHeight ? pos.Y - ActualHeight : 0);
            FileLogger.Info($"key-drop[out-of-bounds]: key={Digit} pos=({pos.X:F1},{pos.Y:F1}) " +
                $"keyRect=(0,0,{ActualWidth:F1},{ActualHeight:F1}) overshoot=({dx:F1},{dy:F1})");
            return;
        }

        var info = new KeyPressInfo(Digit, CommitDirect, _lastVariants);
        DigitPressed?.Invoke(info);
    }

    // ⚠ 坑：DragMove（窗口拖动）会抢鼠标捕获并触发 LostMouseCapture，
    // 提交判断必须用独立的 _pressPending，不能依赖会被 LostMouseCapture 复位的 IsPressedVisual。
    private void ResetPressState()
    {
        IsPressedVisual = false;
    }

    // ---- 鼠标适配（与触屏走同一状态机）----

    private void OnMousePress(object sender, MouseButtonEventArgs e)
    {
        if (SuppressMouse() || !TryAcceptPress())
        {
            return;
        }

        // 按下即捕获：滑出键边界后仍能收到 MouseUp，保证按下态可复位
        _ = CaptureMouse();
        Press(e.GetPosition(this), false);
        // ⚠ 关键：按键按下必须吃掉事件，阻止冒泡到 RootBorder 启动 DragMove 抢走捕获（否则点击被吞）。
        e.Handled = true;
    }

    private void OnMouseRelease(object sender, MouseButtonEventArgs e)
    {
        if (SuppressMouse())
        {
            return;
        }

        Release(e.GetPosition(this));
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void OnCaptureLost(object sender, MouseEventArgs e)
    {
        ResetPressState();
    }

    // ---- 触屏适配（§13.9：触摸走 Touch 事件通道；多点触摸取首触点）----

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        var pos = e.GetTouchPoint(this).Position;
        var now = Environment.TickCount64;
        _touchFilter.RecordRawTouch();

        if (!_touchFilter.TryAcceptTouch(now, new TouchPoint2D(pos.X, pos.Y), _activeTouch is not null, out _))
        {
            e.Handled = true;
            return;
        }

        if (_activeTouch is not null)
        {
            // 已有活动触点且未被幽灵过滤：继续忽略，防止打断进行中的手势
            e.Handled = true;
            return;
        }

        if (!TryAcceptPress())
        {
            e.Handled = true;
            return;
        }

        _activeTouch = e.TouchDevice;
        _touchActive = true;
        _touchFilter.RecordAcceptedTouch(now, new TouchPoint2D(pos.X, pos.Y));
        _ = CaptureTouch(e.TouchDevice); // 按下即捕获：滑出键边界后仍收到 TouchUp，保证状态闭环
        Press(pos, true);
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (_touchActive && ReferenceEquals(e.TouchDevice, _activeTouch))
        {
            var pos = e.GetTouchPoint(this).Position;
            Release(pos);
            _activeTouch = null;
            _touchActive = false;
            _lastTouchUp = Environment.TickCount64;
            _touchFilter.RecordTouchUp(_lastTouchUp, new TouchPoint2D(pos.X, pos.Y));
            ReleaseTouchCapture(e.TouchDevice);
            e.Handled = true;
        }
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        var pos = e.GetTouchPoint(this).Position;
        var now = Environment.TickCount64;
        if (ReferenceEquals(e.TouchDevice, _activeTouch))
        {
            _activeTouch = null;
        }

        _touchActive = false;
        _lastTouchUp = now;
        _touchFilter.RecordTouchUp(now, new TouchPoint2D(pos.X, pos.Y));

        // 批 2026-10-05（冻结区 A 方案：只加观测，判定未改）：捕获丢失时**只复位不提交**（必要，否则会误提交），
        // 但它静默——`pressPending=true` 就是"按下去却没有结果"的现场（第三条静默丢弃路径）。
        FileLogger.Info($"key-drop[lost-capture]: key={Digit} pos=({pos.X:F1},{pos.Y:F1}) pressPending={_pressPending}");
        ResetPressState();
    }

    /// <summary>WPF 触屏会提升为鼠标事件：触摸活跃期及抬起后 500ms 内屏蔽鼠标路径，防双触发。</summary>
    private bool SuppressMouse() =>
        _touchActive || Environment.TickCount64 - _lastTouchUp < 500;

    /// <summary>
    /// §13.37：跨键时间窗抑制。任意键 Pressed 后 50ms 内其他键 Pressed 一律丢弃（不进缓冲、不产生误触变体）。
    /// 人类最快交替按键 >100ms，50ms 内跨键必为幽灵。
    /// </summary>
    private bool TryAcceptPress()
    {
        var now = Environment.TickCount64;
        var accepted = CrossKeySuppressor.TryAccept(now, this, out var reason);
        if (!accepted)
        {
            // 批 2026-10-05（冻结区 A 方案：只加观测，判定未改）：时间窗抑制会**静默**吃掉这次按下——
            // 同键 100ms（防触屏+鼠标提升双发）/ 跨键 50ms（防幽灵触点）。连点同一颗键太快也会命中同键窗，
            // 这正是"1 键无法每次呼出"的第二大嫌疑，日志带原因 + 与上次按键的间隔（ms）+ 累计抑制数。
            FileLogger.Info($"key-drop[suppress]: key={Digit} reason={reason} " +
                $"gap={now - CrossKeySuppressor.LastPressTime}ms total={CrossKeySuppressor.SuppressedCount}");
        }

        return accepted;
    }

    /// <summary>§M8-1：根据触摸点相对按键矩形的位置，计算本键及四邻键的概率。</summary>
    private IReadOnlyList<TouchVariant> ComputeTouchVariants(Point pos)
    {
        // §M8-1：仅触屏输入启用误触纠正；鼠标点击视为精确输入。
        if (!_isTouchPress || Digit.Length != 1 || !char.IsDigit(Digit[0]) || TouchCorrectionSigma <= 0)
        {
            return [new TouchVariant(Digit[0], 1.0)];
        }

        var variants = new List<TouchVariant> { new(Digit[0], 1.0) };
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return variants;
        }

        var neighbors = T9Neighbors.Get(Digit[0]);
        var sigma = TouchCorrectionSigma;

        void TryAdd(char? neighbor, double distance)
        {
            if (!neighbor.HasValue || distance <= 0)
            {
                return;
            }

            var p = Math.Exp(-distance / sigma);
            if (p >= 0.05)
            {
                variants.Add(new TouchVariant(neighbor.Value, p));
            }
        }

        TryAdd(neighbors.Left, pos.X);
        TryAdd(neighbors.Right, w - pos.X);
        TryAdd(neighbors.Up, pos.Y);
        TryAdd(neighbors.Down, h - pos.Y);

        return variants;
    }
}
