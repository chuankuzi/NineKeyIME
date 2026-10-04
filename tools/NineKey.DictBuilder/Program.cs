using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NineKey.Core.Dictionary;
using NineKey.Core.Pinyin;

namespace NineKey.DictBuilder;

/// <summary>
/// 词库编译器：pinyin-data 原始语料 → lexicon.bin.gz。
/// 用法: NineKey.DictBuilder [rawDir] [outputFile]   （默认取当前目录下 dict/raw 与 dict/lexicon.bin.gz）
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var rawDir = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "dict", "raw");        var outputFile = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "dict", "lexicon.bin.gz");

        var charFile = Path.Combine(rawDir, "pinyin.txt");
        var phraseFile = Path.Combine(rawDir, "large_pinyin.txt");
        if (!File.Exists(charFile) || !File.Exists(phraseFile))
        {
            Console.Error.WriteLine($"raw corpus missing under {rawDir} (need pinyin.txt + large_pinyin.txt)");
            return 1;
        }

        LoadPhraseFreq(Path.Combine(rawDir, "jieba_dict.txt"));

        var entries = new Dictionary<(string Word, string Pinyin), int>();
        var stats = new Stats();

        ParseCharTable(charFile, entries, stats);
        ParsePhraseTable(phraseFile, entries, stats);

        // §13.19-3 THUOCL 词表补充通道：仅取词列（事实数据），拼音/词频均来自门禁内 MIT 源；
        // THUOCL 原件不进仓库。提取脚本 tools/thuocl_extract.py，台账见 docs/DICT_LICENSES.md。
        var thuoclFile = Path.Combine(rawDir, "thuocl_words.txt");
        if (File.Exists(thuoclFile))
        {
            ParsePhraseTable(thuoclFile, entries, stats);
        }
        // THUOCL 长尾 LLM 辅助拼音通道：词取 THUOCL 词表长尾（事实数据，DF 丢弃），拼音由本机 LLM 生成并过四道校验；
        // 生成脚本 tools/thuocl_llm_pinyin.py，台账见 docs/DICT_LICENSES.md。
        var thuoclLlmFile = Path.Combine(rawDir, "thuocl_llm_pinyin.txt");
        if (File.Exists(thuoclLlmFile))
        {
            ParsePhraseTable(thuoclLlmFile, entries, stats);
        }

        ParseJiebaSupplement(entries, stats);

        if (!Validate(entries, out var validationErrors))
        {
            foreach (var error in validationErrors.Take(20))
            {
                Console.Error.WriteLine($"validate: {error}");
            }

            Console.Error.WriteLine($"validation failed: {validationErrors.Count} error(s), output NOT written");
            return 1;
        }

        var dto = new LexiconLoader.LexiconFileDto
        {
            Entries = entries
                .Select(kv => new LexiconLoader.LexiconFileDto.EntryDto { W = kv.Key.Word, P = kv.Key.Pinyin, F = kv.Value })
                .ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        using (var file = File.Create(outputFile))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(gzip, dto);
        }

        Console.WriteLine($"entries={dto.Entries.Count} chars={stats.CharEntries} phrases={stats.PhraseEntries} supplement={stats.SupplementEntries} " +
                          $"skipped={stats.Skipped} altReadings={stats.AltReadings} output={outputFile} " +
                          $"size={new FileInfo(outputFile).Length / 1024}KB");
        return 0;
    }

    /// <summary>
    /// 构建期校验（规格 §4.5）：无空拼音、无重复 (词,拼音) 对、词频 &gt; 0、音节合法、签名可逆查。
    /// 任一失败即返回 false，Main 以非零码退出（CI 红）。
    /// </summary>
    private static bool Validate(Dictionary<(string Word, string Pinyin), int> entries, out List<string> errors)
    {
        errors = [];
        foreach (var kv in entries)
        {
            if (kv.Key.Word.Length == 0)
            {
                errors.Add($"empty word for pinyin '{kv.Key.Pinyin}'");
            }

            if (kv.Key.Pinyin.Length == 0)
            {
                errors.Add($"empty pinyin for word '{kv.Key.Word}'");
            }

            if (kv.Value <= 0)
            {
                errors.Add($"non-positive freq {kv.Value} for '{kv.Key.Word}'/{kv.Key.Pinyin}");
            }

            if (kv.Key.Pinyin.Length > 0 && Syllable.Split(kv.Key.Pinyin).Any(s => !Syllable.IsValid(s)))
            {
                errors.Add($"invalid syllable in pinyin '{kv.Key.Pinyin}' for '{kv.Key.Word}'");
            }
        }

        // 签名可逆查：每条必须能通过自身数字签名从倒排索引查回
        var lexicon = InMemoryLexicon.Build(entries.Select(kv => new LexEntry(kv.Key.Word, kv.Key.Pinyin, kv.Value, LexiconSource.System)));
        foreach (var kv in entries)
        {
            var sig = Signature.Of(kv.Key.Pinyin);
            if (!lexicon.QueryExact(sig).Any(e => e.Word == kv.Key.Word && e.Pinyin == kv.Key.Pinyin))
            {
                errors.Add($"signature lookup miss: '{kv.Key.Word}'/{kv.Key.Pinyin} sig={sig}");
            }
        }

        return errors.Count == 0;
    }

    private static readonly System.Text.Encoding Gbk = System.Text.Encoding.GetEncoding(936);

    private static Dictionary<string, int> _phraseFreq = new();
    private static double _phraseFreqLogMax = 1;
    private static readonly Dictionary<string, string> _charPrimaryPinyin = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _phraseWords = new(StringComparer.Ordinal);

    /// <summary>加载 jieba dict.txt 词频语料（格式: 词 词频 [词性]）。</summary>
    private static void LoadPhraseFreq(string path)
    {
        var max = 1L;
        var map = new Dictionary<string, int>(400_000);
        if (File.Exists(path))
        {
            foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var sp = line.IndexOf(' ');
                if (sp <= 0 || !int.TryParse(line[(sp + 1)..].Split(' ')[0], out var count))
                {
                    continue;
                }

                map[line[..sp]] = count;
                if (count > max)
                {
                    max = count;
                }
            }
        }

        _phraseFreq = map;
        _phraseFreqLogMax = Math.Log10(max + 1);
    }

    /// <summary>返回汉字在 GB2312 中的级别：1=一级字库(最常用), 2=二级字库, 0=不在 GB2312（罕见）。</summary>
    private static int GbkLevel(int code)
    {
        var s = char.ConvertFromUtf32(code);
        Span<byte> bytes = stackalloc byte[4];
        try
        {
            var n = Gbk.GetBytes(s, bytes);
            if (n < 2)
            {
                return 0;
            }

            return bytes[0] switch
            {
                >= 0xB0 and <= 0xD7 => 1,
                >= 0xD8 and <= 0xF7 => 2,
                _ => 0,
            };
        }
        catch (EncoderFallbackException)
        {
            return 0;
        }
    }

    private static void ParseCharTable(string path, Dictionary<(string, string), int> entries, Stats stats)
    {
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine;
            var commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }

            line = line.Trim();
            if (line.Length == 0 || !line.StartsWith("U+", StringComparison.Ordinal))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0 || !int.TryParse(line[2..colon], System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                continue;
            }

            if (code is < 0x3007 or (> 0x3007 and < 0x3400) or (> 0x4DBF and < 0x4E00) or > 0x9FFF)
            {
                continue; // 只要 〇 + 基本区 + 扩展 A 区汉字
            }

            var word = char.ConvertFromUtf32(code);
            // 常用度代理：GB2312 一级字库（GBK 首字节 0xB0-0xD7，按拼音序的 3755 个最常用字）> 二级字库(0xD8-0xF7) > 其余
            var level = GbkLevel(code);
            var primary = true;
            foreach (var rawReading in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryNormalizePinyin(rawReading, out var pinyin))
                {
                    stats.Skipped++;
                    continue;
                }

                if (primary)
                {
                    _charPrimaryPinyin[word] = pinyin; // 补充词通道合成拼音用（§13.19-2）
                }

                // 单字词频：jieba 语料真实频（100..1000 对数归一化）优先；无频字按 GB 级别给低档值，
                // 全部低于归一化下限 100，保证有真实频的条目永远排在启发值条目之前
                var freq = _phraseFreq.TryGetValue(word, out var count)
                    ? (int)(100 + 900 * Math.Log10(count + 1) / _phraseFreqLogMax)
                    : level switch { 1 => 95, 2 => 60, _ => 30 };
                if (!primary)
                {
                    // 多音字次读音降权（§2.3-5）：低频字维持 ×0.1 防噪音；
                    // 高频字（归一化 ≥500）次读音改 ×0.6——否则"谁 shei"这类双常用读音会被同码词埋到后页（§13.7 案例）
                    freq = freq >= 500 ? freq * 3 / 5 : Math.Max(1, freq / 10);
                }

                Upsert(entries, word, pinyin, freq);
                primary = false;
                if (level != 1)
                {
                    stats.AltReadings++;
                }
            }

            stats.CharEntries++;
        }
    }

    private static void ParsePhraseTable(string path, Dictionary<(string, string), int> entries, Stats stats)
    {
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine;
            var commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }

            line = line.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var word = line[..colon].Trim();
            if (word.Length < 2 || !word.All(IsKeepableChar))
            {
                continue; // 单字已由字表覆盖；非常用汉字跳过
            }

            if (!TryNormalizePinyin(line[(colon + 1)..], out var pinyin))
            {
                stats.Skipped++;
                continue;
            }

            // 词组词频：jieba 语料真实频（100..1000 对数归一化）优先；无频词组统一 90，
            // 低于归一化下限，避免 phrase-pinyin-data 的海量生僻词压过日常高频词
            var freq = 90;
            if (_phraseFreq.TryGetValue(word, out var count))
            {
                freq = (int)(100 + 900 * Math.Log10(count + 1) / _phraseFreqLogMax);
            }

            Upsert(entries, word, pinyin, freq);
            _ = _phraseWords.Add(word); // 补充词通道去重用（§13.19-2）
            stats.PhraseEntries++;
        }
    }

    /// <summary>
    /// jieba 补充词通道（§13.19-2，数据全部为门禁内 MIT 源，无新增登记项）：
    /// jieba dict.txt 中 phrase-pinyin-data 未收录的词组，用单字主读音合成拼音入库。
    /// 噪音控制（2026-09-10 修订，用户裁定"词条存在性优先于排序权重"）：全部词条入库，不设词频门槛；
    /// 合成条目词频 ×0.5 降权（多音字读音为猜测值）——排序靠权重调节，不再靠门槛丢弃。
    /// </summary>
    private static void ParseJiebaSupplement(Dictionary<(string, string), int> entries, Stats stats)
    {
        var sb = new StringBuilder();
        foreach (var (word, count) in _phraseFreq)
        {
            if (word.Length < 2 || _phraseWords.Contains(word) || !word.All(IsKeepableChar))
            {
                continue;
            }

            var normalized = (int)(100 + 900 * Math.Log10(count + 1) / _phraseFreqLogMax);    		

            sb.Clear();
            var synthesizable = true;
            foreach (var c in word)
            {
                if (_charPrimaryPinyin.TryGetValue(c.ToString(), out var py))
                {
                    sb.Append(py);
                }
                else
                {
                    synthesizable = false;
                    break;
                }
            }

            if (!synthesizable)
            {
                stats.Skipped++;
                continue;
            }

            Upsert(entries, word, sb.ToString(), Math.Max(1, normalized / 2));
            stats.SupplementEntries++;
        }
    }

    private static bool IsKeepableChar(char c) =>
        c is '\u3007' || c is >= '\u3400' and <= '\u4DBF' || c is >= '\u4E00' and <= '\u9FFF';

    private static void Upsert(Dictionary<(string, string), int> entries, string word, string pinyin, int freq)
    {
        var key = (word, pinyin);
        if (!entries.TryGetValue(key, out var existing) || freq > existing)
        {
            entries[key] = freq;
        }
    }

    /// <summary>带调拼音 → 无声调拼音（ü→v）；含无法识别的字符时返回 false。</summary>
    private static bool TryNormalizePinyin(string toneMarked, out string pinyin)
    {
        var sb = new StringBuilder(toneMarked.Length);
        foreach (var raw in toneMarked.Trim())
        {
            var c = raw switch
            {
                'ā' or 'á' or 'ǎ' or 'à' => 'a',
                'ō' or 'ó' or 'ǒ' or 'ò' => 'o',
                'ē' or 'é' or 'ě' or 'è' => 'e',
                'ī' or 'í' or 'ǐ' or 'ì' => 'i',
                'ū' or 'ú' or 'ǔ' or 'ù' => 'u',
                'ǖ' or 'ǘ' or 'ǚ' or 'ǜ' or 'ü' => 'v',
                'ń' or 'ň' or 'ǹ' or 'n' => 'n',
                'ḿ' or 'm' => 'm',
                >= 'a' and <= 'z' => raw,
                ' ' or '-' or '\'' => ' ',
                _ => '\0',
            };

            if (c == '\0')
            {
                pinyin = string.Empty;
                return false;
            }

            sb.Append(c);
        }

        pinyin = sb.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
        return pinyin.Length > 0;
    }

    private sealed class Stats
    {
        public int CharEntries { get; set; }
        public int PhraseEntries { get; set; }
        public int Skipped { get; set; }
        public int AltReadings { get; set; }
        public int SupplementEntries { get; set; }
    }
}
