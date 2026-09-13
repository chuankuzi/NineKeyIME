// 本文件职责：自定义宏编辑器窗口，负责增删改宏、数量上限控制，以及保存时回写设置。
// 数据流位置：托盘「编辑宏」打开本窗口 → 用户编辑 → Saved 事件 → KeyboardWindow 重建宏面板并持久化。
// ⚠ 坑 1：同进程直插事件必须在窗口关闭时注销，否则 TextInjector 全局事件会残留死引用。
// ⚠ 坑 2：宏列表在构造时深拷贝，保存前才清空/截断/trim，避免中途污染 Settings。
// ⚠ 坑 3：同进程注入回调可能在非 UI 线程触发，必须用 Dispatcher.Invoke 操作 TextBox。
// 相关规格：§13.28、§13.5（同进程注入）、§W5。

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NineKey.Keyboard.Services;
using NineKey.Keyboard.Settings;

namespace NineKey.Keyboard.Views;

/// <summary>
/// 自定义宏编辑器窗口（§13.28）：增删改宏，上限 8 个，保存时触发 Saved 事件回写设置。
/// </summary>
public partial class MacroEditorWindow : Window
{
    public const int MaxMacros = 8;
    private readonly ObservableCollection<Macro> _macros = [];

    /// <summary>用户点击保存时触发，参数为清理后的宏列表。</summary>
    public event Action<IReadOnlyList<Macro>>? Saved;

    /// <summary>用户点击取消时触发。</summary>
    public event Action? Cancelled;

