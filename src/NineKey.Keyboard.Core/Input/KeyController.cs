// 本文件职责：把物理/触屏按键事件转成 T9 查询与上屏调度（UI → 引擎 → 注入器）。
// 数据流位置：KeyboardWindow 转发事件 → KeyController 维护缓冲与候选 → QueryEngine 查词 → ITextInjector 上屏。
// ⚠ 坑 1：退格长按手势必须在缓冲清空处停止，否则会把删除穿透到目标应用（§13.8）。
// ⚠ 坑 2：部分上屏后 _committedSegments 与 _buffer 会同时存在，显示/查询要时刻对齐（§M8-5）。
// 相关规格：§2.3、§M8-1、§M8-5、§13.5、§13.8。

using NineKey.Core.Dictionary;
using NineKey.Core.Engine;
using NineKey.Core.Input;
using NineKey.Core.Pinyin;
using NineKey.Keyboard.Services;

namespace NineKey.Keyboard.Input;

/// <summary>已提交锁定段（§M8-5 部分上屏）：文本、拼音、消费的原始键数。</summary>
public sealed record CommittedSegment(string Text, string Pinyin, int KeyCount);

/// <summary>
/// 按键 → 引擎 → 上屏 调度核心（UI 无关逻辑，窗口只负责渲染和转发事件）。
/// </summary>
public sealed class KeyController
{
    public const int PageSize = 4;

    private readonly QueryEngine _engine;
    private readonly UserDictionary _userDict;
    private readonly ITextInjector _injector;
    private readonly InputBuffer _buffer = new();
    private readonly List<string> _history = new(); // 最近上屏，最新在前

    private IReadOnlyList<Candidate> _candidates = [];
    private int _pageIndex;
    private string? _topPinyin;

    /// <summary>§letter-pin：列号→锁定字母。仅约束组成行显示，不改缓冲与数字签名（同键换字母签名天然不变）。</summary>
    private readonly Dictionary<int, char> _pinnedLetters = new();
    private readonly List<TouchInputColumn> _touchColumns = [];
    private bool _touchCorrectionEnabled;

    // ⚠ 坑：sigma 决定误触概率分布宽度，默认 10.0 像素；过小会漏掉合理邻近键，过大会引入噪声键。
    private double _touchCorrectionSigma = 10.0;

    // §M8-5：部分上屏状态
    private readonly List<CommittedSegment> _committedSegments = [];
    private readonly List<string> _displaySyllables = [];
    private int _activeSyllableIndex;

