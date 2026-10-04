// 本文件职责：KeyboardWindow 的主题涂刷（跟随系统深浅色、自定义背景/按键色、不透明度与闪现）。
// 数据流位置：AppSettings + 系统主题注册表 → ApplyTheme → 根容器/按键/面板按钮画笔。
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

/// <summary>KeyboardWindow 主题部分：ApplyTheme 及其涂刷辅助。</summary>
public partial class KeyboardWindow
{

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

        // 1 键符号选框：弹层不在常规视觉树路径上，必须显式涂刷，否则浅色主题下会白底白字。
        if (_key1PopupBorder is not null)
        {
            _key1PopupBorder.Background = barBg;
            _key1PopupBorder.BorderBrush = keyBorder;
        }

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
}
