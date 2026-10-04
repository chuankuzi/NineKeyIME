// 本文件职责：KeyboardWindow 的布局模式与尺寸缩放（设计尺寸、屏幕适配下限、模式切换、尺寸手柄）。
// 数据流位置：屏幕 WorkArea + AppSettings → RecalculateScaleForCurrentScreen / SetLayoutMode / SetModeScale → 窗口与根容器尺寸。
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

/// <summary>KeyboardWindow 尺寸部分：布局模式、屏幕适配下限与模式缩放。</summary>
public partial class KeyboardWindow
{

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

    // ---- 屏幕几何唯一口径 = DIP ----
    // ⚠ 坑（Deck 1280×800 手动转横向「26键底右双边缘切割」根因）：WinForms `Screen.WorkingArea` 给的是
    //   **物理像素**，而 WPF 的 Left/Top/Width/Height 全是 **DIP**。本进程是 PerMonitorV2，两者相差 dpiScale 倍：
    //   100% DPI 时相等（开发机因此不复现），150% 时窗口被放大 1.5 倍并推向右下 → 底边与右边同时被切。
    //   所以：任何喂给 WPF 几何的屏幕矩形，取到物理值后必须 ÷ 窗口 DPI 缩放。
    private static double GetDpiScale(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;
        if (scale <= 0)
        {
            scale = VisualTreeHelper.GetDpi(window).DpiScaleX;   // 无 PresentationSource 时退到系统/主屏 DPI
        }

        return scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// 物理像素矩形 → DIP 矩形（100% DPI 原样返回）。抽成纯函数是为了让单测能锁住 Deck 单位口径，
    /// 无需真机（100% DPI 开发机上物理==DIP，此路径永远走不到）。
    /// </summary>
    internal static System.Drawing.Rectangle ScaleToDip(System.Drawing.Rectangle physical, double dpiScale)
    {
        if (dpiScale <= 0 || Math.Abs(dpiScale - 1.0) < 0.001)
        {
            return physical;
        }

        return new System.Drawing.Rectangle(
            (int)Math.Round(physical.X / dpiScale),
            (int)Math.Round(physical.Y / dpiScale),
            (int)Math.Round(physical.Width / dpiScale),
            (int)Math.Round(physical.Height / dpiScale));
    }

    /// <summary>物理像素矩形 → DIP 矩形（取窗口当前 DPI 缩放）。</summary>
    private static System.Drawing.Rectangle ToDip(Window window, System.Drawing.Rectangle physical) =>
        ScaleToDip(physical, GetDpiScale(window));

    /// <summary>
    /// 构造期（句柄未生，FromHandle 用不了）的主屏 WorkArea（DIP）。
    /// ⚠ 首选 SystemParameters.WorkArea：WPF 原生、**跟随当前方向**、且已扣掉任务栏；
    ///   退化时才用 PrimaryScreenWidth/Height 全屏兜底（全屏高不含任务栏信息，直接当 WorkArea 会把底边压到任务栏下）。
    /// </summary>
    private static System.Drawing.Rectangle GetPrimaryWorkAreaDip()
    {
        var wa = SystemParameters.WorkArea;
        if (wa.Width > 0 && wa.Height > 0)
        {
            return new System.Drawing.Rectangle((int)Math.Round(wa.X), (int)Math.Round(wa.Y), (int)Math.Round(wa.Width), (int)Math.Round(wa.Height));
        }

        var w = SystemParameters.PrimaryScreenWidth;
        var h = SystemParameters.PrimaryScreenHeight;
        if (w > 0 && h > 0)
        {
            return new System.Drawing.Rectangle(0, 0, (int)Math.Round(w), (int)Math.Round(h));
        }

        return new System.Drawing.Rectangle(0, 0, 1280, 800);   // 极端兜底：横屏安全值（原为 1280×720）
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
        // ⚠ 坑：默认值曾经退回「构造期主屏」口径（GetCurrentWorkArea）——运行期调用会取错屏/错单位，
        // 现在一律取窗口实际所在屏（已 DIP）。
        var work = workArea ?? GetWindowWorkArea(this);
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
        CloseKey1SymbolPopup(); // 切模式一律收掉 1 键符号选框，防残留在别的面板上

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
        LogGeometry($"mode-{mode}");   // TEMP-DIAG

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

    private static System.Drawing.Rectangle GetWindowWorkArea(Window window)
    {
        // §13.45-7：以窗口实际所在屏幕的 WorkArea 为准；⚠ 再统一换成 DIP（见 GetDpiScale 坑注释）
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            // ⚠ 坑：FromHandle(IntPtr.Zero) 不报错，静默返回主屏——句柄未生时显式走构造期兜底，行为才可解释。
            return GetPrimaryWorkAreaDip();
        }

        var screen = System.Windows.Forms.Screen.FromHandle(helper.Handle);
        var physical = screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 800);
        return ToDip(window, physical);
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

    /// <summary>
    /// 运行期显示设置变化（分辨率 / 方向 / DPI）：重算适配下限并把窗口夹回屏内。
    /// ⚠ 坑：SystemEvents 回调不在 UI 线程，必须经 Dispatcher 回到 UI 线程再动窗口几何；关闭中直接丢弃。
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            try
            {
                LogGeometry("display-changed-before");   // TEMP-DIAG
                RecalculateScaleForCurrentScreen();
                _currentScale = GetScaleFor(_layoutMode);
                ApplyModeScale();
                ClampWindowToWorkArea();
                LogGeometry("display-changed-after");    // TEMP-DIAG
            }
            catch (Exception ex)
            {
                FileLogger.Error("display-changed 重算失败", ex);
            }
        });
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
}
