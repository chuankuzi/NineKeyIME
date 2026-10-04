// 本文件职责：句子记忆句库（批11）——整句上屏历史、签名索引、计数与淘汰、纯本地持久化。
// 数据流位置：KeyController 运行缓冲结算 → TryAdd；组串查询 → TryMatch → 控制器第 1 页末位展示。
// ⚠ 坑 1：句库是**独立存储**，不复用 UserDictionary，也不共享 _preLearnEntries 那套机制——
//          句子记忆绝不能参与字词排序权重（首选永远是字词，句子只是次选展示位）。
// ⚠ 坑 2：签名**只能由拼音算**（调用方结算时把 Signature.Of(累计拼音) 传进来）——本层拿不到拼音，
//          若拿汉字去算，Signature.Of 对非字母字符**原样返回**，存进句库的就是"汉字签名"，
//          与查询侧的数字签名永不同源（批11 踩过：句库进了数据却永远匹配不上）。
//          故 TryAdd 只收纯数字签名，非纯数字一律拒绝（根因守卫）。
// ⚠ 坑 3：损坏文件必须备份重命名（.corrupt）后空库重建，绝不闪退；脏条目跳过并回调日志，不整库丢弃。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NineKey.Core.Memory;

/// <summary>句子记忆句库：{text, signature, count, lastUsed}，容量上限按 lastUsed 最旧淘汰。</summary>
public sealed class SentenceMemory
{
    /// <summary>句库容量上限（超出按 lastUsed 最旧淘汰）。</summary>
    public const int MaxSentences = 5000;

    /// <summary>入库句长下限（字）。</summary>
    public const int MinSentenceChars = 4;

    /// <summary>入库句长上限（字）。</summary>
    public const int MaxSentenceChars = 12;

    /// <summary>入库所需最少音节数（= 签名长度下限）。</summary>
    public const int MinSentenceSyllables = 2;

    /// <summary>作为候选展示所需的最少重复次数。</summary>
    public const int MinCountForQuery = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Dictionary<string, SentenceEntry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;
    private readonly Action<string>? _log;

    public SentenceMemory(string? path = null, Func<DateTime>? now = null, Action<string>? log = null)
    {
        Path = path ?? DefaultPath();
        _now = now ?? (() => DateTime.UtcNow);
        _log = log;
        Load();
    }

    /// <summary>句库文件路径（默认 %LocalAppData%\NineKeyIME\sentences.json）。</summary>
    public string Path { get; }

    /// <summary>当前句数。</summary>
    public int Count => _entries.Count;

