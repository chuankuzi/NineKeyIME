using System.Text.Json;

// 本文件职责：用户词学习记录的内存存储、JSON 持久化与转换为词条。
// 数据流位置：候选上屏 → Learn 增加次数 → 启动/保存时 ToEntries 合并进 InMemoryLexicon。
// ⚠ 坑 1：所有写操作必须加 lock，且 Load/Save 的 IO 也在锁内，避免并发损坏 JSON。
// ⚠ 坑 2：无拼音的词无法进入索引，Learn 时若调用方没传拼音会尝试用已有记录补齐。
// ⚠ 坑 3：词频用 long 计数但转 LexEntry 时降到 int，溢出保护在两边都要做。
// 相关规格：§2.3-8、§4.4。

namespace NineKey.Core.Dictionary;

/// <summary>
/// 用户词学习：候选被点击后词频 +1（带溢出保护），JSON 持久化到
/// %LocalAppData%\NineKeyIME\userdict.json，每次启动加载合并（规格 §2.3-8）。
/// 无拼音的词不参与索引；所有写操作经 lock 保证线程安全。
/// </summary>
public sealed class UserDictionary
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineKeyIME", "userdict.json");

    private readonly object _sync = new();
    private readonly string _path;
    private readonly Dictionary<string, UserWord> _words = new(StringComparer.Ordinal);

    public UserDictionary(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _words.Count;
            }
        }
    }

    /// <summary>候选上屏后调用：该词学习次数 +1（上限 long.MaxValue/2 防溢出），返回新次数。</summary>
    public long Learn(string word, string? pinyin)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);
        lock (_sync)
        {
            _words.TryGetValue(word, out var current);
            var py = !string.IsNullOrEmpty(pinyin) ? pinyin : current?.Pinyin ?? string.Empty;

            // ⚠ 坑：学习次数上限钳位到 long.MaxValue/2，与后续转 int 的溢出保护配套。
            var count = current is null || current.Count < long.MaxValue / 2
                ? (current?.Count ?? 0) + 1
                : current.Count;
            _words[word] = new UserWord(py, count);
            return count;
        }
    }

    /// <summary>学习记录 → 用户词条目（词频 = 次数，与系统词库的权重换算在 QueryEngine）。无拼音的条目不出索引。</summary>
    public IReadOnlyList<LexEntry> ToEntries()
    {
        lock (_sync)
        {
            return _words
                .Where(kv => kv.Value.Pinyin.Length > 0)
                // ⚠ 坑：用户词频转系统词频 int 量级时再次钳位，避免与系统词相加溢出。
                .Select(kv => new LexEntry(kv.Key, kv.Value.Pinyin, (int)Math.Min(int.MaxValue / 2, kv.Value.Count), LexiconSource.User))
                .ToList();
        }
    }

    /// <summary>获取某词当前学习次数，无记录时返回 0。</summary>
    public long GetCount(string word)
    {
        lock (_sync)
        {
            return _words.TryGetValue(word, out var w) ? w.Count : 0;
        }
    }

    /// <summary>获取某词记录到的拼音，无记录或无拼音时返回 null。</summary>
    public string? GetPinyin(string word)
    {
        lock (_sync)
        {
            return _words.TryGetValue(word, out var w) && w.Pinyin.Length > 0 ? w.Pinyin : null;
        }
    }

    /// <summary>
    /// 移除学习记录（长按候选→删除错词用）：返回是否真的移除过；落盘由调用方决定。
    /// </summary>
    public bool Remove(string word)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);
        lock (_sync)
        {
            return _words.Remove(word);
        }
    }

    /// <summary>
    /// 置顶：把该词学习次数直接提到上限（排序里用户词权重随之封顶）。
    /// 无拼音且无既有记录时不建条目（无拼音的词不进索引）。
    /// </summary>
    public bool Pin(string word, string? pinyin)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);
        lock (_sync)
        {
            _words.TryGetValue(word, out var current);
            var py = !string.IsNullOrEmpty(pinyin) ? pinyin : current?.Pinyin ?? string.Empty;
            if (py.Length == 0)
            {
                return false;
            }

            _words[word] = new UserWord(py, long.MaxValue / 2);
            return true;
        }
    }

    /// <summary>将当前学习记录 JSON 持久化到构造时指定的路径（含目录创建）。</summary>
    public void Save()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_words));
        }
    }

    /// <summary>从 JSON 文件加载学习记录；文件不存在或损坏时静默从空开始。</summary>
    public void Load()
    {
        lock (_sync)
        {
            _words.Clear();
            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, UserWord>>(File.ReadAllText(_path));
                if (data is null)
                {
                    return;
                }

                foreach (var kv in data)
                {
                    if (kv.Key.Length > 0 && kv.Value.Count > 0)
                    {
                        _words[kv.Key] = new UserWord(kv.Value.Pinyin ?? string.Empty, Math.Min(kv.Value.Count, long.MaxValue / 2));
                    }
                }
            }
            catch (JsonException)
            {
                // ⚠ 坑：损坏或旧格式文件不能导致启动崩溃，静默从空开始。
            }
        }
    }

    private sealed record UserWord(string Pinyin, long Count);
}
