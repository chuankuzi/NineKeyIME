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

    /// <summary>批 2026-10-05：另一实例发来的"唤醒我"消息 id（同会话内 RegisterWindowMessage 同 id）。</summary>
    private static readonly int ShowExistingMessage =
        unchecked((int)NativeMethods.RegisterWindowMessageW(SingleInstanceGuard.ShowExistingMessageName));
    private readonly DispatcherTimer _selectionTracker = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private nint _lastExternalForeground;
    private DateTime _elevatedNoticeAt = DateTime.MinValue;
    private MacroEditorWindow? _macroEditor;

    public KeyboardWindow(QueryEngine engine, UserDictionary userDict, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(userDict);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // §13.45：句柄未生（FromHandle 用不了）时按 WPF 原生 DIP 值初算屏幕适配下限；Loaded 后按实际所在屏重算。
        // ⚠ 坑：此处**必须**拿 DIP（GetPrimaryWorkAreaDip），拿物理像素会把下限算大 dpiScale 倍。
        var primaryWork = GetPrimaryWorkAreaDip();
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

        _currentScale = GetScaleFor(LayoutMode.Chinese);
        var (designW, designH) = GetDesignSize(LayoutMode.Chinese);
        Width = designW * _currentScale;
        Height = designH * _currentScale;
        RootBorder.Width = designW;
        RootBorder.Height = designH;
        RootBorder.RenderTransform = new ScaleTransform(_currentScale, _currentScale);

        // §13.45：位置优先上次保存；保存位置在吸附区或无记录时，默认居中偏下。
        // ⚠ 坑：本块必须在**应用缩放之后**——原来用 XAML 的 480×337 算 Top，与随后真实尺寸不一致；
        // 小屏上（Steam Deck 1280×800）首启就会把底边压出屏，与 DPI 单位错误叠加即"底右双边缘切割"。
        var work = GetPrimaryWorkAreaDip();
        if (_settings.Left is double left && _settings.Top is double top
            && left >= EdgeSnapDistance
            && top >= EdgeSnapDistance
            && work.Width - (left + Width) >= EdgeSnapDistance
            && work.Height - (top + Height) >= EdgeSnapDistance)
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = Math.Max(0, (work.Width - Width) / 2);
            Top = Math.Max(EdgeSnapDistance, work.Height - Height - 40);
        }

        LogGeometry("ctor-first");   // TEMP-DIAG

        // ⚠ 坑：合成输入/极端情况下 MouseUp 丢失会导致鼠标捕获永久卡在某个控件上，
        // 之后所有点击都被路由到该控件（键盘表现为"完全无法输入"）。
        // 在窗口级 PreviewMouseDown 强制释放残留捕获：此刻新按下尚未开始拖拽，释放是安全的。
        // 批 fix/topmost-reassert：同一处理器里顺手重断言置顶（任意键按下即回到置顶组最上，不抢焦点）。
        Mouse.AddPreviewMouseDownHandler(this, (_, e) =>
        {
            var captured = Mouse.Captured;
            if (captured is not null && !ReferenceEquals(captured, e.OriginalSource as DependencyObject))
            {
                captured.ReleaseMouseCapture();
            }

            ReassertTopmost("mouse-down");

            // 批 2026-10-05：1 键选框改 StaysOpen=true（不再抢捕获），"点选框外即关"由这里接管。
            CloseKey1PopupOnOutsideInput(e.OriginalSource);
        });

        // ⚠ 触摸必须单独接：WPF 触屏提升为鼠标事件有延迟，选框的"点外即关"要在触摸按下当刻生效。
        // （Touch 没有 AddPreviewTouchDownHandler 静态助手，走 UIElement.PreviewTouchDownEvent 隧道事件。）
        AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<System.Windows.Input.TouchEventArgs>(
                (_, e) => CloseKey1PopupOnOutsideInput(e.OriginalSource)),
            handledEventsToo: true);

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

        // 0 键 = 空格键：组串中有可用候选时上屏首选，其余情况发 VK_SPACE（分支与开关见 HandleSpaceKey）
        Key0.DigitPressed += _ => HandleSpaceKey();

        BuildEnglish26Panel();
        BuildSymbolPanel();
        BuildMacroPanel();
        BuildKey1SymbolPopup();
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

        // 批 2026-10-05：拼音组合引导项从候选行剥离 → 键盘上方浮条（先装配，再订阅刷新）。
        BuildPinyinBar();
        _controller.StateChanged += RefreshPinyinBar;
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
        _tray.SpaceCommitToggled += on => SetSpaceCommitsCandidate(on);
        _tray.KeyClickToggled += on => SetKeyClickSound(on);
        _tray.SentenceMemoryToggled += on => SetSentenceMemoryEnabled(on);
        _tray.ClearSentenceMemoryRequested += ClearSentenceMemory;
        InitSentenceMemory();
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
            _settings.ShowEditRow, _settings.ShowSymbolRow, _settings.SpaceCommitsCandidate, _settings.KeyClickSound, _settings.SentenceMemoryEnabled,
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
            ClampWindowToWorkArea();   // 切模式后重新夹紧：宽了往左挪，不超出屏幕
            LogGeometry("loaded-after-recalc");   // TEMP-DIAG

            // 运行期分辨率/方向变化（Deck 手动转横向即走这条）：WM_DISPLAYCHANGE 后必须重算 + 夹回屏内，
            // 否则窗口仍按旧方向尺寸摆放 → 底边/右边被切。退订在 OnClosed。
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // §13.45-5：启动必须展开为完整键盘，禁止出生即成细条
            if (IsDockedAsStrip)
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

            // 批 fix/topmost-reassert：同一个 500ms 心跳兼做置顶兜底（复用既有计时器，不新起线程/计时器）。
            TopmostPollTick();
        };
        _fgTracker.Start();
        _selectionTracker.Tick += (_, _) => UpdateCopyButtonState();
        _selectionTracker.Start();
        Activated += (_, _) =>
        {
            FileLogger.Info($"activated: lastFg={_lastExternalForeground} visible={IsVisible} docked={IsDockedAsStrip}");
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

        // 批 fix/topmost-reassert：XAML 的 Topmost=True 是在窗口创建时生效的，而上面刚改过 exstyle；
        // 样式落定后显式再断言一次置顶（Deck 上"键盘被 LM Studio 压住"的可疑点之一就是这一拍丢位置）。
        ReassertTopmost("source-init");

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
        // 批 2026-10-05：另一个实例启动时要求我们现身（第二次双击 exe 不该"什么都没发生"）。
        // 只显示 + 重断言置顶，不抢焦点（W5）。
        if (ShowExistingMessage != 0 && msg == ShowExistingMessage)
        {
            FileLogger.Info("single-instance: show requested by another launch → show + reassert topmost");
            Dispatcher.BeginInvoke(() =>
            {
                ApplyVis(_vis.Show());
                ReassertTopmost("show-existing");
            });
            handled = true;
            return nint.Zero;
        }

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
        PlayKeyClick();
        // 九键 1 键：点按弹符号选框（借鉴百度九宫格"点 1 出更多符号"），不再直出 "1"；数字输入走 123 面板。
        if (info.CommitDirect && info.Value == "1" && _layoutMode == LayoutMode.Chinese)
        {
            ShowKey1SymbolPopup();   // 幂等呼出（原 Toggle：同键再按即关，实感"这次没呼出"）
            return;
        }

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

    /// <summary>
    /// 空格键统一入口（数字/英文模式无缓冲，天然退回发空格）：
    /// 缓冲非空 + 开关打开 + 有可用候选 → 上屏首选候选；否则只发 VK_SPACE（缓冲保留，不吞字）。
    /// </summary>
    private void HandleSpaceKey()
    {
        PlayKeyClick();
        // 有候选才上屏首选；无候选只发空格，缓冲保留（原样上屏字母串是回车键的职责）
        if (!_controller.IsEmpty && _settings.SpaceCommitsCandidate && _controller.HasRealCandidate)
        {
            PushCompositionToScreen(); // 复用 1 键批已验证的顶屏路径（内部走 CommitEnter 全量过滤，禁止裸取 CurrentPage）
            return;
        }

        _controller.CommitSpace();
    }

    private void UpdateShortcutPanelVisibility()
    {
        // 批次 11：26 键英文模式隐藏快捷键行（粘性 Ctrl/Alt 已覆盖组合快捷键）
        var show = _settings.ShowShortcutBar && _layoutMode == LayoutMode.Chinese;
        ShortcutPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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
            PlayKeyClick();
            _controller.CommitDirect(tag);
        }
    }

    private void SpaceButton_Click(object sender, RoutedEventArgs e) => HandleSpaceKey();

    private void RefreshUi()
    {
        InputLabel.Items.Clear();
        if (_controller.IsEmpty)
        {
            InputLabel.Items.Add(new TextBlock
            {
                Text = "点数字键开始输入，按住拖动选字母",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10,
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
            // §letter-pin：组合切换走"键盘上方浮条"的引导项（PinyinGuide），字母片点击仅激活音节重锚定。
            _controller.SetActiveSyllableIndex(syllableIndex);
            if (ReferenceEquals(Mouse.Captured, btn))
            {
                btn.ReleaseMouseCapture();
            }
        };

        return btn;
    }

    /// <summary>显隐动作单一执行点（§13.24：显式 Show / Hide）。</summary>
    private void ApplyVis(VisAction action)
    {
        FileLogger.Info($"apply-vis: action={action} visible={IsVisible} docked={IsDockedAsStrip}");
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
        FileLogger.Info($"toggle-visibility: visible={IsVisible} docked={IsDockedAsStrip}");
        if (IsDockedAsStrip)
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
            else if (IsDockedAsStrip)
            {
                ExpandFromStrip();
            }

            _tray.FlashTray();
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

    private void BackspaceButton_Click(object sender, RoutedEventArgs e)
    {
        PlayKeyClick();
        _controller.Backspace();
    }

    private void EnterButton_Click(object sender, RoutedEventArgs e)
    {
        PlayKeyClick();
        _controller.CommitLettersOrEnter();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        // 批11 触发②：清键 = 用户主动清空 → 结算成句。⚠ 与提交后的内部清缓冲严格区分，
        // 否则逐词连打每提交一个词就结算一次，永远攒不成句（验收①的命门）。
        _controller.SettleRunBuffer();
        _controller.ClearInput();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => ApplyVis(_vis.Hide());

    private void FlashButton_Click(object sender, RoutedEventArgs e) => Flash();

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

    private void SetSpaceCommitsCandidate(bool on)
    {
        _settings.SpaceCommitsCandidate = on;
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
        // ⚠ 坑：SystemEvents 是静态事件，不退订会把窗口（及其 Dispatcher）钉在内存里。
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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

        if (IsDockedAsStrip)
        {
            _settings.Left = RestoredBounds.Left;
            _settings.Top = RestoredBounds.Top;
        }
        else
        {
            _settings.Left = Left;
            _settings.Top = Top;
        }

        _settings.Save();
    }
}
