// 本文件职责：KeyboardWindow 的句子记忆接线（批11）——3 秒停顿成句结算、开关装配、清空入口。
// 数据流位置：AppSettings.SentenceMemoryEnabled → 控制器句库 Enabled；按键空闲 3 秒 → SettleRunBuffer。
// ⚠ 坑 1：停顿计时器只在开关打开时运行，且每键都应重置——否则"边打边结算"会把半句提前入库。
// ⚠ 坑 2：句库日志必须路由到 FileLogger（Core 不引壳层依赖），损坏/脏数据才留得下痕迹。

using System.Windows.Threading;
using NineKey.Core.Memory;
using NineKey.Keyboard.Services;

namespace NineKey.Keyboard.Views;

/// <summary>KeyboardWindow 句子记忆部分：停顿结算与开关/清空装配。</summary>
public partial class KeyboardWindow
{
    /// <summary>成句停顿阈值（毫秒）：设计段③。</summary>
    private const int SentenceIdleMs = 3000;

    private DispatcherTimer? _sentenceIdleTimer;

    /// <summary>构造期装配：句库开关同步 + 日志回调 + 停顿计时器。</summary>
    private void InitSentenceMemory()
    {
        try
        {
            _controller.Sentences = new SentenceMemory(log: msg => FileLogger.Error(msg));
            _controller.Sentences.Enabled = _settings.SentenceMemoryEnabled;

            _sentenceIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SentenceIdleMs) };
            _sentenceIdleTimer.Tick += (_, _) =>
            {
                _sentenceIdleTimer!.Stop();
                _controller.SettleRunBuffer();
            };

            PreviewKeyDown += (_, _) => RestartSentenceIdleTimer();
        }
        catch (Exception ex)
        {
            FileLogger.Error("句子记忆装配失败", ex);
        }
    }

    /// <summary>每次按键重启动停顿计时器（开关关 = 不启动，零记录）。</summary>
    private void RestartSentenceIdleTimer()
    {
        if (_sentenceIdleTimer is null || !_settings.SentenceMemoryEnabled)
        {
            return;
        }

        _sentenceIdleTimer.Stop();
        _sentenceIdleTimer.Start();
    }

    private void SetSentenceMemoryEnabled(bool on)
    {
        _settings.SentenceMemoryEnabled = on;
        _controller.Sentences.Enabled = on;
        if (!on)
        {
            _sentenceIdleTimer?.Stop();
            _controller.DiscardRunBuffer();
        }
    }

    private void ClearSentenceMemory()
    {
        try
        {
            _controller.Sentences.Clear();
            _controller.DiscardRunBuffer();
        }
        catch (Exception ex)
        {
            FileLogger.Error("清空句子记忆失败", ex);
        }
    }
}