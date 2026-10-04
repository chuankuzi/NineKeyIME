// 本文件职责：窗口置顶（WS_EX_TOPMOST）的断言与兜底重断言——防"LM Studio 前台时键盘被压在其下"。
// 数据流位置：XAML Topmost=True + OnSourceInitialized 样式落定 → 重断言；鼠标预览按下 / 500ms 轮询 → 兜底重断言。
// ⚠ 坑 1：重断言**只能动 z 序**，绝不能激活或抢焦点——WS_EX_NOACTIVATE 是焦点防御核心，任何时候禁改。
//          故一律走一次 SetWindowPos(HWND_TOPMOST) + SWP_NOACTIVATE；不用 WPF 的 Topmost=false→true
//          两次赋值：中间那一下会把窗口短暂踢出置顶组，既闪烁又可能被别的窗口抢走一拍。
// ⚠ 坑 2：本机（开发机）**复现不出**该症状：实测 LM Studio 主窗口 ExStyle=0x00000100（无 WS_EX_TOPMOST），
//          键盘窗口恒为 0x08080088（NOACTIVATE|LAYERED|TOOLWINDOW|TOPMOST），"激活 LM Studio + 热键隐藏再呼出"
//          三次采样里 z 序始终在 LM Studio 之上。所以这里是**防御性**修复，验收以 Deck 真机为准；
//          每次重断言都留 INFO 日志（低频事件，不是高频日志），供目标机判定"到底有没有真丢过置顶位"。

using System.Windows.Interop;
using NineKey.Keyboard.Services;

namespace NineKey.Keyboard.Views;

/// <summary>KeyboardWindow 置顶断言与兜底（LM Studio 前台压顶防御）。</summary>
public partial class KeyboardWindow
{
    /// <summary>窗口是否带 WS_EX_TOPMOST；句柄未生（尚未能丢）按 true 处理，避免构造期误判。</summary>
    internal static bool HasTopmostBit(nint hwnd) =>
        hwnd == 0 || (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle) & NativeMethods.WsExTopmost) != 0;

    /// <summary>
    /// 是否需要兜底重断言（纯函数，单测锁住判定口径）：**可见 + 非贴条 + 置顶位丢失**。
    /// 不可见时不动作（隐藏期间丢位无意义，显示时自会重断言）；贴条是右缘细条，不参与置顶组竞争。
    /// </summary>
    internal static bool NeedsTopmostReassert(bool isVisible, bool isDockedAsStrip, bool hasTopmostBit) =>
        isVisible && !isDockedAsStrip && !hasTopmostBit;

    /// <summary>
    /// 重断言置顶：一次 SetWindowPos(HWND_TOPMOST) 把窗口放回置顶组最上；SWP_NOACTIVATE 保证不抢焦点。
    /// </summary>
    private void ReassertTopmost(string reason)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        var before = HasTopmostBit(hwnd);
        var ok = NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

        // ⚠ 坑：本方法每次鼠标按下都会走一趟，**绝不能无脑记 INFO**（纪律：不加高频日志，一次点击一行会把
        // FileLogger 刷爆）。只在"真的丢过置顶位"或"调用失败"时留痕——这两行才是 Deck 定位要的证据。
        if (!before || !ok)
        {
            FileLogger.Info($"topmost-reassert: reason={reason} before={before} ok={ok} visible={IsVisible} " +
                $"docked={IsDockedAsStrip} fg={NativeMethods.GetForegroundWindow()}");
        }
    }

    /// <summary>500ms 轮询兜底（挂在既有 _fgTracker 的 Tick 上，见 KeyboardWindow.xaml.cs）。</summary>
    private void TopmostPollTick()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NeedsTopmostReassert(IsVisible, IsDockedAsStrip, HasTopmostBit(hwnd)))
        {
            return;
        }

        ReassertTopmost("poll-500ms");
    }
}
