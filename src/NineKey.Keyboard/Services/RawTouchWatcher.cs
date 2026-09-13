// 本文件职责：通过 Raw Input 注册全局触摸并判定可编辑区（§13.22、§13.29）。触点消息解析已下沉至纯库 RawTouchParser。
// 数据流位置：KeyboardWindow 消息循环 → RawTouchParser.TryGetTouchPoint → IsEditableAt → 宿主决定弹出。
// ⚠ 坑 2：部分自绘输入框没有 UIA 信息，需用窗口类名白名单兜底（§13.29）。

using System.Runtime.InteropServices;
using System.Windows.Automation;
using static NineKey.Keyboard.Services.NativeMethods;

namespace NineKey.Keyboard.Services;

/// <summary>
/// 全局触摸捕获（§13.22）：Raw Input 注册 + 可编辑区判定；触点解析见 <see cref="RawTouchParser"/>。
/// </summary>
public sealed class RawTouchWatcher
{
    private bool _registered;

    /// <summary>注册 Raw Input 触控设备到指定窗口。</summary>
    public bool Register(nint hwnd)
    {
        if (_registered || hwnd == 0)
        {
            return _registered;
        }

        var devices = new RAWINPUTDEVICE[]
        {
            new()
            {
                usUsagePage = 0x0D, // Digitizer
                usUsage = 0x04,     // Touch Screen
                dwFlags = RidInputSink | RidDevNotify,
                hwndTarget = hwnd,
            },
        };

        _registered = RegisterRawInputDevices(
            devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!_registered)
        {
            FileLogger.Error($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");
        }

        return _registered;
    }

    /// <summary>
    /// 处理 <paramref name="msg"/> 消息；当它是来自触控设备的按下事件时返回触点屏幕坐标。
    /// 转发至纯库 <see cref="RawTouchParser"/>，保留本签名以兼容调用方。
    /// </summary>
    public static bool TryGetTouchPoint(int msg, nint lParam, out RawTouchParser.TouchPoint point) =>
        RawTouchParser.TryGetTouchPoint(msg, lParam, out point);

    /// <summary>
    /// §13.29：先以 UIA 保守判定是否可编辑；UIA 不可编辑时，以窗口类名白名单兜底。
    /// </summary>
    public static bool IsEditableAt(RawTouchParser.TouchPoint point, IReadOnlyList<string> whitelist)
    {
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            if (el is not null && FocusWatcher.IsEditable(el))
            {
                return true;
            }

            var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y });
            if (hwnd == 0)
            {
                return false;
            }

            var className = GetClassName(hwnd);
            return whitelist.Contains(className, StringComparer.OrdinalIgnoreCase);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static string GetClassName(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        _ = NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
