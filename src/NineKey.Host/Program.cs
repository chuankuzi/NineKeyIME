using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using NineKey.Core.Dictionary;
using NineKey.Core.Engine;
using NineKey.Keyboard;
using NineKey.Keyboard.Services;
using NineKey.Keyboard.Settings;
using NineKey.Keyboard.Views;

namespace NineKey.Host;

// 本文件职责：NineKey 宿主启动器：单实例锁、词库加载、引擎装配、主窗口运行。
// 数据流位置：exe 启动 → Program.Main → SingleInstanceGuard / LexiconLoader / QueryEngine / KeyboardWindow → WPF 消息循环。
// ⚠ 坑 1：InvariantGlobalization 关闭后 'en-us' 文化表被剥离，必须显式固定 zh-CN，否则 WPF 绑定异常。
// ⚠ 坑 2：单实例的判定/交接/唤醒全在 SingleInstanceGuard（批 2026-10-05 重写：原实现有提权交接竞态、
//          跨完整性崩溃、失败分支全静默三处洞）。
// ⚠ 坑 3：找不到词库时直接弹 MessageBox 并退出，不能继续运行（否则引擎查询空索引）。
// 相关规格：§2.1、§2.3、§13.5、§W5。

/// <summary>装配：单实例锁 → 词库加载 → 引擎 → 主窗口。</summary>
public static class Program
{
    /// <summary>
    /// 程序入口：设置中文环境、确保单实例、加载词库与用户词典、装配引擎并启动主窗口。
    /// </summary>
    /// <returns>进程退出码；0 表示正常，1 表示词库缺失。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // ⚠ 坑：InvariantGlobalization 已关闭；显式固定中文环境，避免 WPF 绑定回退到被剥离的 'en-us' 文化表。
        var zhCn = new CultureInfo("zh-CN");
        CultureInfo.CurrentCulture = zhCn;
        CultureInfo.CurrentUICulture = zhCn;

        // 单实例：--takeover 由提权重启路径传入（旧实例仍在持锁，必须等它释放再接管，否则"两个都没了"）。
        // 非属主时**唤醒已有实例**再退出——第二次启动不该像什么都没发生（批 2026-10-05 修）。
        var takeover = args.Any(a => string.Equals(a, "--takeover", StringComparison.OrdinalIgnoreCase));
        using var singleInstance = SingleInstanceGuard.Acquire(takeover);
        if (!singleInstance.IsOwner)
        {
            _ = SingleInstanceGuard.TryActivateExisting();
            return 0;
        }

        // ⚠ 坑：找不到词库必须立即退出，否则引擎持有空索引，后续查询全部无候选。
        var lexiconPath = FindLexicon();
        if (lexiconPath is null)
        {
            _ = MessageBox.Show(
                "未找到词库文件 dict\\lexicon.bin.gz。\n请先运行: dotnet run --project tools/NineKey.DictBuilder",
                "NineKey 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var lexicon = LexiconLoader.Load(lexiconPath);
        sw.Stop();
        Trace.WriteLine($"lexicon loaded: {lexicon.EntryCount} entries in {sw.ElapsedMilliseconds}ms");

        var userDict = new UserDictionary();
        userDict.Load();

        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            FileLogger.Error($"dispatcher-unhandled: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            FileLogger.Error($"appdomain-unhandled: {e.ExceptionObject}");
        };

        var settings = AppSettings.Load();
        var engine = new QueryEngine(lexicon, new Ranker(), userDict, settings.ToFuzzyProfile());
        var window = new KeyboardWindow(engine, userDict, settings)
        {
            ShowActivated = false, // W5 不抢焦点：显示但不激活
        };

        app.Run(window);
        return 0;
    }

    private static string? FindLexicon()
    {
        // ⚠ 坑：发布形态与仓库内 dotnet run 的目录深度不同，向上找 6 级覆盖常见输出路径；不够会启动失败。
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "dict", "lexicon.bin.gz"),
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            dir = dir.Parent;
            if (dir is not null)
            {
                candidates.Add(Path.Combine(dir.FullName, "dict", "lexicon.bin.gz"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
