// 本文件职责：单实例守卫——互斥判定 / 提权交接（--takeover）/ 唤醒已有实例 / 全分支可见日志。
// 数据流位置：Program.Main 最先调用；非属主 → 唤醒已有实例后退出；提权实例等旧实例释放后接管。
// ⚠ 坑 1（原实现的洞，2026-10-05 修）：提权重启是**竞态**——旧进程刚 Process.Start(runas) 就 Close()，
//          新进程启动时旧进程仍持锁，于是 createdNew=false 直接自杀 ⇒ 用户点"以管理员运行"后**两个都没了**。
//          修法：提权实例带 --takeover，用 WaitOne 等旧实例释放（旧实例退出后锁变 abandoned，
//          AbandonedMutexException 也算取得），并且**一直持有**到进程退出。
// ⚠ 坑 2：跨完整性不对称——High 先建锁时，Medium 的 new Mutex 会抛 UnauthorizedAccessException。
//          这必须**当成"已有实例"**处理，绝不能让异常冒泡成崩溃（原实现无 try/catch）。
// ⚠ 坑 3：失败分支一律写日志，并附其它 NineKey.Host 进程 pid 清单——"到底有没有多开"要能自证，
//          不靠肉眼与猜测（用户报"多开"时，日志第一行就能定性）。
// ⚠ 坑 4：本锁是 Local\（会话级）——多用户/多会话各能起一个，这是有意为之（托盘/热键属于各自会话）。

using System.Diagnostics;
using System.Threading;

namespace NineKey.Keyboard.Services;

/// <summary>单实例守卫：判定/交接/唤醒，全分支留日志。属主需持有到进程退出（Dispose 释放）。</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>默认互斥名（会话级）。</summary>
    public const string DefaultMutexName = @"Local\NineKeyIME.SingleInstance";

    /// <summary>唤醒已有实例的自定义窗口消息名（两个进程各自 RegisterWindowMessage 同一字符串）。</summary>
    public const string ShowExistingMessageName = "NineKeyIME.ShowExisting";

    /// <summary>主窗口标题（FindWindow 用；与 KeyboardWindow.xaml 的 Title 一致）。</summary>
    public const string MainWindowTitle = "NineKey 九宫格输入法";

    private readonly string _name;
    private Mutex? _mutex;

    private SingleInstanceGuard(string name) => _name = name;

    /// <summary>本进程是否持有锁（true = 应当继续启动）。</summary>
    public bool IsOwner { get; private set; }

    /// <summary>是否检测到已有实例（含"访问被拒"与"锁被占用"两种来源）。</summary>
    public bool AnotherInstanceRunning { get; private set; }

    /// <summary>是否通过 --takeover 从旧实例手里接管（提权重启路径）。</summary>
    public bool TookOver { get; private set; }

    /// <summary>本进程启动时观察到的其它 NineKey.Host 进程 pid（自证用）。</summary>
    public IReadOnlyList<int> OtherProcessIds { get; private set; } = [];

    /// <summary>
    /// 取得单实例守卫。takeover=true 表示"允许等旧实例释放后接管"（提权重启专用）。
    /// name 仅供单测注入唯一名，生产走 <see cref="DefaultMutexName"/>。
    /// </summary>
    public static SingleInstanceGuard Acquire(bool takeover, TimeSpan? takeoverWait = null, string? name = null)
    {
        var guard = new SingleInstanceGuard(name ?? DefaultMutexName);
        guard.AcquireCore(takeover, takeoverWait ?? TimeSpan.FromSeconds(6));
        return guard;
    }

    private void AcquireCore(bool takeover, TimeSpan wait)
    {
        OtherProcessIds = ProbeOtherProcesses();
        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, _name, out createdNew);
        }
        catch (UnauthorizedAccessException ex)
        {
            // ⚠ 跨完整性：High 实例先建锁 → 本进程（Medium）无权打开。这就是"已有实例"，不是崩溃理由。
            AnotherInstanceRunning = true;
            Log($"mutex denied ({ex.GetType().Name}) → treat as another instance running");
            return;
        }
        catch (Exception ex)
        {
            // 其它创建失败（例如名字非法）：宁可放行启动，也不要静默不启动。
            Log($"mutex create failed ({ex.GetType().Name}: {ex.Message}) → continue as owner");
            IsOwner = true;
            return;
        }

        if (createdNew)
        {
            IsOwner = true;
            Log("acquired (createdNew=true)");
            return;
        }

        if (takeover)
        {
            // 等旧实例释放：它退出后锁变 abandoned，AbandonedMutexException 表示"我们拿到了"。
            try
            {
                IsOwner = _mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                IsOwner = true;
            }

            TookOver = IsOwner;
            Log(IsOwner ? $"takeover acquired (waited ≤{wait.TotalSeconds:F0}s)" : $"takeover timed out after {wait.TotalSeconds:F0}s");
            if (!IsOwner)
            {
                AnotherInstanceRunning = true;
            }

            return;
        }

        AnotherInstanceRunning = true;
        Log("mutex already exists (createdNew=false) → another instance running");
    }

    /// <summary>唤醒已有实例：发自定义消息让它把自己显示出来并重断言置顶（不抢焦点）。</summary>
    public static bool TryActivateExisting()
    {
        try
        {
            var msg = NativeMethods.RegisterWindowMessageW(ShowExistingMessageName);
            var hwnd = NativeMethods.FindWindowW(null, MainWindowTitle);
            if (msg == 0 || hwnd == 0)
            {
                FileLogger.Info($"single-instance: cannot wake existing (msg={msg} hwnd=0x{hwnd.ToInt64():X})");
                return false;
            }

            var ok = NativeMethods.PostMessageW(hwnd, msg, 0, 0);
            FileLogger.Info($"single-instance: asked existing instance hwnd=0x{hwnd.ToInt64():X} to show (ok={ok})");
            return ok;
        }
        catch (Exception ex)
        {
            FileLogger.Error("single-instance: wake existing failed", ex);
            return false;
        }
    }

    /// <summary>本机其它 NineKey.Host 进程（不含自己）——多开自证。</summary>
    private static IReadOnlyList<int> ProbeOtherProcesses()
    {
        try
        {
            return Process.GetProcessesByName("NineKey.Host")
                .Select(p =>
                {
                    var id = p.Id;
                    p.Dispose();
                    return id;
                })
                .Where(id => id != Environment.ProcessId)
                .ToList();
        }
        catch (Exception ex)
        {
            FileLogger.Error("single-instance: process probe failed", ex);
            return [];
        }
    }

    private void Log(string what) =>
        FileLogger.Info($"single-instance: pid={Environment.ProcessId} {what} " +
            $"others=[{string.Join(",", OtherProcessIds)}] owner={IsOwner} another={AnotherInstanceRunning}");

    public void Dispose()
    {
        // ⚠ 坑：必须最后才释放——提权交接靠"旧实例退出→新实例 WaitOne 拿到"，提前释放会让两个实例并存。
        _mutex?.Dispose();
        _mutex = null;
    }
}
