// 本文件职责：SendInput 上屏封装，处理 Unicode 文本、虚拟键、组合键与焦点归还。
// 数据流位置：KeyController 调用 → 本类组织 INPUT → user32.SendInput → 目标窗口。
// ⚠ 坑 1：WPF 等本进程控件不吃 KEYEVENTF_UNICODE，必须走 SameProcess*Injecting 回调直插（§2.8）。
// ⚠ 坑 2：UIA 等程序化路径会意外激活键盘窗口，发送前须把焦点归还给最近的外部目标（§W5）。
// ⚠ 坑 3：非提权进程无法向提权窗口发按键，需提前拦截并提示用户（§0.3 UIPI）。
// 相关规格：§2.8、§0.3、§W5、§M8-9。

using System.Runtime.InteropServices;
using static NineKey.Keyboard.Services.NativeMethods;

namespace NineKey.Keyboard.Services;

/// <summary>
/// SendInput 上屏封装（规格 §2.8）：Unicode 直送，支持代理对字符；退格/回车走虚拟键。
/// 发送前检查前台目标窗口有效性；SendInput 返回 0 时重试 1 次，再失败写日志放弃。
/// </summary>
/// <summary>同进程按键操作类型。</summary>
public enum SameProcessKeyKind
{
    Backspace,
    Enter,
    Space,
    Delete,
    Left,
    Right,
    Up,
    Down,
    Esc,
    Tab,
}

public sealed class TextInjector : ITextInjector
{
    /// <summary>
    /// 同进程文本直插回调：当目标前台窗口属于本进程时，绕过 SendInput/TSF，
    /// 由订阅方直接操作焦点 WPF 控件。返回 true 表示已处理，false 则回退到 SendInput。
    /// </summary>
    public static event Func<string, bool>? SameProcessTextInjecting;

    /// <summary>
    /// 同进程按键直插回调：当目标前台窗口属于本进程时，绕过 SendInput/TSF，
    /// 由订阅方直接操作焦点 WPF 控件。返回 true 表示已处理，false 则回退到 SendInput。
    /// </summary>
    public static event Func<SameProcessKeyKind, bool>? SameProcessKeyInjecting;

    /// <summary>测试钩子：覆盖 <see cref="GetForegroundWindow"/> 返回值，用于金丝雀测试。</summary>
    internal static nint ForegroundWindowOverride { get; set; }

