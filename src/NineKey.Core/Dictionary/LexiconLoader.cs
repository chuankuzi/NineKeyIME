using System.IO.Compression;
using System.Text.Json;
using NineKey.Core.Pinyin;

// 本文件职责：加载 gzip 压缩的 UTF-8 JSON 系统词库文件并校验，生成 InMemoryLexicon。
// 数据流位置：启动时读取 dict/lexicon.bin.gz → LexiconLoader.Load → InMemoryLexicon.Build。
// ⚠ 坑 1：JSON 字段短名（W/P/F）由 DictBuilder 控制，手改文件会触发运行期兜底丢弃。
// ⚠ 坑 2：GZipStream 不支持随机寻址，必须顺序反序列化，大文件时关注内存峰值。
// ⚠ 坑 3：版本号不匹配必须抛异常，防止旧格式/新格式混用导致索引错误。
// 相关规格：§2.3、tools/DictBuilder-README.md。

namespace NineKey.Core.Dictionary;

/// <summary>
/// 系统词库文件加载器。文件格式：gzip 压缩的 UTF-8 JSON：
/// { "version": 1, "entries": [ { "w": "你好", "p": "nihao", "f": 800 } ] }
/// （DictBuilder 产出；gzip+JSON 属自定义容器，见 tools/DictBuilder/README.md）
/// </summary>
public static class LexiconLoader
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>从指定路径加载 gzip 压缩的 JSON 词库文件。</summary>
    public static InMemoryLexicon Load(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    /// <summary>从流加载 gzip 压缩的 JSON 词库文件。调用方负责流的生命周期。</summary>
    public static InMemoryLexicon Load(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        var dto = JsonSerializer.Deserialize<LexiconFileDto>(gzip, JsonOptions)
                  ?? throw new InvalidDataException("lexicon file is empty or malformed");

        if (dto.Version != CurrentVersion)
        {
            throw new InvalidDataException($"unsupported lexicon version {dto.Version}, expected {CurrentVersion}");
        }

        var entries = new List<LexEntry>(dto.Entries.Count);
        foreach (var e in dto.Entries)
        {
            // ⚠ 坑：DictBuilder 已校验，但手改词库文件仍可能传入脏数据，运行期兜底丢弃。
            if (string.IsNullOrEmpty(e.W) || string.IsNullOrEmpty(e.P) || e.F <= 0)
            {
                continue;
            }

            // ⚠ 坑：非法音节若进索引会导致查询结果出现无法拼读的候选，必须整条丢弃。
            var syllables = Syllable.Split(e.P);
            if (syllables.Any(s => !Syllable.IsValid(s)))
            {
                continue;
            }

            entries.Add(new LexEntry(e.W, e.P, e.F, LexiconSource.System));
        }

        return InMemoryLexicon.Build(entries);
    }

    /// <summary>词库文件 DTO（JSON 序列化用，字段短名控制文件体积）。</summary>
    public sealed class LexiconFileDto
    {
        public int Version { get; set; } = CurrentVersion;

        public List<EntryDto> Entries { get; set; } = [];

        public sealed class EntryDto
        {
            /// <summary>词。</summary>
            public string W { get; set; } = string.Empty;

            /// <summary>拼音，无声调，ü 用 v。</summary>
            public string P { get; set; } = string.Empty;

            /// <summary>词频。</summary>
            public int F { get; set; }
        }
    }
}
