// 本文件职责（**临时**）：Deck 方向/DPI bug 真机定位用的几何日志——把各 WorkArea 读数、DPI 缩放、
// 适配下限、当前模式与最终窗口几何一次打全，供目标机回传（%LocalAppData%\NineKeyIME\logs\ninekey.log）。
// ⚠ 本文件与所有 `// TEMP-DIAG` 调用点是**临时诊断**：Deck 验收通过后整文件删除 + 调用点清除，单独提交。
// ⚠ 日志只用 FileLogger.Info（不新增依赖），任何异常都吞掉，绝不影响输入链路。

using System.Windows;
using System.Windows.Interop;
using NineKey.Keyboard.Services;

namespace NineKey.Keyboard.Views;

/// <summary>KeyboardWindow 临时几何诊断（Deck 方向 bug 专用，验收后删）。</summary>
public partial class KeyboardWindow
{
    /// <summary>打印一次完整几何读数。tag 用来说明调用时机（ctor/loaded/mode/dock/clamp/display-changed）。</summary>
    private void LogGeometry(string tag)     // TEMP-DIAG
    {
        try
        {
            var spWork = SystemParameters.WorkArea;
            var ready = new WindowInteropHelper(this).Handle != IntPtr.Zero;
            var primaryPhys = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? System.Drawing.Rectangle.Empty;
            var windowPhys = ready
                ? System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle)?.WorkingArea ?? System.Drawing.Rectangle.Empty
                : System.Drawing.Rectangle.Empty;
            var usedDip = GetWindowWorkArea(this);

            FileLogger.Info(
                $"geom[{tag}] handle={(ready ? "ready" : "none")} dpi={GetDpiScale(this):F2} " +
                $"spWorkArea={spWork.Width:F0}x{spWork.Height:F0}@{spWork.X:F0},{spWork.Y:F0} " +
                $"spScreen={SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0} " +
                $"primaryPhys={primaryPhys.Width}x{primaryPhys.Height}@{primaryPhys.X},{primaryPhys.Y} " +
                $"windowPhys={windowPhys.Width}x{windowPhys.Height}@{windowPhys.X},{windowPhys.Y} " +
                $"usedDip={usedDip.Width}x{usedDip.Height}@{usedDip.X},{usedDip.Y} " +
                $"minScale[cn={_screenMinScaleChinese:F3} num={_screenMinScaleNumber:F3} en={_screenMinScaleEnglish:F3} sym={_screenMinScaleSymbol:F3}] " +
                $"mode={_layoutMode} scale={_currentScale:F3} " +
                $"bounds=({Left:F0},{Top:F0},{Width:F0},{Height:F0}) docked={IsDockedAsStrip}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("geom[log] 打印失败", ex);
        }
    }
}