    public KeyController(QueryEngine engine, UserDictionary userDict, ITextInjector injector)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _userDict = userDict ?? throw new ArgumentNullException(nameof(userDict));
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        Refresh();
    }

    /// <summary>输入串变化或候选翻页后触发，UI 订阅刷新。</summary>
    public event Action? StateChanged;

    public string DigitInput => _buffer.Letters;

    /// <summary>§letter-pin：把第 column 列锁定为指定字母。校验同键（字母必须在原键位上）；成功返回 true 并刷新。</summary>
    public bool PinLetter(int column, char letter)
    {
        if (column < 0 || column >= _touchColumns.Count)
        {
            return false;
        }
        var lower = char.ToLowerInvariant(letter);
        if (LetterKeyMap.LetterToDigit(lower) != _touchColumns[column].Variants[0].Digit)
        {
            return false;
        }
        _pinnedLetters[column] = lower;
        Refresh();
        return true;
    }
    /// <summary>§letter-pin：撤销第 column 列的锁定。</summary>
    public void UnpinLetter(int column)
    {
        if (_pinnedLetters.Remove(column))
        {
            Refresh();
        }
    }
    /// <summary>§letter-pin：第 column 列的可选值（该键全部字母 + 末位数字=撤销锁定）。</summary>
    public IReadOnlyList<string> KeyOptions(int column)
    {
        if (column < 0 || column >= _touchColumns.Count)
        {
            return [];
        }
        var digit = _touchColumns[column].Variants[0].Digit;
        var def = KeyLayout.Keys.FirstOrDefault(k => k.Digit == digit.ToString());
        if (def is null)
        {
            return [digit.ToString()];
        }
        var options = new List<string>();
        if (def.Center.Length > 0)
        {
            options.Add(def.Center);
        }
        options.AddRange(def.Slots.Where(s => s.Length > 0));
        options.Add(digit.ToString());
        return options;
    }

    /// <summary>
    /// 状态栏显示串：锁定段 + 当前缓冲按音节分段。
    /// 优先显示当前最佳候选的拼音分段；无候选时回退原始输入。
    /// </summary>
    public string DisplayInput
    {
        get
        {
            if (_committedSegments.Count == 0 && _buffer.IsEmpty)
            {
                return string.Empty;
            }

            var segments = new List<string>();
            segments.AddRange(_committedSegments.Select(s => s.Text));

            if (!_buffer.IsEmpty)
            {
                if (_displaySyllables.Count > 0)
                {
                    segments.AddRange(_displaySyllables.Skip(_activeSyllableIndex));
                }
                else
                {
                    segments.Add(_buffer.Letters);
                }
            }

            return string.Join(" ", segments);
        }
    }

    /// <summary>锁定段（已提交部分），供 UI 显示为锁定态。</summary>
    public IReadOnlyList<CommittedSegment> CommittedSegments => _committedSegments;

    /// <summary>当前缓冲的音节分段（完整缓冲），供 UI 点击分段。</summary>
    public IReadOnlyList<string> DisplaySyllables => _displaySyllables;

    /// <summary>当前激活音节索引：候选栏只显示从该音节开始的候选。</summary>
    public int ActiveSyllableIndex => _activeSyllableIndex;

    public bool IsEmpty => _buffer.IsEmpty;

    /// <summary>测试钩子：误触概率列是否已清空。</summary>
    internal bool TouchColumnsEmptyForTest => _touchColumns.Count == 0;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(_candidates.Count / (double)PageSize));

    public int PageIndex => _pageIndex;

    /// <summary>当前页候选（最多 PageSize 个）。</summary>
    public IReadOnlyList<Candidate> CurrentPage
    {
        get
        {
            var page = _candidates.Skip(_pageIndex * PageSize).Take(PageSize).ToList();
            return page;
        }
    }

    /// <summary>候选总数（供 UI 显示 "3/12"）。</summary>
    public int TotalCount => _candidates.Count;

    /// <summary>数字键输入。</summary>
    public void AppendDigit(char digit)
    {
        StartNewInputIfNeeded();
        _buffer.Append(digit);
        _touchColumns.Add(new TouchInputColumn { Variants = [new TouchVariant(digit, 1.0)] });
        Refresh();
    }

    /// <summary>字母键（Flick 选中）输入。</summary>
    public void AppendLetter(char letter)
    {
        StartNewInputIfNeeded();
        _buffer.Append(letter);
        _touchColumns.Add(new TouchInputColumn { Variants = [new TouchVariant(LetterKeyMap.LetterToDigit(letter), 1.0)] });
        Refresh();
    }

    /// <summary>§M8-1：带误触概率的数字键输入。</summary>
    public void AppendDigitWithProbability(char digit, IReadOnlyList<TouchVariant> variants)
    {
        StartNewInputIfNeeded();
        _buffer.Append(digit);
        _touchColumns.Add(new TouchInputColumn { Variants = [.. variants] });
        Refresh();
    }

    /// <summary>
    /// §M8-5：开始新输入时，若已有锁定段则清空（上一组拼音已处理完毕）。
    /// 退格回退锁定段时不触发。
    /// </summary>
    private void StartNewInputIfNeeded()
    {
        if (_buffer.IsEmpty && _committedSegments.Count > 0)
        {
            _committedSegments.Clear();
            _pinnedLetters.Clear();
            _activeSyllableIndex = 0;
            _displaySyllables.Clear();
        }
    }

    /// <summary>退格（§13.8 + §M8-5）：缓冲非空删缓冲键；缓冲空则回退一级锁定段。</summary>
    public void Backspace()
    {
        if (!_buffer.IsEmpty)
        {
            _buffer.Backspace();
            if (_touchColumns.Count > 0)
            {
                _touchColumns.RemoveAt(_touchColumns.Count - 1);
            _pinnedLetters.Remove(_touchColumns.Count);
            }

            if (_buffer.IsEmpty)
            {
                // ⚠ 坑：缓冲清空瞬间必须标记，否则同一次长按手势会穿透删除目标应用文本。
                _bsGestureClearedBuffer = true;
            }

            Refresh();
            return;
        }

        if (_bsGestureActive && _bsGestureClearedBuffer)
        {
            // ⚠ 坑：§13.8 同一长按手势内缓冲已删空，连删必须停止，不得穿透到目标应用。
            return;
        }

        // §M8-5：缓冲为空且存在锁定段时，回退一级：把最后一段拼音加回缓冲
        if (_committedSegments.Count > 0)
        {
            var last = _committedSegments[^1];
            _committedSegments.RemoveAt(_committedSegments.Count - 1);
            foreach (var c in last.Pinyin)
            {
                _buffer.Append(c);
                _touchColumns.Add(new TouchInputColumn { Variants = [new TouchVariant(LetterKeyMap.LetterToDigit(c), 1.0)] });
            }

            _activeSyllableIndex = 0;
            Refresh();
            return;
        }

        _injector.InjectBackspace();
    }

    private bool _bsGestureActive;
    private bool _bsGestureClearedBuffer;

    /// <summary>长按退格手势开始（RepeatButton 按下时调用）。</summary>
    public void BeginBackspaceGesture()
    {
        _bsGestureActive = true;
        _bsGestureClearedBuffer = false;
    }

    /// <summary>长按退格手势结束（RepeatButton 抬起时调用）。</summary>
    public void EndBackspaceGesture() => _bsGestureActive = false;

    public void ClearInput()
    {
        _buffer.Clear();
        _touchColumns.Clear();
        _committedSegments.Clear();
        _displaySyllables.Clear();
        _activeSyllableIndex = 0;
        Refresh();
    }

    /// <summary>状态不变量检查：任何操作后回显应与内部缓冲一致。</summary>
    private void AssertStateInvariant()
    {
        var bufferEmpty = _buffer.IsEmpty;
        var candidatesEmpty = _candidates.Count == 0;

        if (bufferEmpty != candidatesEmpty)
        {
            FileLogger.Error($"state-mismatch: buffer-empty={bufferEmpty} candidates-count={_candidates.Count} buffer={_buffer.Letters}");
        }

        if (bufferEmpty && _topPinyin is not null)
        {
            FileLogger.Error($"state-mismatch: buffer-empty=true top-pinyin={_topPinyin}");
        }

        if (!bufferEmpty && string.IsNullOrEmpty(DisplayInput))
        {
            FileLogger.Error($"state-mismatch: buffer={_buffer.Letters} display-empty=true");
        }
    }

    /// <summary>直接上屏（标点/空格/数字 1 等不走候选）。</summary>
    public void CommitDirect(string text)
    {
        _injector.InjectText(text);
        ClearInput();
    }

    /// <summary>
    /// 点击候选：§M8-5 部分上屏。
    /// 只提交候选覆盖的拼音段，剩余缓冲保留继续组词；
    /// 若候选覆盖整个缓冲，则清空并记录历史。
    /// </summary>
    public void CommitCandidate(Candidate candidate)
    {
        if (candidate.Source == CandidateSource.PinyinGuide)
        {
            SelectPinyinCombo(candidate.Pinyin);
            return;
        }

        var prefixOffset = PrefixOffsetBeforeActiveSyllable();
        var consumed = candidate.ConsumedKeys;
        if (consumed <= 0)
        {
            consumed = _buffer.Letters.Length;
        }

        var totalConsumed = prefixOffset + consumed;
        if (totalConsumed >= _buffer.Letters.Length)
        {
            // ⚠ 坑：候选覆盖全部剩余缓冲时直接完整提交，不能遗留空缓冲造成状态不一致。
            _injector.InjectText(candidate.Text);
            _engine.LearnFromCommit(candidate.Text, candidate.Pinyin);
            _history.Remove(candidate.Text);
            _history.Insert(0, candidate.Text);
            ClearInput();
            return;
        }

        // ⚠ 坑：部分上屏只消费前缀，剩余缓冲继续组词，_committedSegments 与 _buffer 需同步刷新。
        _injector.InjectText(candidate.Text);
        _engine.LearnFromCommit(candidate.Text, candidate.Pinyin);

        var committedPinyin = _buffer.Letters[..totalConsumed];
        _committedSegments.Add(new CommittedSegment(candidate.Text, committedPinyin, totalConsumed));

        _buffer.RemovePrefix(totalConsumed);
        ShiftPinsHead(totalConsumed);

        // ⚠ 坑：_touchColumns 与 _buffer 按位对齐，移除前缀时长度必须取小值，避免越界。
        if (_touchColumns.Count > 0)
        {
            var remove = Math.Min(totalConsumed, _touchColumns.Count);
            _touchColumns.RemoveRange(0, remove);
        }

        _activeSyllableIndex = 0;
        Refresh();
    }

    /// <summary>§M8-5：点击回显某音节后，候选栏只显示从该音节开始的候选。</summary>
    public void SetActiveSyllableIndex(int index)
    {
        if (index < 0 || index >= _displaySyllables.Count)
        {
            return;
        }

        _activeSyllableIndex = index;
        _pageIndex = 0;
        RefreshCandidates();
        StateChanged?.Invoke();
    }

    /// <summary>回车（§13.5）：缓冲非空 = 提交首选候选（无候选时提交字母串）；缓冲为空 = 发送 VK_RETURN。</summary>
    public void CommitEnter()
    {
        if (_buffer.IsEmpty)
        {
            _injector.InjectEnter();
            return;
        }

        if (_candidates.Count > 0)
        {
            var firstReal = _candidates.FirstOrDefault(c => c.Source != CandidateSource.PinyinGuide);
            if (firstReal is not null)
            {
                CommitCandidate(firstReal);
            }
            return;
        }

        _injector.InjectText(_buffer.Letters);
        ClearInput();
    }

    /// <summary>空格键（§13.5）：发送 VK_SPACE，与 0 键职责分离，永不输出字符 '0'。</summary>
    public void CommitSpace() => _injector.InjectSpace();

    public void NextPage()
    {
        if (PageCount > 1)
        {
            _pageIndex = (_pageIndex + 1) % PageCount;
            StateChanged?.Invoke();
        }
    }

    public void PreviousPage()
    {
        if (PageCount > 1)
        {
            _pageIndex = (_pageIndex - 1 + PageCount) % PageCount;
            StateChanged?.Invoke();
        }
    }

    /// <summary>空输入串时的历史候选（最近上屏前 8 个）。</summary>
    public IReadOnlyList<string> History => _history.Take(PageSize).ToList();

    public void SaveUserDictionary() => _userDict.Save();

    private void Refresh()
    {
        _pageIndex = 0;
        RefreshCandidates();
        RebuildDisplaySyllables();
        StateChanged?.Invoke();
        AssertStateInvariant();
    }

    private void RefreshCandidates()
    {
        if (_buffer.IsEmpty)
        {
            _candidates = [];
            _topPinyin = null;
            _pinnedLetters.Clear();
            return;
        }

        var queryInput = GetQueryInput();
        var result = _touchCorrectionEnabled && _touchColumns.Count > 0
            ? QueryWithCorrection(queryInput)
            : _engine.Query(queryInput);
        var startOffset = _buffer.Letters.Length - queryInput.Length;
        _candidates = ApplyPinFilter(result.Candidates, startOffset, queryInput.Length);
        _candidates = PrependPinyinCombos(_candidates, queryInput.Length);
        _topPinyin = _candidates.FirstOrDefault(c => c.Pinyin is not null && c.Pinyin.Length == queryInput.Length)?.Pinyin
            ?? (_candidates.Count > 0 ? result.TopPinyin : null);
    }

    /// <summary>当前实际查询串：从激活音节开始到末尾。</summary>
    private string GetQueryInput()
    {
        // ⚠ 坑：输入刚变化时 _displaySyllables 尚未反映当前缓冲，必须直接查询完整缓冲，否则丢键。
        var syllableTotal = _displaySyllables.Sum(s => s.Length);
        if (_displaySyllables.Count == 0 || syllableTotal != _buffer.Letters.Length)
        {
            return _buffer.Letters;
        }

        return string.Concat(_displaySyllables.Skip(_activeSyllableIndex));
    }

    /// <summary>激活音节之前的字符数（用于把候选消费长度映射回原始缓冲）。</summary>
    private int PrefixOffsetBeforeActiveSyllable()
    {
        if (_displaySyllables.Count == 0 || _activeSyllableIndex <= 0)
        {
            return 0;
        }

        return _displaySyllables.Take(_activeSyllableIndex).Sum(s => s.Length);
    }

    private void RebuildDisplaySyllables()
    {
        _displaySyllables.Clear();
        if (_buffer.IsEmpty)
        {
            return;
        }

        var pinyin = EffectiveDisplayLetters();
        var split = Syllable.Split(pinyin).ToList();

        _displaySyllables.AddRange(split);

        // ⚠ 坑：最佳拼音与缓冲长度不一致时（简拼/无候选），回退到按缓冲长度分段显示，避免音节错位。
        if (_displaySyllables.Sum(s => s.Length) != _buffer.Letters.Length)
        {
            _displaySyllables.Clear();
            _displaySyllables.Add(_buffer.Letters);
        }

        // ⚠ 坑：激活索引必须钳位，否则音节点击后删除/新增音节会越界。
        if (_activeSyllableIndex >= _displaySyllables.Count)
        {
            _activeSyllableIndex = 0;
        }
    }

    /// <summary>§letter-pin：组成行显示用的字母视图（与缓冲等长）：锁定 &gt; 最佳拼音 &gt; 原始字母 &gt; 键首字母。无候选时也显示字母码。</summary>
    private string EffectiveDisplayLetters()
    {
        var buffer = _buffer.Letters;
        var top = _topPinyin;
        var chars = new char[buffer.Length];
        for (var i = 0; i < buffer.Length; i++)
        {
            if (_pinnedLetters.TryGetValue(i, out var pinned))
            {
                chars[i] = pinned;
            }
            else if (top is not null && top.Length == buffer.Length && char.IsLetter(top[i]))
            {
                chars[i] = top[i];
            }
            else if (char.IsLetter(buffer[i]))
            {
                chars[i] = buffer[i];
            }
            else
            {
                chars[i] = FirstLetterOfKey(buffer[i]);
            }
        }
        return new string(chars);
    }
    private static char FirstLetterOfKey(char digit)
    {
        var def = KeyLayout.Keys.FirstOrDefault(k => k.Digit == digit.ToString());
        return def is not null && def.Center.Length > 0 ? char.ToLowerInvariant(def.Center[0]) : digit;
    }
    /// <summary>§letter-pin：头部移除 removed 列后钉子编号前移，越界丢弃。</summary>
    private void ShiftPinsHead(int removed)
    {
        if (removed <= 0 || _pinnedLetters.Count == 0)
        {
            return;
        }
        var shifted = _pinnedLetters
            .Where(kv => kv.Key >= removed)
            .ToDictionary(kv => kv.Key - removed, kv => kv.Value);
        _pinnedLetters.Clear();
        foreach (var kv in shifted)
        {
            _pinnedLetters[kv.Key] = kv.Value;
        }
    }
    /// <summary>§letter-pin：按钉子过滤候选——全拼且与钉子冲突的剔除（同键不同字母在此分流）；长度不符的（简拼等）不约束。</summary>
    private IReadOnlyList<Candidate> ApplyPinFilter(IReadOnlyList<Candidate> candidates, int startOffset, int queryLen)
    {
        if (_pinnedLetters.Count == 0)
        {
            return candidates;
        }
        var relevant = _pinnedLetters
            .Where(kv => kv.Key >= startOffset && kv.Key - startOffset < queryLen)
            .ToList();
        if (relevant.Count == 0)
        {
            return candidates;
        }
        var strict = relevant.Count == queryLen;
        return candidates.Where(cand =>
        {
            if (cand.Pinyin is null || cand.Pinyin.Length != queryLen)
            {
                return !strict;
            }

            return relevant.All(kv => cand.Pinyin[kv.Key - startOffset] == kv.Value);
        }).ToList();
    }
    /// <summary>§letter-pin：把查询串的全部全拼组合（候选拼音去重，按词频序）前置为引导项；存在锁定时首项为"全部"（点击=撤销锁定）。</summary>
    private IReadOnlyList<Candidate> PrependPinyinCombos(IReadOnlyList<Candidate> candidates, int queryLen)
    {
        var combos = candidates
            .Where(c => c.Pinyin is not null && c.Pinyin.Length == queryLen)
            .Select(c => c.Pinyin!)
            .Distinct()
            .ToList();
        if (combos.Count == 0)
        {
            return candidates;
        }
        var guides = new List<Candidate>();
        if (_pinnedLetters.Count > 0)
        {
            guides.Add(new Candidate("全部", CandidateSource.PinyinGuide, 0, null));
        }
        guides.AddRange(combos.Select(p => new Candidate(p, CandidateSource.PinyinGuide, 0, p)));
        return guides.Concat(candidates).ToList();
    }
    /// <summary>§letter-pin：点选拼音组合。null=撤销全部锁定；否则把查询区间全列锁定为该组合（严格过滤生效）。</summary>
    public void SelectPinyinCombo(string? pinyin)
    {
        _pinnedLetters.Clear();
        if (!string.IsNullOrEmpty(pinyin))
        {
            var queryLen = GetQueryInput().Length;
            var startOffset = _buffer.Letters.Length - queryLen;
            for (var i = 0; i < pinyin.Length && i < queryLen; i++)
            {
                _pinnedLetters[startOffset + i] = pinyin[i];
            }
        }
        Refresh();
    }
    private QueryResult QueryWithCorrection(string queryInput)
    {
        var startOffset = PrefixOffsetBeforeActiveSyllable();
        var activeColumns = _touchColumns.Skip(startOffset).ToList();

        // ⚠ 坑：maxBeams 硬顶 8，误触变体随长度指数增长，过大时查询延迟不可接受（§M8-1）。
        var beams = BeamExpand(activeColumns, maxBeams: 8);
        var originalDigits = string.Concat(activeColumns.Select(c => c.Variants[0].Digit));
        var variants = beams.Select(b => (b.Digits, b.Probability)).ToList();
        return _engine.QueryVariants(variants, originalDigits);
    }

    private static List<(string Digits, double Probability)> BeamExpand(IReadOnlyList<TouchInputColumn> columns, int maxBeams)
    {
        var beams = new List<(string Digits, double Probability)> { ("", 1.0) };
        foreach (var column in columns)
        {
            var next = new List<(string Digits, double Probability)>();
            foreach (var (digits, prob) in beams)
            {
                foreach (var variant in column.Variants)
                {
                    next.Add((digits + variant.Digit, prob * variant.Probability));
                }
            }

            beams = next.OrderByDescending(x => x.Probability).Take(maxBeams).ToList();
        }

        return beams;
    }

    /// <summary>§M8-1：运行时切换误触纠正配置。</summary>
    public void SetTouchCorrection(bool enabled, double sigma)
    {
        _touchCorrectionEnabled = enabled;
        _touchCorrectionSigma = sigma;
        Refresh();
    }
}
