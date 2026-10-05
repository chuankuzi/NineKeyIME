// 本文件职责：候选栏控件，渲染当前页候选或历史上屏，处理点击与翻页事件。
// 数据流位置：KeyController 状态变化 → KeyboardWindow 调用 Render → CandidatesPanel 生成按钮 → 点击回调 CommitCandidate。
// ⚠ 坑 1：每次 Render 必须清空 Children，否则翻页/状态变化会重复叠加按钮。
// ⚠ 坑 2：MouseUp 丢失时按钮可能残留鼠标捕获，提交后必须显式释放，否则后续点击被吞。
// ⚠ 坑 3：空输入时显示历史上屏，但历史上屏不走引擎排名，直接按最近上屏顺序排列。
// 相关规格：§2.3、§13.5、§M8-5。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using NineKey.Core.Engine;
using NineKey.Keyboard.Input;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace NineKey.Keyboard.Views;

/// <summary>候选栏：横向候选 + 翻页。空输入串时显示历史上屏。</summary>
public partial class CandidateBar : UserControl
{
    public CandidateBar()
    {
        InitializeComponent();

        // 批5 滑动手势：Preview（隧道）级接入，触摸与鼠标**共用同一套判定**（同一控制器实例）。
        // ⚠ 坑：候选按钮会 CaptureMouse，Move/Up 只保证送到按钮自身；所以候选条与每个候选按钮**双通道**
        // 都接同一套处理器，由控制器的 _consumed 保证同一手势只翻一页。
        AttachGesture(this);
        EnsureCandidateMenu();
    }

    /// <summary>用户点击某个候选时触发。</summary>
    public event Action<Candidate>? CandidateClicked;

    /// <summary>用户点击下一页时触发。</summary>
    public event Action? PageNext;

    /// <summary>用户点击上一页时触发。</summary>
    public event Action? PagePrevious;

    public Brush BarBackground
    {
        get => (Brush)GetValue(BarBackgroundProperty);
        set => SetValue(BarBackgroundProperty, value);
    }

