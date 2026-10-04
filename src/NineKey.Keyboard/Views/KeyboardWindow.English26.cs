// 本文件职责：KeyboardWindow 的 26 键英文面板（字母行/数字行/符号行/编辑行构建，Shift/Caps/粘性 Ctrl/Alt）。
// 数据流位置：面板按钮点击 → OnEnglishLetterClick / OnSymbolOrLetterCommit / OnModifierClick → KeyController 或原生按键注入。
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

/// <summary>KeyboardWindow 英文 26 键部分：面板构建与 Shift/Caps/修饰键状态机。</summary>
public partial class KeyboardWindow
{

    // §M8-9 / 批次 10：26 键大小写、编辑层、符号行、粘性修饰键状态
    private bool _isShiftOn;
	
    // v5：PC 美式键盘 Shift 变换表。数字行 Shift 形态 = 符号行 13 符号，符号行从此可隐藏不占地方。
    private static readonly Dictionary<char, char> ShiftedSymbolMap = new()
    {
        ['`'] = '~', ['1'] = '!', ['2'] = '@', ['3'] = '#', ['4'] = '$', ['5'] = '%',
        ['6'] = '^', ['7'] = '&', ['8'] = '*', ['9'] = '(', ['0'] = ')',
        ['-'] = '_', ['='] = '+', ['['] = '{', [']'] = '}', ['\\'] = '|',
        [';'] = ':', ['\''] = '"', [','] = '<', ['.'] = '>', ['/'] = '?',
    };
    private bool _isCapsLockOn;
    private bool _isEditRowExpanded;
    private double _editRowTopBeforeExpand;

    private double _editRowHeight;

    // ⚠ 坑：符号行显隐会改变窗口 Height，必须记录已补偿像素，重复调用时幂等。
    //private double _symbolRowCompensation;

        private void UpdateSymbolRowVisibility()
    {
        var visible = _layoutMode == LayoutMode.English && _settings.ShowSymbolRow;
        English26SymbolRowDef.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        English26SymbolRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // v5：数字行同机制折叠。⚠ 铁律：只动面板内部行高，窗口尺寸一律不碰（空高由字母行 * 吸收，键自动变大）。
        var numberVisible = _layoutMode == LayoutMode.English && _settings.ShowNumberRow;
        English26NumberRowDef.Height = numberVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        English26NumberRow.Visibility = numberVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly Dictionary<char, System.Windows.Controls.Button> _englishLetterButtons = new();

    // §13.40-S2：Ctrl / Alt 粘性修饰键
    private bool _isCtrlActive;
    private bool _isAltActive;
    private bool _isCtrlLocked;
    private bool _isAltLocked;
    private readonly DispatcherTimer _ctrlDoubleTapTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _altDoubleTapTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };



    private double GetLetterRowHeight() => English26Row1.ActualHeight;

