// 本文件职责：WM_INPUT 原始触摸消息的解析——把 lParam 句柄解析为触点屏幕坐标（§13.22）。
// 数据流位置：壳侧收到 WM_INPUT → 本类解析 → 命中可编辑区才弹出键盘。
// ⚠ 坑：当前实现不解析 HID 报告细节，靠 Raw Input 注册过滤 + GetCursorPos 取首触点位置（§13.22）。
// ⚠ 平台边界：P/Invoke 声明在库内 NativeMethods（仅声明）；真调用只发生在 Windows 运行时，Linux 上触发即 EntryPointNotFound，壳侧保证只在 Windows 路径使用。

using System.Runtime.InteropServices;
using static NineKey.Keyboard.Services.NativeMethods;

namespace NineKey.Keyboard.Services;

/// <summary>原始触摸消息解析（§13.22）。</summary>
public static class RawTouchParser
{
    /// <summary>触点屏幕坐标。</summary>
    public sealed record TouchPoint(int X, int Y);

    /// <summary>
    /// 处理 <paramref name="msg"/> 消息；当它是来自触控设备的按下事件时返回触点屏幕坐标。
    /// 当前实现不解析 HID 报告，依赖 Raw Input 注册过滤 + GetCursorPos 获取触点位置。
    /// </summary>
    public static bool TryGetTouchPoint(int msg, nint lParam, out TouchPoint point)
    {
        point = new TouchPoint(0, 0);
        if (msg != WmInput)
        {
            return false;
        }

        var hRawInput = lParam;
        var size = 0u;
        _ = GetRawInputData(hRawInput, RidInput, 0, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0)
        {
            return false;
        }

        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RidInput, ptr, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size)
            {
                return false;
            }

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(ptr);
            if (header.dwType != RIM_TYPEHID)
            {
                return false;
            }

            // 我们注册了触摸屏，收到 HID 事件即视为触摸事件；
            // 用 GetCursorPos 获取当前触点位置（Windows 会把光标同步到首触点）。
            if (!GetCursorPos(out var pt))
            {
                return false;
            }

            point = new TouchPoint(pt.X, pt.Y);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
