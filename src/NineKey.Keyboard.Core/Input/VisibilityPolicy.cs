// 本文件职责：键盘显隐决策的单一出口，把所有显隐意图收敛为 Show/Hide 两个显式动作。
// 数据流位置：热键/托盘/RawTouchWatcher 等触发 → VisibilityPolicy 返回 VisAction → KeyboardWindow 执行。
// ⚠ 坑 1：焦点变化、点击外部、托盘菜单开闭均不得触发隐藏，否则窗口会反复闪（§13.24）。
// ⚠ 坑 2：显隐必须是显式动作枚举，不能直接用 bool，否则后续扩展生命周期状态会混乱。
// 相关规格：§13.24。

namespace NineKey.Keyboard.Input;

/// <summary>可见性动作（§13.24：生命周期显式化，只保留 Show / Hide）。</summary>
public enum VisAction
{
    Show,
    Hide,
}

/// <summary>
/// 键盘显隐决策单一出口（§13.24 显式生命周期）。
/// 弹出：手动唤出 / 触摸点击可编辑区（由 RawTouchWatcher 触发）。
/// 隐藏：仅隐藏按钮。
/// 焦点变化、点击外部、托盘菜单开闭均不影响显隐。
/// </summary>
public sealed class VisibilityPolicy
{
    /// <summary>请求显示键盘（手动唤出或点击可编辑区）。</summary>
    public VisAction Show() => VisAction.Show;

    /// <summary>请求隐藏键盘（仅由隐藏按钮触发）。</summary>
    public VisAction Hide() => VisAction.Hide;
}
