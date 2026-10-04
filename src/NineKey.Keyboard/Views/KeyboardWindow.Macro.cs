// 本文件职责：KeyboardWindow 的宏键行（宏按钮构建、宏面板显隐、宏点击执行入口）。
// 数据流位置：AppSettings.Macros → BuildMacroPanel / UpdateMacroPanelVisibility → 宏内容注入或宏编辑器窗口。
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

/// <summary>KeyboardWindow 宏键部分：宏面板构建与显隐。</summary>
public partial class KeyboardWindow
{

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
        if (IsDockActive)
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

    private void BuildMacroPanel()
    {
        MacroPanel.Children.Clear();

        // 批8：宏上限放宽到 16 后，单行等分缩窄（每键自动变窄），避免横向溢出被 ClipToBounds 裁掉。
        // 取舍：未做"两行"——那需要同步改 Scale.cs 的 ExpandedDesignHeight（窗口高度唯一真相），该文件不在本批白名单。
        var count = Math.Max(1, _settings.Macros.Count);
        MacroPanel.Rows = 1;
        MacroPanel.Columns = count;
        foreach (var macro in _settings.Macros)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = string.IsNullOrWhiteSpace(macro.Name) ? "宏" : macro.Name,
                Style = (Style)FindResource("ShortcutButtonStyle"),
                Focusable = false,
                Tag = macro,
            };

            // 超 8 条时字号随密度收一档，4 字名在窄格内仍可辨
            if (count > 8)
            {
                btn.FontSize = 10;
                btn.Padding = new System.Windows.Thickness(0);
            }

            btn.Click += OnMacroClick;
            _ = MacroPanel.Children.Add(btn);
        }

        ApplyTheme();
    }

    private void OnMacroClick(object sender, RoutedEventArgs e)
    {
        PlayKeyClick();
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
}
