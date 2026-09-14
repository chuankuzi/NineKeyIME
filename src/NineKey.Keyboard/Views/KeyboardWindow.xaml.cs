// 本文件职责：主键盘窗口（无边框、置顶、可拖动），负责渲染键位、主题、布局切换与焦点防御。
// 数据流位置：用户事件 → KeyboardWindow → KeyController → QueryEngine/ITextInjector；托盘/热键/Raw Input 也汇总于此。
// ⚠ 坑 1：WS_EX_NOACTIVATE 窗口被 UIA/程序化激活后会吃鼠标消息，必须归还焦点（§W5）。
// ⚠ 坑 2：RootBorder 与符号行/编辑行/宏键行高度变化会改变窗口 Height，需同步补偿避免跳动。
// ⚠ 坑 3：窗口尺寸基于设计分辨率 × scale，ActualHeight 不含 RenderTransform，动画必须用设计值再乘 scale。
// 相关规格：§13.5、§13.8、§13.11、§13.15、§13.18、§13.22、§13.23、§13.24、§13.40、§13.42、§13.43、§13.44、§13.45、§M8-6、§M8-9、§W5。

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

/// <summary>主键盘窗口：无边框置顶圆角，可拖动，透明度可调，热键/托盘唤回。</summary>
public partial class KeyboardWindow : Window
{
    private readonly QueryEngine _engine;
    private readonly KeyController _controller;
    private readonly TextInjector _injector;
    private readonly ConvertingTextInjector _outputInjector;
    private Opencc? _opencc;
    private readonly AppSettings _settings;
    private readonly HotkeyService _hotkey = new();
    private readonly TrayService _tray = new();
    private readonly FocusWatcher _focusWatcher = new();
    private readonly RawTouchWatcher _rawTouchWatcher = new();
    private readonly VisibilityPolicy _vis = new();
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _fgTracker = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _selectionTracker = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private nint _lastExternalForeground;
    private DateTime _elevatedNoticeAt = DateTime.MinValue;
    private MacroEditorWindow? _macroEditor;

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

    // §13.42：各布局模式设计尺寸（未缩放基准）
    private const double ChineseDesignWidth = 400;
    private const double ChineseDesignHeight = 420;
    private const double NumberDesignWidth = 400;
    private const double NumberDesignHeight = 420;
    private const double EnglishDesignWidth = 800;
    private const double EnglishDesignHeight = 450;
    private const double SymbolDesignWidth = 400;
    private const double SymbolDesignHeight = 420;

    private double _currentScale = 1.0;

    // §13.45：屏幕适配下限（每模式独立，启动后按窗口实际所在屏幕重算）
    private double _screenMinScaleChinese;
    private double _screenMinScaleNumber;
    private double _screenMinScaleEnglish;
    private double _screenMinScaleSymbol;

    private const double ChineseTargetWidthRatio = 0.55;
    private const double EnglishTargetWidthRatio = 0.60;

    private double ComputeScreenMinScale(double designWidth, double designHeight, double targetWidthRatio, System.Drawing.Rectangle work)
    {
        // 目标宽度占比 -> scale；再校验高度不超出 WorkArea，超出则二次收缩
        var scale = work.Width * targetWidthRatio / designWidth;
        var actualHeight = designHeight * scale;
        if (actualHeight > work.Height)
        {
            scale = work.Height / designHeight;
        }

        return scale;
    }

    private static System.Drawing.Rectangle GetCurrentWorkArea()
    {
        // §13.45-7：优先用 WinForms Screen 读取当前屏幕 WorkArea，避免 WPF SystemParameters 方向错误
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        return screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
    }

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

    public KeyboardWindow(QueryEngine engine, UserDictionary userDict, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(userDict);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // §13.45：先按 PrimaryScreen 初算屏幕适配下限，窗口创建后再按实际所在屏幕重算
        var primaryWork = GetCurrentWorkArea();
        _screenMinScaleChinese = Math.Min(1.0, ComputeScreenMinScale(ChineseDesignWidth, ChineseDesignHeight, ChineseTargetWidthRatio, primaryWork));
        _screenMinScaleNumber = Math.Min(1.0, ComputeScreenMinScale(NumberDesignWidth, NumberDesignHeight, ChineseTargetWidthRatio, primaryWork));
        _screenMinScaleEnglish = ComputeScreenMinScale(EnglishDesignWidth, EnglishDesignHeight, EnglishTargetWidthRatio, primaryWork);
        _screenMinScaleSymbol = Math.Min(1.0, ComputeScreenMinScale(SymbolDesignWidth, SymbolDesignHeight, ChineseTargetWidthRatio, primaryWork));

        _engine = engine;
        _injector = new TextInjector();
        _outputInjector = new ConvertingTextInjector(_injector);
        if (_settings.TraditionalOutput)
        {
            _outputInjector.Converter = s => (_opencc ??= new Opencc(OpenccConfig.S2T)).Convert(s);
        }

        _controller = new KeyController(engine, userDict, _outputInjector);
        InitializeComponent();
        ApplyTheme();

        // §13.45：位置优先上次保存；保存位置在吸附区或无记录时，默认居中偏下
        var work = GetCurrentWorkArea();
        if (_settings.Left is double left && _settings.Top is double top
            && left >= EdgeSnapDistance
            && top >= EdgeSnapDistance
            && work.Height - (top + Height) >= EdgeSnapDistance)
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = (work.Width - Width) / 2;
            Top = Math.Max(EdgeSnapDistance, work.Height - Height - 40);
        }

        _currentScale = GetScaleFor(LayoutMode.Chinese);
        var (designW, designH) = GetDesignSize(LayoutMode.Chinese);
        Width = designW * _currentScale;
        Height = designH * _currentScale;
        RootBorder.Width = designW;
        RootBorder.Height = designH;
        RootBorder.RenderTransform = new ScaleTransform(_currentScale, _currentScale);

        // ⚠ 坑：合成输入/极端情况下 MouseUp 丢失会导致鼠标捕获永久卡在某个控件上，
        // 之后所有点击都被路由到该控件（键盘表现为"完全无法输入"）。
        // 在窗口级 PreviewMouseDown 强制释放残留捕获：此刻新按下尚未开始拖拽，释放是安全的。
        Mouse.AddPreviewMouseDownHandler(this, (_, e) =>
        {
            var captured = Mouse.Captured;
            if (captured is not null && !ReferenceEquals(captured, e.OriginalSource as DependencyObject))
            {
                captured.ReleaseMouseCapture();
            }
        });

        // 按键接线：字母布局取自 KeyLayout（唯一真源，§13.9 方向映射）
        var keyButtons = new[] { Key1, Key2, Key3, Key4, Key5, Key6, Key7, Key8, Key9, Key0 };
        for (var i = 0; i < keyButtons.Length; i++)
        {
            var def = KeyLayout.Keys[i];
            keyButtons[i].Center = def.Center;
            keyButtons[i].Options = def.Options;
            keyButtons[i].CommitDirect = def.CommitDirect;
            //keyButtons[i].BubbleDelayMs = _settings.BubbleDelayMs;//气泡的延迟
            //keyButtons[i].BubbleShowMs = _settings.BubbleShowMs;//显示时长参数
            keyButtons[i].TouchCorrectionSigma = _settings.TouchCorrectionSigma;//是误触纠正
        }

        _controller.SetTouchCorrection(_settings.TouchCorrectionEnabled, _settings.TouchCorrectionSigma);

        foreach (var key in new[] { Key1, Key2, Key3, Key4, Key5, Key6, Key7, Key8, Key9 })
        {
            // §13.37：统一走 DigitPressed，避免 FlickCommitted + DigitPressed 双发
            //key.DigitPressed += OnDigitPressed;
            // §13.37（修订）：Flick 已移除，统一走 DigitPressed 点按路径
            key.DigitPressed += OnDigitPressed;
        }

