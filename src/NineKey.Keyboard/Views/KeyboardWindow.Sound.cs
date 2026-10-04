// 本文件职责：KeyboardWindow 的按键音（托盘开关 + 按键按下播短促提示音）。
// 数据流位置：各按键处理器 → PlayKeyClick → SoundPlayer（内嵌 assets/keyclick.wav，40ms 自产提示音）。
// ⚠ 坑 1：关=零开销——开关关闭时只做一次 bool 判断，连播放器都不创建。
// ⚠ 坑 2：发声失败（无声卡/资源缺失/COM 异常）必须静默吞，绝不影响输入链路。
// ⚠ 坑 3：退格连删（RepeatButton Interval=70ms）按 70ms 限频，避免连续爆音。
// ⚠ 坑 4：SoundPlayer 绑流时必须先 Load() 缓存，否则每次 Play 都从流尾读→后续静音。

using System.Media;
using NineKey.Keyboard.Services;

namespace NineKey.Keyboard.Views;

/// <summary>KeyboardWindow 按键音部分：内嵌 wav 播放与限频。</summary>
public partial class KeyboardWindow
{
    /// <summary>连发限频窗口（毫秒）：与退格 RepeatButton Interval=70 对齐。</summary>
    private const int KeyClickMinIntervalMs = 70;

    private SoundPlayer? _keyClickPlayer;
    private DateTime _lastKeyClickAt = DateTime.MinValue;

    /// <summary>键按下统一入口：开关关 → 立即返回（零开销）；否则限频播放一次。</summary>
    private void PlayKeyClick()
    {
        if (!_settings.KeyClickSound)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastKeyClickAt).TotalMilliseconds < KeyClickMinIntervalMs)
        {
            return;
        }

        _lastKeyClickAt = now;
        try
        {
            if (_keyClickPlayer is null)
            {
                var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("keyclick.wav");
                if (stream is null)
                {
                    return; // 资源缺失：静默降级（表现为无声，不影响输入）
                }

                // ⚠ 坑：绑流必须先 Load() 把音频缓存进内存，否则第二次 Play 从流尾读→静音。
                _keyClickPlayer = new SoundPlayer(stream);
                _keyClickPlayer.Load();
            }

            // ⚠ 用 Play()（内部后台线程）而非 PlaySync()：40ms 同步播放会阻塞 UI 输入链路。
            _keyClickPlayer.Play();
        }
        catch (Exception)
        {
            // 发声失败静默吞：绝不影响输入。
        }
    }

    private void SetKeyClickSound(bool on) => _settings.KeyClickSound = on;
}