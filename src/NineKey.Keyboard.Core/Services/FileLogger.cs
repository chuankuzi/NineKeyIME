// 本文件职责：极简滚动文件日志，所有异常与运行事件统一落盘。
// 数据流位置：各服务/控制器遇到异常或关键状态时调用 Info/Error → 写入本地日志文件。
// ⚠ 坑 1：日志写入必须吞掉所有异常，日志失败不能拖垮输入主流程。
// ⚠ 坑 2：文件超 512KB 直接删除重建而非滚动，避免日志无限膨胀（M6）。
// 相关规格：M6。

using System.IO;
using System.Text;

namespace NineKey.Keyboard.Services;

/// <summary>
/// 极简滚动文件日志（M6 交付物）：%LocalAppData%\NineKeyIME\logs\ninekey.log。
/// 单文件追加，超 512KB 截断重写，失败静默（日志永不拖垮主流程）。
/// </summary>
public static class FileLogger
{
    private static readonly object Gate = new();
    private static string? _path;

    private static string PathForFile
    {
        get
        {
            if (_path is null)
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NineKeyIME", "logs");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "ninekey.log");
            }

            return _path;
        }
    }

    /// <summary>写入 INFO 级别日志。</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>写入 ERROR 级别日志，可附带异常对象。</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : message + " | " + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(PathForFile);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    File.Delete(PathForFile);
                }

                File.AppendAllText(PathForFile,
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // ⚠ 坑：日志失败必须静默，不能影响输入主流程。
        }
    }
}
