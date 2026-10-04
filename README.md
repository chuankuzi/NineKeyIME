# NineKeyIME

Windows 触屏软键盘输入法：**九键 T9 拼音 + 26 键英文**，WPF 实现，面向 Windows 掌机与小屏平板（鼠标同样可用）。

*A touch-friendly T9 + QWERTY soft keyboard for Windows (WPF), built for handhelds and small tablets.*

## 特性

**输入**

- 九键 T9 拼音：全拼 / 简拼；1 键点按弹符号选框
- 26 键英文：Shift / Caps / 粘性 Ctrl / Alt，附数字行、符号行、编辑行
- 123 数字面板、符号面板、文本型宏（常用语一键发送整句）
- 简繁转换（OpenCC）、9 组模糊音开关（zhi↔zi、chi↔ci、shi↔si、ni↔li、ri↔li、fu↔hu、an↔ang、en↔eng、in↔ing）
- 空格上屏首选、标点顶屏、退格长按连删（缓冲删空即停，不会穿透到目标应用）

**候选**

- 每页 5 个候选；鼠标 / 触摸滑动翻页；长按候选可置顶或删除
- 用户词本地学习：同码词上屏 3 次升到前列；删除系统同字词后回落为系统候选
- 句子记忆：连续打出的整句自动记忆，再打同一串拼音时在第 1 页末位提示整句（托盘可开关 / 清空）

**手感与外观**

- 误触纠正：按触摸落点概率纠正，三档灵敏度（σ = 6 / 10 / 16）
- 尺寸 100% / 150% / 200%；键盘背景色、按键背景色、按键边框宽度、背景与按键透明度
- 贴边：拖到屏幕右缘吸附成竖条，悬停或点击展开，ESC 收起

**系统集成**

- 托盘图标右键即全部设置；热键 `Ctrl+Alt+K`；开机自启；单实例运行
- 可屏蔽系统触摸键盘；编辑器白名单；支持以管理员身份重启

## 隐私

纯本地运行：源码中没有任何网络访问代码（`HttpClient` / `WebRequest` / `Socket` / `System.Net` 全仓零命中）。
用户词典、设置与句子记忆都存放在 `%LocalAppData%\NineKeyIME\`。

## 词库与许可

词库 **735,067 条**，由以下数据源构建（构建流程见 `tools/NineKey.DictBuilder`）：

| 数据源 | 用途 |
|---|---|
| [pinyin-data](https://github.com/mozillazg/pinyin-data) | 单字读音（含多音字降权读音） |
| [phrase-pinyin-data](https://github.com/mozillazg/phrase-pinyin-data) | 词组拼音 |
| [jieba](https://github.com/fxsjy/jieba) `dict.txt` | 词组词频（候选排序权重） |
| [THUOCL](https://github.com/thunlp/THUOCL) | 分类词表（长尾补拼音） |

- 第三方许可全文见 [`licenses/`](licenses/)（随安装包一并分发到安装目录）
- 数据源许可台账见 [`docs/DICT_LICENSES.md`](docs/DICT_LICENSES.md)
- 本仓分发的第三方数据与组件许可如上述；**项目自身未附 `LICENSE` 文件**

## 安装

从 [Releases](https://github.com/chuankuzi/NineKeyIME/releases) 下载 `NineKeyIME-Setup-<版本>.exe`：
自包含单文件安装包（.NET 8 运行时已内置），Windows 10 / 11 x64，**无需预装任何依赖**。

## 源码构建

```powershell
dotnet build NineKeyIME.sln
# 产物：src\NineKey.Host\bin\Debug\net8.0-windows\NineKey.Host.exe
```

- 需要 Windows + .NET SDK 8.0 或更高（开发机为 .NET SDK 9.0.318 + WindowsDesktop 8.0 运行时）
- 词库 `dict/lexicon.bin.gz` 已随仓提供；需要重建时：
  `dotnet run --project tools/NineKey.DictBuilder`（数据在 `dict/raw/`）
- 本公开仓是**源码快照**（白名单同步）：不含测试工程与个人开发文档

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/NineKey.Core` | 纯逻辑库：T9 签名、词库、查询引擎、候选排序、用户词学习、句子记忆 |
| `src/NineKey.Keyboard.Core` | 键盘领域纯逻辑：按键调度 `KeyController`、键位映射、设置模型 |
| `src/NineKey.Keyboard` | WPF 壳（唯一 UI 层） |
| `src/NineKey.Host` | WinExe 入口（单实例 Mutex） |
| `dict/` | 词库产物与原始数据 |
| `tools/NineKey.DictBuilder` | 词库构建工具 |
| `licenses/` | 第三方许可全文 |

## 已知限制

- 仅 Windows（WPF）；无 Linux 版本，该路线已冻结
- 发布包为自包含 x64 版本，安装包约 54 MB