    /// <summary>通用面板/窗口扩张动画：复用于宏键行（只变高度）与编辑行（同时上移窗口）。</summary>
    private void AnimateExpansion(FrameworkElement panel, double panelTargetHeight, double windowHeightDelta, double windowTopDelta, Action? onCompleted = null, double? rootTargetHeight = null)
    {
        // ⚠ 坑：panel.Height 与 window.Height 必须同时动画，否则面板展开时窗口会露黑边或挤压内容。
        var panelAnim = new DoubleAnimation(panel.Height, panelTargetHeight, ExpansionAnimationDuration);
        var windowAnim = new DoubleAnimation(Height, Height + windowHeightDelta, ExpansionAnimationDuration);

        var storyboard = new Storyboard();
        Storyboard.SetTarget(panelAnim, panel);
        Storyboard.SetTargetProperty(panelAnim, new PropertyPath(HeightProperty));
        Storyboard.SetTarget(windowAnim, this);
        Storyboard.SetTargetProperty(windowAnim, new PropertyPath(HeightProperty));
        storyboard.Children.Add(panelAnim);
        storyboard.Children.Add(windowAnim);

        if (windowTopDelta != 0)
        {
            var topAnim = new DoubleAnimation(Top, Top + windowTopDelta, ExpansionAnimationDuration);
            Storyboard.SetTarget(topAnim, this);
            Storyboard.SetTargetProperty(topAnim, new PropertyPath(TopProperty));
            storyboard.Children.Add(topAnim);
        }

        // ⚠ 坑（修订）：扩张行加高窗口时必须同步加高内容容器 RootBorder，否则扩张行挤占容器内
        // 按键区空间、窗口底部拖透明空白（表现为按键被切割）。
        if (rootTargetHeight.HasValue)
        {
            var rootAnim = new DoubleAnimation(RootBorder.Height, rootTargetHeight.Value, ExpansionAnimationDuration);
            Storyboard.SetTarget(rootAnim, RootBorder);
            Storyboard.SetTargetProperty(rootAnim, new PropertyPath(HeightProperty));
            storyboard.Children.Add(rootAnim);
        }

        if (onCompleted is not null)
        {
            storyboard.Completed += (_, _) => onCompleted();
        }

        storyboard.Begin(this);
    }

    private System.Windows.Controls.Button MakePanelButton(string text) => new()
    {
        Content = text,
        Style = (Style)FindResource("OpButtonStyle"),
        Focusable = false,
    };

