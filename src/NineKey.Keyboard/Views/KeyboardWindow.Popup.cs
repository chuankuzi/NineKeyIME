// 本文件职责：KeyboardWindow 的 1 键符号选框与符号面板。
// 数据流位置：1 键点按 / 符号页切页 → 选框与符号面板 → CommitDirect（组串非空时先顶屏首选候选）。
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

/// <summary>KeyboardWindow 弹层部分：1 键符号选框与符号面板。</summary>
public partial class KeyboardWindow
{

    // ---- 九键 1 键符号选框（借鉴百度九宫格"点 1 出更多符号"，点按弹窗形态）----

    /// <summary>选框符号集：沿用原 1 键四方向槽（，。！？）并补全 ；：，排布 3 列 × 2 行。</summary>
    private static readonly string[] Key1PopupSymbols = ["，", "。", "！", "？", "；", "："];

    private Popup? _key1Popup;
    private System.Windows.Controls.Border? _key1PopupBorder;

    /// <summary>已挂上 MA_NOACTIVATE 钩子的弹层 HWND（HWND 若被重建必须重挂）。</summary>
    private nint _key1PopupHookedHwnd;

    /// <summary>最近使用符号的存储上限（超出淘汰最旧）。</summary>
    private const int RecentSymbolsMax = 8;

    /// <summary>选框"最近"行的显示槽位数（与固定格同为 3 列）。</summary>
    private const int RecentRowSlots = 3;

    private readonly Button[] _recentSymbolButtons = new Button[RecentRowSlots];
    private System.Windows.Controls.StackPanel? _recentSymbolRow;

    /// <summary>构建 1 键符号选框：首行"最近"（空则整行隐藏）+ 固定 3×2 格，贴 Key1 上沿；贴屏幕上缘时由 Popup 自行翻转到下方。</summary>
    private void BuildKey1SymbolPopup()
    {
        var grid = new UniformGrid { Columns = 3, Rows = 2 };
        foreach (var symbol in Key1PopupSymbols)
        {
            var btn = MakePanelButton(symbol);
            btn.Width = 48;
            btn.Height = 40;
            btn.FontSize = 20;
            btn.Margin = new System.Windows.Thickness(2);
            var picked = symbol;
            btn.Click += (_, _) => OnKey1SymbolClick(picked);
            _panelButtons.Add(btn); // 并入统一涂刷清单：浅色主题下符号不会白底白字
            grid.Children.Add(btn);
        }

        // 最近行：三个定长槽位（与固定格同尺寸同风格），按 MRU 顺序填充，列表空则整行隐藏。
        _recentSymbolRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        for (var slot = 0; slot < RecentRowSlots; slot++)
        {
            var btn = MakePanelButton(string.Empty);
            btn.Width = 48;
            btn.Height = 40;
            btn.FontSize = 20;
            btn.Margin = new System.Windows.Thickness(2);
            var index = slot;
            btn.Click += (_, _) => OnRecentSymbolClick(index);
            _panelButtons.Add(btn);
            _recentSymbolButtons[slot] = btn;
            _ = _recentSymbolRow.Children.Add(btn);
        }

        var stack = new System.Windows.Controls.StackPanel();
        _ = stack.Children.Add(_recentSymbolRow);
        _ = stack.Children.Add(grid);

        _key1PopupBorder = new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(4),
            Child = stack,
        };

        _key1Popup = new Popup
        {
            PlacementTarget = Key1,
            Placement = PlacementMode.Top,
            // ⚠ 坑（2026-10-05 修，Deck 实测「1 键仍未修复」的元凶）：原来是 StaysOpen=false，
            // 而 StaysOpen=false 的 Popup 一旦打开就**持有鼠标/触摸捕获**——选项框外的那一次点按会被它吃掉
            // （只把弹窗关掉，ShowKey1SymbolPopup 根本没被调用），表现就像"这次没呼出"、外观仍像 Toggle。
            // 触摸设备尤其明显（合成鼠标点击测不出 ⇒ 本机 10/10、Deck 仍坏）。
            // 改为 StaysOpen=true：不抢捕获，点按必定送达按键；「点选框外即关」由本类自己实现
            // （CloseKey1PopupOnOutsideInput ← 窗口级 PreviewMouseDown/TouchDown）。
            StaysOpen = true,
            AllowsTransparency = true,
            Focusable = false, // W5：新增控件一律不入焦点链
            Child = _key1PopupBorder,
        };