        // §13.5：空格键只发 VK_SPACE，不走 DigitPressed（避免输出字符 '0'）
        // §13.5：0 键 = 空格键。KeyLayout 中 Digit="0"，不走 OnDigitPressed（会输出 '0'），单独直挂空格提交。
        Key0.DigitPressed += _ => _controller.CommitSpace();

        BuildEnglish26Panel();
        BuildSymbolPanel();
        BuildMacroPanel();
        SetLayoutMode(LayoutMode.Chinese);
        ApplyTheme(); // 面板按钮生成后补一次主题（首次 ApplyTheme 时面板按钮尚不存在）

        // §13.40-S2：双击检测计时器到点后自动停止
        _ctrlDoubleTapTimer.Tick += (_, _) => _ctrlDoubleTapTimer.Stop();
        _altDoubleTapTimer.Tick += (_, _) => _altDoubleTapTimer.Stop();

        // §13.8：长按退格手势边界（连删在缓冲清空处停止）
        BackspaceButton.PreviewMouseLeftButtonDown += (_, _) => _controller.BeginBackspaceGesture();
        BackspaceButton.PreviewMouseLeftButtonUp += (_, _) => _controller.EndBackspaceGesture();
        BackspaceButton.PreviewTouchDown += (_, _) => _controller.BeginBackspaceGesture();
        BackspaceButton.PreviewTouchUp += (_, _) => _controller.EndBackspaceGesture();

        Bar.CandidateClicked += candidate => _controller.CommitCandidate(candidate);
        Bar.PageNext += () => _controller.NextPage();
        Bar.PagePrevious += () => _controller.PreviousPage();
        _controller.StateChanged += RefreshUi;
        RefreshUi();

