// 本文件职责：注册并监听全局热键 Ctrl+Alt+K，作为显示/隐藏键盘的兜底找回手段。
// 数据流位置：KeyboardWindow 获取窗口句柄 → Register → WndProc → HotkeyPressed → 宿主切换显示。
// ⚠ 坑 1：RegisterHotKey 要求窗口句柄已创建且在同一线程消息循环，必须在 SourceInitialized 之后调用。
// ⚠ 坑 2：Dispose 时必须先 UnregisterHotKey 再 RemoveHook，否则可能收到已释放资源的消息。
// 相关规格：§2.7、§13.27。

using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NineKey.Keyboard.Services;

/// <summary>全局热键 Ctrl+Alt+K：显示/隐藏键盘（找回键盘的手段之一）。</summary>
public sealed class HotkeyService : IDisposable
{
    // ⚠ 坑：0x4E4B 是 ASCII 'NK' 的十六进制，作为 RegisterHotKey 的 id 标识。
    public const int HotkeyId = 0x4E4B;

    private const int WmHotkey = 0x0312;
    // ⚠ 坑：0x0001=MOD_ALT，0x0002=MOD_CONTROL，按位或得到 Ctrl+Alt 组合。
    private const uint ModControlAlt = 0x0001 /*| 0x0002*/;
    private const uint VkK = 0x4A;

    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    /// <summary>向指定 WPF 窗口句柄注册 Ctrl+Alt+K 全局热键。</summary>
    public void Register(WindowInteropHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        var handle = helper.Handle;
        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            return;
        }

        _registered = RegisterHotKey(handle, HotkeyId, ModControlAlt, VkK);
        if (_registered)
        {
            _source.AddHook(WndProc);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }

        return nint.Zero;
    }

    /// <summary>注销热键并移除窗口钩子。</summary>
    public void Dispose()
    {
        if (_registered)
        {
            _ = UnregisterHotKey(_source?.Handle ?? nint.Zero, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