    /// <summary>v5 角标键面：主字 + 右上角 Shift 符号（ShiftedSymbolMap 为唯一事实源，键面显示与 Shift 输入必然一致，同 PC 美式键盘）。</summary>
    private System.Windows.Controls.Button MakeCornerButton(string main, string corner)
    {
        var btn = MakePanelButton(main);
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = main,
            FontSize = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new System.Windows.Thickness(6, 0, 0, 3),
        });
        grid.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = corner,
            FontSize = 9,
            Opacity = 0.75,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new System.Windows.Thickness(0, 2, 4, 0),
        });
        btn.Content = grid;
        return btn;
    }

    private System.Windows.Controls.Button? _editCapsLockButton;

    private void BuildEnglish26Panel()
    {
        
        string[] rows = ["qwertyuiop[]\\", "asdfghjkl;'", "zxcvbnm,./"];
        var grids = new[] { English26Row1, English26Row2, English26Row3 };
        for (var r = 0; r < 3; r++)
        {
            foreach (var c in rows[r])
            {
                var lower = char.ToLowerInvariant(c);
                var btn = ShiftedSymbolMap.TryGetValue(lower, out var cornerSym)
                    ? MakeCornerButton(lower.ToString(), cornerSym.ToString())
                    : MakePanelButton(lower.ToString());
                btn.Tag = lower;
                btn.Click += (_, _) => OnEnglishLetterClick(lower);
                _englishLetterButtons[lower] = btn;
                _panelButtons.Add(btn);
                _ = grids[r].Children.Add(btn);
            }
        }

        BuildEnglish26NumberRow();
		BuildEnglish26SymbolRow();
        BuildEnglish26EditRow();
        UpdateEnglish26ButtonLabels();
        UpdateEnglish26ShiftState();
        UpdateModifierButtonState();
        UpdateEditCapsLockButtonState();
		UpdateRowToggleButtonState();
		UpdateSymbolSetToggleState();
        // 底行大键在 XAML 中定义，由 ApplyTheme 统一涂刷。
    }

        // v5：符号行双套。半角套 = 数字行 Shift 形态（§13.40-S1 PC 标准）；全角套 = 中文标点，
    // 英文模式下免切中文直出全角。第 14 格是切换键，状态存 AppSettings 重启保持。
        // v5：符号行双套（各 16 键，格子全留给符号）。半角套补齐 - = \ 凑满；全角套 = 用户点名的全套，
    // 一个不缺。全角/半角切换键在底栏（原"符"按钮位），状态存 AppSettings 重启保持。
    private static readonly string[] SymbolRowHalf = ["~", "!", "@", "#", "$", "%", "^", "&", "*", "(", ")", "_", "+", "-", "=", "\\"];
    private static readonly string[] SymbolRowFull = ["；", "：", "＇", "＂", "／", "？", "，", "．", "＞", "＜", "［", "］", "｛", "｝", "＼", "｜"];

    private void BuildEnglish26SymbolRow()
    {
        var symbols = _settings.SymbolRowFullWidth ? SymbolRowFull : SymbolRowHalf;
        English26SymbolRow.Children.Clear();
        foreach (var sym in symbols)
        {
            var s = sym;
            var btn = MakePanelButton(s);
            btn.MinWidth = 0; // 符号行 16 列均分：解除按 13 键行调校的 MinWidth，防右缘裁切
            btn.Click += (_, _) => OnSymbolOrLetterCommit(s);
            _panelButtons.Add(btn);
            _ = English26SymbolRow.Children.Add(btn);
        }
    }

    /// <summary>构建 26 键数字行（v5）：全 ASCII，`=` `-` 是写代码的正门，不再绕符号页。⌫ 走退格。</summary>
    private void BuildEnglish26NumberRow()
    {
        string[] keys = ["`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "⌫"];
        English26NumberRow.Children.Clear();
        foreach (var key in keys)
        {
            var k = key;
            var btn = k.Length == 1 && ShiftedSymbolMap.TryGetValue(k[0], out var shifted)
                ? MakeCornerButton(k, shifted.ToString())
                : MakePanelButton(k);
            btn.MinWidth = 0; // 数字行 14 键同理
            btn.Click += (_, _) => OnEnglish26NumberCommit(k);
            _panelButtons.Add(btn);
            _ = English26NumberRow.Children.Add(btn);
        }
    }

    private void OnEnglish26NumberCommit(string key)
    {
        // 粘性修饰键优先：Ctrl+1/2/3… 是编辑器的标签页切换，必须能发出去
        if (TryCommitModifierCombo(key))
        {
            return;
        }

        if (key == "⌫")
        {
            // 走 KeyController 退格：缓冲空时才发 VK_BACK 到目标应用（§13.8 状态机）
            _controller.Backspace();
            return;
        }
		
		
        // v5：Shift+数字 = 符号（Shift+3→#），单次有效随 Shift 复位
        if (_isShiftOn && key.Length == 1 && ShiftedSymbolMap.TryGetValue(key[0], out var shiftedDigit))
        {
            _isShiftOn = false;
            UpdateEnglish26ShiftState();
            _controller.CommitDirect(shiftedDigit.ToString());
            return;
        }

        _controller.CommitDirect(key);
    }
    private void BuildEnglish26EditRow()
    {
        English26EditRow.Children.Clear();
        var editKeys = new (string Label, RoutedEventHandler Handler)[]
        {
            ("Esc", EditEsc_Click!),
            ("Tab", EditTab_Click!),
            ("←", EditLeft_Click!),
            ("↑", EditUp_Click!),
            ("↓", EditDown_Click!),
            ("→", EditRight_Click!),
            ("Delete", EditDelete_Click!),
            ("Home", EditHome_Click!),
            ("End", EditEnd_Click!),
            ("Caps Lock", EditCapsLock_Click!),
            ("返回", EditLayerClose_Click!),
        };
        // ⚠ 坑：编辑行用 Grid 而非 UniformGrid，必须显式 SetColumn，否则所有按钮挤在第 0 列。
        for (var i = 0; i < editKeys.Length; i++)
        {
            var (label, handler) = editKeys[i];
            var btn = MakePanelButton(label);
            btn.Click += handler;
            if (label == "Caps Lock")
            {
                _editCapsLockButton = btn;
            }

            _panelButtons.Add(btn);
            System.Windows.Controls.Grid.SetColumn(btn, i);
            _ = English26EditRow.Children.Add(btn);
        }
    }

    private void OnEnglishLetterClick(char lower)
    {
        PlayKeyClick();
        // §13.40-S2：粘性修饰键优先
        if (TryCommitModifierCombo(lower.ToString()))
        {
            return;
        }
		
		// v5：字母行内的符号键（; ' , . / [ ] \）Shift 变换走表（; → :、' → "），单次有效。
        // ⚠ 坑：本方法处理字母行全部键（含步骤2加入的符号键），字母不在表中不受影响，表查必须先于大小写逻辑。
        if (_isShiftOn && ShiftedSymbolMap.TryGetValue(lower, out var shiftedSym))
        {
            _isShiftOn = false;
            UpdateEnglish26ShiftState();
            _controller.CommitDirect(shiftedSym.ToString());
            return;
        }

        var output = lower;
        if (_isShiftOn)
        {
            // P1-2：Caps Lock 开时 Shift 给出反切小写
            output = _isCapsLockOn ? lower : char.ToUpperInvariant(lower);
            _isShiftOn = false;
            UpdateEnglish26ShiftState();
        }
        else if (_isCapsLockOn)
        {
            output = char.ToUpperInvariant(lower);
        }

        _controller.CommitDirect(output.ToString());
    }

        private void OnSymbolOrLetterCommit(string text)
    {
        PlayKeyClick();
        if (TryCommitModifierCombo(text))
        {
            return;
        }

        // v5：Shift+符号 = PC 式变换（; → :、' → "、[ → { 等），单次有效随 Shift 复位
        if (_isShiftOn && text.Length == 1 && ShiftedSymbolMap.TryGetValue(text[0], out var shifted))
        {
            _isShiftOn = false;
            UpdateEnglish26ShiftState();
            _controller.CommitDirect(shifted.ToString());
            return;
        }

        _controller.CommitDirect(text);
    }

    private bool TryCommitModifierCombo(string text)
    {
        if ((!_isCtrlActive && !_isAltActive && !_isCtrlLocked && !_isAltLocked) || text.Length != 1)
        {
            return false;
        }

        var vkInfo = MapCharToVk(text[0]);
        if (vkInfo.vk == 0)
        {
            return false;
        }

        var keys = new List<ushort>();
        if (_isCtrlActive || _isCtrlLocked)
        {
            keys.Add(NativeMethods.VkControl);
        }

        if (_isAltActive || _isAltLocked)
        {
            keys.Add(NativeMethods.VkMenu);
        }

        if (vkInfo.needsShift)
        {
            keys.Add(NativeMethods.VkShift);
        }

        keys.Add(vkInfo.vk);
        _injector.InjectShortcut(keys.ToArray());

        // 非锁定态自动复位
        if (!_isCtrlLocked)
        {
            _isCtrlActive = false;
        }

        if (!_isAltLocked)
        {
            _isAltActive = false;
        }

        UpdateModifierButtonState();
        return true;
    }

    private static (ushort vk, bool needsShift) MapCharToVk(char c)
    {
        // 字母直接映射到 A-Z
        if (c is >= 'a' and <= 'z')
        {
            return ((ushort)(NativeMethods.VkA + (c - 'a')), false);
        }

        if (c is >= 'A' and <= 'Z')
        {
            return ((ushort)(NativeMethods.VkA + (c - 'A')), true);
        }

        // 数字直接映射
        if (c is >= '0' and <= '9')
        {
            return ((ushort)(NativeMethods.Vk0 + (c - '0')), false);
        }

        // 其他字符走 VkKeyScan
        var result = NativeMethods.VkKeyScan(c);
        if (result == -1)
        {
            return (0, false);
        }

        var vk = (ushort)(result & 0xFF);
        var shiftState = (result >> 8) & 0xFF;
        var needsShift = (shiftState & 1) != 0;
        return (vk, needsShift);
    }

    private void UpdateEnglish26ButtonLabels()
    {
        var showUpper = (_isCapsLockOn && !_isShiftOn) || (_isShiftOn && !_isCapsLockOn);
        foreach (var (lower, btn) in _englishLetterButtons)
        {
            btn.Content = showUpper ? char.ToUpperInvariant(lower).ToString() : lower.ToString();
        }
    }

    private void UpdateEnglish26ShiftState()
    {
        // P1-1：Shift 键始终显示 ↑
        English26ShiftButton.Content = "↑";
        English26ShiftButton.Foreground = _isShiftOn || (_isCapsLockOn && !_isShiftOn) ? Brushes.LightBlue : Foreground;
        UpdateEnglish26ButtonLabels();
    }

    private void UpdateModifierButtonState()
    {
        English26CtrlButton.Foreground = (_isCtrlActive || _isCtrlLocked) ? Brushes.LightBlue : Foreground;
        English26AltButton.Foreground = (_isAltActive || _isAltLocked) ? Brushes.LightBlue : Foreground;
    }

    private void UpdateEditCapsLockButtonState()
    {
        if (_editCapsLockButton is not null)
        {
            _editCapsLockButton.Foreground = _isCapsLockOn ? Brushes.LightBlue : Foreground;
        }
    }

    // ---- §M8-9：26 键编辑层与大小写切换 ----
    private void English26Shift_Click(object sender, RoutedEventArgs e)
    {
        // Shift 与编辑层解耦：Shift = 大小写切换，单字符后自动复位
        if (_isCapsLockOn)
        {
            _isShiftOn = !_isShiftOn; // Caps Lock 开时 Shift 临时小写
        }
        else
        {
            _isShiftOn = !_isShiftOn; // 临时大写
        }

        UpdateEnglish26ShiftState();
    }

    private void English26Edit_Click(object sender, RoutedEventArgs e) => SetEditRowVisible(!_isEditRowExpanded);
	
	
    // v5：数字行/符号行折叠开关。状态存 AppSettings（重启保持），高亮表示行展开。
    private void English26NumberToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowNumberRow = !_settings.ShowNumberRow;
        UpdateSymbolRowVisibility();
        UpdateRowToggleButtonState();
    }

    private void English26SymbolToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowSymbolRow = !_settings.ShowSymbolRow;
        UpdateSymbolRowVisibility();
        UpdateRowToggleButtonState();
    }
	
	    // v5：底栏全角/半角切换（原"符"按钮位）。符号页删除后，全角符号的唯一出口就是符号行此套。
    private void English26SymbolSetToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.SymbolRowFullWidth = !_settings.SymbolRowFullWidth;
        BuildEnglish26SymbolRow();
        UpdateSymbolSetToggleState();
    }

    private void UpdateSymbolSetToggleState()
    {
        if (English26SymbolSetToggle is null)
        {
            return;
        }

        var full = _settings.SymbolRowFullWidth;
        English26SymbolSetToggle.Content = full ? "半角" : "全角";
        English26SymbolSetToggle.Foreground = full ? Brushes.LightBlue : Foreground;
    }

    private void UpdateRowToggleButtonState()
    {
        English26NumberToggle.Foreground = _settings.ShowNumberRow ? Brushes.LightBlue : Foreground;
        English26SymbolToggle.Foreground = _settings.ShowSymbolRow ? Brushes.LightBlue : Foreground;
    }

    private void EditLayerClose_Click(object sender, RoutedEventArgs e) => SetEditRowVisible(false);

    private void SetEditRowVisible(bool visible)
    {
        if (visible == _isEditRowExpanded)
        {
            return;
        }

        // 编辑行被托盘关闭或非英文模式时禁止展开
        if (visible && (!_settings.ShowEditRow || _layoutMode != LayoutMode.English))
        {
            return;
        }

        _isEditRowExpanded = visible;

        // ⚠ 坑：贴条/贴条动画期间只记状态与行高，不动窗口 Height/Top（基数是细条高度，会污染）。
        if (IsDockActive)
        {
            English26EditRowDef.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            English26EditRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            English26EditRow.Height = visible ? double.NaN : 0;
            return;
        }

        if (visible)
        {
            // ⚠ 坑：编辑行高度取字母行设计高，动画增量要乘 _currentScale（ActualHeight 不含 RenderTransform）。
            var h = GetLetterRowHeight();
            _editRowHeight = h;
            _editRowTopBeforeExpand = Top;
            English26EditRowDef.Height = new GridLength(1, GridUnitType.Star);
            var dh = h * _currentScale;

            if (!IsVisible)
            {
                English26EditRow.Visibility = Visibility.Visible;
                English26EditRow.Height = double.NaN;
                Height = ExpandedWindowHeight();
                RootBorder.Height = ExpandedDesignHeight();
                Top = Math.Max(SystemParameters.WorkArea.Top, Top - dh);
                _isEditRowExpanded = true;
                return;
            }

            English26EditRow.Visibility = Visibility.Visible;
            English26EditRow.Height = 0;
            AnimateExpansion(English26EditRow, h, dh, -dh, () => English26EditRow.Height = double.NaN, ExpandedDesignHeight());
            _isEditRowExpanded = true;
        }
        else
        {
            var h = _editRowHeight;
            var dh = h * _currentScale;

            if (!IsVisible)
            {
                English26EditRowDef.Height = new GridLength(0);
                English26EditRow.Visibility = Visibility.Collapsed;
                English26EditRow.Height = 0;
                Height = ExpandedWindowHeight();
                RootBorder.Height = ExpandedDesignHeight();
                Top = _editRowTopBeforeExpand;
                _isEditRowExpanded = false;
                return;
            }

            English26EditRow.Height = h;
            AnimateExpansion(English26EditRow, 0, -dh, _editRowTopBeforeExpand - Top, () =>
            {
                English26EditRowDef.Height = new GridLength(0);
                English26EditRow.Visibility = Visibility.Collapsed;
                English26EditRow.Height = 0;
            }, ExpandedDesignHeight());
            _isEditRowExpanded = false;
        }
    }

    private void EditCapsLock_Click(object sender, RoutedEventArgs e)
    {
        _isCapsLockOn = !_isCapsLockOn;
        _isShiftOn = false;
        UpdateEnglish26ShiftState();
        UpdateEditCapsLockButtonState();
        // 编辑层不自动关闭
    }

    private void EditDelete_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkDelete);
    private void EditLeft_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkLeft);
    private void EditRight_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkRight);
    private void EditUp_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkUp);
    private void EditDown_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkDown);
    private void EditEsc_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkEscape);
    private void EditTab_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkTab);
    private void EditHome_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkHome);
    private void EditEnd_Click(object sender, RoutedEventArgs e) => _injector.InjectKey(NativeMethods.VkEnd);

    // §13.40-S2：粘性修饰键 Ctrl / Alt
    private void English26Ctrl_Click(object sender, RoutedEventArgs e) => OnModifierClick(ref _isCtrlActive, ref _isCtrlLocked, _ctrlDoubleTapTimer);

    private void English26Alt_Click(object sender, RoutedEventArgs e) => OnModifierClick(ref _isAltActive, ref _isAltLocked, _altDoubleTapTimer);

    private void OnModifierClick(ref bool active, ref bool locked, DispatcherTimer timer)
    {
        // ⚠ 坑：粘性修饰键状态机 = 未激活 → 激活 → 300ms 内再点锁定，超时取消激活；锁定时再点解锁。
        if (locked)
        {
            locked = false;
            active = false;
        }
        else if (active)
        {
            if (timer.IsEnabled)
            {
                timer.Stop();
                locked = true;
            }
            else
            {
                active = false;
            }
        }
        else
        {
            active = true;
            timer.Stop();
            timer.Start();
        }

        UpdateModifierButtonState();
    }
}
