// 本文件职责：KeyboardWindow 的贴边系统（右缘吸附、竖条收起与展开、窗口几何夹取、贴边拖动）。
// 数据流位置：拖动/悬停/ESC/托盘 → TrySnapToEdges / DockToRightEdge / ExpandFromStrip → 窗口 Left/Top/Width/Height。
// ⚠ 坑：贴边状态字段只有唯一读写入口，转场仅「进入贴边/开动画锁/收动画锁/脱离贴边」四种，禁止散点直改。
// 拆分说明：2026-10-02 由 KeyboardWindow.xaml.cs 原样剪切搬运（partial class 拆分，零行为改动）。

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using NineKey.Core.Dictionary;
using NineKey.Core.Engine;
using NineKey.Keyboard.Input;
using NineKey.Keyboard.Services;
using NineKey.Keyboard.Settings;
using OpenccNetLib;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace NineKey.Keyboard.Views;

/// <summary>KeyboardWindow 贴边部分：右缘吸附、竖条收起/展开与几何夹取。</summary>
public partial class KeyboardWindow
{

    // §13.44 / §13.45：贴边系统
    private const double EdgeSnapDistance = 32.0;
    private const double DockedStripWidth = 32.0;
    private const double DockedStripHeight = 30;                   //长度
    private static readonly Duration StripAnimationDuration = new(TimeSpan.FromMilliseconds(200));
    private bool _isDockedAsStrip;
    private bool _isAnimatingStrip;
    private bool _isDraggingTitle;
    //private System.Windows.Point _dragStartPoint;
    //private bool _dragCandidate;
    private Rect _restoredBounds;
    private readonly DispatcherTimer _stripHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // ⚠ 坑（2026-09-14 清理批②）：贴边状态两次 stale-cache bug 同模式复发，根因是上面三个字段被
    // 十几处直接读写，状态机隐没在散点里。此处收拢为唯一读写入口——读走属性、写走方法，
    // 转场只有「进入贴边 / 开动画锁 / 收动画锁 / 脱离贴边」四种，禁止散点直改字段。
    private bool IsDockedAsStrip => _isDockedAsStrip;
    private bool IsStripAnimating => _isAnimatingStrip;
    /// <summary>贴边或贴边动画期间：窗口几何不由常规尺寸路径管理。</summary>
    private bool IsDockActive => _isDockedAsStrip || _isAnimatingStrip;
    /// <summary>展开要恢复的几何（宽高在展开时按当前模式现算，此处只消费 Left/Top）。</summary>
    private Rect RestoredBounds => _restoredBounds;

    /// <summary>进入贴边：开动画锁、缓存当前几何为恢复位置、置贴边态。</summary>
    private void EnterDockedState()
    {
        CloseKey1SymbolPopup(); // 贴边收起前先收掉符号选框（弹层是独立窗口，不会随窗口收成细条）
        _isAnimatingStrip = true;
        _restoredBounds = new Rect(Left, Top, Width, Height);
        _isDockedAsStrip = true;
    }

    /// <summary>开动画锁（贴边已置位，仅防展开动画重入）。</summary>
    private void BeginStripAnimation() => _isAnimatingStrip = true;

    /// <summary>收动画锁（贴边方向动画结束，保持贴边态）。</summary>
    private void EndStripAnimation() => _isAnimatingStrip = false;

    /// <summary>脱离贴边（展开完成或手动展开）：同时清贴边态与动画锁。</summary>
    private void ExitDockedState()
    {
        _isDockedAsStrip = false;
        _isAnimatingStrip = false;
    }

