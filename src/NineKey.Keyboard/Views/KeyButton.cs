/*// 本文件职责：T9 按键控件，处理点按/Flick 手势、提示泡渲染、误触概率计算与鼠标/触屏双通道输入。
// 数据流位置：KeyboardWindow 配置键定义 → KeyButton 识别手势 → 触发 DigitPressed/FlickCommitted → KeyController 处理输入。
// ⚠ 坑 1：WPF 触屏会提升为鼠标事件，必须用 _touchActive 和 500ms 抑制避免双发。
// ⚠ 坑 2：手势锚定（CaptureMouse/CaptureTouch）必须按下即做，滑出键边界后仍需持续收到 Move。
// ⚠ 坑 3：丢失 MouseUp/TouchUp 时状态会卡住，Press 入口和 LostCapture 都要做防御复位。
// 相关规格：§2.1、§2.2、§4.9、§13.9、§13.13、§13.15、§13.17、§13.19、§13.33、§13.37、§M8-1。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
/// T9 按键（§2.1/§2.2）：点按 = 中心值；Flick = 方向槽字母。
/// 鼠标与触屏统一适配到同一个 FlickGestureRecognizer（§4.9）。
/// </summary>
public class KeyButton : Control
{
    /// <summary>提示泡半径（px）。取 52：64px 气泡相邻中心距 ≈73.5 > 68，互不重叠（§13.15 下限 64×64）。</summary>
    public const double PopupRadius = 52;

    /// <summary>§13.33：气泡延迟 T1（默认 150ms）。</summary>
    public long BubbleDelayMs { get; set; } = 150;

    /// <summary>§13.33：气泡显示 T2（默认 200ms）。</summary>
    public long BubbleShowMs { get; set; } = 200;

    private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2F, 0x7C, 0xD6));

    // 方向槽角度（0=上，顺时针）：槽 0=右 1=下 2=左 3=上
    private static readonly double[] SlotAngles = [90, 180, 270, 0];

    private Canvas? _popupCanvas;
    private System.Windows.Controls.Primitives.Popup? _bubblePopup;
    private FlickGestureRecognizer? _recognizer;
    private readonly Dictionary<int, Border> _bubbleBySlot = [];
    private Border? _centerBubble;
    private List<string> _slots = ["", "", "", ""];
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _touchActive;
    private long _lastTouchUp;
    private bool _isTouchPress;
    private readonly TouchGhostFilter _touchFilter = new();
    private IReadOnlyList<TouchVariant> _lastVariants = [new('0', 1.0)];

    static KeyButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KeyButton), new FrameworkPropertyMetadata(typeof(KeyButton)));
    }

    /// <summary>创建一个 T9 按键，注册鼠标与触屏事件。</summary>
    public KeyButton()
    {
        MouseLeftButtonDown += OnMousePress;
        MouseMove += OnMouseDrag;
        MouseLeftButtonUp += OnMouseRelease;
        LostMouseCapture += OnCaptureLost;
        _holdTimer.Tick += (_, _) =>
        {
            if (_recognizer?.CheckTimeout(Environment.TickCount64) == true)
            {
                // 长按（F4）：字母键不重复。§13.15：提示泡持续到抬起才消失，此处只停表不关泡
                _holdTimer.Stop();
            }

            UpdateBubbleVisibility();
        };
    }

    /// <summary>提交事件：(值, 是否直接上屏)。Tap 提交 Digit，Flick 提交方向槽字母。</summary>
    public event Action<string, bool>? FlickCommitted;

    /// <summary>§M8-1：带误触概率的按键事件。Tap 提供多位候选概率；Flick 视为精确输入。</summary>
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

    /// <summary>中心显示字（提示泡中心 + 键面小字首字母），如 "A"。</summary>
    public string Center
    {
        get => (string)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public static readonly DependencyProperty CenterProperty =
        DependencyProperty.Register(nameof(Center), typeof(string), typeof(KeyButton),
            new PropertyMetadata(string.Empty, (d, _) => ((KeyButton)d).UpdatePopup()));

    /// <summary>Flick 方向槽，'|' 分隔，固定 4 槽（右|下|左|上），空槽用空串（如 "B|C||"）。</summary>
    public string Options
    {
        get => (string)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public static readonly DependencyProperty OptionsProperty =
        DependencyProperty.Register(nameof(Options), typeof(string), typeof(KeyButton),
            new PropertyMetadata(string.Empty, (d, _) => ((KeyButton)d).UpdatePopup()));

    /// <summary>键面小字（由 Center + 非空槽自动生成）。</summary>
    public string OptionsLabel
    {
        get => (string)GetValue(OptionsLabelProperty);
        private set => SetValue(OptionsLabelPropertyKey, value);
    }

    public static readonly DependencyPropertyKey OptionsLabelPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(OptionsLabel), typeof(string), typeof(KeyButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty OptionsLabelProperty = OptionsLabelPropertyKey.DependencyProperty;

    /// <summary>true：Flick 结果直接上屏（标点/空格）；false：进入输入串（字母/数字）。</summary>
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

    public int HighlightIndex
    {
        get => (int)GetValue(HighlightIndexProperty);
        private set => SetValue(HighlightIndexPropertyKey, value);
    }

    public static readonly DependencyPropertyKey HighlightIndexPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HighlightIndex), typeof(int), typeof(KeyButton), new PropertyMetadata(-1));

    public static readonly DependencyProperty HighlightIndexProperty = HighlightIndexPropertyKey.DependencyProperty;

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

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bubblePopup = GetTemplateChild("BubblePopup") as System.Windows.Controls.Primitives.Popup;
        _popupCanvas = GetTemplateChild("PopupCanvas") as Canvas;
        UpdatePopup();
    }

    private List<string> ParseSlots()
    {
        var parts = (Options ?? string.Empty).Split('|');
        var slots = new List<string> { "", "", "", "" };
        for (var i = 0; i < Math.Min(4, parts.Length); i++)
        {
            slots[i] = parts[i];
        }

        return slots;
    }

    private void UpdatePopup()
    {
        _slots = ParseSlots();
        OptionsLabel = (Center ?? string.Empty) + string.Concat(_slots);
        if (_popupCanvas is not null && IsPressedVisual)
        {
            RebuildBubbles();
        }
    }

    private void RebuildBubbles()
    {
        if (_popupCanvas is null)
        {
            return;
        }

        _popupCanvas.Children.Clear();
        _bubbleBySlot.Clear();
        var w = Math.Max(_popupCanvas.Width, ActualWidth + PopupPad * 2);
        var h = Math.Max(_popupCanvas.Height, ActualHeight + PopupPad * 2);

        // 中心气泡（F1：显示中心字）
        _centerBubble = MakeBubble(string.IsNullOrEmpty(Center) ? Digit : Center, null);
        _ = _popupCanvas.Children.Add(_centerBubble);
        Canvas.SetLeft(_centerBubble, w / 2 - BubbleSize / 2);
        Canvas.SetTop(_centerBubble, h / 2 - BubbleSize / 2);

        // 方向槽气泡：空槽不显示（§13.9 取消区）；标注：左=「点/左」（§13.17），其余标方向箭头
        for (var i = 0; i < 4; i++)
        {
            if (string.IsNullOrEmpty(_slots[i]))
            {
                continue;
            }

            var angle = SlotAngles[i] * Math.PI / 180;
            var cx = w / 2 + Math.Sin(angle) * PopupRadius;
            var cy = h / 2 - Math.Cos(angle) * PopupRadius;
            var bubble = MakeBubble(_slots[i], SlotCaption(i));
            _ = _popupCanvas.Children.Add(bubble);
            Canvas.SetLeft(bubble, cx - BubbleSize / 2);
            Canvas.SetTop(bubble, cy - BubbleSize / 2);
            _bubbleBySlot[i] = bubble;
        }

        // F7：提示泡动画 ≤120ms（WPF 渲染线程 GPU 合成）
        _popupCanvas.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80)));
    }

    /// <summary>提示泡 Canvas 四周余量 = 泡半径 + 气泡半径 + 边距。</summary>
    private const double PopupPad = PopupRadius + 32 + 4;

    /// <summary>气泡尺寸（§13.15 下限 64×64）。</summary>
    public const double BubbleSize = 64;

    /// <summary>方向槽标注（§13.17：左槽标「点/左」教用户点按=左滑等价；其余标方向箭头）。</summary>
    private static string SlotCaption(int slot) => slot switch
    {
        0 => "→",
        1 => "↓",
        2 => "点/左",
        _ => "↑",
    };

    private Border MakeBubble(string text, string? caption)
    {
        var panel = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Foreground,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });
        if (caption is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = SubForeground,
                FontSize = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });
        }

        return new Border
        {
            Width = BubbleSize,
            Height = BubbleSize,
            CornerRadius = new CornerRadius(BubbleSize / 2),
            Background = KeyPressedBackground,
            BorderBrush = KeyBorderBrush,
            BorderThickness = new Thickness(1),
            Child = panel,
        };
    }

    private void ApplyHighlight()
    {
        foreach (var (slot, bubble) in _bubbleBySlot)
        {
            bubble.Background = slot == HighlightIndex ? HighlightBrush : KeyPressedBackground;
        }
    }

    private void Press(Point pos, bool isTouch)
    {
        if (IsPressedVisual)
        {
            // 防御：合成输入或异常路径可能丢失抬起事件导致状态卡住，新按下前先复位
            _recognizer?.Cancel();
            ClosePopup();
        }

        IsPressedVisual = true;
        // §13.19-1：Holding→Flicking 复活仅字母键开放（CommitDirect 的 1/0 键不开放）
        _recognizer = new FlickGestureRecognizer(_slots, allowHoldEscapeToFlick: !CommitDirect)
        {
            BubbleDelayMs = BubbleDelayMs,
            BubbleShowMs = BubbleShowMs,
        };
        _recognizer.Begin(pos.X, pos.Y, Environment.TickCount64);
        _isTouchPress = isTouch;
        _lastVariants = ComputeTouchVariants(pos);
        _holdTimer.Start();
        if (_popupCanvas is not null && _bubblePopup is not null)
        {
            // Canvas 比键大一圈（四周 PopupPad），保证 Popup 窗口容纳全部方向气泡（P1-10）
            _popupCanvas.Width = ActualWidth + PopupPad * 2;
            _popupCanvas.Height = ActualHeight + PopupPad * 2;
            _bubblePopup.IsOpen = false; // §13.33：T1 内不弹，由时序/位移控制
            RebuildBubbles();
        }
    }

    private void Drag(Point pos)
    {
        if (_recognizer is null)
        {
            return;
        }

        _recognizer.Move(pos.X, pos.Y, Environment.TickCount64);
        HighlightIndex = _recognizer.HighlightSlot;
        ApplyHighlight();
        UpdateBubbleVisibility();
    }

    private void UpdateBubbleVisibility()
    {
        if (_bubblePopup is null || _recognizer is null)
        {
            return;
        }

        var show = _recognizer.ShouldShowBubble(Environment.TickCount64);
        if (_bubblePopup.IsOpen != show)
        {
            _bubblePopup.IsOpen = show;
            if (show && _popupCanvas is not null && _popupCanvas.Children.Count == 0)
            {
                RebuildBubbles();
            }
        }
    }

    private void Release(Point pos)
    {
        if (_recognizer is null)
        {
            return;
        }

        var result = _recognizer.End(pos.X, pos.Y, Environment.TickCount64);
        ClosePopup();

        switch (result.Outcome)
        {
            case FlickOutcome.Tap:
            {
                // 点按 = 中心值：数字键进输入串（T9），直投键上屏（F6 中心字语义见 §2.1 双输入方式并存）
                FlickCommitted?.Invoke(Digit, CommitDirect);
                var info = new KeyPressInfo(Digit, CommitDirect, false, _lastVariants);
                FileLogger.Info($"digit-pressed: value={info.Value} source={(_isTouchPress ? "touch" : "mouse")} commitDirect={info.CommitDirect}");
                DigitPressed?.Invoke(info);
                break;
            }
            case FlickOutcome.Commit when result.Slot >= 0 && result.Slot < 4:
            {
                var flickValue = _slots[result.Slot];
                FlickCommitted?.Invoke(flickValue, CommitDirect);
                var info = new KeyPressInfo(flickValue, CommitDirect, true, [new TouchVariant(flickValue[0], 1.0)]);
                FileLogger.Info($"digit-pressed: value={info.Value} source=flick commitDirect={info.CommitDirect}");
                DigitPressed?.Invoke(info);
                break;
            }
            // Cancelled / None：F5 回滑取消或长按结束，不上字
        }
    }

    // ---- 鼠标适配（F8：与触屏走同一状态机）----

    private void OnMousePress(object sender, MouseButtonEventArgs e)
    {
        if (SuppressMouse() || !TryAcceptPress())
        {
            return;
        }

        // §13.13 手势锚定：按下即捕获起始键，之后滑动穿越其他键时事件仍路由到本键
        _ = CaptureMouse();
        Press(e.GetPosition(this), false);
    }

    private void OnMouseDrag(object sender, MouseEventArgs e)
    {
        if (SuppressMouse())
        {
            return;
        }

        Drag(e.GetPosition(this));
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
        _recognizer?.Cancel();
        ClosePopup();
    }

    // ---- 触屏适配（§13.9：触摸走 Touch 事件通道 + 按下即捕获；多点触摸取首触点）----

    private TouchDevice? _activeTouch;

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        var pos = e.GetTouchPoint(this).Position;
        var now = Environment.TickCount64;
        _touchFilter.RecordRawTouch();

        if (!_touchFilter.TryAcceptTouch(now, new TouchPoint2D(pos.X, pos.Y), _activeTouch is not null, out var reason))
        {
            var (raw, accepted, ghost) = _touchFilter.Counts;
            FileLogger.Info($"touch ghost merged: raw={raw} accepted={accepted} ghost={ghost} suppressed={CrossKeySuppressor.SuppressedCount} reason={reason}");
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
        _ = CaptureTouch(e.TouchDevice); // 按下即捕获：滑出键边界后仍持续收到 Move（防断触）
        Press(pos, true);
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (_touchActive && ReferenceEquals(e.TouchDevice, _activeTouch))
        {
            Drag(e.GetTouchPoint(this).Position);
            e.Handled = true;
        }
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
        _recognizer?.Cancel();
        ClosePopup();
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
        var lastTime = CrossKeySuppressor.LastPressTime;
        var delta = lastTime == 0 ? -1 : now - lastTime;
        var accepted = CrossKeySuppressor.TryAccept(now, this, out var reason);
        FileLogger.Info($"key-press: digit={Digit} delta={delta}ms accepted={accepted} reason={reason} suppressed={CrossKeySuppressor.SuppressedCount}");
        return accepted;
    }

    private void ClosePopup()
    {
        _holdTimer.Stop();
        IsPressedVisual = false;
        HighlightIndex = -1;
        if (_bubblePopup is not null)
        {
            _bubblePopup.IsOpen = false;
        }
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
*/
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
            return;
        }

        var info = new KeyPressInfo(Digit, CommitDirect, false, _lastVariants);
        FileLogger.Info($"digit-pressed: value={info.Value} source={(_isTouchPress ? "touch" : "mouse")} commitDirect={info.CommitDirect}");
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

        if (!_touchFilter.TryAcceptTouch(now, new TouchPoint2D(pos.X, pos.Y), _activeTouch is not null, out var reason))
        {
            var (raw, accepted, ghost) = _touchFilter.Counts;
            FileLogger.Info($"touch ghost merged: raw={raw} accepted={accepted} ghost={ghost} suppressed={CrossKeySuppressor.SuppressedCount} reason={reason}");
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
        var lastTime = CrossKeySuppressor.LastPressTime;
        var delta = lastTime == 0 ? -1 : now - lastTime;
        var accepted = CrossKeySuppressor.TryAccept(now, this, out var reason);
        FileLogger.Info($"key-press: digit={Digit} delta={delta}ms accepted={accepted} reason={reason} suppressed={CrossKeySuppressor.SuppressedCount}");
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
