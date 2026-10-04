// 本文件职责：应用设置的内存模型与 JSON 持久化，覆盖透明度、窗口位置、各布局缩放、宏与输入行为开关。
// 数据流位置：启动时 App.Load() 读取 → 绑定到 KeyboardWindow/设置窗口 → 修改后 Save() 写回 %LocalAppData%。
// ⚠ 坑 1：System.Text.Json 默认按属性名原样序列化，随意重命名公共属性会丢失用户旧配置。
// ⚠ 坑 2：窗口几何与缩放存的是逻辑值，多 DPI 或多显示器环境下读取方须按当前屏幕重新换算（§13.43、§13.45）。
// ⚠ 坑 3：模糊音开关只在 ToFuzzyProfile() 聚合一次，新增开关必须同步加入，否则 UI 开了引擎不生效。
// 相关规格：§2.6、§2.7、§8 M7、§13.18、§13.26、§13.28、§13.29、§13.33、§13.39、§13.40、§13.43、§13.45、§M8-1。

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NineKey.Keyboard.Settings;

/// <summary>
/// 应用设置：键盘透明度、窗口位置。JSON 持久化到 %LocalAppData%\NineKeyIME\settings.json。
/// 透明度范围 0.2~1.0（低于 0.2 键盘基本不可操作，没有意义）。
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public const double MinOpacity = 0.2;
    public const double MaxOpacity = 1.0;
    public const double DefaultOpacity = 0.85;

    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineKeyIME", "settings.json");

    private double _opacity = DefaultOpacity;
    private double _keyOpacity = DefaultKeyOpacity;

    public const double DefaultKeyOpacity = 0.95;

    /// <summary>背景不透明度（R8，0~1，默认 0.85）。</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, Math.Clamp(value, MinOpacity, MaxOpacity));
    }

    /// <summary>按键不透明度（R8，0~1，默认 0.95）。边框与字符恒不透明（始终取 1）。</summary>
    public double KeyOpacity
    {
        get => _keyOpacity;
        set => SetField(ref _keyOpacity, Math.Clamp(value, MinOpacity, MaxOpacity));
    }

    /// <summary>键盘背景色 #RRGGBB（§13.18-2）；null = 跟随系统深浅色。</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>按键背景色 #RRGGBB（§13.18-2）；null = 跟随主题。</summary>
    public string? KeyBackgroundColor { get; set; }

    private double _keyBorderWidth = 1;

    /// <summary>按键边框宽度 0~4px（§13.18-2），默认 1。</summary>
    public double KeyBorderWidth
    {
        get => _keyBorderWidth;
        set => SetField(ref _keyBorderWidth, Math.Clamp(value, 0, 4));
    }

    public double? Left { get; set; }

    public double? Top { get; set; }

    /// <summary>窗口尺寸（R1 手柄无级缩放结果）。null = 默认 360×280。
    /// 批次 12 后仅作兼容保留，实际尺寸由各模式缩放比例决定。</summary>
    public double? Width { get; set; }

    public double? Height { get; set; }

    // ⚠ 坑：各布局模式 Scale 为 null 表示首次启动，窗口须按当前工作区自动计算，不能当作 1.0 直接使用（§13.43、§13.45）。
    public double? ChineseScale { get; set; }
    public bool ChineseScaleIsManual { get; set; }
    public double? ChineseScaleWorkAreaWidth { get; set; }
    public double? ChineseScaleWorkAreaHeight { get; set; }

    public double? NumberScale { get; set; }
    public bool NumberScaleIsManual { get; set; }
    public double? NumberScaleWorkAreaWidth { get; set; }
    public double? NumberScaleWorkAreaHeight { get; set; }

    public double? EnglishScale { get; set; }
    public bool EnglishScaleIsManual { get; set; }
    public double? EnglishScaleWorkAreaWidth { get; set; }
    public double? EnglishScaleWorkAreaHeight { get; set; }

    public double? SymbolScale { get; set; }
    public bool SymbolScaleIsManual { get; set; }
    public double? SymbolScaleWorkAreaWidth { get; set; }
    public double? SymbolScaleWorkAreaHeight { get; set; }

    /// <summary>自动弹出（§2.6/W10）：焦点进入可编辑控件时自动显示键盘。默认开（W10 修订，与系统触摸键盘行为一致）。</summary>
    public bool AutoPopup { get; set; } = true;

    /// <summary>空格上屏首选：缓冲非空时空格提交首选候选，缓冲为空时空格正常上屏（手机输入法标点顶屏同源的标配行为）。默认开。</summary>
    public bool SpaceCommitsCandidate { get; set; } = true;

    /// <summary>按键音：按键按下播 40ms 短促提示音（默认关，托盘「输入设置→按键音」切换）。</summary>
    public bool KeyClickSound { get; set; }

    /// <summary>§13.31 简繁输出：true=上屏时转繁体（OpenCC s2t，托盘"其他→简繁输出"切换）。</summary>
    public bool TraditionalOutput { get; set; }

    /// <summary>全局热键 Ctrl+Alt+K 显隐（§2.7）。默认开。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>开机自启（HKCU Run 键，§2.7）。默认关。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>以管理员身份运行（§0.3）：开启后重启自身为提权（UAC 确认一次）。默认关。</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>禁止系统触摸键盘自动弹出（§0.5），防止双弹。默认关。</summary>
    public bool DisableSystemTouchKeyboard { get; set; }

    /// <summary>快捷键行显示开关（§13.26），默认开。</summary>
    public bool ShowShortcutBar { get; set; } = true;

    /// <summary>宏键行显示开关（§13.28），默认开。</summary>
    public bool ShowMacroBar { get; set; } = true;

    /// <summary>26 键编辑行显示开关（§13.39），默认开。</summary>
    public bool ShowEditRow { get; set; } = true;

    /// <summary>26 键符号行显示开关（§13.40-S1），默认开。</summary>
    public bool ShowSymbolRow { get; set; } = true;
	
	public bool ShowNumberRow { get; set; } = true;
	
	public bool SymbolRowFullWidth { get; set; }

    /// <summary>最近使用的符号（MRU，最多 8 个）：1 键符号选框"最近"行的数据源，点选符号后更新并立即落盘。</summary>
    public List<string> RecentSymbols { get; set; } = [];

    /// <summary>自定义宏列表（§13.28），上限 8 个。</summary>
    public List<Macro> Macros { get; set; } = [];

    /// <summary>气泡延迟 T1（§13.33），默认 150ms。</summary>
    //public int BubbleDelayMs { get; set; } = 150;

    /// <summary>气泡显示 T2（§13.33），默认 200ms。</summary>
    //public int BubbleShowMs { get; set; } = 200;

    /// <summary>编辑器类名白名单（§13.29），默认包含 Scintilla。</summary>
    public List<string> EditorClassWhitelist { get; set; } = ["Scintilla"];

    /// <summary>误触纠正开关（§M8-1），默认开。</summary>
    public bool TouchCorrectionEnabled { get; set; } = true;

    /// <summary>误触纠正高斯 σ（§M8-1），默认 10px。</summary>
    public double TouchCorrectionSigma { get; set; } = 10.0;

    // ---- 模糊音开关（§8 M7）----
    public bool FuzzyZhiZu { get; set; }

    public bool FuzzyChiCu { get; set; }

    public bool FuzzyShiSu { get; set; }

    public bool FuzzyNiLi { get; set; }

    public bool FuzzyRiLi { get; set; }

    public bool FuzzyFuHu { get; set; }

    public bool FuzzyAnAng { get; set; }

    public bool FuzzyEnEng { get; set; }

    public bool FuzzyInIng { get; set; }

    /// <summary>句子记忆（批11）：关 = 零记录零查询（运行缓冲也不记）。默认开。</summary>
    public bool SentenceMemoryEnabled { get; set; } = true;

    /// <summary>由当前设置构建模糊音配置（§8 M7）。</summary>
    public NineKey.Core.Pinyin.FuzzyProfile ToFuzzyProfile() => new()
    {
        ZhiZu = FuzzyZhiZu,
        ChiCu = FuzzyChiCu,
        ShiSu = FuzzyShiSu,
        NiLi = FuzzyNiLi,
        RiLi = FuzzyRiLi,
        FuHu = FuzzyFuHu,
        AnAng = FuzzyAnAng,
        EnEng = FuzzyEnEng,
        InIng = FuzzyInIng,
    };

    [field: NonSerialized]
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 从默认路径加载设置；文件不存在、损坏或被占用时返回全新默认实例，不阻塞启动。
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath));
                if (data is not null)
                {
                    data._opacity = Math.Clamp(data._opacity, MinOpacity, MaxOpacity);


                    return data;
                }
            }
        }
        catch (JsonException)
        {
            // ⚠ 坑：损坏的设置文件直接丢弃，用默认值，避免一次坏文件导致程序无法启动。
        }
        catch (IOException)
        {
            // ⚠ 坑：文件被占用或权限不足时同样回退默认值，持久化失败不应阻塞使用。
        }


        return new AppSettings();
    }


    /// <summary>将当前设置序列化到默认路径；目录创建或写入失败时静默忽略，不抛异常。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            // ⚠ 坑：System.Text.Json 默认按属性名原样序列化，重命名公共属性会丢失用户旧配置。
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this));
        }
        catch (IOException)
        {
            // ⚠ 坑：持久化失败不影响当前使用，静默吞掉，避免设置窗口因写盘报错而卡死。
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