        // 闪现计时（R8：透明度走刷色 alpha，闪现 = 临时拉满 1.5 秒）
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flashOverride = false;
            ApplyTheme();
        };

        // 热键 / 托盘
        _hotkey.HotkeyPressed += ToggleVisibility;
        _tray.ToggleRequested += ToggleVisibility;
        _tray.FlashRequested += Flash;
        _tray.ResetOpacityRequested += () =>
        {
            _settings.Opacity = AppSettings.DefaultOpacity;
            _settings.KeyOpacity = AppSettings.DefaultKeyOpacity;
            ApplyTheme();
        };
        _tray.BackgroundOpacityChanged += v =>
        {
            _settings.Opacity = v;
            ApplyTheme();
        };
        _tray.KeyOpacityChanged += v =>
        {
            _settings.KeyOpacity = v;
            ApplyTheme();
        };
        _tray.SizeScaleRequested += scale => SetModeScale(scale);
        _tray.BackgroundColorChanged += v =>
        {
            _settings.BackgroundColor = v;
            ApplyTheme();
        };
        _tray.KeyBackgroundColorChanged += v =>
        {
            _settings.KeyBackgroundColor = v;
            ApplyTheme();
        };
        _tray.KeyBorderWidthChanged += v =>
        {
            _settings.KeyBorderWidth = v;
            ApplyTheme();
        };
        _tray.AutoPopupToggled += on => SetAutoPopup(on);
        _tray.TraditionalToggled += on => SetTraditionalOutput(on);
        _tray.HotkeyToggled += on => SetHotkey(on);
        _tray.AdminRunRequested += () => OnAdminChecked();
        _tray.TouchKbGuardToggled += on => SetTouchKbGuard(on);
        _tray.ShortcutBarToggled += on =>
        {
            _settings.ShowShortcutBar = on;
            UpdateShortcutPanelVisibility();
        };
        /*_tray.BubbleDelayChanged += ms =>
        {
            _settings.BubbleDelayMs = ms;
            foreach (var key in keyButtons)
            {
                key.BubbleDelayMs = ms;
            }
        };
        _tray.BubbleShowChanged += ms =>
        {
            _settings.BubbleShowMs = ms;
            foreach (var key in keyButtons)
            {
                key.BubbleShowMs = ms;
            }
        };*/
        _tray.TouchCorrectionToggled += on =>
        {
            _settings.TouchCorrectionEnabled = on;
            _controller.SetTouchCorrection(on, _settings.TouchCorrectionSigma);
            _settings.Save();
        };
        _tray.TouchCorrectionSigmaChanged += sigma =>
        {
            _settings.TouchCorrectionSigma = sigma;
            foreach (var key in keyButtons)
            {
                key.TouchCorrectionSigma = sigma;
            }

            _controller.SetTouchCorrection(_settings.TouchCorrectionEnabled, sigma);
            _settings.Save();
        };
        _tray.FuzzyZhiZuToggled += on => SetFuzzyProfile(s => s.FuzzyZhiZu = on);
        _tray.FuzzyChiCuToggled += on => SetFuzzyProfile(s => s.FuzzyChiCu = on);
        _tray.FuzzyShiSuToggled += on => SetFuzzyProfile(s => s.FuzzyShiSu = on);
        _tray.FuzzyNiLiToggled += on => SetFuzzyProfile(s => s.FuzzyNiLi = on);
        _tray.FuzzyRiLiToggled += on => SetFuzzyProfile(s => s.FuzzyRiLi = on);
        _tray.FuzzyFuHuToggled += on => SetFuzzyProfile(s => s.FuzzyFuHu = on);
        _tray.FuzzyAnAngToggled += on => SetFuzzyProfile(s => s.FuzzyAnAng = on);
        _tray.FuzzyEnEngToggled += on => SetFuzzyProfile(s => s.FuzzyEnEng = on);
        _tray.FuzzyInIngToggled += on => SetFuzzyProfile(s => s.FuzzyInIng = on);
        _tray.StartupToggled += on =>
        {
            _settings.RunAtStartup = on;
            SystemSettingsService.SetRunAtStartup(on);
        };
        _tray.MacroBarToggled += on =>
        {
            _settings.ShowMacroBar = on;
            UpdateMacroPanelVisibility();
        };
        _tray.EditRowToggled += on =>
        {
            _settings.ShowEditRow = on;
            if (!on && _isEditRowExpanded)
            {
                SetEditRowVisible(false);
            }

            English26EditButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _settings.Save();
        };
        _tray.SymbolRowToggled += on =>
        {
            _settings.ShowSymbolRow = on;
            UpdateSymbolRowVisibility();
            _settings.Save();
        };
        _tray.EditEditorWhitelistRequested += () =>
        {
            var window = new EditorWhitelistWindow(_settings.EditorClassWhitelist)
            {
                Owner = null,
            };
            window.Closed += (_, _) =>
            {
                if (window.Saved)
                {
                    _settings.EditorClassWhitelist = window.Whitelist.ToList();
                    _settings.Save();
                }
            };
            window.Show();
        };
        _tray.EditMacrosRequested += () =>
        {
            // ⚠ 坑：键盘窗口带 WS_EX_NOACTIVATE（§W5），若设为 Owner 会导致模态 ShowDialog 激活死锁，必须非模态 Show。
            if (_macroEditor is not null)
            {
                _macroEditor.Activate();
                return;
            }

            // ⚠ 坑：宏编辑器是输入场景，必须先 Show 键盘窗口，否则用户无法按软键盘键输入。
            if (!IsVisible)
            {
                Show();
            }

            var editor = new MacroEditorWindow(_settings.Macros)
            {
                Owner = null,
            };
            _macroEditor = editor;

            editor.Saved += macros =>
            {
                _settings.Macros = macros.ToList();
                BuildMacroPanel();
                UpdateMacroPanelVisibility();
                _settings.Save();
            };
            editor.Closed += (_, _) => _macroEditor = null;
            editor.Show();
        };
        _tray.SyncRequested += () => _tray.SyncState(
            _settings.AutoPopup, _settings.HotkeyEnabled, SystemSettingsService.IsRunAtStartup(),
            _settings.DisableSystemTouchKeyboard, _settings.ShowShortcutBar, _settings.ShowMacroBar,
            _settings.ShowEditRow, _settings.ShowSymbolRow,
            _settings.FuzzyZhiZu, _settings.FuzzyChiCu, _settings.FuzzyShiSu,
            _settings.FuzzyNiLi, _settings.FuzzyRiLi, _settings.FuzzyFuHu,
            _settings.FuzzyAnAng, _settings.FuzzyEnEng, _settings.FuzzyInIng,
            _settings.Opacity, _settings.KeyOpacity
            /*_settings.BubbleDelayMs, _settings.BubbleShowMs*/);
        _tray.SyncRequested += () => _tray.SyncTraditional(_settings.TraditionalOutput);
        _tray.ExitRequested += Close;

        // §0.3：目标是提权窗口时的指引（节流 15 秒）
        TextInjector.ElevatedTargetBlocked += () => Dispatcher.BeginInvoke(() =>
        {
            if (DateTime.Now - _elevatedNoticeAt > TimeSpan.FromSeconds(15))
            {
                _elevatedNoticeAt = DateTime.Now;
                _tray.ShowBalloon("目标是管理员窗口", "无法向以管理员身份运行的窗口输入文字。可在托盘菜单开启「以管理员运行」（需 UAC 确认一次）。");
            }
        });

        // §13.24 显式生命周期：焦点变化不再触发隐藏；FocusWatcher 仅用于日志留痕。
        _focusWatcher.EditableFocusChanged += editable =>
        {
            // 自动弹出改由 RawTouchWatcher 负责（M7-4）；此处不再驱动显隐。
        };

        _tray.MenuOpened += () => { };
        _tray.MenuClosed += () => { };

        Loaded += (_, _) =>
        {
            if (_settings.HotkeyEnabled)
            {
                _hotkey.Register(new WindowInteropHelper(this));
            }

            if (_settings.AutoPopup)
            {
                _focusWatcher.Start();
            }

            // §13.45-7：窗口创建后按实际所在屏幕重算适配下限与首启/环境变化检测
            RecalculateScaleForCurrentScreen();
            _currentScale = GetScaleFor(_layoutMode);
            ApplyModeScale();
            if (IsLoaded)
            {
               
                var (designWLog, designHLog) = GetDesignSize(_layoutMode);
                
                ClampWindowToWorkArea();   // 切模式后重新夹紧：宽了往左挪，不超出屏幕
                FileLogger.Info($"expand-from-strip: completion-final Window=({Width},{Height}) Left={Left} Top={Top}");  // ← 新增这行，只此一处
            }
           
            // §13.45-5：启动必须展开为完整键盘，禁止出生即成细条
            if (_isDockedAsStrip)
            {
                ExpandFromStrip();
            }

            // §13.45：确保窗口不超出 WorkArea
            

            // §13.24：W9 作废，鼠标点击外部不再隐藏。
        };

        // ⚠ 坑：NOACTIVATE 只防鼠标激活；UIA Invoke 等程序化路径仍可能强行激活本窗口，
        // 必须周期性记录最近外部前台窗口，一旦被意外激活立即归还焦点。
        _fgTracker.Tick += (_, _) =>
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != 0 && fg != new WindowInteropHelper(this).Handle)
            {
                _lastExternalForeground = fg;
            }
        };
        _fgTracker.Start();
        _selectionTracker.Tick += (_, _) => UpdateCopyButtonState();
        _selectionTracker.Start();
        Activated += (_, _) =>
        {
            FileLogger.Info($"activated: lastFg={_lastExternalForeground} visible={IsVisible} docked={_isDockedAsStrip}");
            if (_lastExternalForeground != 0 && NativeMethods.IsWindow(_lastExternalForeground))
            {
                FileLogger.Info("keyboard activated unexpectedly (UIA/programmatic), returning focus");
                NativeMethods.SetForegroundWindow(_lastExternalForeground);
            }
        };

        // §13.44：边缘吸附 + 右缘竖条
        LocationChanged += OnLocationChanged;
        Mouse.AddPreviewMouseUpHandler(this, OnPreviewMouseUpGlobal);
        PreviewKeyDown += OnPreviewKeyDownGlobal;
        RightEdgeStripButton.MouseEnter += (_, _) => _stripHoverTimer.Start();
        RightEdgeStripButton.MouseLeave += (_, _) => _stripHoverTimer.Stop();
        _stripHoverTimer.Tick += (_, _) =>
        {
            if (RightEdgeStripButton.IsMouseOver)
            {
                ExpandFromStrip();
            }

            _stripHoverTimer.Stop();
        };

        Closed += OnClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);

        // ⚠ 坑：W5 不抢焦点 = NOACTIVATE（点击键位不激活窗口）+ TOOLWINDOW（隐藏任务栏按钮/Alt-Tab）。
        var style = NativeMethods.GetWindowLongPtr(helper.Handle, NativeMethods.GwlExStyle);
        _ = NativeMethods.SetWindowLongPtr(helper.Handle, NativeMethods.GwlExStyle,
            style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);

        // §13.22：Raw Input 全局捕获触控按下
        _ = _rawTouchWatcher.Register(helper.Handle);
        if (helper.Handle != 0)
        {
            var source = HwndSource.FromHwnd(helper.Handle);
            source?.AddHook(WndProc);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // ⚠ 坑：WM_INPUT 在窗口隐藏时仍能收到全局触控按下，需在 editable 控件上方才自动弹出。
        if (msg == NativeMethods.WmInput && !IsVisible)
        {
            if (RawTouchWatcher.TryGetTouchPoint(msg, lParam, out var point) &&
                RawTouchWatcher.IsEditableAt(point, _settings.EditorClassWhitelist))
            {
                FileLogger.Info($"raw-touch popup at ({point.X},{point.Y})");
                Dispatcher.BeginInvoke(() => ApplyVis(_vis.Show()));
            }
        }

        // ⚠ 坑：§M8-6 键盘窗口带 WS_EX_NOACTIVATE，其他窗口激活后点击键盘区域会发 WM_MOUSEACTIVATE；
        // 若走 WPF 默认处理，激活冲突会吃掉鼠标消息，导致按键无响应。必须显式返回 MA_NOACTIVATE。
        if (msg == NativeMethods.WmMouseActivate)
        {
            handled = true;
            return NativeMethods.MaNoActivate;
        }

        return nint.Zero;
    }

    private void OnDigitPressed(KeyPressInfo info)
    {
        // ⚠ 坑：§M8-1 只有触屏点按启用误触纠正；Flick/鼠标点击视为精确输入，避免把滑动判定成误触。
        // ⚠ 坑：§M8-1 只有触屏点按启用误触纠正；鼠标点击视为精确输入，避免把精确点击判定成误触。
        if (!_settings.TouchCorrectionEnabled || info.Value.Length != 1)
        {
            if (info.CommitDirect)
            {
                var text = info.Value == "空格" ? " " : info.Value;
                _controller.CommitDirect(text);
            }
            else if (char.IsDigit(info.Value[0]))
            {
                _controller.AppendDigit(info.Value[0]);
            }
            else
            {
                _controller.AppendLetter(char.ToLowerInvariant(info.Value[0]));
            }

            return;
        }

        // 触屏点按走误触概率路径
        var digit = char.ToLowerInvariant(info.Value[0]);
        _controller.AppendDigitWithProbability(digit, info.Variants);
    }

    // ---- 三态布局状态机（§13.23：中 / 123 / EN）----
    private enum LayoutMode
    {
        Chinese,
        Number,
        English,
        Symbol,
    }

    private LayoutMode _layoutMode = LayoutMode.Chinese;
    private readonly List<System.Windows.Controls.Button> _panelButtons = [];

    private static (double width, double height) GetDesignSize(LayoutMode mode) => mode switch
    {
        LayoutMode.Chinese => (ChineseDesignWidth, ChineseDesignHeight),
        LayoutMode.Number => (NumberDesignWidth, NumberDesignHeight),
        LayoutMode.English => (EnglishDesignWidth, EnglishDesignHeight),
        LayoutMode.Symbol => (SymbolDesignWidth, SymbolDesignHeight),
        _ => (ChineseDesignWidth, ChineseDesignHeight),
    };

    private double GetMinScaleFor(LayoutMode mode) => mode switch
    {
        LayoutMode.Chinese => _screenMinScaleChinese,
        LayoutMode.Number => _screenMinScaleNumber,
        LayoutMode.English => _screenMinScaleEnglish,
        LayoutMode.Symbol => _screenMinScaleSymbol,
        _ => 1.0,
    };

    private double GetScaleFor(LayoutMode mode)
    {
        var min = GetMinScaleFor(mode);
        var saved = mode switch
        {
            LayoutMode.Chinese => _settings.ChineseScale,
            LayoutMode.Number => _settings.NumberScale,
            LayoutMode.English => _settings.EnglishScale,
            LayoutMode.Symbol => _settings.SymbolScale,
            _ => null,
        };
        return Math.Max(min, saved ?? min);
    }

    private void SetScaleFor(LayoutMode mode, double scale, bool? isManual = null, System.Drawing.Rectangle? workArea = null)
    {
        var s = Math.Max(GetMinScaleFor(mode), scale);
        var work = workArea ?? GetCurrentWorkArea();
        switch (mode)
        {
            case LayoutMode.Chinese:
                _settings.ChineseScale = s;
                _settings.ChineseScaleWorkAreaWidth = work.Width;
                _settings.ChineseScaleWorkAreaHeight = work.Height;
                if (isManual is not null) _settings.ChineseScaleIsManual = isManual.Value;
                break;
            case LayoutMode.Number:
                _settings.NumberScale = s;
                _settings.NumberScaleWorkAreaWidth = work.Width;
                _settings.NumberScaleWorkAreaHeight = work.Height;
                if (isManual is not null) _settings.NumberScaleIsManual = isManual.Value;
                break;
            case LayoutMode.English:
                _settings.EnglishScale = s;
                _settings.EnglishScaleWorkAreaWidth = work.Width;
                _settings.EnglishScaleWorkAreaHeight = work.Height;
                if (isManual is not null) _settings.EnglishScaleIsManual = isManual.Value;
                break;
            case LayoutMode.Symbol:
                _settings.SymbolScale = s;
                _settings.SymbolScaleWorkAreaWidth = work.Width;
                _settings.SymbolScaleWorkAreaHeight = work.Height;
                if (isManual is not null) _settings.SymbolScaleIsManual = isManual.Value;
                break;
        }
    }

    /// <summary>展开态窗口高度唯一真相：设计高 + 设计高外的扩张行（宏行/编辑行）。所有 Height 写入以此为准。</summary>
    /// <summary>展开态设计高（容器）唯一真相：设计高 + 设计高外的扩张行（宏行/编辑行）。</summary>
    private double ExpandedDesignHeight()
    {
        var (_, designH) = GetDesignSize(_layoutMode);
        return designH
            + (_isMacroBarExpanded ? MacroBarHeight : 0)
            + (_isEditRowExpanded ? _editRowHeight : 0);
    }

    /// <summary>展开态窗口高度 = 容器设计高 × scale。窗口与容器永远同步，谁改都得成对改。</summary>
    private double ExpandedWindowHeight() => ExpandedDesignHeight() * _currentScale;

    private void ApplyModeScale()
    {
       
        var (designWLog, designHLog) = GetDesignSize(_layoutMode);
       
        var (designW, designH) = GetDesignSize(_layoutMode);
        RootBorder.Width = designW;
        RootBorder.Height = designH;
        RootBorder.RenderTransform = new ScaleTransform(_currentScale, _currentScale);
        //Width = designW * _currentScale;
        //Height = designH * _currentScale;
        Width = designW * _currentScale;
        RootBorder.Height = ExpandedDesignHeight();
        Height = ExpandedWindowHeight();
    }

    private void SetLayoutMode(LayoutMode mode)
    {
        if (mode != _layoutMode)
        {
            FileLogger.Info($"layout: {_layoutMode} -> {mode}, fg={NativeMethods.GetForegroundWindow()}");

            // ⚠ 坑：切换模式只保存当前 scale，必须保留手动/自动标记，避免切回时误判为首次启动。
            SetScaleFor(_layoutMode, _currentScale, isManual: null);
        }
        else
        {
            FileLogger.Info($"layout: set same {mode}");
        }

        _layoutMode = mode;
        _currentScale = GetScaleFor(mode);
        ApplyModeScale();

        T9Panel.Visibility = mode == LayoutMode.Chinese ? Visibility.Visible : Visibility.Collapsed;
        NumberPanel.Visibility = mode == LayoutMode.Number ? Visibility.Visible : Visibility.Collapsed;
        English26Panel.Visibility = mode == LayoutMode.English ? Visibility.Visible : Visibility.Collapsed;
        SymbolPanel.Visibility = mode == LayoutMode.Symbol ? Visibility.Visible : Visibility.Collapsed;
       

        // 批次 10：切出 26 键时收起编辑层、复位 Shift、清空粘性修饰键；Caps Lock 保留
        if (mode != LayoutMode.English)
        {
            SetEditRowVisible(false);
            _isShiftOn = false;
            _isCtrlActive = false;
            _isAltActive = false;
            _isCtrlLocked = false;
            _isAltLocked = false;
            UpdateEnglish26ShiftState();
            UpdateModifierButtonState();
        }

        var label = mode switch
        {
            LayoutMode.Chinese => "中",
            LayoutMode.Number => "123",
            LayoutMode.English => "EN",
            _ => "中",
        };
        LangButton.Content = label;
        NumberLangButton.Content = label;
        English26LangButton.Content = label;
        English26EditButton.Visibility = _settings.ShowEditRow ? Visibility.Visible : Visibility.Collapsed;
        UpdateShortcutPanelVisibility();
        UpdateMacroPanelVisibility();
        UpdateSymbolRowVisibility();

        // ⚠ 坑：切模式必须清空组串，否则中文缓冲带到英文模式会误发字母串。
        _controller.ClearInput();
    }



    private double GetLetterRowHeight() => English26Row1.ActualHeight;

    private void UpdateShortcutPanelVisibility()
    {
        // 批次 11：26 键英文模式隐藏快捷键行（粘性 Ctrl/Alt 已覆盖组合快捷键）
        var show = _settings.ShowShortcutBar && _layoutMode == LayoutMode.Chinese;
        ShortcutPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _isMacroBarExpanded;
    private const double MacroBarHeight = 34.0;
    private static readonly Duration ExpansionAnimationDuration = new(TimeSpan.FromMilliseconds(150));

    private void UpdateMacroPanelVisibility()
    {
        var show = _settings.ShowMacroBar && _settings.Macros.Count > 0
            && (_layoutMode == LayoutMode.Chinese || _layoutMode == LayoutMode.English);

        if (show == _isMacroBarExpanded)
        {
            return;
        }

        _isMacroBarExpanded = show;
        MacroPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MacroPanel.Height = show ? MacroBarHeight : 0;

        // ⚠ 坑：贴条/贴条动画期间窗口高度不归这里管。贴条时 Height=120（细条），
        // 此刻加增量会污染基数；只记状态，展开时由 ExpandFromStrip 统一校准。
        if (_isDockedAsStrip || _isAnimatingStrip)
        {
            return;
        }

        var target = ExpandedWindowHeight();
        if (!IsVisible)
        {
            Height = target;
            RootBorder.Height = ExpandedDesignHeight();
            return;
        }

        var sb = new Storyboard();
        var windowAnim = new DoubleAnimation(Height, target, ExpansionAnimationDuration);
        Storyboard.SetTarget(windowAnim, this);
        Storyboard.SetTargetProperty(windowAnim, new PropertyPath(HeightProperty));
        var rootAnim = new DoubleAnimation(RootBorder.Height, ExpandedDesignHeight(), ExpansionAnimationDuration);
        Storyboard.SetTarget(rootAnim, RootBorder);
        Storyboard.SetTargetProperty(rootAnim, new PropertyPath(HeightProperty));
        sb.Children.Add(windowAnim);
        sb.Children.Add(rootAnim);
        sb.Begin(this);
    }

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

    private void BuildMacroPanel()
    {
        MacroPanel.Children.Clear();
        foreach (var macro in _settings.Macros)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = string.IsNullOrWhiteSpace(macro.Name) ? "宏" : macro.Name,
                Style = (Style)FindResource("ShortcutButtonStyle"),
                Focusable = false,
                Tag = macro,
            };
            btn.Click += OnMacroClick;
            _ = MacroPanel.Children.Add(btn);
        }

        ApplyTheme();
    }

    private void OnMacroClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Macro macro })
        {
            return;
        }

        try
        {
            switch (macro.ActionType)
            {
                case MacroActionType.Text:
                    _injector.InjectText(macro.Content);
                    break;
                case MacroActionType.Shortcut:
                    var keys = MacroShortcutParser.Parse(macro.Content);
                    if (keys.Length > 0)
                    {
                        _injector.InjectShortcut(keys);
                    }
                    else
                    {
                        FileLogger.Error($"macro '{macro.Name}' shortcut parse failed: '{macro.Content}'");
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"macro '{macro.Name}' execute failed: {ex.Message}");
        }
    }

    private void LangButton_Click(object sender, RoutedEventArgs e) => SetLayoutMode(_layoutMode switch
    {
        LayoutMode.Chinese => LayoutMode.Number,
        LayoutMode.Number => LayoutMode.English,
        _ => LayoutMode.Chinese,
    });

    private void SymbolButton_Click(object sender, RoutedEventArgs e) =>
        SetLayoutMode(_layoutMode == LayoutMode.Symbol ? LayoutMode.Chinese : LayoutMode.Symbol);

    private void NumberKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
        {
            _controller.CommitDirect(tag);
        }
    }

    private void SpaceButton_Click(object sender, RoutedEventArgs e) => _controller.CommitSpace();

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

    private void BuildSymbolPanel()
    {
        // 页签行：每页一个页签 + 返回键常驻（§13.11：返回键必须可见、一眼能找到）
        for (var i = 0; i < SymbolLayout.Pages.Count; i++)
        {
            var pageIndex = i;
            var tab = MakePanelButton(SymbolLayout.Pages[i].Title);
            tab.Click += (_, _) => ShowSymbolPage(pageIndex);
            _panelButtons.Add(tab);
            _ = SymbolTabs.Children.Add(tab);
        }

        var back = MakePanelButton("返回");
        back.Click += (_, _) => SetLayoutMode(LayoutMode.Chinese);
        _panelButtons.Add(back);
        _ = SymbolTabs.Children.Add(back);

        ShowSymbolPage(0);
    }

    private void ShowSymbolPage(int pageIndex)
    {
        SymbolGrid.Children.Clear();
        foreach (var sym in SymbolLayout.Pages[pageIndex].Symbols)
        {
            var btn = MakePanelButton(sym);
            btn.Click += (_, _) => _controller.CommitDirect(sym);
            _ = SymbolGrid.Children.Add(btn);
        }

        ApplyTheme(); // 新页按钮应用当前主题/透明度
    }

    private void RefreshUi()
    {
        InputLabel.Items.Clear();
        if (_controller.IsEmpty)
        {
            InputLabel.Items.Add(new TextBlock
            {
                Text = "点数字键开始输入，按住拖动选字母",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
                Foreground = InputLabel.Foreground,
                Opacity = 0.7,
            });
        }
        else
        {
            foreach (var segment in _controller.CommittedSegments)
            {
                InputLabel.Items.Add(MakeSegmentButton(segment.Text, isCommitted: true, -1));
            }

            var syllables = _controller.DisplaySyllables;
            var activeIndex = _controller.ActiveSyllableIndex;
            var column = 0;
            for (var i = 0; i < syllables.Count; i++)
            {
                var isActive = i >= activeIndex;
                foreach (var ch in syllables[i])
                {
                    InputLabel.Items.Add(MakeLetterButton(ch.ToString(), isActive, i, column));
                    column++;
                }
            }
        }

        Bar.Render(_controller);

        // §13.37 审计：UI 回显与引擎缓冲不一致时写日志
        var echo = string.Join("", InputLabel.Items.OfType<Button>().Select(b => b.Content?.ToString() ?? string.Empty));
        var buffer = _controller.IsEmpty ? string.Empty : _controller.DisplayInput.Replace(" ", string.Empty);
        if (!_controller.IsEmpty && echo != buffer)
        {
            FileLogger.Error($"state-mismatch: echo={echo} buffer={buffer} controller-empty={_controller.IsEmpty}");
        }
    }

    private Button MakeSegmentButton(string text, bool isCommitted, int syllableIndex, bool isActive = true)
    {
        var btn = new Button
        {
            Content = text,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 29,
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        if (isCommitted)
        {
            btn.Foreground = Brushes.Gray;
            btn.Opacity = 0.6;
            btn.IsEnabled = false;
        }
        else
        {
            btn.Foreground = isActive ? Foreground : Brushes.Gray;
            btn.Opacity = isActive ? 1.0 : 0.5;
            btn.Click += (_, _) =>
            {
                _controller.SetActiveSyllableIndex(syllableIndex);
                if (ReferenceEquals(Mouse.Captured, btn))
                {
                    btn.ReleaseMouseCapture();
                }
            };
        }

        return btn;
    }

    private Button MakeLetterButton(string text, bool isActive, int syllableIndex, int columnIndex)
    {
        var btn = new Button
        {
            Content = text,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 29,
            Padding = new Thickness(1, 0, 1, 0),
            Margin = new Thickness(0.5, 0, 0.5, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = isActive ? Foreground : Brushes.Gray,
            Opacity = isActive ? 1.0 : 0.5,
        };

        btn.Click += (_, _) =>
        {
            // §letter-pin：组合切换走候选栏引导项（PinyinGuide），字母片点击仅激活音节重锚定。
            _controller.SetActiveSyllableIndex(syllableIndex);
            if (ReferenceEquals(Mouse.Captured, btn))
            {
                btn.ReleaseMouseCapture();
            }
        };

        return btn;
    }

    private void ApplyTheme()
    {
        // 跟随系统深浅色
        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch (System.Security.SecurityException)
        {
            // 注册表不可读：用深色
        }

        // ⚠ 坑：R8 背景/按键不透明度独立（默认 85%/95%），边框与字符恒不透明；
        // 窗口级 Opacity 必须保持 1，否则子元素 alpha 会二次叠加。
        var bgAlpha = (byte)(_flashOverride ? 255 : 255 * _settings.Opacity);
        var keyAlpha = (byte)(_flashOverride ? 255 : 255 * _settings.KeyOpacity);

        // §13.18-2：键盘背景色/按键背景色可自定义（无效值回退主题默认）
        var palette = ThemeColors.Resolve(light, _settings.BackgroundColor, _settings.KeyBackgroundColor);
        Brush bg = new SolidColorBrush(ThemeColors.WithAlpha(palette.Background, bgAlpha));
        Brush keyBg = new SolidColorBrush(ThemeColors.WithAlpha(palette.KeyBackground, keyAlpha));
        Brush keyPressed = new SolidColorBrush(ThemeColors.WithAlpha(palette.KeyPressed, keyAlpha));
        Brush keyBorder = new SolidColorBrush(palette.KeyBorder);
        Brush fg = new SolidColorBrush(palette.Foreground);
        Brush subFg = new SolidColorBrush(palette.SubForeground);
        Brush barBg = new SolidColorBrush(ThemeColors.WithAlpha(palette.BarBackground, keyAlpha));
        var borderWidth = new Thickness(_settings.KeyBorderWidth);

        RootBorder.Background = bg;
        RootBorder.BorderBrush = keyBorder;
        Bar.BarBackground = barBg;
        Bar.SubForeground = subFg;
        Bar.Foreground = fg;
        InputLabel.Foreground = fg;
        GripRect.Fill = subFg;

        // ⚠ 坑：§13.15 回车/删除/面板键前景必须随主题，否则浅色主题下会出现白底白字看不见。
        EnterButton.Background = keyBg;
        EnterButton.Foreground = fg;
        EnterButton.BorderThickness = borderWidth;
        BackspaceButton.Background = keyBg;
        BackspaceButton.Foreground = fg;
        BackspaceButton.BorderThickness = borderWidth;
        foreach (var btn in new[] { LangButton, SymbolButton, FlashButton, ClearButton, HideMiniButton, English26ShiftButton, English26EditButton, English26LangButton, English26CtrlButton, English26AltButton, RightEdgeStripButton })
        {
            btn.Foreground = subFg;
        }
        foreach (var key in new[] { Key1, Key2, Key3, Key4, Key5, Key6, Key7, Key8, Key9, Key0 })
        {
            key.KeyBackground = keyBg;
            key.KeyPressedBackground = keyPressed;
            key.KeyBorderBrush = keyBorder;
            key.KeyBorderThickness = borderWidth;
            key.SubForeground = subFg;
            key.Foreground = fg;
        }
        foreach (var btn in _panelButtons)
        {
            btn.Background = keyBg;
            btn.Foreground = fg;
            btn.BorderThickness = borderWidth;
        }
        foreach (var btn in new[] { SelectAllButton, CopyButton, PasteButton, SearchButton, ScreenshotButton })
        {
            btn.Background = keyBg;
            btn.Foreground = fg;
            btn.BorderThickness = borderWidth;
        }
        foreach (var child in MacroPanel.Children)
        {
            if (child is System.Windows.Controls.Button macroBtn)
            {
                macroBtn.Background = keyBg;
                macroBtn.Foreground = fg;
                macroBtn.BorderThickness = borderWidth;
            }
        }

        ApplyThemeToGridChildren(NumberPanel, keyBg, fg, borderWidth);
        ApplyThemeToGridChildren(SymbolPanel, keyBg, fg, borderWidth);
        // 编辑行按钮已加入 _panelButtons，由上方循环统一涂刷；符号行按钮同理。

        // 重新应用功能键高亮，防止被主题涂刷覆盖
        UpdateEnglish26ShiftState();
        UpdateModifierButtonState();
        UpdateEditCapsLockButtonState();
    }

    private void ApplyThemeToGridChildren(System.Windows.Controls.Grid grid, Brush bg, Brush fg, Thickness borderWidth)
    {
        foreach (var child in grid.Children)
        {
            if (child is System.Windows.Controls.Button btn)
            {
                btn.Background = bg;
                btn.Foreground = fg;
                btn.BorderThickness = borderWidth;
            }
            else if (child is RepeatButton rb)
            {
                rb.Background = bg;
                rb.Foreground = fg;
                rb.BorderThickness = borderWidth;
            }
            else if (child is System.Windows.Controls.Panel panel)
            {
                foreach (var inner in panel.Children)
                {
                    if (inner is System.Windows.Controls.Button innerBtn)
                    {
                        innerBtn.Background = bg;
                        innerBtn.Foreground = fg;
                        innerBtn.BorderThickness = borderWidth;
                    }
                    else if (inner is RepeatButton innerRb)
                    {
                        innerRb.Background = bg;
                        innerRb.Foreground = fg;
                        innerRb.BorderThickness = borderWidth;
                    }
                }
            }
        }
    }

    private bool _flashOverride;

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b) =>
        new(Color.FromArgb(a, r, g, b));

    /// <summary>显隐动作单一执行点（§13.24：显式 Show / Hide）。</summary>
    private void ApplyVis(VisAction action)
    {
        FileLogger.Info($"apply-vis: action={action} visible={IsVisible} docked={_isDockedAsStrip}");
        if (action == VisAction.Show && !IsVisible)
        {
            Show();
        }
        else if (action == VisAction.Hide)
        {
            DockToRightEdge();
        }
    }

    private void ToggleVisibility()
    {
        FileLogger.Info($"toggle-visibility: visible={IsVisible} docked={_isDockedAsStrip}");
        if (_isDockedAsStrip)
        {
            ExpandFromStrip();
        }
        else if (Visibility == Visibility.Visible && IsVisible)
        {
            DockToRightEdge();
        }
        else
        {
            Show();
            Flash();
        }
    }

    /// <summary>找回键盘：透明度瞬时拉满，1.5 秒后恢复。</summary>
    public void Flash()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _flashOverride = true;
            ApplyTheme();
            _flashTimer.Start();
            if (!IsVisible)
            {
                Show();
            }
            else if (_isDockedAsStrip)
            {
                ExpandFromStrip();
            }

            _tray.FlashTray();
        });
    }

    // ---- §13.44：贴边系统 ----
    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // 拖拽标题条期间不处理；释放时由 OnPreviewMouseUpGlobal 统一吸附。
    }

    private void OnPreviewMouseUpGlobal(object sender, MouseButtonEventArgs e)
    {
        FileLogger.Info($"preview-mouse-up: button={e.ChangedButton} dragging={_isDraggingTitle}");

        // ⚠ 坑：拖拽释放时才吸附，预览 MouseUp 在点击时也会触发，必须靠 _isDraggingTitle 区分。
        if (e.ChangedButton == MouseButton.Left && _isDraggingTitle)
        {
            _isDraggingTitle = false;
            TrySnapToEdges();
        }
    }

    private void OnPreviewKeyDownGlobal(object sender, System.Windows.Input.KeyEventArgs e)
    {
        FileLogger.Info($"preview-key-down: key={e.Key} docked={_isDockedAsStrip}");
        if (e.Key == Key.Escape && !_isDockedAsStrip)
        {
            DockToRightEdge();
            e.Handled = true;
        }
    }

    private static System.Drawing.Rectangle GetWindowWorkArea(Window window)
    {
        // §13.45-7：以窗口实际所在屏幕的 WorkArea 为准
        var helper = new WindowInteropHelper(window);
        var screen = System.Windows.Forms.Screen.FromHandle(helper.Handle);
        return screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
    }

    private void RecalculateScaleForCurrentScreen()
    {
        var work = GetWindowWorkArea(this);
        _screenMinScaleChinese = Math.Min(1.0, ComputeScreenMinScale(ChineseDesignWidth, ChineseDesignHeight, ChineseTargetWidthRatio, work));
        _screenMinScaleNumber = Math.Min(1.0, ComputeScreenMinScale(NumberDesignWidth, NumberDesignHeight, ChineseTargetWidthRatio, work));
        _screenMinScaleEnglish = ComputeScreenMinScale(EnglishDesignWidth, EnglishDesignHeight, EnglishTargetWidthRatio, work);
        _screenMinScaleSymbol = Math.Min(1.0, ComputeScreenMinScale(SymbolDesignWidth, SymbolDesignHeight, ChineseTargetWidthRatio, work));

        // ⚠ 坑：§13.45 首启自动计算；后续 WorkArea 变化超 30% 且非手动值时才重算，避免覆盖用户手动缩放。
        var isFirstRun = !_settings.ChineseScale.HasValue || !_settings.NumberScale.HasValue
            || !_settings.EnglishScale.HasValue || !_settings.SymbolScale.HasValue;

        if (isFirstRun)
        {
            SetScaleFor(LayoutMode.Chinese, _screenMinScaleChinese, false, work);
            SetScaleFor(LayoutMode.Number, _screenMinScaleNumber, false, work);
            SetScaleFor(LayoutMode.English, _screenMinScaleEnglish, false, work);
            SetScaleFor(LayoutMode.Symbol, _screenMinScaleSymbol, false, work);
            _settings.Save();
            _tray.ShowBalloon("NineKey", "已按当前屏幕自动调整键盘尺寸");
            return;
        }

        var anyRecalculated = false;
        if (ShouldRecalculateScale(LayoutMode.Chinese, work))
        {
            SetScaleFor(LayoutMode.Chinese, _screenMinScaleChinese, false, work);
            anyRecalculated = true;
        }

        if (ShouldRecalculateScale(LayoutMode.Number, work))
        {
            SetScaleFor(LayoutMode.Number, _screenMinScaleNumber, false, work);
            anyRecalculated = true;
        }

        if (ShouldRecalculateScale(LayoutMode.English, work))
        {
            SetScaleFor(LayoutMode.English, _screenMinScaleEnglish, false, work);
            anyRecalculated = true;
        }

        if (ShouldRecalculateScale(LayoutMode.Symbol, work))
        {
            SetScaleFor(LayoutMode.Symbol, _screenMinScaleSymbol, false, work);
            anyRecalculated = true;
        }

        if (anyRecalculated)
        {
            _settings.Save();
            _tray.ShowBalloon("屏幕分辨率变化", "已自动重新计算键盘尺寸");
        }
    }

    private bool ShouldRecalculateScale(LayoutMode mode, System.Drawing.Rectangle work)
    {
        var (isManual, savedW, savedH) = mode switch
        {
            LayoutMode.Chinese => (_settings.ChineseScaleIsManual, _settings.ChineseScaleWorkAreaWidth, _settings.ChineseScaleWorkAreaHeight),
            LayoutMode.Number => (_settings.NumberScaleIsManual, _settings.NumberScaleWorkAreaWidth, _settings.NumberScaleWorkAreaHeight),
            LayoutMode.English => (_settings.EnglishScaleIsManual, _settings.EnglishScaleWorkAreaWidth, _settings.EnglishScaleWorkAreaHeight),
            LayoutMode.Symbol => (_settings.SymbolScaleIsManual, _settings.SymbolScaleWorkAreaWidth, _settings.SymbolScaleWorkAreaHeight),
            _ => (false, null, null),
        };

        if (isManual) return false;
        if (savedW is null || savedH is null || savedW.Value == 0 || savedH.Value == 0) return true;

        var dw = Math.Abs(work.Width - savedW.Value) / savedW.Value;
        var dh = Math.Abs(work.Height - savedH.Value) / savedH.Value;
        return dw > 0.30 || dh > 0.30;
    }

    private void ClampWindowToWorkArea()
    {
        if (_isDockedAsStrip)
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
    }

    private void TrySnapToEdges()
    {
        FileLogger.Info($"try-snap: docked={_isDockedAsStrip} bounds=({Left},{Top},{Width},{Height})");
        if (_isDockedAsStrip)
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
        FileLogger.Info($"dock-right: docked={_isDockedAsStrip} animating={_isAnimatingStrip} visible={IsVisible} bounds=({Left},{Top},{Width},{Height})");
        if (_isDockedAsStrip || _isAnimatingStrip)
        {
            return;
        }

        _isAnimatingStrip = true;
        _restoredBounds = new Rect(Left, Top, Width, Height);
        _isDockedAsStrip = true;

        // ⚠ 坑：贴边计算必须用窗口实际所在屏幕 WorkArea，SystemParameters.WorkArea 只返回主屏。
        var work = SystemParameters.WorkArea;
        var stripTop = Math.Max(work.Top, Math.Min(Top, work.Bottom - DockedStripHeight));
        var target = new Rect(work.Right - DockedStripWidth, stripTop, DockedStripWidth, DockedStripHeight);

        RootBorder.Visibility = Visibility.Collapsed;
        RightEdgeStrip.Visibility = Visibility.Visible;

        AnimateWindowBounds(target, () => _isAnimatingStrip = false);
    }

    private void ExpandFromStrip()
    {
        FileLogger.Info($"expand-from-strip: docked={_isDockedAsStrip} animating={_isAnimatingStrip} restored=({_restoredBounds})");
        if (!_isDockedAsStrip || _isAnimatingStrip)
        {
            return; // 非贴边或动画进行中：本方法只管贴边展开，不干预其他状态
        }

        _isAnimatingStrip = true; // 上锁：防止展开动画期间重复进入
        _stripHoverTimer.Stop();  // 停悬停检测，避免动画中被再次触发

        // ===== 修复一（2026-09-09）：展开尺寸按当前模式实时计算 =====
        // 病根：_restoredBounds 缓存的是"贴边那一刻"的窗口尺寸。贴边期间切模式（9键→26键）
        // 不会刷新缓存，展开时按旧尺寸恢复，26键内容被塞进 9 键窗口 → 裁切。
        // 修法：宽/高按当前模式现算；缓存只保留展开位置（Left/Top）。
        var (designW, _) = GetDesignSize(_layoutMode); // 当前模式的设计宽（未缩放）；高度走下方 ExpandedWindowHeight()
        var targetWidth = designW * _currentScale;    // 窗口实际宽 = 设计宽 × 缩放
        var targetHeight = ExpandedWindowHeight();     // 窗口实际高（内部已乘 scale）
        var targetRect = new Rect(_restoredBounds.Left, _restoredBounds.Top, targetWidth, targetHeight);

        FileLogger.Info($"expand-from-strip: computed targetRect=({targetRect.Left},{targetRect.Top},{targetRect.Width},{targetRect.Height}) layout={_layoutMode} scale={_currentScale}");

        AnimateWindowBounds(targetRect, () =>
        {
            // ===== 修复二（2026-09-09）：动画完成后同步校准窗口与内容布局 =====
            // 病根：回调原来只校准 Height，宽度和内容面板（English26Panel）没有重新测量，
            // 首次展开时面板仍按旧度量渲染（实测 ActualHeight=326，期望铺满 450），按键行被压扁；
            // 折叠再展开才恢复正常。
            FileLogger.Info($"expand-from-strip: completion-start English26Panel.Actual=({English26Panel.ActualWidth},{English26Panel.ActualHeight}) RootBorder=({RootBorder.Width},{RootBorder.Height}) Window=({Width},{Height})");

            _isDockedAsStrip = false;                          // 展开完成：脱离贴边状态
            _isAnimatingStrip = false;                         // 解锁
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

            // 验证日志（判读：Actual 才是窗口真实渲染尺寸，Width/Height 只是期望值）
            FileLogger.Info($"expand-from-strip: completion-end English26Panel.Actual=({English26Panel.ActualWidth},{English26Panel.ActualHeight}) RootBorder=({RootBorder.Width},{RootBorder.Height}) Window=({Width},{Height}) ActualWin=({ActualWidth},{ActualHeight}) Pos=({Left},{Top})");

            ClampWindowToWorkArea(); // 兜底：展开后若超出屏幕工作区则夹回（窗口被挪属正常）
        });
    }

    /// <summary>沿视觉树向上查：命中点是否落在按键/按钮内。</summary>
    private static bool IsOnInteractiveControl(DependencyObject? hit)
    {
        while (hit is not null)
        {
            if (hit is System.Windows.Controls.Primitives.ButtonBase
                or NineKey.Keyboard.Views.KeyButton)
            {
                return true;
            }

            var parent = VisualTreeHelper.GetParent(hit);
            hit = parent ?? (hit as FrameworkElement)?.Parent;
        }

        return false;
    }

    private void RightEdgeStripButton_Click(object sender, RoutedEventArgs e) => ExpandFromStrip();

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // §13.45-6：整个窗口背景区均可拖动
        // ⚠ 坑：点在 KeyButton（UserControl）/按钮上时不得启动 DragMove——DragMove 会抢走鼠标捕获，
        // 导致 KeyButton 的 LostMouseCapture 复位按下态、抬起时点击被吞。
        if (IsOnInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        SafeDragMove();
    }

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // §13.45-6：候选栏空白区也可拖动（候选按钮已处理事件，不会冒泡到此）
        if (IsOnInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }
        SafeDragMove();
    }

    private void SafeDragMove()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _isDraggingTitle = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // ⚠ 坑：合成输入/焦点切换可能导致鼠标状态与 WPF 不一致，DragMove 认为按钮未按下；
            // 吞掉异常避免闪退，拖拽状态由 MouseUp 清理。
            _isDraggingTitle = false;
        }
    }

    private System.Windows.Point _stripDragStart;

    private void RightEdgeStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // §13.45-6：细条上按下先同步展开；若随后移动则跟随拖动
        if (_isDockedAsStrip)
        {
            _isDockedAsStrip = false;
            RightEdgeStrip.Visibility = Visibility.Collapsed;
            RootBorder.Visibility = Visibility.Visible;
            Left = _restoredBounds.Left;
            Top = _restoredBounds.Top;
            Width = _restoredBounds.Width;
            Height = _restoredBounds.Height;
            _isAnimatingStrip = false;
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

    // §13.42/§13.43：尺寸调节改为按当前模式设计尺寸等比缩放，scale ≥ 1
    private double _rsMouseX, _rsMouseY, _rsOppX, _rsOppY, _rsDiagX, _rsDiagY, _rsDiagLen2, _rsStartScale;

    private void ResizeGrip_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        var p = Mouse.GetPosition(null); // 屏幕坐标
        _rsMouseX = p.X;
        _rsMouseY = p.Y;
        _rsStartScale = _currentScale;

        var corner = (sender as System.Windows.FrameworkElement)?.Tag as string ?? "SE";
        (_rsOppX, _rsOppY) = corner switch
        {
            "SE" => (Left, Top),
            "SW" => (Left + Width, Top),
            "NE" => (Left, Top + Height),
            _ => (Left + Width, Top + Height), // NW
        };

        _rsDiagX = _rsMouseX - _rsOppX;
        _rsDiagY = _rsMouseY - _rsOppY;
        _rsDiagLen2 = _rsDiagX * _rsDiagX + _rsDiagY * _rsDiagY;
        if (_rsDiagLen2 < 1)
        {
            _rsDiagLen2 = 1;
        }
    }

    private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var corner = (sender as System.Windows.FrameworkElement)?.Tag as string ?? "SE";
        var p = Mouse.GetPosition(null);
        var mx = p.X - _rsOppX;
        var my = p.Y - _rsOppY;

        // ⚠ 坑：鼠标位移必须投影到对角线方向，否则斜向拖动会改变宽高比。
        var dot = mx * _rsDiagX + my * _rsDiagY;
        var ratio = dot / _rsDiagLen2;
        var newScale = Math.Max(GetMinScaleFor(_layoutMode), _rsStartScale * ratio);
        SetModeScale(newScale, keepOpposite: corner);
    }

    private void SetModeScale(double scale, string? keepOpposite = null)
    {
        var s = Math.Max(GetMinScaleFor(_layoutMode), scale);
        _currentScale = s;
        SetScaleFor(_layoutMode, s, isManual: true, GetWindowWorkArea(this));

        var (designW, designH) = GetDesignSize(_layoutMode);
        RootBorder.Width = designW;
        RootBorder.Height = designH;
        RootBorder.RenderTransform = new ScaleTransform(s, s);

        var newW = designW * s;
        var newH = designH * s;

        // ⚠ 坑：从非 SE 角缩放时，必须同步移动 Left/Top 保持对边不动，否则窗口会飘。
        switch (keepOpposite)
        {
            case "SE":
                break;
            case "SW":
                Left = _rsOppX - newW;
                break;
            case "NE":
                Top = _rsOppY - newH;
                break;
            case "NW":
                Left = _rsOppX - newW;
                Top = _rsOppY - newH;
                break;
        }

        Width = newW;
        Height = newH;
    }

    private void ResizeGrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // §13.43 二次修订：双击恢复屏幕适配默认尺寸
        SetModeScale(GetMinScaleFor(_layoutMode));
    }

    private void BackspaceButton_Click(object sender, RoutedEventArgs e) => _controller.Backspace();

    private void EnterButton_Click(object sender, RoutedEventArgs e) => _controller.CommitEnter();

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _controller.ClearInput();

    private void HideButton_Click(object sender, RoutedEventArgs e) => ApplyVis(_vis.Hide());

    private void FlashButton_Click(object sender, RoutedEventArgs e) => Flash();

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
        if (_isDockedAsStrip || _isAnimatingStrip)
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

    // ---- §13.26 快捷键行 ----
    private void ShortcutSelectAll_Click(object sender, RoutedEventArgs e) =>
        _injector.InjectShortcut([NativeMethods.VkControl, NativeMethods.VkA]);

    private void ShortcutCopy_Click(object sender, RoutedEventArgs e) =>
        _injector.InjectShortcut([NativeMethods.VkControl, NativeMethods.VkC]);

    private void ShortcutPaste_Click(object sender, RoutedEventArgs e) =>
        _injector.InjectShortcut([NativeMethods.VkControl, NativeMethods.VkV]);

    private void ShortcutSearch_Click(object sender, RoutedEventArgs e) =>
        _injector.InjectShortcut([NativeMethods.VkControl, NativeMethods.VkF]);

    private void ShortcutScreenshot_Click(object sender, RoutedEventArgs e) =>
        _injector.InjectShortcut([NativeMethods.VkLWin, NativeMethods.VkShift, NativeMethods.VkS]);

    private void UpdateCopyButtonState()
    {
        // ⚠ 坑：目标控件可能随时释放或返回 COM 错误，必须用空 catch 吞掉，否则后台定时器会崩。
        bool hasSelection = false;
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el is not null && el.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) && pattern is TextPattern textPattern)
            {
                var ranges = textPattern.GetSelection();
                hasSelection = ranges.Length > 0 && Array.Exists(ranges, static r => !string.IsNullOrEmpty(r.GetText(-1)));
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        CopyButton.IsEnabled = hasSelection;
    }

    private void SetTraditionalOutput(bool on)
    {
        _settings.TraditionalOutput = on;
        if (on)
        {
            _outputInjector.Converter = s => (_opencc ??= new Opencc(OpenccConfig.S2T)).Convert(s);
        }
        else
        {
            _outputInjector.Converter = null;
        }
        _settings.Save();
    }

    private void SetAutoPopup(bool on)
    {
        _settings.AutoPopup = on;
        if (on)
        {
            _focusWatcher.Start();
        }
        else
        {
            _focusWatcher.Stop();
        }
    }

    private void SetHotkey(bool on)
    {
        _settings.HotkeyEnabled = on;
        if (on)
        {
            _hotkey.Register(new WindowInteropHelper(this));
        }
        else
        {
            _hotkey.Dispose();
        }
    }

    private void SetStartup(bool on)
    {
        _settings.RunAtStartup = on;
        SystemSettingsService.SetRunAtStartup(on);
    }

    private void SetTouchKbGuard(bool on)
    {
        _settings.DisableSystemTouchKeyboard = on;
        SystemSettingsService.SetDisableSystemTouchKeyboard(on);
    }

    private void SetFuzzyProfile(Action<AppSettings> update)
    {
        update(_settings);
        _engine.FuzzyProfile = _settings.ToFuzzyProfile();
    }

    /// <summary>§0.3：开启"以管理员运行"→ 保存设置后以 runas 重启自身（UAC 确认一次）。</summary>
    private void OnAdminChecked()
    {
        _settings.RunAsAdmin = true;
        if (SystemSettingsService.IsCurrentProcessElevated())
        {
            return; // 已是提权运行，仅记录偏好
        }

        _settings.Save();
        if (SystemSettingsService.RestartElevated())
        {
            Close();
        }
        else
        {
            // 用户取消 UAC：还原偏好
            _settings.RunAsAdmin = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _flashTimer.Stop();
        _fgTracker.Stop();
        _selectionTracker.Stop();
        _ctrlDoubleTapTimer.Stop();
        _altDoubleTapTimer.Stop();
        _focusWatcher.Dispose();
        _hotkey.Dispose();
        _tray.Dispose();
        _controller.SaveUserDictionary();
        SetScaleFor(_layoutMode, _currentScale, isManual: null); // 关闭时只保存当前 scale，保留手动/自动标记和 WorkArea 记录

        var (designW, designH) = GetDesignSize(_layoutMode);
        _settings.Width = designW * _currentScale;
        _settings.Height = designH * _currentScale;

        if (_isDockedAsStrip)
        {
            _settings.Left = _restoredBounds.Left;
            _settings.Top = _restoredBounds.Top;
        }
        else
        {
            _settings.Left = Left;
            _settings.Top = Top;
        }

        _settings.Save();
    }
}
