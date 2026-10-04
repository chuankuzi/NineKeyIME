// 本文件职责：轮询 UIA 焦点元素，判定当前是否为可编辑输入框并触发键盘自动弹出。
// 数据流位置：AutomationElement.FocusedElement → IsEditable → EditableFocusChanged → 宿主控制显示。
// ⚠ 坑 1：UIA 可能抛 COMException / ElementNotAvailableException，必须吞掉异常避免轮询崩溃（§13.16）。
// ⚠ 坑 2：去抖只按元素 RuntimeId + 判定结果缓存，宿主手动隐藏后必须 ResetDebounce 才能再次弹出（P1-11）。
// ⚠ 坑 3：无 UIA 信息的自绘输入框检测不到，与系统触摸键盘同处境，不单独兼容（§2.6）。
// 相关规格：§2.6、§13.16、P1-11。

using System.Windows.Automation;

namespace NineKey.Keyboard.Services;

/// <summary>
/// UIA 焦点监听（§2.6 自动弹出 + §13.16 误报控制）：300ms 轮询 AutomationElement.FocusedElement。
/// 保守判定：仅 ControlType ∈ {Edit, Document} 且启用的元素才判可编辑；可疑（TextPattern 复合控件）默认不弹。
/// 去抖：同一元素同一判定不重复上报；W9/手动隐藏后由宿主 ResetDebounce 允许重新弹出（兼容 P1-11）。
/// 已知边界（§2.6）：无 UIA 信息的自绘输入框检测不到，与系统触摸键盘同一处境，不做兼容。
/// </summary>
public sealed class FocusWatcher : IDisposable
{
    private const int PollIntervalMs = 300;

    private System.Threading.Timer? _timer;
    private string? _lastKey;
    private bool _lastReportedEditable;

    /// <summary>焦点可编辑性判定上报（去抖后）。在线程池线程触发，订阅方需自行调度到 UI 线程。</summary>
    public event Action<bool>? EditableFocusChanged;

    /// <summary>启动 300ms 轮询，监听焦点元素可编辑性变化。</summary>
    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
    }

    /// <summary>停止轮询并释放定时器。</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>宿主隐藏键盘后调用：允许同一元素重新判定弹出（P1-11 与 §13.16 去抖的调和点）。
    /// 同时清空可编辑状态，确保去抖路径不会绕过下一次 IsEditable 判定（P1-30）。</summary>
    public void ResetDebounce()
    {
        _lastKey = null;
        _lastReportedEditable = false;
    }

    /// <summary>停止轮询并释放资源。</summary>
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void Poll()
    {
        AutomationElement? el;
        bool editable;
        string key;
        try
        {
            el = AutomationElement.FocusedElement;
            if (el is null)
            {
                return;
            }

            editable = IsEditable(el);
            key = el.GetRuntimeId() is int[] id ? string.Join(',', id) : string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (key == _lastKey && editable == _lastReportedEditable)
        {
            return; // §13.16 去抖：同一元素同一判定不重复弹
        }

        _lastKey = key;
        _lastReportedEditable = editable;

        EditableFocusChanged?.Invoke(editable);
    }

    /// <summary>§13.16 保守判定：仅 Edit/Document 且启用；TextPattern 复合控件等可疑元素一律不弹。</summary>
    internal static bool IsEditable(AutomationElement el)
    {
        var ct = el.Current.ControlType;
        if (ct != ControlType.Edit && ct != ControlType.Document)
        {
            return false;
        }

        try
        {
            return el.Current.IsEnabled;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }
}
