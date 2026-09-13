// 本文件职责：系统托盘图标与右键设置菜单，聚合所有运行时开关入口。
// 数据流位置：用户点击托盘菜单 → 触发事件 → KeyboardWindow/Host 消费并回写状态。
// ⚠ 坑 1：菜单打开/关闭会钉住/释放键盘自动隐藏，必须在同步状态前触发 MenuOpened（§13.15）。
// ⚠ 坑 2：SyncState 通过 _syncing 标志抑制 CheckedChanged/ValueChanged 回环事件，否则宿主与托盘互相触发。
// 相关规格：§2.7、§13.15、§13.28、§13.33、P2-14/R9。

using System.Drawing;
using System.Windows.Forms;

namespace NineKey.Keyboard.Services;

/// <summary>
/// 托盘设置中心（P2-14/R9）：左键 = 显示/隐藏键盘，双击 = 闪现找回；
/// 右键菜单按功能分组为输入设置 / 外观 / 宏管理 / 其他。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _autoPopupItem;
    private readonly ToolStripMenuItem _hotkeyItem;
    private readonly ToolStripMenuItem _touchKbItem;
    private readonly ToolStripMenuItem _traditionalItem;
    private readonly ToolStripMenuItem _shortcutBarItem;
    private readonly ToolStripMenuItem _editRowItem;
    private readonly ToolStripMenuItem _symbolRowItem;

    private readonly ToolStripMenuItem _fuzzyZhiZuItem;
    private readonly ToolStripMenuItem _fuzzyChiCuItem;
    private readonly ToolStripMenuItem _fuzzyShiSuItem;
    private readonly ToolStripMenuItem _fuzzyNiLiItem;
    private readonly ToolStripMenuItem _fuzzyRiLiItem;
    private readonly ToolStripMenuItem _fuzzyFuHuItem;
    private readonly ToolStripMenuItem _fuzzyAnAngItem;
    private readonly ToolStripMenuItem _fuzzyEnEngItem;
    private readonly ToolStripMenuItem _fuzzyInIngItem;
    private readonly ToolStripMenuItem _macroBarItem;

    private readonly TrackBar _bgTrack;//透明度
    private readonly TrackBar _keyTrack;//透明度
    //private readonly TrackBar _bubbleDelayTrack;
    //private readonly TrackBar _bubbleShowTrack;
    // ⚠ 坑：_syncing 为 true 时禁止触发事件，避免 SyncState 回写菜单状态时又反向通知宿主。
    private bool _syncing;

    public event Action? ToggleRequested;
    public event Action? FlashRequested;
    public event Action? ResetOpacityRequested;
    public event Action? ExitRequested;
    public event Action<double>? BackgroundOpacityChanged;
    public event Action<double>? KeyOpacityChanged;
    public event Action<double>? SizeScaleRequested;
    public event Action<bool>? AutoPopupToggled;
    public event Action<bool>? TraditionalToggled;
    public event Action<bool>? HotkeyToggled;
    public event Action<bool>? StartupToggled;
    public event Action<bool>? TouchKbGuardToggled;
    public event Action<bool>? ShortcutBarToggled;
    public event Action<bool>? EditRowToggled;
    public event Action<bool>? SymbolRowToggled;

    //public event Action<int>? BubbleDelayChanged;//气泡事件申明
    //public event Action<int>? BubbleShowChanged;//气泡事件申明

    public event Action<bool>? TouchCorrectionToggled;
    public event Action<double>? TouchCorrectionSigmaChanged;

    public event Action<bool>? FuzzyZhiZuToggled;
    public event Action<bool>? FuzzyChiCuToggled;
    public event Action<bool>? FuzzyShiSuToggled;
    public event Action<bool>? FuzzyNiLiToggled;
    public event Action<bool>? FuzzyRiLiToggled;
    public event Action<bool>? FuzzyFuHuToggled;
    public event Action<bool>? FuzzyAnAngToggled;
    public event Action<bool>? FuzzyEnEngToggled;
    public event Action<bool>? FuzzyInIngToggled;

    public event Action? AdminRunRequested;
    public event Action? EditMacrosRequested;
    public event Action? EditEditorWhitelistRequested;
    public event Action<bool>? MacroBarToggled;
    public event Action<string?>? BackgroundColorChanged;
    public event Action<string?>? KeyBackgroundColorChanged;
    public event Action<double>? KeyBorderWidthChanged;

    /// <summary>菜单打开时触发：宿主同步勾选/滑块状态（经 SyncState 回写）。</summary>
    public event Action? SyncRequested;

    /// <summary>§13.15：菜单打开期间键盘钉住不隐藏。</summary>
    public event Action? MenuOpened;

    /// <summary>菜单关闭，恢复自动隐藏逻辑。</summary>
    public event Action? MenuClosed;

    /// <summary>创建托盘图标并构建右键菜单结构。</summary>
    public TrayService()
    {
        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "NineKey 九宫格输入法",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleRequested?.Invoke();
            }
        };
        _icon.DoubleClick += (_, _) => FlashRequested?.Invoke();

        var menu = new ContextMenuStrip();
        _ = menu.Items.Add("显示 / 隐藏", null, (_, _) => ToggleRequested?.Invoke());
        _ = menu.Items.Add("闪现键盘 1.5 秒", null, (_, _) => FlashRequested?.Invoke());
        _ = menu.Items.Add(new ToolStripSeparator());

        // ---- 输入设置 ----
        var inputMenu = new ToolStripMenuItem("输入设置");
        _fuzzyZhiZuItem = AddToggle(inputMenu.DropDown, "模糊音 zhi↔zi", on => FuzzyZhiZuToggled?.Invoke(on));
        _fuzzyChiCuItem = AddToggle(inputMenu.DropDown, "模糊音 chi↔ci", on => FuzzyChiCuToggled?.Invoke(on));
        _fuzzyShiSuItem = AddToggle(inputMenu.DropDown, "模糊音 shi↔si", on => FuzzyShiSuToggled?.Invoke(on));
        _fuzzyNiLiItem = AddToggle(inputMenu.DropDown, "模糊音 ni↔li", on => FuzzyNiLiToggled?.Invoke(on));
        _fuzzyRiLiItem = AddToggle(inputMenu.DropDown, "模糊音 ri↔li", on => FuzzyRiLiToggled?.Invoke(on));
        _fuzzyFuHuItem = AddToggle(inputMenu.DropDown, "模糊音 fu↔hu", on => FuzzyFuHuToggled?.Invoke(on));
        _fuzzyAnAngItem = AddToggle(inputMenu.DropDown, "模糊音 an↔ang", on => FuzzyAnAngToggled?.Invoke(on));
        _fuzzyEnEngItem = AddToggle(inputMenu.DropDown, "模糊音 en↔eng", on => FuzzyEnEngToggled?.Invoke(on));
        _fuzzyInIngItem = AddToggle(inputMenu.DropDown, "模糊音 in↔ing", on => FuzzyInIngToggled?.Invoke(on));
        _ = inputMenu.DropDownItems.Add(new ToolStripSeparator());
        _shortcutBarItem = AddToggle(inputMenu.DropDown, "快捷键行", on => ShortcutBarToggled?.Invoke(on));
        _editRowItem = AddToggle(inputMenu.DropDown, "26 键编辑行", on => EditRowToggled?.Invoke(on));
        _symbolRowItem = AddToggle(inputMenu.DropDown, "26 键符号行", on => SymbolRowToggled?.Invoke(on));
		
        /* ---- 气泡时序（§13.33）----
        var bubbleMenu = new ToolStripMenuItem("气泡时序");
        _bubbleDelayTrack = MakeTrack(50, 400, 10);
        _bubbleDelayTrack.ValueChanged += (_, _) =>
        {
            if (!_syncing)
            {
                BubbleDelayChanged?.Invoke(_bubbleDelayTrack.Value);
            }
        };
        _bubbleShowTrack = MakeTrack(50, 400, 10);
        _bubbleShowTrack.ValueChanged += (_, _) =>
        {
            if (!_syncing)
            {
                BubbleShowChanged?.Invoke(_bubbleShowTrack.Value);
            }
        };
        _ = bubbleMenu.DropDownItems.Add(new ToolStripMenuItem("延迟 T1 (ms)") { Enabled = false });
        _ = bubbleMenu.DropDownItems.Add(new ToolStripControlHost(_bubbleDelayTrack));
        _ = bubbleMenu.DropDownItems.Add(new ToolStripMenuItem("显示 T2 (ms)") { Enabled = false });
        _ = bubbleMenu.DropDownItems.Add(new ToolStripControlHost(_bubbleShowTrack));
        _ = menu.Items.Add(bubbleMenu);
		*/

        // ---- 误触纠正（§M8-1）----
        var touchMenu = new ToolStripMenuItem("误触纠正");
        _ = touchMenu.DropDownItems.Add("关", null, (_, _) => TouchCorrectionToggled?.Invoke(false));
        _ = touchMenu.DropDownItems.Add("低灵敏度 (σ=6)", null, (_, _) =>
        {
            TouchCorrectionToggled?.Invoke(true);
            TouchCorrectionSigmaChanged?.Invoke(6);
        });
        _ = touchMenu.DropDownItems.Add("中灵敏度 (σ=10)", null, (_, _) =>
        {
            TouchCorrectionToggled?.Invoke(true);
            TouchCorrectionSigmaChanged?.Invoke(10);
        });
        _ = touchMenu.DropDownItems.Add("高灵敏度 (σ=16)", null, (_, _) =>
        {
            TouchCorrectionToggled?.Invoke(true);
            TouchCorrectionSigmaChanged?.Invoke(16);
        });
        _ = menu.Items.Add(touchMenu);

        _ = menu.Items.Add(inputMenu);

        // ---- 外观 ----
        var lookMenu = new ToolStripMenuItem("外观");
        var sizeMenu = new ToolStripMenuItem("尺寸");
        _ = sizeMenu.DropDownItems.Add("默认（100%）", null, (_, _) => SizeScaleRequested?.Invoke(1.0));
        _ = sizeMenu.DropDownItems.Add("大（150%）", null, (_, _) => SizeScaleRequested?.Invoke(1.5));
        _ = sizeMenu.DropDownItems.Add("特大（200%）", null, (_, _) => SizeScaleRequested?.Invoke(2.0));
        _ = lookMenu.DropDownItems.Add(sizeMenu);

        var bgMenu = new ToolStripMenuItem("键盘背景色");
        foreach (var (label, hex) in new (string, string?)[] { ("跟随系统", null), ("深灰", "#2A2A2E"), ("深蓝", "#1F3A5F"), ("墨绿", "#204020"), ("暗紫", "#3A2A4A"), ("米白", "#F3F3F0") })
        {
            _ = bgMenu.DropDownItems.Add(label, null, (_, _) => BackgroundColorChanged?.Invoke(hex));
        }
        _ = lookMenu.DropDownItems.Add(bgMenu);

        var keyBgMenu = new ToolStripMenuItem("按键背景色");
        foreach (var (label, hex) in new (string, string?)[] { ("跟随主题", null), ("深灰", "#3A3A3D"), ("深蓝", "#2A4A6F"), ("墨绿", "#2A4A2A"), ("暗紫", "#4A3A5A"), ("米白", "#FFFFFF") })
        {
            _ = keyBgMenu.DropDownItems.Add(label, null, (_, _) => KeyBackgroundColorChanged?.Invoke(hex));
        }
        _ = lookMenu.DropDownItems.Add(keyBgMenu);

        var borderMenu = new ToolStripMenuItem("按键边框");
        foreach (var w in new[] { 0, 1, 2, 3, 4 })
        {
            _ = borderMenu.DropDownItems.Add($"{w} px", null, (_, _) => KeyBorderWidthChanged?.Invoke(w));
        }
        _ = lookMenu.DropDownItems.Add(borderMenu);

        _bgTrack = MakeTrack();
        _bgTrack.ValueChanged += (_, _) =>
        {
            if (!_syncing)
            {
                BackgroundOpacityChanged?.Invoke(_bgTrack.Value / 100.0);
            }
        };
        _keyTrack = MakeTrack();
        _keyTrack.ValueChanged += (_, _) =>
        {
            if (!_syncing)
            {
                KeyOpacityChanged?.Invoke(_keyTrack.Value / 100.0);
            }
        };
        _ = lookMenu.DropDownItems.Add(new ToolStripMenuItem("背景透明度") { Enabled = false });
        _ = lookMenu.DropDownItems.Add(new ToolStripControlHost(_bgTrack));
        _ = lookMenu.DropDownItems.Add(new ToolStripMenuItem("按键透明度") { Enabled = false });
        _ = lookMenu.DropDownItems.Add(new ToolStripControlHost(_keyTrack));
        _ = lookMenu.DropDownItems.Add("透明度重置（85%/95%）", null, (_, _) => ResetOpacityRequested?.Invoke());
        _ = menu.Items.Add(lookMenu);

        // ---- 宏管理（§13.28）----
        var macroMenu = new ToolStripMenuItem("宏管理");
        _ = macroMenu.DropDownItems.Add("编辑宏...", null, (_, _) => EditMacrosRequested?.Invoke());
        _macroBarItem = AddToggle(macroMenu.DropDown, "显示宏键行", on => MacroBarToggled?.Invoke(on));
        _ = menu.Items.Add(macroMenu);

        // ---- 其他 ----
        var miscMenu = new ToolStripMenuItem("其他");
        _autoPopupItem = AddToggle(miscMenu.DropDown, "自动弹出", on => AutoPopupToggled?.Invoke(on));
        _traditionalItem = AddToggle(miscMenu.DropDown, "简繁输出", on => TraditionalToggled?.Invoke(on));
        _hotkeyItem = AddToggle(miscMenu.DropDown, "热键 Ctrl+Alt+K", on => HotkeyToggled?.Invoke(on));
        _startupItem = AddToggle(miscMenu.DropDown, "开机自启", on => StartupToggled?.Invoke(on));
        _touchKbItem = AddToggle(miscMenu.DropDown, "屏蔽系统触摸键盘", on => TouchKbGuardToggled?.Invoke(on));
        _ = miscMenu.DropDownItems.Add("编辑器白名单...", null, (_, _) => EditEditorWhitelistRequested?.Invoke());
        _ = miscMenu.DropDownItems.Add("以管理员运行（UAC 重启）", null, (_, _) => AdminRunRequested?.Invoke());
        _ = menu.Items.Add(miscMenu);

        _ = menu.Items.Add(new ToolStripSeparator());
        _ = menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        menu.Opening += (_, _) =>
        {
            MenuOpened?.Invoke();
            _syncing = true;
            SyncRequested?.Invoke();
            _syncing = false;
        };
        menu.Closed += (_, _) => MenuClosed?.Invoke();
        _icon.ContextMenuStrip = menu;
    }

    private static TrackBar MakeTrack() =>
        new()
        {
            Minimum = 20,
            Maximum = 100,
            TickFrequency = 5,
            SmallChange = 5,
            LargeChange = 10,
            Width = 140,
            AutoSize = false,
            Height = 28,
        };

    /*private static TrackBar MakeTrack(int min, int max, int tickFrequency) =>
        new()
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = tickFrequency,
            SmallChange = tickFrequency,
            LargeChange = tickFrequency * 2,
            Width = 140,
            AutoSize = false,
            Height = 28,
        };
	*/

    /// <summary>§13.31 回写"简繁输出"勾选状态（独立方法，避免改动 SyncState 长参数签名）。</summary>
    public void SyncTraditional(bool on)
    {
        _syncing = true;
        _traditionalItem.Checked = on;
        _syncing = false;
    }

    /// <summary>§13.32 托盘图标：加载嵌入资源 app.ico（与 exe 图标同源），缺失或损坏时回退系统默认。</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream);
            }
        }
        catch
        {
            // 嵌入资源缺失或损坏时回退系统图标。
        }

        return SystemIcons.Application;
    }

    private ToolStripMenuItem AddToggle(ToolStrip parent, string text, Action<bool> onToggle)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = true };
        item.CheckedChanged += (_, _) =>
        {
            if (!_syncing)
            {
                onToggle(item.Checked);
            }
        };
        _ = parent.Items.Add(item);
        return item;
    }

    /// <summary>宿主同步设置状态（菜单打开时调用，不回环触发事件）。</summary>
    public void SyncState(
        bool autoPopup, bool hotkey, bool startup, bool touchKbGuard, bool shortcutBar, bool macroBar,
        bool editRow, bool symbolRow,
        bool fuzzyZhiZu, bool fuzzyChiCu, bool fuzzyShiSu, bool fuzzyNiLi, bool fuzzyRiLi,
        bool fuzzyFuHu, bool fuzzyAnAng, bool fuzzyEnEng, bool fuzzyInIng,
        double bgOpacity, double keyOpacity)
        //int bubbleDelayMs, int bubbleShowMs)
    {
        _syncing = true;
        _autoPopupItem.Checked = autoPopup;
        _hotkeyItem.Checked = hotkey;
        _startupItem.Checked = startup;
        _touchKbItem.Checked = touchKbGuard;
        _shortcutBarItem.Checked = shortcutBar;
        _macroBarItem.Checked = macroBar;
        _editRowItem.Checked = editRow;
        _symbolRowItem.Checked = symbolRow;

        _fuzzyZhiZuItem.Checked = fuzzyZhiZu;
        _fuzzyChiCuItem.Checked = fuzzyChiCu;
        _fuzzyShiSuItem.Checked = fuzzyShiSu;
        _fuzzyNiLiItem.Checked = fuzzyNiLi;
        _fuzzyRiLiItem.Checked = fuzzyRiLi;
        _fuzzyFuHuItem.Checked = fuzzyFuHu;
        _fuzzyAnAngItem.Checked = fuzzyAnAng;
        _fuzzyEnEngItem.Checked = fuzzyEnEng;
        _fuzzyInIngItem.Checked = fuzzyInIng;

        _bgTrack.Value = Math.Clamp((int)(bgOpacity * 100), _bgTrack.Minimum, _bgTrack.Maximum);
        _keyTrack.Value = Math.Clamp((int)(keyOpacity * 100), _keyTrack.Minimum, _keyTrack.Maximum);
        //_bubbleDelayTrack.Value = Math.Clamp(bubbleDelayMs, _bubbleDelayTrack.Minimum, _bubbleDelayTrack.Maximum);
        //_bubbleShowTrack.Value = Math.Clamp(bubbleShowMs, _bubbleShowTrack.Minimum, _bubbleShowTrack.Maximum);
        _syncing = false;
    }

    /// <summary>闪现键盘后给出托盘气泡提示。</summary>
    public void FlashTray()
    {
        _icon.ShowBalloonTip(1500, "NineKey", "键盘已闪现，可拖标题条移动", ToolTipIcon.Info);
    }

    /// <summary>§0.3 指引等提示气球。</summary>
    public void ShowBalloon(string title, string message)
    {
        _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.Warning);
    }

    /// <summary>隐藏并释放托盘图标。</summary>
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }
}
