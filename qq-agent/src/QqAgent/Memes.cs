using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqAgent;

/// <summary>群梗词条：term/aliases 命中现场才注入；last_used 在冷却期内只进"刚用过别再用"。</summary>
internal sealed class MemeEntry
{
    public string Term { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public string Meaning { get; set; } = "";
    public string Scope { get; set; } = "";
    public List<string> Examples { get; set; } = new();
    public long FirstSeen { get; set; }
    public long LastUsed { get; set; }
    public int Uses { get; set; }
    public string Source { get; set; } = "seed";
}

internal sealed record MemeCandidate(string Term, int Count, int Senders, List<string> Examples);

/// <summary>梗词库（data/memes.json）。daemon 只做启发式候选，入库由值班脑/管理员决定（duty meme add）。</summary>
internal sealed class MemeLexicon
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed class Doc
    {
        public int Version { get; set; } = 1;
        public List<MemeEntry> Entries { get; set; } = new();
    }

    private readonly string _path;
    public List<MemeEntry> Entries { get; private set; } = new();

    private MemeLexicon(string path) => _path = path;

    public static MemeLexicon Load(string path)
    {
        var lex = new MemeLexicon(path);
        try
        {
            if (File.Exists(path))
            {
                var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(path, Encoding.UTF8), Opt);
                if (doc?.Entries is not null) lex.Entries = doc.Entries;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[memes] 读取失败（按空库继续）: {ex.Message}");
        }
        return lex;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Doc { Entries = Entries }, Opt), Encoding.UTF8);
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[memes] 保存失败: {ex.Message}");
        }
    }

    private static bool ContainsAny(MemeEntry e, string text)
    {
        if (e.Term.Length > 0 && text.Contains(e.Term, StringComparison.Ordinal)) return true;
        foreach (var a in e.Aliases)
            if (a.Length > 0 && text.Contains(a, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>命中现场的词条：useful=可接，cooling=刚用过别再用。</summary>
    public (List<MemeEntry> Useful, List<string> Cooling) Match(IEnumerable<string> texts, int cooldownHours)
    {
        var joined = string.Join("\n", texts);
        var useful = new List<MemeEntry>();
        var cooling = new List<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cooldown = (long)Math.Max(0, cooldownHours) * 3600;
        foreach (var e in Entries)
        {
            if (!ContainsAny(e, joined)) continue;
            if (e.LastUsed > 0 && now - e.LastUsed < cooldown) cooling.Add(e.Term);
            else useful.Add(e);
        }
        return (useful, cooling);
    }

    public void Bump(IEnumerable<string> sentTexts)
    {
        var joined = string.Join("\n", sentTexts);
        if (joined.Length == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var changed = false;
        foreach (var e in Entries)
        {
            if (!ContainsAny(e, joined)) continue;
            e.LastUsed = now;
            e.Uses++;
            changed = true;
        }
        if (changed) Save();
    }

    public bool TryAdd(string term, string meaning, IEnumerable<string> examples, string scope, out string message)
    {
        term = term.Trim();
        if (term.Length < 2)
        {
            message = "term 太短（至少 2 字）";
            return false;
        }
        if (Entries.Any(e => e.Term == term || e.Aliases.Contains(term)))
        {
            message = $"'{term}' 已在词库";
            return false;
        }
        Entries.Add(new MemeEntry
        {
            Term = term,
            Meaning = meaning.Trim(),
            Scope = scope,
            Examples = examples.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(3).ToList(),
            FirstSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Source = "amy",
        });
        Save();
        message = $"added '{term}'（{Entries.Count} 条）";
        return true;
    }

    /// <summary>零使用且登记超过 30 天的词条清理（种子/手动加的不会立即被清）。</summary>
    public int Prune()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var before = Entries.Count;
        Entries = Entries.Where(e => e.Uses > 0 || e.Source != "amy" || now - e.FirstSeen < 30 * 86400).ToList();
        var removed = before - Entries.Count;
        if (removed > 0) Save();
        return removed;
    }

    // ===== 新梗候选（纯启发：引号片段 + 高频 CJK 2-4 元组，≥N 次且 ≥M 人）=====

    private static readonly Regex QuotedRx = new(@"[「『“""]([^」』”""]{2,8})[」』”""]", RegexOptions.Compiled);
    private static readonly Regex CjkRunRx = new(@"[\u4e00-\u9fff]{2,}", RegexOptions.Compiled);
    /// <summary>转发卡片/系统提示/工具词不是梗：候选里带这些字直接丢（2026-10-26 实测噪声：聊天记录/该群因涉嫌违规）。</summary>
    private static readonly Regex NoiseRx = new(
        @"(记录|聊天|转发|涉嫌|违规|停用|图片|表情|文件|红包|撤回|系统|欢迎|入群|公告|群管家|管理员|消息|未读)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "就是", "什么", "一个", "没有", "不是", "这个", "那个", "我们", "你们", "他们", "自己", "知道",
        "现在", "可以", "还是", "已经", "因为", "所以", "但是", "然后", "真的", "感觉", "有点", "一起",
        "时候", "东西", "问题", "事情", "群里", "哈哈", "卧槽", "笑死", "不是", "这种", "那种", "怎么",
        "这样", "那样", "如果", "而且", "并且", "或者", "虽然", "可是", "应该", "可能", "一定", "直接",
        "开始", "结束", "今天", "昨天", "明天", "刚才", "好像", "确实", "有点", "比较", "非常", "特别",
        "猫娘", "电子", "群友", "消息", "图片", "表情", "头像", "名字", "昵称", "管理", "管理员",
        "机器人", "模型", "系统", "网络", "也是", "还有", "已被", "没有", "可能", "手机", "电脑", "游戏", "时间", "世界", "人生", "东西",
    };

    private bool Known(string term) =>
        Entries.Any(e => e.Term == term || e.Aliases.Contains(term) || e.Term.Contains(term) && term.Length >= 3);

    /// <summary>从最近若干行里挖候选新词（只给线索，不自动入库）。</summary>
    public List<MemeCandidate> Detect(IReadOnlyList<JournalLine> lines, MemesConfig cfg)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var senders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var examples = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var nicknames = lines.Select(l => l.Sender).Where(s => s.Length > 0).Distinct().ToList();

        void Note(string term, JournalLine line)
        {
            if (term.Length < 2 || Stop.Contains(term) || Known(term) || NoiseRx.IsMatch(term)) return;
            if (nicknames.Any(n => n.Contains(term, StringComparison.Ordinal))) return;   // 昵称/@ 引用不当候选
            counts[term] = counts.TryGetValue(term, out var c) ? c + 1 : 1;
            if (!senders.TryGetValue(term, out var s))
            {
                s = new HashSet<string>(StringComparer.Ordinal);
                senders[term] = s;
            }
            s.Add(line.Sender);
            if (!examples.TryGetValue(term, out var ex))
            {
                ex = new List<string>();
                examples[term] = ex;
            }
            if (ex.Count < 2 && !ex.Contains(line.Text)) ex.Add(Trim(line.Text, 40));
        }

        foreach (var line in lines)
        {
            if (line.IsSelf) continue;
            var text = line.Text;
            if (text.StartsWith("[图片", StringComparison.Ordinal)) continue;   // OCR 文字不参与造词
            if (text.StartsWith("[聊天记录", StringComparison.Ordinal)) continue;   // 转发卡片摘要不参与造词
            foreach (Match m in QuotedRx.Matches(text)) Note(m.Groups[1].Value, line);
            foreach (Match run in CjkRunRx.Matches(text))
            {
                var s = run.Value;
                for (var len = 2; len <= 4; len++)
                for (var i = 0; i + len <= s.Length; i++)
                    Note(s.Substring(i, len), line);
            }
        }

        var ranked = counts
            .Where(kv => kv.Value >= Math.Max(2, cfg.MinCount)
                         && senders[kv.Key].Count >= Math.Max(1, cfg.MinSenders))
            .Select(kv => new MemeCandidate(kv.Key, kv.Value, senders[kv.Key].Count,
                examples.TryGetValue(kv.Key, out var ex) ? ex : new List<string>()))
            .OrderByDescending(c => c.Count)
            .ThenByDescending(c => c.Term.Length)
            .ToList();

        // 去重：短词是已选长词的一部分且频次不更高 → 丢
        var picked = new List<MemeCandidate>();
        foreach (var c in ranked)
        {
            if (picked.Any(p => p.Term.Contains(c.Term, StringComparison.Ordinal) && p.Count >= c.Count)) continue;
            picked.Add(c);
            if (picked.Count >= Math.Max(1, cfg.CandidateTop)) break;
        }
        return picked;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}