    /// <summary>发送 Unicode 文本，代理对字符会拆成两个 INPUT 事件。</summary>
    public void InjectText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 同进程路由：WPF TextBox 走 TSF 不吃 KEYEVENTF_UNICODE，需直插文本。
        if (TrySameProcessTextInject(text))
        {
            return;
        }

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            var code = (ushort)rune.Value;
            inputs.Add(MakeKey(0, code, KeyEventFlags.UNICODE));
            inputs.Add(MakeKey(0, code, KeyEventFlags.UNICODE | KeyEventFlags.KEYUP));
        }

        Send(inputs, $"text '{text}'");
    }

    private static bool TrySameProcessTextInject(string text)
    {
        var foreground = ForegroundWindowOverride != 0 ? ForegroundWindowOverride : GetForegroundWindow();
        if (foreground == 0 || !IsOwnProcess(foreground))
        {
            return false;
        }

        var handler = SameProcessTextInjecting;
        if (handler is null)
        {
            return false;
        }

        try
        {
            return handler(text);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"SameProcessTextInjecting failed for '{text}'", ex);
            return false;
        }
    }

    private static bool TrySameProcessKeyInject(SameProcessKeyKind kind)
    {
        var foreground = ForegroundWindowOverride != 0 ? ForegroundWindowOverride : GetForegroundWindow();
        if (foreground == 0 || !IsOwnProcess(foreground))
        {
            return false;
        }

        var handler = SameProcessKeyInjecting;
        if (handler is null)
        {
            return false;
        }

        try
        {
            return handler(kind);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"SameProcessKeyInjecting failed for {kind}", ex);
            return false;
        }
    }

    /// <summary>发送 VK_BACK 退格键。</summary>
    public void InjectBackspace()
    {
        if (TrySameProcessKeyInject(SameProcessKeyKind.Backspace))
        {
            return;
        }

        Send([MakeKey(VkBack, 0, 0), MakeKey(VkBack, 0, KeyEventFlags.KEYUP)], "backspace");
    }

    /// <summary>发送 VK_RETURN 回车键。</summary>
    public void InjectEnter()
    {
        if (TrySameProcessKeyInject(SameProcessKeyKind.Enter))
        {
            return;
        }

        Send([MakeKey(VkReturn, 0, 0), MakeKey(VkReturn, 0, KeyEventFlags.KEYUP)], "enter");
    }

    /// <summary>发送 VK_SPACE 空格键。</summary>
    public void InjectSpace()
    {
        if (TrySameProcessKeyInject(SameProcessKeyKind.Space))
        {
            return;
        }

        Send([MakeKey(VkSpace, 0, 0), MakeKey(VkSpace, 0, KeyEventFlags.KEYUP)], "space");
    }

    /// <summary>§M8-9：发送单个虚拟键（Delete/方向键/Esc/Tab 等）。</summary>
    public void InjectKey(ushort vk)
    {
        if (TrySameProcessKeyInject(MapVkToKind(vk)))
        {
            return;
        }

        Send([MakeKey(vk, 0, 0), MakeKey(vk, 0, KeyEventFlags.KEYUP)], $"key-{vk:X}");
    }

    private static SameProcessKeyKind MapVkToKind(ushort vk) => vk switch
    {
        VkDelete => SameProcessKeyKind.Delete,
        VkLeft => SameProcessKeyKind.Left,
        VkRight => SameProcessKeyKind.Right,
        VkUp => SameProcessKeyKind.Up,
        VkDown => SameProcessKeyKind.Down,
        VkEscape => SameProcessKeyKind.Esc,
        VkTab => SameProcessKeyKind.Tab,
        _ => SameProcessKeyKind.Backspace, // fallback，仅占位，不会真命中同进程路径
    };

    /// <summary>发送一组按键组合（按下顺序 + 逆序抬起）。不进 ITextInjector 接口（M7-7 用户裁定）。</summary>
    public void InjectShortcut(ushort[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0)
        {
            return;
        }

        var inputs = new List<INPUT>(keys.Length * 2);
        foreach (var vk in keys)
        {
            inputs.Add(MakeKey(vk, 0, 0));
        }

        for (var i = keys.Length - 1; i >= 0; i--)
        {
            inputs.Add(MakeKey(keys[i], 0, KeyEventFlags.KEYUP));
        }

        Send(inputs, $"shortcut[{string.Join(",", keys)}]");
    }

    private static INPUT MakeKey(ushort vk, ushort scan, KeyEventFlags flags) =>
        new() { type = InputType.KEYBOARD, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };

    // 显式静态构造函数：确保 TargetTracker 在 new TextInjector()（Host 启动装配）时即启动，
    // 而不是推迟到第一次 Send（beforefieldinit 会那样做，导致首轮注入没有目标窗口记录）。
    static TextInjector()
    {
    }

    private static nint _lastTarget;

    // 每 500ms 记录最近的外部前台窗口（排除自己进程）。
    // UIA Invoke 等程序化路径会绕过 WS_EX_NOACTIVATE 强行激活键盘（W5），
    // 激活到 OnClick 是同步的，Activated 事件回退赶不上——故在发送路径上归还焦点。
    private static readonly System.Threading.Timer TargetTracker = new(_ =>
    {
        var fg = GetForegroundWindow();
        if (fg != 0 && IsWindow(fg) && !IsOwnProcess(fg))
        {
            _lastTarget = fg;
        }
    }, null, 500, 500);

    /// <summary>目标窗口提权导致无法输入时触发（§0.3，订阅方负责节流与提示）。</summary>
    public static event Action? ElevatedTargetBlocked;

    private static void Send(IReadOnlyList<INPUT> inputs, string what)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        // 发送前检查目标窗口有效性：前台真空（fg=0）或前台是自己（键盘被 UIA 等程序化路径
        // 意外激活，W5）时，先把焦点归还给最近目标窗口再发送——否则文本会发进键盘自己或丢失。
        var foreground = GetForegroundWindow();
        if (foreground == 0 || IsOwnProcess(foreground))
        {
            if (_lastTarget == 0 || !IsWindow(_lastTarget) || !SetForegroundWindow(_lastTarget))
            {
                FileLogger.Error($"inject {what}: no valid target window (fg={foreground}), skipped");
                return;
            }

            FileLogger.Info($"inject {what}: foreground was {(foreground == 0 ? "empty" : "self")}, restored target window");
            foreground = _lastTarget;
        }
        else if (!IsWindow(foreground))
        {
            FileLogger.Error($"inject {what}: no valid foreground window, skipped");
            return;
        }

        // §0.3：非提权进程无法向提权窗口发按键（UIPI）——提前拦截并提示，不做无效发送
        if (!SystemSettingsService.IsCurrentProcessElevated() && IsWindowElevated(foreground))
        {
            FileLogger.Error($"inject {what}: target window is elevated, blocked by UIPI");
            ElevatedTargetBlocked?.Invoke();
            return;
        }

        var batch = inputs as INPUT[] ?? inputs.ToArray();
        var sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            // 失败重试 1 次再放弃（§2.8）
            sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>());
        }

        if (sent == 0)
        {
            FileLogger.Error($"inject {what}: SendInput blocked after retry (err={Marshal.GetLastWin32Error()})");
        }
    }

    private static bool IsOwnProcess(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == GetCurrentProcessId();
    }
}
