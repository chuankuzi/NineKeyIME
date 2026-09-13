// 本文件职责：编辑器类名白名单维护窗口，把换行分隔的类名字串解析成列表。
// 数据流位置：托盘「编辑白名单」打开本窗口 → 保存后回写 AppSettings → RawTouchWatcher 依据白名单判断是否自动弹出键盘。
// ⚠ 坑 1：空行/空白类名必须过滤，否则白名单匹配会意外命中空串。
// ⚠ 坑 2：Windows 窗口类名大小写不敏感，但调用方目前按原串比较，编辑时应提醒用户不要依赖大小写。
// ⚠ 坑 3：取消后 Saved 仍为 false，调用方必须检查 Saved 再回写 Settings，否则未保存的编辑会覆盖原列表。
// 相关规格：§13.29、§13.24（自动弹出）、§M7-4。

using System.Windows;

namespace NineKey.Keyboard.Views;

/// <summary>§13.29 编辑器类名白名单维护窗口。</summary>
public partial class EditorWhitelistWindow : Window
{
    /// <summary>
    /// 创建白名单编辑器。
    /// </summary>
    /// <param name="whitelist">当前白名单类名列表，多个类名会按换行拼接进文本框。</param>
    public EditorWhitelistWindow(IReadOnlyList<string> whitelist)
    {
        InitializeComponent();
        ClassNameBox.Text = string.Join(Environment.NewLine, whitelist);
        ClassNameBox.Focus();
    }

    /// <summary>保存后的白名单列表；未保存时为传入的原列表。</summary>
    public IReadOnlyList<string> Whitelist { get; private set; } = [];

    /// <summary>是否点击过保存；调用方必须检查此标志再回写 Settings。</summary>
    public bool Saved { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // ⚠ 坑：三种换行符都要兼容，Windows 文本框粘贴 Unix/Mac 换行会混用。
        Whitelist = ClassNameBox.Text
            .Split([Environment.NewLine, "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Saved = false;
        Close();
    }
}
