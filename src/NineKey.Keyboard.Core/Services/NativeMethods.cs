// 本文件职责：集中声明项目所需的全部 Win32 P/Invoke 签名、常量与结构体。
// 数据流位置：被 TextInjector / HotkeyService / RawTouchWatcher 等调用，与 Windows 用户层 API 交互。
// ⚠ 坑 1：INPUT / RAWINPUT 等联合体在 x64 下的 sizeof 必须实测，cbSize 错传会被 API 以 87 拒绝（§4.3）。
// ⚠ 坑 2：IsWindowElevated 查询失败时按非提权处理，避免 UIPI 判定过度阻塞。
// 相关规格：§2.8、§4.3、§13.22、§13.26、§13.28、§W5、§M8-6。

using System.Runtime.InteropServices;
using System.Security;

namespace NineKey.Keyboard.Services;

/// <summary>
/// 全部 Win32 P/Invoke 集中处（规格 §2.8）：精确签名 + x64 结构体布局。
/// INPUT 的 cbSize 必须等于 Marshal.SizeOf&lt;INPUT&gt;()（§4.3，结构体大小错误是 P/Invoke 头号 bug）。
/// </summary>
[SuppressUnmanagedCodeSecurity]
public static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const nint WsExNoActivate = 0x08000000;
    public const nint WsExToolWindow = 0x00000080;
    public const nint WsExTopmost = 0x00000008;

    // ---- 置顶重断言（批 fix/topmost-reassert）----
    // ⚠ 坑：HWND_TOPMOST 是 (HWND)-1；SWP_NOACTIVATE 必须带——重断言只许动 z 序，绝不许抢焦点。
    public static readonly nint HwndTopmost = new(-1);
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ---- 鼠标激活控制（§W5 / M8-6：NOACTIVATE 窗口被点击时仍须收到鼠标消息）----
    public const int WmMouseActivate = 0x0021;
    public const nint MaActivate = 1;
    public const nint MaActivateAndEat = 2;
    public const nint MaNoActivate = 3;
    public const nint MaNoActivateAndEat = 4;

    // ---- 虚拟键码（§2.8 / §13.26 / §13.28）----
    public const ushort VkBack = 0x08;
    public const ushort VkTab = 0x09;
    public const ushort VkReturn = 0x0D;
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12; // Alt
    public const ushort VkPause = 0x13;
    public const ushort VkCapital = 0x14; // Caps Lock
    public const ushort VkEscape = 0x1B;
    public const ushort VkSpace = 0x20;
    public const ushort VkPrior = 0x21; // Page Up
    public const ushort VkNext = 0x22; // Page Down
    public const ushort VkEnd = 0x23;
    public const ushort VkHome = 0x24;
    public const ushort VkLeft = 0x25;
    public const ushort VkUp = 0x26;
    public const ushort VkRight = 0x27;
    public const ushort VkDown = 0x28;
    public const ushort VkInsert = 0x2D;
    public const ushort VkDelete = 0x2E;
    public const ushort VkLWin = 0x5B;
    public const ushort VkRWin = 0x5C;
    public const ushort VkF1 = 0x70;
    public const ushort VkF2 = 0x71;
    public const ushort VkF3 = 0x72;
    public const ushort VkF4 = 0x73;
    public const ushort VkF5 = 0x74;
    public const ushort VkF6 = 0x75;
    public const ushort VkF7 = 0x76;
    public const ushort VkF8 = 0x77;
    public const ushort VkF9 = 0x78;
    public const ushort VkF10 = 0x79;
    public const ushort VkF11 = 0x7A;
    public const ushort VkF12 = 0x7B;
    public const ushort Vk0 = 0x30;
    public const ushort Vk9 = 0x39;
    public const ushort VkA = 0x41;
    public const ushort VkB = 0x42;
    public const ushort VkC = 0x43;
    public const ushort VkD = 0x44;
    public const ushort VkE = 0x45;
    public const ushort VkF = 0x46;
    public const ushort VkG = 0x47;
    public const ushort VkH = 0x48;
    public const ushort VkI = 0x49;
    public const ushort VkJ = 0x4A;
    public const ushort VkK = 0x4B;
    public const ushort VkL = 0x4C;
    public const ushort VkM = 0x4D;
    public const ushort VkN = 0x4E;
    public const ushort VkO = 0x4F;
    public const ushort VkP = 0x50;
    public const ushort VkQ = 0x51;
    public const ushort VkR = 0x52;
    public const ushort VkS = 0x53;
    public const ushort VkT = 0x54;
    public const ushort VkU = 0x55;
    public const ushort VkV = 0x56;
    public const ushort VkW = 0x57;
    public const ushort VkX = 0x58;
    public const ushort VkY = 0x59;
    public const ushort VkZ = 0x5A;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    /// <summary>将字符映射为虚拟键码与 Shift 状态（§13.40-S2：粘性修饰键组合快捷键用）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScan(char ch);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(POINT lpPoint);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(nint TokenHandle, int TokenInformationClass, out int TokenInformation, int TokenInformationLength, out int ReturnLength);

    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;
    public const int TokenElevation = 20;

    /// <summary>窗口属主进程是否提权（UIPI 判定用，§0.3）。查询失败按非提权处理。</summary>
    public static bool IsWindowElevated(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return false;
            }

            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevation, sizeof(int), out _)
                       && elevation != 0;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public readonly bool Contains(int x, int y) =>
            x >= Left && x < Right && y >= Top && y < Bottom;
    }

    public delegate nint HookProc(int nCode, nint wParam, nint lParam);

    public const int WhMouseLl = 14;
    public const int WmLButtonDown = 0x0201;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public int ptX;
        public int ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public InputType type;
        [FieldOffset(8)] public KEYBDINPUT ki;

        // x64 下 INPUT 联合体的 sizeof 为 40（对齐到 MOUSEINPUT 的 8 字节指针成员），
        // cbSize 传 32 会被 SendInput 以 ERROR_INVALID_PARAMETER (87) 拒绝。
        [FieldOffset(32)] private nint _unionPad;
    }

    public enum InputType : uint
    {
        KEYBOARD = 1,
    }

    [Flags]
    public enum KeyEventFlags : uint
    {
        KEYUP = 0x0002,
        UNICODE = 0x0004,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public KeyEventFlags dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    // ---- Raw Input（§13.22 全局触摸捕获）----
    public const int WmInput = 0x00FF;
    public const uint RidInput = 0x10000003;
    public const uint RidDeviceInfo = 0x2000000B;
    public const uint RidInputSink = 0x00000100;
    public const uint RidDevNotify = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        // hid/mouse/keyboard 联合体：x64 下 RAWINPUT 大小为 48 字节，header 占 24 字节，联合体占 24 字节
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public byte[] rawData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public RID_DEVICE_INFO_HID hid;
    }

    public const uint RIM_TYPEHID = 2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetRawInputData(nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetRawInputDeviceInfo(nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }
}