    // ---- §13.44：贴边系统 ----
    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // 拖拽标题条期间不处理；释放时由 OnPreviewMouseUpGlobal 统一吸附。
    }

    private void OnPreviewMouseUpGlobal(object sender, MouseButtonEventArgs e)
    {
        // ⚠ 坑：拖拽释放时才吸附，预览 MouseUp 在点击时也会触发，必须靠 _isDraggingTitle 区分。
        if (e.ChangedButton == MouseButton.Left && _isDraggingTitle)
        {
            _isDraggingTitle = false;
            TrySnapToEdges();
        }
    }

    private void OnPreviewKeyDownGlobal(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !IsDockedAsStrip)
        {
            DockToRightEdge();
            e.Handled = true;
        }
    }

    private void ClampWindowToWorkArea()
    {
        if (IsDockedAsStrip)
        {
            return;
        }

        var work = GetWindowWorkArea(this);
        if (Width > work.Width)
        {
            // 宽度超出：按宽度二次收缩
            var scale = work.Width / Width * _currentScale;
            SetModeScale(scale);
        }

        /*if (Height > work.Height)
        {
            // 高度超出：按高度二次收缩
            var (designW, designH) = GetDesignSize(_layoutMode);
            var scale = work.Height / designH;
            SetModeScale(scale);
        }*/

        Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
        Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
        LogGeometry("clamp");   // TEMP-DIAG
    }

    private void TrySnapToEdges()
    {
        FileLogger.Info($"try-snap: docked={IsDockedAsStrip} bounds=({Left},{Top},{Width},{Height})");
        if (IsDockedAsStrip)
        {
            return;
        }

        var work = GetWindowWorkArea(this);
        var newLeft = Left;
        var newTop = Top;
        var snapped = false;

        if (Left < work.Left + EdgeSnapDistance)
        {
            newLeft = work.Left;
            snapped = true;
        }

        if (Top < work.Top + EdgeSnapDistance)
        {
            newTop = work.Top;
            snapped = true;
        }

        if (work.Bottom - (Top + Height) < EdgeSnapDistance)
        {
            newTop = work.Bottom - Height;
            snapped = true;
        }

        if (snapped)
        {
            Left = newLeft;
            Top = newTop;
        }
    }

    private void DockToRightEdge()
    {
        FileLogger.Info($"dock-right: docked={IsDockedAsStrip} animating={IsStripAnimating} visible={IsVisible} bounds=({Left},{Top},{Width},{Height})");
        if (IsDockActive)
        {
            return;
        }

        EnterDockedState();

        // ⚠ 坑（2026-10-04 修）：这里原来残留 SystemParameters.WorkArea——它与上行注释自相矛盾，
        // 只返回**主屏**（多屏/副屏贴到错屏），且未走 DIP 口径统一。贴边计算一律用窗口实际所在屏的 WorkArea（已 DIP）。
        var work = GetWindowWorkArea(this);
        var stripTop = Math.Max(work.Top, Math.Min(Top, work.Bottom - DockedStripHeight));
        var target = new Rect(work.Right - DockedStripWidth, stripTop, DockedStripWidth, DockedStripHeight);
        LogGeometry("dock-target");   // TEMP-DIAG

        RootBorder.Visibility = Visibility.Collapsed;
        RightEdgeStrip.Visibility = Visibility.Visible;

        AnimateWindowBounds(target, EndStripAnimation);
    }

    private void ExpandFromStrip()
    {
        if (!IsDockedAsStrip || IsStripAnimating)
        {
            return; // 非贴边或动画进行中：本方法只管贴边展开，不干预其他状态
        }

        BeginStripAnimation(); // 上锁：防止展开动画期间重复进入
        _stripHoverTimer.Stop();  // 停悬停检测，避免动画中被再次触发

        // ===== 修复一（2026-09-09）：展开尺寸按当前模式实时计算 =====
        // 病根：_restoredBounds 缓存的是"贴边那一刻"的窗口尺寸。贴边期间切模式（9键→26键）
        // 不会刷新缓存，展开时按旧尺寸恢复，26键内容被塞进 9 键窗口 → 裁切。
        // 修法：宽/高按当前模式现算；缓存只保留展开位置（Left/Top）。
        var (designW, _) = GetDesignSize(_layoutMode); // 当前模式的设计宽（未缩放）；高度走下方 ExpandedWindowHeight()
        var targetWidth = designW * _currentScale;    // 窗口实际宽 = 设计宽 × 缩放
        var targetHeight = ExpandedWindowHeight();     // 窗口实际高（内部已乘 scale）
        var targetRect = new Rect(RestoredBounds.Left, RestoredBounds.Top, targetWidth, targetHeight);

        AnimateWindowBounds(targetRect, () =>
        {
            // ===== 修复二（2026-09-09）：动画完成后同步校准窗口与内容布局 =====
            // 病根：回调原来只校准 Height，宽度和内容面板（English26Panel）没有重新测量，
            // 首次展开时面板仍按旧度量渲染（实测 ActualHeight=326，期望铺满 450），按键行被压扁；
            // 折叠再展开才恢复正常。

            ExitDockedState();
            RightEdgeStrip.Visibility = Visibility.Collapsed;  // 隐藏右缘细条
            RootBorder.Visibility = Visibility.Visible;        // 显示完整键盘

            // 1) 窗口与根容器尺寸同步（与 ApplyModeScale 同一口径：容器存设计值，缩放交给 RenderTransform）
            RootBorder.Width = designW;                                             // 根容器宽：设计值（未缩放）
            RootBorder.RenderTransform = new ScaleTransform(_currentScale, _currentScale); // 视觉缩放
            Width = targetRect.Width;                                               // 窗口宽：实际值（设计宽 × scale）
            Height = ExpandedWindowHeight();                                        // 窗口高：实际值
            RootBorder.Height = ExpandedDesignHeight();                             // 根容器高：设计值

            // 2) 强制内容重新测量：首次展开时子面板可能还挂着贴边期间的旧度量
            try
            {
                English26Panel.InvalidateMeasure();
                English26Panel.UpdateLayout();
                RootBorder.InvalidateMeasure();
                RootBorder.UpdateLayout();
            }
            catch (Exception ex)
            {
                FileLogger.Error("expand-from-strip: force layout failed", ex);
            }

            ClampWindowToWorkArea(); // 兜底：展开后若超出屏幕工作区则夹回（窗口被挪属正常）
            LogGeometry("expanded");   // TEMP-DIAG
        });
    }

    private void RightEdgeStripButton_Click(object sender, RoutedEventArgs e) => ExpandFromStrip();

    private System.Windows.Point _stripDragStart;

    private void RightEdgeStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // §13.45-6：细条上按下先同步展开；若随后移动则跟随拖动
        if (IsDockedAsStrip)
        {
            ExitDockedState();
            RightEdgeStrip.Visibility = Visibility.Collapsed;
            RootBorder.Visibility = Visibility.Visible;
            Left = RestoredBounds.Left;
            Top = RestoredBounds.Top;
            Width = RestoredBounds.Width;
            Height = RestoredBounds.Height;
            _stripHoverTimer.Stop();
        }

        _stripDragStart = e.GetPosition(this);
        Mouse.AddPreviewMouseMoveHandler(this, OnStripPreviewMouseMove);
    }

    private void OnStripPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _stripDragStart.X) > 4 || Math.Abs(p.Y - _stripDragStart.Y) > 4)
        {
            Mouse.RemovePreviewMouseMoveHandler(this, OnStripPreviewMouseMove);
            SafeDragMove();
        }
    }

    private void AnimateWindowBounds(Rect target, Action? onCompleted = null)
    {
        var storyboard = new Storyboard();
        var leftAnim = new DoubleAnimation(Left, target.Left, StripAnimationDuration);
        var topAnim = new DoubleAnimation(Top, target.Top, StripAnimationDuration);
        var widthAnim = new DoubleAnimation(Width, target.Width, StripAnimationDuration);
        var heightAnim = new DoubleAnimation(Height, target.Height, StripAnimationDuration);

        Storyboard.SetTarget(leftAnim, this);
        Storyboard.SetTargetProperty(leftAnim, new PropertyPath(LeftProperty));
        Storyboard.SetTarget(topAnim, this);
        Storyboard.SetTargetProperty(topAnim, new PropertyPath(TopProperty));
        Storyboard.SetTarget(widthAnim, this);
        Storyboard.SetTargetProperty(widthAnim, new PropertyPath(WidthProperty));
        Storyboard.SetTarget(heightAnim, this);
        Storyboard.SetTargetProperty(heightAnim, new PropertyPath(HeightProperty));

        storyboard.Children.Add(leftAnim);
        storyboard.Children.Add(topAnim);
        storyboard.Children.Add(widthAnim);
        storyboard.Children.Add(heightAnim);

        // 动画结束时必须先解除属性持有再钉死终值：
        // WPF Storyboard 默认 HoldEnd，跑完后仍"按住"属性，期间直接赋值（如回调里的 Height=504）会被动画值屏蔽，
        // 导致窗口实际尺寸停在动画半途值（本 bug：Height 钉死在起点 367，内容被裁）。解除持有后赋值才生效。
        storyboard.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null); Left = target.Left;
            BeginAnimation(TopProperty, null); Top = target.Top;
            BeginAnimation(WidthProperty, null); Width = target.Width;
            BeginAnimation(HeightProperty, null); Height = target.Height;
            onCompleted?.Invoke();
        };

        storyboard.Begin(this);
    }

    private void DragStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SafeDragMove();
    }
}