        _key1Popup.Opened += (_, _) =>
        {
            // 弹层是独立 HWND：① 必须先变成"不抢焦点"，② 再断言置顶，③ 最后记下真实矩形与样式位。
            MakePopupNonActivating(_key1Popup, "key1-popup", ref _key1PopupHookedHwnd);
            AssertOwnedPopupTopmost(_key1Popup, "key1-popup");
            LogKey1PopupRect("open");
        };

        // 窗口收起（托盘/贴条）时弹层不会自动跟走，必须显式收掉。
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                CloseKey1SymbolPopup("window-hidden");
            }
        };

        // 挂进根 Grid 只为进入逻辑树；Popup 自身不占布局。
        if (Content is System.Windows.Controls.Grid root)
        {
            root.Children.Add(_key1Popup);
        }

        // 启动即自证：弹窗对象确实建成了（否则 ShowKey1SymbolPopup 会在第一行静默 return，日志里什么都看不到）。
        FileLogger.Info($"key1-popup[built] symbols={Key1PopupSymbols.Length} staysOpen={_key1Popup.StaysOpen}");
    }

    /// <summary>
    /// 呼出 1 键符号选框（**幂等**：按一次一定出现/保持）。
    /// ⚠ 坑（2026-10-04 修）：原实现是 `IsOpen = !IsOpen`（Toggle）——同一颗键连按两次就"开了又关"，
    /// 用户实感是"这次没呼出标点"。本机实测连点 10 次只有 5 次可见弹窗，且完全交替（开→关→开→关…）。
    /// 关闭仍走既有路径：点选框外即关（StaysOpen=false）/ 选中符号 / 切模式 / 贴边收起。
    /// </summary>
    private void ShowKey1SymbolPopup()
    {
        if (_key1Popup is null)
        {
            return;
        }

        // ⚠ 探针（2026-10-05）：这一行是"1 键的那次点按有没有送达"的唯一判据——
        // Deck 日志里若"点了 1 但完全没有 key1-popup[show-called]"，就是点按在送达按键前被丢/被吃。
        FileLogger.Info($"key1-popup[show-called] alreadyOpen={_key1Popup.IsOpen}");
        _key1Popup.IsOpen = true;
        RefreshRecentSymbolRow();
    }

    private void CloseKey1SymbolPopup(string reason = "explicit")
    {
        if (_key1Popup is not null && _key1Popup.IsOpen)
        {
            _key1Popup.IsOpen = false;
            FileLogger.Info($"key1-popup[close] reason={reason}");
        }
    }

    /// <summary>
    /// 点选框外（窗口内非 Key1 的输入）即收掉选框。
    /// ⚠ StaysOpen=true 之后 Popup 不再自己处理"外点即关"，这条必须自己管，否则选框会赖着不走。
    /// </summary>
    private void CloseKey1PopupOnOutsideInput(object? source)
    {
        if (_key1Popup is null || !_key1Popup.IsOpen)
        {
            return;
        }

        var node = source as DependencyObject;

        // ⚠ 坑（2026-10-05，我自己引入过的回归）：Popup 的视觉树挂在**宿主窗口树**上（_key1Popup 就加在根 Grid 里），
        // 所以弹层内部的按下也会冒泡到窗口级预览处理器。若无脑按"不是 Key1 就关"，弹层内点击会被误判为"点外"，
        // 先被关掉 ⇒ 符号按钮的 Click 再也发不出来 ⇒ 实感"符号上不了屏"。故"弹层内"必须显式排除。
        if (IsWithinPopup(_key1Popup, node) || IsWithinKey1(node))
        {
            return;
        }

        CloseKey1SymbolPopup("outside-input");
    }

    /// <summary>source 是否位于该弹层之内（沿树向上找弹层的根内容元素）。</summary>
    private static bool IsWithinPopup(Popup? popup, DependencyObject? source)
    {
        if (popup?.Child is not DependencyObject root)
        {
            return false;
        }

        var node = source;
        while (node is not null)
        {
            if (ReferenceEquals(node, root))
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>source 是否位于 Key1 之内（命中元素常是 Key1 的子元素，必须沿树向上找）。</summary>
    private bool IsWithinKey1(DependencyObject? source)
    {
        var node = source;
        while (node is not null)
        {
            if (ReferenceEquals(node, Key1))
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// 弹层独立 HWND 的置顶断言（1 键选框与拼音浮条共用）：只动 z 序，
    /// 一律 SWP_NOACTIVATE —— 重断言全程不抢焦点（W5）。
    /// </summary>
    private static void AssertOwnedPopupTopmost(Popup? popup, string tag)    {
        try
        {
            if (popup?.Child is not FrameworkElement child)
            {
                return;
            }

            var hwnd = (PresentationSource.FromVisual(child) as HwndSource)?.Handle ?? 0;
            if (hwnd == 0)
            {
                return;
            }

            _ = NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"popup[{tag}]: assert topmost failed", ex);
        }
    }

    /// <summary>
    /// 记下选框独立 HWND 的真实矩形与置顶位。
    /// ⚠ Deck 上"呼出了却看不见/位置跑到屏外"这类只能靠它自证——有 rect 与 topmost 就能一眼定性。
    /// </summary>
    private void LogKey1PopupRect(string phase)
    {
        try
        {
            if (_key1Popup?.Child is not FrameworkElement child)
            {
                return;
            }

            var hwnd = (PresentationSource.FromVisual(child) as HwndSource)?.Handle ?? 0;
            if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            var topmost = (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64()
                & NativeMethods.WsExTopmost.ToInt64()) != 0;
            var noActivate = (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64()
                & NativeMethods.WsExNoActivate.ToInt64()) != 0;
            FileLogger.Info($"key1-popup[{phase}] rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) " +
                $"topmost={topmost} noactivate={noActivate}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("key1-popup: rect log failed", ex);
        }
    }

    private void OnKey1SymbolClick(string symbol)
    {
        PlayKeyClick();
        LogForegroundBeforeInject(symbol);
        // 标点顶屏（手机输入法规则）：组串非空先把首选候选落屏，再上屏符号。
        if (!_controller.IsEmpty)
        {
            PushCompositionToScreen();
        }

        _controller.CommitDirect(symbol);
        RecordRecentSymbol(symbol);
        CloseKey1SymbolPopup();
    }

    /// <summary>
    /// 顶屏：组串按当前首选候选落屏（无候选时 CommitEnter 原样上屏字母串）。
    /// ⚠ 坑：候选只覆盖前缀（部分上屏）时会留余串，而 CommitDirect 内部 ClearInput 会连带吞掉余串，
    /// 故余串直接原样上屏，保证不丢击键。
    /// </summary>
    private void PushCompositionToScreen()
    {
        _controller.CommitEnter();
        if (!_controller.IsEmpty)
        {
            _controller.CommitDirect(_controller.DigitInput);
        }
    }

    /// <summary>
    /// 把弹层的独立 HWND 也做成"不抢焦点"（与主窗口同一套）：OR `WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW`，
    /// 并挂 `WM_MOUSEACTIVATE → MA_NOACTIVATE`。
    /// ⚠ 坑（2026-10-05，Deck 实测「符号偶尔弹得出来却上不了屏」）：Popup 是**独立 HWND**，主窗口的 NOACTIVATE
    /// 管不到它。点选框一旦让它激活，前台窗口就变成我们自己 ⇒ `SendInput` 打不到目标程序 ⇒ 符号被静默丢弃；
    /// 同时激活冲突还会吃掉鼠标消息（表现为点了符号没反应）。两者都靠这里堵死。
    /// </summary>
    private static void MakePopupNonActivating(Popup? popup, string tag, ref nint hookedHwnd)
    {
        try
        {
            if (popup?.Child is not FrameworkElement child)
            {
                return;
            }

            if (PresentationSource.FromVisual(child) is not HwndSource source || source.Handle == 0)
            {
                return;
            }

            var hwnd = source.Handle;
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            var wanted = style
                | NativeMethods.WsExNoActivate.ToInt64()
                | NativeMethods.WsExToolWindow.ToInt64();
            if (wanted != style)
            {
                _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new nint(wanted));
                FileLogger.Info($"popup[{tag}]: exstyle 0x{style:X} → 0x{wanted:X}（NOACTIVATE|TOOLWINDOW）");
            }

            // ⚠ 钩子只在 HWND 变化时挂一次；重复挂会在同一次点击里被调用多次（幂等但浪费）。
            if (hookedHwnd == hwnd)
            {
                return;
            }

            hookedHwnd = hwnd;
            source.AddHook((nint _, int msg, nint _, nint _, ref bool handled) =>
            {
                if (msg == NativeMethods.WmMouseActivate)
                {
                    handled = true;
                    return NativeMethods.MaNoActivate;
                }

                return nint.Zero;
            });
        }
        catch (Exception ex)
        {
            FileLogger.Error($"popup[{tag}]: make non-activating failed", ex);
        }
    }

    /// <summary>
    /// 上屏前记下前台窗口。⚠ Deck 上"符号上不了屏"只有两种可能：前台被我们自己的弹层抢了（pid 会是本进程），
    /// 或者按下根本没送达弹窗按钮（日志里连 symbol-inject 都没有）。这一行就是判据。
    /// </summary>
    private static void LogForegroundBeforeInject(string symbol)
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            var cls = new System.Text.StringBuilder(256);
            _ = NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            FileLogger.Info($"key1-popup[symbol-inject] symbol={symbol} fg=0x{hwnd.ToInt64():X} pid={pid} class={cls}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("key1-popup: foreground log failed", ex);
        }
    }

    /// <summary>
    /// MRU 纯函数：把符号提到首位、去重、截断到上限；返回新列表，不改入参（便于单测与复用）。
    /// </summary>
    internal static List<string> PushRecentSymbol(IReadOnlyList<string>? current, string symbol, int max = RecentSymbolsMax)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(symbol))
        {
            list.Add(symbol);
        }

        if (current is not null)
        {
            foreach (var s in current)
            {
                if (string.IsNullOrEmpty(s) || s == symbol || list.Contains(s))
                {
                    continue;
                }

                if (list.Count >= max)
                {
                    break;
                }

                list.Add(s);
            }
        }

        return list.Count > max ? list.GetRange(0, max) : list;
    }

    /// <summary>记录最近使用的符号并立即落盘（验收要求"重启仍在"，含被强杀场景）。</summary>
    private void RecordRecentSymbol(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return;
        }

        _settings.RecentSymbols = PushRecentSymbol(_settings.RecentSymbols, symbol);
        _settings.Save();
        RefreshRecentSymbolRow();
    }

    /// <summary>按当前 MRU 刷新"最近"行：无数据时整行隐藏。</summary>
    private void RefreshRecentSymbolRow()
    {
        if (_recentSymbolRow is null)
        {
            return;
        }

        var list = _settings.RecentSymbols ?? [];
        for (var i = 0; i < _recentSymbolButtons.Length; i++)
        {
            var btn = _recentSymbolButtons[i];
            if (i < list.Count)
            {
                btn.Content = list[i];
                btn.Visibility = Visibility.Visible;
            }
            else
            {
                btn.Visibility = Visibility.Collapsed;
            }
        }

        _recentSymbolRow.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点"最近"行：与固定格走完全同一条路径（含顶屏、MRU 复提、关闭选框）。</summary>
    private void OnRecentSymbolClick(int index)
    {
        var list = _settings.RecentSymbols;
        if (list is null || index < 0 || index >= list.Count)
        {
            return;
        }

        OnKey1SymbolClick(list[index]);
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
            btn.Click += (_, _) =>
            {
                PlayKeyClick();
                RecordRecentSymbol(sym); // 符号面板与 1 键弹窗共用同一条 MRU（最近行只在弹窗显示）
                _controller.CommitDirect(sym);
            };
            _ = SymbolGrid.Children.Add(btn);
        }

        ApplyTheme(); // 新页按钮应用当前主题/透明度
    }
}
