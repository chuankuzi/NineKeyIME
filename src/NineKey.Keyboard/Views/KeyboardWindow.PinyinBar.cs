// 本文件职责：键盘上方"拼音浮条"（批 2026-10-05）——展示当前查询的拼音组合引导项，点击=锁定/撤销该组合。
// 数据流位置：KeyController.PinyinGuides → 浮条 ItemsControl；浮条点击 → CommitCandidate(PinyinGuide) → SelectPinyinCombo。
// ⚠ 坑 1：引导项原先混在候选行里，**占掉 PageSize 的一格**（把词挤掉一个）——分离成独立浮条后，
//          候选行只放词（+句子位），"预选框出现字母挤压字词"从结构上消失；候选栏里那段 PinyinGuide 着色分支随之失效（保留无害）。
// ⚠ 坑 2：浮条是**独立 HWND**，必须自己断言置顶（SWP_NOACTIVATE，不抢焦点），否则多键盘/其他置顶窗会盖住它。
// ⚠ 坑 3：浮条不得进焦点链（W5）：Popup 与按钮一律 Focusable=false。
// ⚠ 坑 4：Popup 不会随宿主隐藏而隐藏——必须挂在 IsVisibleChanged 上，窗口一收（托盘/贴条）浮条同步收起。
// ⚠ 坑 5：本项目 UseWindowsForms + UseWPF 并存，`Button`/`Color` 等类型二义——壳代码一律**全限定**（同壳代码纪律）。

using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using NineKey.Keyboard.Services;

namespace NineKey.Keyboard.Views;

/// <summary>KeyboardWindow 的拼音浮条：键盘上方常显的小条，承载拼音组合引导项。</summary>
public partial class KeyboardWindow
{
    private Popup? _pinyinBar;
    private System.Windows.Controls.ItemsControl? _pinyinBarItems;

    /// <summary>构造期装配浮条（与 1 键选框同风格：挂进根 Grid 只为进入逻辑树，不占布局）。</summary>
    private void BuildPinyinBar()
    {
        _pinyinBarItems = new System.Windows.Controls.ItemsControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(
                new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel))),
        };
        var border = new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            Child = _pinyinBarItems,
        };

        _pinyinBar = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Top,
            VerticalOffset = -6,
            StaysOpen = true,      // 常显：不因点别处而消失（收起只由"无引导项"或窗口隐藏决定）
            AllowsTransparency = true,
            Focusable = false,     // W5：不进焦点链
            Child = border,
        };

        if (Content is System.Windows.Controls.Grid root)
        {
            root.Children.Add(_pinyinBar);
        }

        // ⚠ 坑 4：Popup 不随宿主隐藏而隐藏，必须显式同步。
        IsVisibleChanged += (_, _) => RefreshPinyinBar();
    }

    /// <summary>刷新浮条（StateChanged / 可见性变化时调用）：无引导项则收起。</summary>
    private void RefreshPinyinBar()
    {
        if (_pinyinBar is null || _pinyinBarItems is null)
        {
            return;
        }

        _pinyinBarItems.Items.Clear();
        var guides = _controller.PinyinGuides;
        foreach (var guide in guides)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = guide.Text,
                Focusable = false,          // W5
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(8, 2, 8, 2),
                ToolTip = guide.Pinyin is null ? "撤销拼音锁定，显示全部候选" : "点选此拼音组合，收窄候选",
            };
            btn.Click += (_, _) => _controller.CommitCandidate(guide);   // 与候选栏点击同一条语义路径
            _pinyinBarItems.Items.Add(btn);
        }

        var show = guides.Count > 0 && IsVisible;
        if (_pinyinBar.IsOpen != show)
        {
            _pinyinBar.IsOpen = show;
        }

        if (show)
        {
            // 浮条刚开时其 HWND 尚未就绪，延后一拍再断言置顶。
            Dispatcher.BeginInvoke(AssertPinyinBarTopmost);
        }
    }

    /// <summary>浮条独立 HWND：断言置顶，但一律 SWP_NOACTIVATE（不抢焦点）。</summary>
    private void AssertPinyinBarTopmost()
    {
        try
        {
            if (_pinyinBar?.Child is not FrameworkElement child)
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
            FileLogger.Error("pinyin-bar: assert topmost failed", ex);
        }
    }
}
