// 本文件职责：候选栏控件，渲染当前页候选或历史上屏，处理点击与翻页事件。
// 数据流位置：KeyController 状态变化 → KeyboardWindow 调用 Render → CandidatesPanel 生成按钮 → 点击回调 CommitCandidate。
// ⚠ 坑 1：每次 Render 必须清空 Children，否则翻页/状态变化会重复叠加按钮。
// ⚠ 坑 2：MouseUp 丢失时按钮可能残留鼠标捕获，提交后必须显式释放，否则后续点击被吞。
// ⚠ 坑 3：空输入时显示历史上屏，但历史上屏不走引擎排名，直接按最近上屏顺序排列。
// 相关规格：§2.3、§13.5、§M8-5。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NineKey.Core.Engine;
using NineKey.Keyboard.Input;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace NineKey.Keyboard.Views;

/// <summary>候选栏：横向候选 + 翻页。空输入串时显示历史上屏。</summary>
public partial class CandidateBar : UserControl
{
    public CandidateBar()
    {
        InitializeComponent();
    }

    /// <summary>用户点击某个候选时触发。</summary>
    public event Action<Candidate>? CandidateClicked;

    /// <summary>用户点击下一页时触发。</summary>
    public event Action? PageNext;

    /// <summary>用户点击上一页时触发。</summary>
    public event Action? PagePrevious;

    public Brush BarBackground
    {
        get => (Brush)GetValue(BarBackgroundProperty);
        set => SetValue(BarBackgroundProperty, value);
    }

    public static readonly DependencyProperty BarBackgroundProperty =
        DependencyProperty.Register(nameof(BarBackground), typeof(Brush), typeof(CandidateBar),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2E))));

    public Brush SubForeground
    {
        get => (Brush)GetValue(SubForegroundProperty);
        set => SetValue(SubForegroundProperty, value);
    }

    public static readonly DependencyProperty SubForegroundProperty =
        DependencyProperty.Register(nameof(SubForeground), typeof(Brush), typeof(CandidateBar),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))));

    /// <summary>
    /// 根据 KeyController 当前状态重绘候选栏。
    /// </summary>
    /// <param name="controller">输入控制器，提供候选、翻页、历史上屏。</param>
    public void Render(KeyController controller)
    {
        // ⚠ 坑：不清空会累积旧候选，翻页时尤其明显。
        CandidatesPanel.Children.Clear();

        if (controller.IsEmpty)
        {
            foreach (var word in controller.History)
            {
                CandidatesPanel.Children.Add(MakeCandidateButton(new Candidate(word, CandidateSource.FullMatch, 0), isHistory: true));
            }

            CountLabel.Text = controller.History.Count > 0 ? "历史" : string.Empty;
            return;
        }

        foreach (var candidate in controller.CurrentPage)
        {
            CandidatesPanel.Children.Add(MakeCandidateButton(candidate, isHistory: false));
        }

        CountLabel.Text = controller.TotalCount > 0
            ? $"{controller.PageIndex + 1}/{controller.PageCount} ({controller.TotalCount})"
            : "无候选";
    }

    /// <summary>
    /// 创建一个候选按钮。
    /// </summary>
    /// <param name="candidate">候选对象。</param>
    /// <param name="isHistory">true 表示来自历史上屏，false 表示引擎候选。</param>
    private Button MakeCandidateButton(Candidate candidate, bool isHistory)
    {
        var btn = new Button
        {
            Content = candidate.Text,
            Style = (Style)FindResource("CandidateButtonStyle"),
        };
        if (!isHistory && candidate.Source == CandidateSource.JianpinMatch)
        {
            btn.ToolTip = "简拼匹配";
        }

        if (!isHistory && candidate.Source == CandidateSource.PinyinGuide)
        {
            btn.ToolTip = candidate.Pinyin is null ? "撤销拼音锁定，显示全部候选" : "点选此拼音组合，收窄候选";
            btn.Foreground = System.Windows.Media.Brushes.DodgerBlue;
            btn.FontWeight = FontWeights.Bold;
        }

        btn.Click += (_, _) =>
        {
            // ⚠ 坑：合成输入或异常路径会丢失 MouseUp，导致捕获残留，提交后必须显式释放。
            if (ReferenceEquals(Mouse.Captured, btn))
            {
                btn.ReleaseMouseCapture();
            }

            CandidateClicked?.Invoke(candidate);
        };
        return btn;
    }

    private void NextPage_Click(object sender, RoutedEventArgs e) => PageNext?.Invoke();

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => PagePrevious?.Invoke();
}
