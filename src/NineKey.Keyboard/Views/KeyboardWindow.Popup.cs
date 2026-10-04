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
            StaysOpen = false, // 点选框外即关（外点与贴边翻转都交给 Popup 自身，不手写）
            AllowsTransparency = true,
            Focusable = false, // W5：新增控件一律不入焦点链
            Child = _key1PopupBorder,
        };

        // 挂进根 Grid 只为进入逻辑树；Popup 自身不占布局。
        if (Content is System.Windows.Controls.Grid root)
        {
            root.Children.Add(_key1Popup);
        }
    }

    private void ToggleKey1SymbolPopup()
    {
        if (_key1Popup is not null)
        {
            _key1Popup.IsOpen = !_key1Popup.IsOpen;
            if (_key1Popup.IsOpen)
            {
                RefreshRecentSymbolRow();
            }
        }
    }

    private void CloseKey1SymbolPopup()
    {
        if (_key1Popup is not null && _key1Popup.IsOpen)
        {
            _key1Popup.IsOpen = false;
        }
    }

    private void OnKey1SymbolClick(string symbol)
    {
        PlayKeyClick();
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