    /// <summary>总开关：关 = 零记录零查询（KeyController 侧同时停止喂 run buffer）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>默认句库路径。</summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineKeyIME",
        "sentences.json");

    /// <summary>
    /// 入库一条整句（调用方负责去掉尾部标点）。signature 必须由**拼音**经 Signature.Of 算好传入，
    /// 本层不自己算（拿不到拼音，用汉字算会得到永不同源的伪签名，见文件头坑 2）。
    /// 满足句长/签名门槛才计入，重复则计数 +1。
    /// </summary>
    public bool TryAdd(string text, string signature)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var sentence = text.Trim();
        if (sentence.Length is < MinSentenceChars or > MaxSentenceChars)
        {
            return false;
        }

        if (!IsValidSignature(signature))
        {
            _log?.Invoke($"句库拒收：签名非法（{signature}）——只收由拼音算出的纯数字签名");
            return false;
        }

        if (_entries.TryGetValue(sentence, out var existing))
        {
            // ⚠ 坑：更新分支必须一并刷新签名，否则旧版脏签名（汉字算出来的）一直留着，重打也治不好。
            _entries[sentence] = existing with { Signature = signature, Count = existing.Count + 1, LastUsed = _now() };
        }
        else
        {
            _entries[sentence] = new SentenceEntry(sentence, signature, 1, _now());
        }

        EvictIfNeeded();
        Save();
        return true;
    }

    /// <summary>
    /// 按签名精确匹配、count ≥ MinCountForQuery 且**不在字词候选里出现过**（去重铁律）的句子。
    /// 多条命中取 count 最高、其次 lastUsed 最新的一条；无命中返回 null。
    /// </summary>
    public string? TryMatch(string signature, IReadOnlyCollection<string>? excludeTexts = null)
    {
        if (!Enabled || string.IsNullOrEmpty(signature))
        {
            return null;
        }

        string? best = null;
        var bestCount = 0L;
        var bestUsed = DateTime.MinValue;
        foreach (var entry in _entries.Values)
        {
            if (entry.Signature != signature || entry.Count < MinCountForQuery)
            {
                continue;
            }

            if (excludeTexts is not null && excludeTexts.Contains(entry.Text))
            {
                continue;   // ⚠ 坑：词候选已经能给整句时，句子位不再重复插入
            }

            if (entry.Count > bestCount || (entry.Count == bestCount && entry.LastUsed > bestUsed))
            {
                best = entry.Text;
                bestCount = entry.Count;
                bestUsed = entry.LastUsed;
            }
        }

        return best;
    }

    /// <summary>提交命中句子后更新计数（不喂 LearnFromCommit，防权重污染）。</summary>
    public void Touch(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !_entries.TryGetValue(text, out var entry))
        {
            return;
        }

        _entries[text] = entry with { Count = entry.Count + 1, LastUsed = _now() };
        Save();
    }

    /// <summary>清空句库：删文件 + 清内存，立即生效。</summary>
    public void Clear()
    {
        _entries.Clear();
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"清空句库文件失败：{ex.Message}");
        }
    }

    /// <summary>落盘（单文件 JSON，写入即落盘）。</summary>
    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var list = new List<SentenceEntry>(_entries.Values);
            File.WriteAllText(Path, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"句库落盘失败：{ex.Message}");
        }
    }

    /// <summary>读盘：文件损坏→备份为 .corrupt 后空库重建；单条脏数据跳过并记日志。</summary>
    public void Load()
    {
        _entries.Clear();
        try
        {
            if (!File.Exists(Path))
            {
                return;
            }

            var json = File.ReadAllText(Path);
            List<SentenceEntry>? list;
            try
            {
                list = JsonSerializer.Deserialize<List<SentenceEntry>>(json);
            }
            catch (JsonException ex)
            {
                BackupCorrupt(ex.Message);
                return;
            }

            if (list is null)
            {
                BackupCorrupt("反序列化结果为 null");
                return;
            }

            foreach (var entry in list)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Text))
                {
                    _log?.Invoke("句库脏条目跳过：文本为空");
                    continue;
                }

                // ⚠ 坑：旧版句库可能存着"由汉字算的伪签名"（非字母字符原样返回）——它永不同源、
                // 查不出来，此处清空签名但**保留文本与计数**（用户重新打一次即自愈），不整条丢弃。
                var signature = IsValidSignature(entry.Signature) ? entry.Signature : string.Empty;
                if (signature.Length == 0)
                {
                    _log?.Invoke($"句库条目无有效拼音签名，暂不可查：{entry.Text}");
                }

                _entries[entry.Text] = entry with { Signature = signature };
            }

            EvictIfNeeded();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"句库读取失败：{ex.Message}");
            BackupCorrupt(ex.Message);
        }
    }

    /// <summary>测试/诊断：当前全部条目（按 lastUsed 升序）。</summary>
    public IReadOnlyList<SentenceEntry> Snapshot() =>
        _entries.Values.OrderBy(e => e.LastUsed).ToList();

    /// <summary>
    /// 签名合法性：**纯数字**且达到音节数下限（拼音经 Signature.Of 的产物恒为纯数字）。
    /// ⚠ 根因守卫：汉字签名（Signature.Of 把非字母原样返回）在这里被拦下，不再进句库。
    /// </summary>
    private static bool IsValidSignature(string? signature) =>
        !string.IsNullOrEmpty(signature)
        && signature.Length >= MinSentenceSyllables
        && signature.All(c => c is >= '0' and <= '9');

    private void BackupCorrupt(string reason)
    {
        _entries.Clear();
        try
        {
            if (File.Exists(Path))
            {
                var backup = Path + ".corrupt";
                File.Move(Path, backup, overwrite: true);
                _log?.Invoke($"句库损坏（{reason}）→ 已备份为 {backup}，空库重建");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"句库损坏备份失败：{ex.Message}");
        }
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count <= MaxSentences)
        {
            return;
        }

        var excess = _entries.Count - MaxSentences;
        foreach (var stale in _entries.Values.OrderBy(e => e.LastUsed).ThenBy(e => e.Text).Take(excess).ToList())
        {
            _ = _entries.Remove(stale.Text);
        }
    }
}

/// <summary>句库单条记录。</summary>
public sealed record SentenceEntry(string Text, string Signature, long Count, DateTime LastUsed);