    /// <summary>
    /// 创建宏编辑器，拷贝传入列表避免直接修改 Settings。
    /// </summary>
    /// <param name="macros">当前已保存的宏列表。</param>
    public MacroEditorWindow(IReadOnlyList<Macro> macros)
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            FileLogger.Error("MacroEditorWindow 初始化失败", ex);
            throw;
        }

        // ⚠ 坑：TextInjector 全局事件是静态的，忘记在 OnClosed 注销会导致窗口对象无法释放。
        TextInjector.SameProcessTextInjecting += OnSameProcessTextInject;
        TextInjector.SameProcessKeyInjecting += OnSameProcessKeyInject;

        try
        {
            // ⚠ 坑：拷贝而非直接引用原列表，取消/异常时不会污染 Settings 里的宏。
            foreach (var m in macros)
            {
                _macros.Add(new Macro { Name = m.Name, ActionType = m.ActionType, Content = m.Content });
            }

            MacroItems.ItemsSource = _macros;
            UpdateLimitText();
        }
        catch (Exception ex)
        {
            FileLogger.Error("MacroEditorWindow 加载宏列表失败", ex);
        }
    }

    /// <summary>同进程文本直插：绕过 SendInput/TSF，直接写入焦点 WPF 文本框（必须在 UI 线程执行）。</summary>
    /// <param name="text">要插入的文本。</param>
    private bool OnSameProcessTextInject(string text)
    {
        return Dispatcher.Invoke(() =>
        {
            try
            {
                if (System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.TextBox tb)
                {
                    return false;
                }

                var caret = tb.CaretIndex;
                tb.Text = tb.Text.Insert(caret, text);
                tb.CaretIndex = caret + text.Length;
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Error($"MacroEditorWindow 同进程文本直插失败: {text}", ex);
                return false;
            }
        });
    }

    /// <summary>同进程按键直插：退格/空格/回车等直接操作焦点 WPF 文本框（必须在 UI 线程执行）。</summary>
    /// <param name="kind">按键种类。</param>
    private bool OnSameProcessKeyInject(SameProcessKeyKind kind)
    {
        return Dispatcher.Invoke(() =>
        {
            try
            {
                if (System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.TextBox tb)
                {
                    return false;
                }

                var caret = tb.CaretIndex;
                switch (kind)
                {
                    case SameProcessKeyKind.Backspace:
                        if (caret > 0 && tb.Text.Length > 0)
                        {
                            tb.Text = tb.Text.Remove(caret - 1, 1);
                            tb.CaretIndex = caret - 1;
                        }

                        return true;

                    case SameProcessKeyKind.Space:
                        tb.Text = tb.Text.Insert(caret, " ");
                        tb.CaretIndex = caret + 1;
                        return true;

                    case SameProcessKeyKind.Enter:
                        if (tb.AcceptsReturn)
                        {
                            tb.Text = tb.Text.Insert(caret, Environment.NewLine);
                            tb.CaretIndex = caret + Environment.NewLine.Length;
                        }

                        return true;

                    case SameProcessKeyKind.Delete:
                        if (caret < tb.Text.Length)
                        {
                            tb.Text = tb.Text.Remove(caret, 1);
                        }

                        return true;

                    case SameProcessKeyKind.Left:
                        if (caret > 0)
                        {
                            tb.CaretIndex = caret - 1;
                        }

                        return true;

                    case SameProcessKeyKind.Right:
                        if (caret < tb.Text.Length)
                        {
                            tb.CaretIndex = caret + 1;
                        }

                        return true;

                    case SameProcessKeyKind.Up:
                    case SameProcessKeyKind.Down:
                        // 单行 TextBox 无行概念；多行框由 SendInput 路径处理
                        return false;

                    case SameProcessKeyKind.Esc:
                        // Esc 交给窗口默认处理（关闭对话框/菜单）
                        return false;

                    case SameProcessKeyKind.Tab:
                        // Tab 焦点移动交给 WPF 默认焦点路由
                        return false;
                }

                return false;
            }
            catch (Exception ex)
            {
                FileLogger.Error($"MacroEditorWindow 同进程按键直插失败: {kind}", ex);
                return false;
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        TextInjector.SameProcessTextInjecting -= OnSameProcessTextInject;
        TextInjector.SameProcessKeyInjecting -= OnSameProcessKeyInject;
        base.OnClosed(e);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FocusNameBoxAt(_macros.Count > 0 ? 0 : -1);
    }

    private void FocusNameBoxAt(int index)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (index < 0)
                {
                    // 无宏时聚焦新增按钮，新增后再聚焦名称框
                    System.Windows.Input.Keyboard.Focus(AddButton);
                    return;
                }

                if (MacroItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                {
                    return;
                }

                var nameBox = FindVisualChild<System.Windows.Controls.TextBox>(container);
                if (nameBox is not null)
                {
                    System.Windows.Input.Keyboard.Focus(nameBox);
                    nameBox.CaretIndex = nameBox.Text.Length;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error("MacroEditorWindow 聚焦名称框失败", ex);
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
            {
                return t;
            }

            var result = FindVisualChild<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>动作类型下拉选项（绕过 XAML x:Array 在部分文化环境下的解析异常）。</summary>
    public IReadOnlyList<MacroActionType> ActionTypes { get; } =
        [MacroActionType.Text, MacroActionType.Shortcut];

    /// <summary>当前编辑器中的宏列表（保存前已被清理/截断/trim）。</summary>
    public IReadOnlyList<Macro> Result => _macros.ToList();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_macros.Count >= MaxMacros)
            {
                return;
            }

            _macros.Add(new Macro { Name = "新宏", ActionType = MacroActionType.Text, Content = string.Empty });
            UpdateLimitText();
            FocusNameBoxAt(_macros.Count - 1);
        }
        catch (Exception ex)
        {
            FileLogger.Error("MacroEditorWindow 新增宏失败", ex);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: Macro macro })
            {
                _macros.Remove(macro);
                UpdateLimitText();
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("MacroEditorWindow 删除宏失败", ex);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            for (var i = _macros.Count - 1; i >= 0; i--)
            {
                var m = _macros[i];
                // ⚠ 坑：名称和内容全空的项必须剔除，否则宏面板会显示空白按钮。
                if (string.IsNullOrWhiteSpace(m.Name) && string.IsNullOrWhiteSpace(m.Content))
                {
                    _macros.RemoveAt(i);
                    continue;
                }

                // ⚠ 坑：宏面板按钮宽度有限，名称硬截到 4 字避免布局溢出。
                m.Name = m.Name.Trim();
                if (m.Name.Length > 4)
                {
                    m.Name = m.Name[..4];
                }

                m.Content = m.Content.Trim();
            }

            Saved?.Invoke(Result);
            Close();
        }
        catch (Exception ex)
        {
            FileLogger.Error("MacroEditorWindow 保存宏失败", ex);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Cancelled?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            FileLogger.Error("MacroEditorWindow 取消失败", ex);
        }
    }

    private void UpdateLimitText()
    {
        try
        {
            LimitText.Text = $"已用 {_macros.Count}/{MaxMacros}";
            AddButton.IsEnabled = _macros.Count < MaxMacros;
        }
        catch (Exception ex)
        {
            FileLogger.Error("MacroEditorWindow 更新限制文本失败", ex);
        }
    }
}
