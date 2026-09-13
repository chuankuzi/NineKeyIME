using System.Text;

namespace NineKey.Core.Pinyin;

// 本文件职责：根据模糊音配置，把单个输入签名扩展为一组候选签名，供查询引擎做别名匹配。
// 数据流位置：QueryEngine 收到输入签名 → FuzzySignature.Variants → 返回签名列表（含原签名）→ 查倒排索引。
// ⚠ 坑 1：MaxVariants 硬顶 16，模糊音组合数量会指数增长，超限后直接截断，否则查询延迟不可接受（§8 M7 / §M8-1）。
// ⚠ 坑 2：返回列表的第 0 项必须是原始签名，QueryEngine 靠这个顺序区分“标准来源”与“模糊来源”并分别降权。
// ⚠ 坑 3：子串替换规则是双向的（如 9↔94），但 ApplyOnce 一次只替换一处，否则 BFS 会在同一签名间反复横跳。
// 相关规格：§8 M7、§M8-1。

/// <summary>
/// 根据模糊音配置，把输入数字串扩展为候选签名集合（§8 M7）。
/// 上限 16 个；原始签名始终包含在结果第 0 位。
/// </summary>
public static class FuzzySignature
{
    // ⚠ 坑：16 是查询引擎能接受的总变体数上限，变更时必须同步 QueryEngine 的硬顶逻辑（§M8-1）。
    public const int MaxVariants = 16;

    /// <summary>
    /// 返回输入签名的所有模糊音变体（含原签名），总数不超过 <see cref="MaxVariants"/>。
    /// </summary>
    /// <param name="signature">原始数字签名。</param>
    /// <param name="profile">当前启用的模糊音配置。</param>
    public static IReadOnlyList<string> Variants(string signature, FuzzyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrEmpty(signature))
        {
            return [string.Empty];
        }

        var groups = profile.EnabledGroups;
        if (groups.Count == 0)
        {
            return [signature];
        }

        // ⚠ 坑：原始签名先放入 results[0]，BFS 顺序保证调用方把它视为标准来源。
        var results = new List<string> { signature };
        var visited = new HashSet<string> { signature };
        var queue = new Queue<string>();
        queue.Enqueue(signature);

        while (queue.Count > 0 && results.Count < MaxVariants)
        {
            var current = queue.Dequeue();
            foreach (var variant in ApplyOnce(current, groups))
            {
                if (visited.Add(variant) && results.Count < MaxVariants)
                {
                    results.Add(variant);
                    queue.Enqueue(variant);
                }
            }
        }

        return results;
    }

    /// <summary>对单个签名应用所有模糊音规则一次，返回生成的新签名集合。</summary>
    private static IEnumerable<string> ApplyOnce(string signature, IReadOnlyList<FuzzyProfile.Group> groups)
    {
        foreach (var group in groups)
        {
            foreach (var rule in group.CharRules)
            {
                for (var i = 0; i < signature.Length; i++)
                {
                    if (signature[i] == rule.From)
                    {
                        yield return ReplaceChar(signature, i, rule.To);
                    }
                }
            }

            foreach (var rule in group.SubstringRules)
            {
                foreach (var replaced in ReplaceSubstring(signature, rule.Short, rule.Long))
                {
                    yield return replaced;
                }

                foreach (var replaced in ReplaceSubstring(signature, rule.Long, rule.Short))
                {
                    yield return replaced;
                }
            }
        }
    }

    private static string ReplaceChar(string s, int index, char c)
    {
        var sb = new StringBuilder(s);
        sb[index] = c;
        return sb.ToString();
    }

    private static IEnumerable<string> ReplaceSubstring(string s, string from, string to)
    {
        // ⚠ 坑：from 为空时若继续循环，会在每个位置都产生一次空替换导致无限膨胀，必须直接 yield break。
        if (from.Length == 0)
        {
            yield break;
        }

        for (var i = 0; i <= s.Length - from.Length; i++)
        {
            if (s.AsSpan(i, from.Length).SequenceEqual(from.AsSpan()))
            {
                yield return string.Concat(s.AsSpan(0, i), to, s.AsSpan(i + from.Length));
            }
        }
    }
}
