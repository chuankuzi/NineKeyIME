// 本文件职责：系统级设置读写，包括开机自启、系统触摸键盘互斥与进程提权操作。
// 数据流位置：托盘菜单/设置页调用 → 读写注册表或启动新进程 → 系统生效。
// ⚠ 坑 1：HKCU Run 键写入的是当前可执行文件路径，若程序移动位置会失效。
// ⚠ 坑 2：RestartElevated 只负责启动新进程，调用方必须随后关闭旧进程，否则会出现双实例（§0.3）。
// ⚠ 坑 3：TabletTip 注册表项不存在时 CreateSubKey 会自动创建，不影响系统默认行为。
// 相关规格：§0.3、§0.5、§2.7。

using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace NineKey.Keyboard.Services;

/// <summary>
/// 系统级设置：开机自启（HKCU Run 键，§2.7）、系统触摸键盘互斥（§0.5）、
/// 当前进程提权检测与提权重启（§0.3）。
/// </summary>
public static class SystemSettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NineKeyIME";
    private const string TabletTipKeyPath = @"Software\Microsoft\TabletTip\1.7";
    private const string AutoInvokeValue = "EnableDesktopModeAutoInvoke";

    /// <summary>查询是否已写入 HKCU Run 开机自启项。</summary>
    public static bool IsRunAtStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    /// <summary>写入或删除 HKCU Run 开机自启项。</summary>
    public static void SetRunAtStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>§0.5：写入 EnableDesktopModeAutoInvoke=0 防止系统触摸键盘与本产品双弹。</summary>
    public static void SetDisableSystemTouchKeyboard(bool disabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(TabletTipKeyPath);
        if (disabled)
        {
            key.SetValue(AutoInvokeValue, 0, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(AutoInvokeValue, throwOnMissingValue: false);
        }
    }

    /// <summary>判断当前进程是否以管理员身份运行（UIPI 判定用）。</summary>
    public static bool IsCurrentProcessElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>§0.3：以管理员身份重启自身（UAC 确认一次），由调用方随后关闭本进程。</summary>
    /// <remarks>
    /// ⚠ 坑（批 2026-10-05 修）：必须带 <c>--takeover</c>。旧进程此刻仍持单实例锁，新进程若按普通路径启动
    /// 会看到 createdNew=false 直接退出 ⇒ 用户点"以管理员运行"后**两个都没了**。带 --takeover 后新进程会
    /// 等旧进程退出（锁转 abandoned）再接管，交接不再靠竞态。
    /// </remarks>
    public static bool RestartElevated()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--takeover",
                Verb = "runas",
                UseShellExecute = true,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户取消 UAC
            return false;
        }
    }
}