    public static readonly DependencyProperty BarBackgroundProperty =
        DependencyProperty.Register(nameof(BarBackground), typeof(Brush), typeof(CandidateBar),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2E))));

    public Brush SubForeground
    {
        get => (Brush)GetValue(SubForegroundProperty);
        set => SetValue(SubForegroundProperty, value);
    }

    public static readonly DependencyProperty SubForegroundProperty =
        DependencyProperty.Register(nameof(SubForeground), typeof(Brush), typeof(CandidateBar),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))));

    /// <summary>
    /// 根据 KeyController 当前状态重绘候选栏。
    /// </summary>
    /// <param name="controller">输入控制器，提供候选、翻页、历史上屏。</param>
    public void Render(KeyController controller)
    {
        _controller = controller; // 长按菜单直接调它的删词/置顶（避免为接线去动 KeyboardWindow）

        // ⚠ 坑：不清空会累积旧候选，翻页时尤其明显。
        CandidatesPanel.Children.Clear();

        if (controller.IsEmpty)
        {
            foreach (var word in controller.History)
            {
                CandidatesPanel.Children.Add(MakeCandidateButton(new Candidate(word, CandidateSource.FullMatch, 0), isHistory: true));
            }

            CountLabel.Text = controller.History.Count > 0 ? "历史" : string.Empty;
            return;
        }

        foreach (var candidate in controller.CurrentPage)
        {
            CandidatesPanel.Children.Add(MakeCandidateButton(candidate, isHistory: false));
        }

        CountLabel.Text = controller.TotalCount > 0
            ? $"{controller.PageIndex + 1}/{controller.PageCount} ({controller.TotalCount})"
            : "无候选";
    }

    /// <summary>
    /// 创建一个候选按钮。
    /// </summary>
    /// <param name="candidate">候选对象。</param>
    /// <param name="isHistory">true 表示来自历史上屏，false 表示引擎候选。</param>
    private Button MakeCandidateButton(Candidate candidate, bool isHistory)
    {
        var btn = new Button
        {
            Content = candidate.Text,
            Style = TryFindResource("CandidateButtonStyle") as Style,
        };
        if (!isHistory && candidate.Source == CandidateSource.JianpinMatch)
        {
            btn.ToolTip = "简拼匹配";
        }

        // 批 2026-10-05：PinyinGuide（拼音组合引导项）已从候选行剥离到"键盘上方浮条"，
        // 候选行不会再出现该来源，原先的着色/提示分支随之删除（死代码按纪律清掉）。

        // 批7 长按：按住 500ms 弹小菜单（删除/置顶/取消）；位移超容差或抬手即取消。
        AttachGesture(btn);   // 滑动与长按共用同一控制器（按钮 CaptureMouse 时也能收到 Move/Up）
        btn.PreviewMouseLeftButtonDown += (_, _) => StartLongPress(btn, candidate);
        btn.PreviewTouchDown += (_, _) => StartLongPress(btn, candidate);
        btn.PreviewMouseLeftButtonUp += (_, _) => CancelLongPress();
        btn.PreviewTouchUp += (_, _) => CancelLongPress();
        btn.MouseLeave += (_, _) => CancelLongPress();

        btn.Click += (_, _) =>
        {
            if (_longPressFired)
            {
                _longPressFired = false; // 长按弹过菜单：本次抬起不算点击
                return;
            }

            // ⚠ 坑：合成输入或异常路径会丢失 MouseUp，导致捕获残留，提交后必须显式释放。
            if (ReferenceEquals(Mouse.Captured, btn))
            {
                btn.ReleaseMouseCapture();
            }

            CandidateClicked?.Invoke(candidate);
        };
        return btn;
    }

    // ---- 批5：候选条滑动手势（仅候选条本体区域；水平占优且过阈值才翻页，未过阈值交还原点击逻辑）----

    /// <summary>手势判定控制器（触摸与鼠标共用；纯逻辑，见 CandidateGesture.cs）。</summary>
    private readonly CandidateGestureController _gesture = new();

    private System.Windows.Point _lastPoint;

    /// <summary>给元素接上触摸 + 鼠标两套 Preview 处理器（候选条与候选按钮都接，规避 CaptureMouse 抢投递）。
    /// 两条路径都走同一组 internal 入口，测试可直调同一份代码（RaiseEvent 无法注入坐标：GetPosition 读真实光标）。</summary>
    private void AttachGesture(System.Windows.UIElement element)
    {
        element.PreviewTouchDown += (_, e) => GestureDown(e.GetTouchPoint(this).Position.X, e.GetTouchPoint(this).Position.Y);
        element.PreviewTouchMove += (_, e) => GestureMove(e.GetTouchPoint(this).Position.X, e.GetTouchPoint(this).Position.Y);
        element.PreviewTouchUp += (_, e) => GestureUp(e.GetTouchPoint(this).Position.X, e.GetTouchPoint(this).Position.Y, () => e.Handled = true);
        element.PreviewMouseLeftButtonDown += (_, e) => GestureDown(e.GetPosition(this).X, e.GetPosition(this).Y);
        element.PreviewMouseMove += (_, e) => GestureMove(e.GetPosition(this).X, e.GetPosition(this).Y);
        element.PreviewMouseLeftButtonUp += (_, e) => GestureUp(e.GetPosition(this).X, e.GetPosition(this).Y, () => e.Handled = true);
    }

    /// <summary>手势入口：按下（触摸/鼠标共用，测试直调）。</summary>
    internal void GestureDown(double x, double y)
    {
        _lastPoint = new System.Windows.Point(x, y);
        _gesture.Down(x, y);
    }

    /// <summary>手势入口：移动（触摸/鼠标共用，测试直调）。</summary>
    internal void GestureMove(double x, double y)
    {
        _lastPoint = new System.Windows.Point(x, y);
        _gesture.Move(x, y);
    }

    /// <summary>手势入口：抬起（触摸/鼠标共用，测试直调）。</summary>
    internal void GestureUp(double x, double y, Action? markHandled = null)
    {
        _lastPoint = new System.Windows.Point(x, y);
        FinishGesture(_lastPoint, markHandled ?? (() => { }));
    }

    /// <summary>测试用：让长按定时器逻辑按当前状态判定一次（等价于 500ms 到点）。</summary>
    internal bool FireLongPressForTests() => _gesture.TryFireLongPress();

    /// <summary>测试用：长按定时器的 Tick 处理器是否已挂上（批7 情况A 根因回归断言点）。</summary>
    internal bool HasLongPressTickHandler => _longPressTickWired;

    /// <summary>抬起统一出口：控制器给结论，+1 上一页 / -1 下一页 / 0 交还原点击逻辑。</summary>
    private void FinishGesture(System.Windows.Point p, Action markHandled)
    {
        var verdict = _gesture.Up(p.X, p.Y);
        if (verdict < 0)
        {
            PageNext?.Invoke();     // 左滑 = 下一页
        }
        else if (verdict > 0)
        {
            PagePrevious?.Invoke(); // 右滑 = 上一页
        }
        else
        {
            return;                 // 未构成滑动：不吞事件，原点击逻辑零变化
        }

        markHandled();
    }

    // ---- 批7：长按候选弹小菜单（删除 / 置顶 / 取消）----

    /// <summary>长按判定时长（毫秒）：与控制器同源，避免两处漂移。</summary>
    private const int LongPressMs = CandidateGestureController.LongPressMs;

    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromMilliseconds(LongPressMs) };

    private KeyController? _controller;
    private Candidate? _longPressCandidate;
    private Button? _longPressButton;
    private bool _longPressFired;
    private bool _longPressTickWired;
    private Popup? _candidateMenu;
    private System.Windows.Controls.Border? _candidateMenuBorder;
    private Button? _menuDeleteButton;

    private void StartLongPress(Button btn, Candidate candidate)
    {
        // ⚠ 坑（批7 情况A 根因）：菜单与 Tick 处理器原先只在 EnsureCandidateMenu 里建，而该方法是死代码
        // （全文件零调用）→ 定时器到点没有处理器，长按永远不弹菜单。这里先确保建好再启动。
        EnsureCandidateMenu();
        _longPressCandidate = candidate;
        _longPressButton = btn;
        _longPressTimer.Stop();
        _longPressTimer.Start();
    }

    private void CancelLongPress()
    {
        _longPressTimer.Stop();
    }

    private void EnsureCandidateMenu()
    {
        if (_candidateMenu is not null)
        {
            return;
        }

        _longPressTickWired = true;
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            if (!_gesture.TryFireLongPress())
            {
                return;     // 已构成滑动或已触发过 → 长按让位
            }

            if (_longPressButton is null || _longPressCandidate is null)
            {
                return;
            }

            _longPressFired = true;
            OpenCandidateMenu(_longPressButton, _longPressCandidate);
        };

        var stack = new System.Windows.Controls.StackPanel();
        _menuDeleteButton = MakeMenuButton("删除", () =>
        {
            var c = _longPressCandidate;
            CloseCandidateMenu();
            if (c is not null)
            {
                _ = _controller?.RemoveUserWord(c);
            }
        });
        var pin = MakeMenuButton("置顶", () =>
        {
            var c = _longPressCandidate;
            CloseCandidateMenu();
            if (c is not null)
            {
                _ = _controller?.PinCandidate(c);
            }
        });
        var cancel = MakeMenuButton("取消", CloseCandidateMenu);
        _ = stack.Children.Add(_menuDeleteButton);
        _ = stack.Children.Add(pin);
        _ = stack.Children.Add(cancel);

        // 主题跟随：底色取候选栏底色，文字沿用 SubForeground（浅色主题不白底白字）
        _candidateMenuBorder = new System.Windows.Controls.Border
        {
            Background = BarBackground,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Child = stack,
        };

        // ⚠ 坑：本应用早前用 ContextMenu 做长按菜单实测不可达，故沿用已验证的 Popup 方案（1 键符号选框同款）。
        _candidateMenu = new Popup
        {
            StaysOpen = false, // 点菜单外即关（外部点击被菜单吃掉，不会误上屏）
            AllowsTransparency = true,
            Focusable = false, // 焦点防御纪律：新控件一律不入焦点链
            Child = _candidateMenuBorder,
        };

        if (Parent is System.Windows.Controls.Panel panel)
        {
            _ = panel.Children.Add(_candidateMenu);
        }
    }

    private Button MakeMenuButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            Style = TryFindResource("CandidateButtonStyle") as Style,
            Focusable = false,
            MinWidth = 76,
            Foreground = SubForeground,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void OpenCandidateMenu(Button target, Candidate candidate)
    {
        if (_candidateMenu is null)
        {
            return;
        }

        _longPressCandidate = candidate;
        if (_menuDeleteButton is not null)
        {
            // ⚠ 需求：删除仅用户词可用；系统词只有置顶/取消。
            _menuDeleteButton.IsEnabled = _controller?.IsUserWord(candidate) ?? false;
        }

        _candidateMenu.PlacementTarget = target;
        _candidateMenu.Placement = PlacementMode.Top;
        _candidateMenu.IsOpen = true;
    }

    private void CloseCandidateMenu()
    {
        if (_candidateMenu is not null && _candidateMenu.IsOpen)
        {
            _candidateMenu.IsOpen = false;
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e) => PageNext?.Invoke();

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => PagePrevious?.Invoke();
}
