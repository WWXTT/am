using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QqAgent;

/// <summary>声线模块：唯一出正文的地方。输入只有「人设 + 现场 + 意图」，看不到记忆与旧历史；
/// 输出经守卫过滤（长度/分泡/AI 腔/重复/照抄意图）。</summary>
internal sealed class Writer
{
    private readonly AppConfig _cfg;
    private readonly string _persona;
    private readonly Brain _brain;

    public sealed record Result(List<string> Bubbles, string Raw, List<string> Violations);

    private static readonly Regex AiismRx = new(
        @"(作为|总之|总而言之|建议|不过|其实|首先|其次|最后|总结一下|希望能|有什么可以帮|抱歉|用户|对方|根据记忆|我记得|上次说的|系统|设定|模型|提示词|管理员)",
        RegexOptions.Compiled);

    private static readonly Regex MarkdownRx = new(@"(```|\*\*|^#{1,6}\s|^\s*[-*•]\s|`[^`]+`)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PrefixRx = new(@"^\s*(Amy|艾米|amy)\s*[:：]\s*", RegexOptions.Compiled);
    private static readonly Regex WrapQuoteRx = new(@"^\s*[「『“""](.*)[」』”""]\s*$", RegexOptions.Compiled | RegexOptions.Singleline);

    public Writer(AppConfig cfg, string persona, Brain brain)
    {
        _cfg = cfg;
        _persona = persona;
        _brain = brain;
    }

    public static string LoadPersona(AppConfig cfg)
    {
        var p = Path.IsPathRooted(cfg.Writer.PersonaPath)
            ? cfg.Writer.PersonaPath
            : Path.GetFullPath(Path.Combine(AppConfig.BaseDir, cfg.Writer.PersonaPath));
        return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8).Trim() : "你是 Amy，群里的一只电子猫，说话短、不解释。";
    }

    private string SystemPrompt(string mood)
    {
        var sb = new StringBuilder(_persona);
        if (string.Equals(mood, "dark", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine("现在有人冲你来了：狠、短、不解释，三句内收尾。");
        }
        return sb.ToString();
    }

    /// <summary>把现场包 + 意图拼成 writer 的 user 消息（不传任何操作手册/记忆块）。
    /// anchor = 值班 Amy 选定的「要回谁、回他哪几句」；同一个人的连续几句当一整句话。
    /// 为 null 时只给最新一条，并明确它**不一定**冲 Amy
    /// （2026-10-28 定案：投递层只报信，"这句冲谁"由值班 Amy 判断，不再当成事实喂给声线）。</summary>
    private static string UserPrompt(JsonNode pack, string intent, string? extraHint, bool force, JsonArray? anchor = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 现场（群里已经聊到这）");
        AppendLines(sb, pack["scene"]?["now"] as JsonArray);
        var ambient = pack["scene"]?["ambient"] as JsonArray;
        if (ambient is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("# 更早的几行（只听懂梗，别回应它们）");
            AppendLines(sb, ambient, Math.Min(6, ambient.Count));
        }

        sb.AppendLine();
        if (anchor is { Count: > 0 })
        {
            var who = anchor[0]?["sender"]?.GetValue<string>() ?? "";
            sb.AppendLine(anchor.Count == 1
                ? "# 你要回的这一句（回合锚点，值班的 Amy 已经选定）"
                : $"# 你要回的这一组（{who} 连着说的 {anchor.Count} 句，当成一整句话看）");
            foreach (var n in anchor)
                sb.AppendLine($"{n?["sender"]}: {n?["text"]}");
            var quoted = anchor.FirstOrDefault(n => n?["ref"] is not null)?["ref"];
            if (quoted is not null) sb.AppendLine($"(引用了 {quoted["from"]}：{quoted["text"]})");
            sb.AppendLine("就回这一组（或这一句）；旁边的话题、别人互相使唤和玩笑都不在你这一轮里。");
        }
        else
        {
            sb.AppendLine("# 群里最新一条（不一定是冲你说的）");
            var trig = pack["trigger"];
            if (trig is not null) sb.AppendLine($"{trig["sender"]}: {trig["text"]}");
            sb.AppendLine("它是不是在跟你说，你自己判断；不是就只接它自带的钩子，别替别人接话。");
        }
        sb.AppendLine("只走一条线：第二泡只能是第一泡的延续，别把现场别的话题混进来。");

        sb.AppendLine();
        sb.AppendLine("# 要传的意思（这是意思，不是台词；别照抄它的措辞）");
        sb.AppendLine(intent.Trim());

        var useful = pack["memes"]?["useful"] as JsonArray;
        if (useful is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("# 现场在玩的梗（用得上就接，用不上别硬塞）");
            foreach (var m in useful.Take(4))
                sb.AppendLine($"- {m?["term"]}：{m?["meaning"]}");
        }

        var self = pack["self_recent"] as JsonArray;
        if (self is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("# 你刚说过的（别再重复这些开头和梗）");
            foreach (var s in self.Take(3)) sb.AppendLine($"- {Clip(s?.GetValue<string>() ?? "", 12)}");
        }

        if (!string.IsNullOrWhiteSpace(extraHint))
        {
            sb.AppendLine();
            sb.AppendLine("# 上一版的问题（重写时改掉）");
            sb.AppendLine(extraHint!.Trim());
        }

        sb.AppendLine();
        sb.AppendLine(force
            ? "只输出你在群里要打的字，1-2 行，用换行分开。这一条必须接（不要输出 [SILENT]）。"
            : "只输出你在群里要打的字，1-2 行，用换行分开。不想接就输出 [SILENT]。");
        return sb.ToString();
    }

    private static void AppendLines(StringBuilder sb, JsonArray? arr, int? cap = null)
    {
        if (arr is null) return;
        var items = cap is null ? arr : arr.Take(cap.Value);
        foreach (var l in items)
        {
            if (l is null) continue;
            sb.AppendLine($"{l["t"]} {l["sender"]}: {l["text"]}");
        }
    }

    /// <summary>成文结果：Result=正文；Failure 非空 = 技术性失败（空正文/被截断），不是「不想说」。</summary>
    public sealed record Outcome(Result? Result, string? Failure);

    /// <summary>成文：attempts=1+RetryOnViolation。
    /// Result=null 且 Failure=null = 模型选择沉默（[SILENT]）；Failure 非空 = 空正文/截断等技术失败。</summary>
    public Outcome Compose(JsonNode pack, string intent, string mood, int attempts, bool force = false, JsonArray? anchor = null)
    {
        var w = _cfg.Writer;
        var violations = new List<string>();
        string? lastRaw = null;
        var tries = Math.Max(1, attempts);
        for (var attempt = 1; attempt <= tries; attempt++)
        {
            var body = UserPrompt(pack, intent, attempt > 1 ? string.Join("；", violations) : null, force, anchor);
            var reply = _brain.ChatRaw(SystemPrompt(mood), new[] { ("user", body) }, w.Temperature, w.MaxTokens);
            var raw = reply.Text;
            var truncated = string.Equals(reply.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
            lastRaw = raw;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AMY_WRITER_DUMP")))
            {
                try
                {
                    File.AppendAllText(Path.Combine(AppConfig.BaseDir, "logs", "writer-raw.log"),
                        $"\n===== {DateTime.Now:HH:mm:ss} force={force} attempt={attempt} rawLen={raw.Length} finish={reply.FinishReason}\n--prompt--\n{body}\n--raw--\n{raw}\n", Encoding.UTF8);
                }
                catch { }
            }

            // 空正文 / 被 max_tokens 截断：这是模型配额问题，不是「不想说」——重试，绝不把半句发出去
            if (truncated || raw.Length == 0)
            {
                var why = truncated
                    ? $"被截断（finish_reason=length，正文 {raw.Length} 字）"
                    : "空正文（content 为空）";
                if (attempt < tries)
                {
                    violations = new List<string>
                    {
                        truncated ? "上一版被截断了，这次只写 1-2 条短句，别先想再写" : "上一版是空的，这次必须直接输出要打在群里的字"
                    };
                    continue;
                }
                return new Outcome(null, why);
            }

            if (raw.Trim() is "[SILENT]" or "SILENT")
            {
                // brain 已经决定要接（force），声线还不能擅自沉默：再试一次，明确禁止 SILENT
                if (force && attempt < tries)
                {
                    violations = new List<string> { "这一条必须接，不要输出 [SILENT]" };
                    continue;
                }
                return new Outcome(null, null);
            }
            var bubbles = Normalize(raw, intent, SelfRecentOf(pack), out var hard, out var soft);
            if (bubbles.Count == 0) return new Outcome(null, "模型只回了 [SILENT] 之类的空内容");
            violations = hard.Concat(soft).ToList();
            if (violations.Count == 0) return new Outcome(new Result(bubbles, raw, new List<string>()), null);
        }

        // 重试仍不合规：能降级就降级（只留第一条、压到上限内），否则放弃
        if (lastRaw is null) return new Outcome(null, "没有拿到模型回复");
        var fallback = Normalize(lastRaw, intent, SelfRecentOf(pack), out _, out _).FirstOrDefault();
        if (fallback is null) return new Outcome(null, "重试后正文仍为空");
        if (fallback.Length > w.MaxLineChars * 2) return new Outcome(null, "重试后仍超长，放弃");
        return new Outcome(new Result(new List<string> { fallback }, lastRaw, violations), null);
    }

    /// <summary>清洗 + 规则检查。hard=必须重写，soft=可接受但记一笔（喂给重试提示）。</summary>
    private List<string> Normalize(string raw, string intentIn, List<string> selfRecentIn, out List<string> hard, out List<string> soft)
    {
        var w = _cfg.Writer;
        hard = new List<string>();
        soft = new List<string>();

        var text = (raw ?? "").Replace("\r\n", "\n").Trim();
        text = PrefixRx.Replace(text, "");
        var wrap = WrapQuoteRx.Match(text);
        if (wrap.Success) text = wrap.Groups[1].Value.Trim();
        text = text.Replace("```", "").Trim();

        var lines = text.Split('\n')
            .Select(l => l.Trim().Trim('-', '*', '•', ' '))
            .Where(l => l.Length > 0 && l != "[SILENT]")
            .ToList();

        if (lines.Count == 0) return lines;

        if (text.Contains("[SILENT]", StringComparison.OrdinalIgnoreCase))
            hard.Add("不要输出 [SILENT] 以外的内容（不想说就只输出 [SILENT]）");
        if (MarkdownRx.IsMatch(raw ?? "")) hard.Add("不要用 Markdown/列表/加粗");
        if (w.BanAIisms && AiismRx.IsMatch(text)) hard.Add("去掉解释、说教、客服腔（作为/不过/其实/建议/抱歉/用户 之类）");

        var total = lines.Sum(l => l.Length);
        if (total > w.MaxChars) hard.Add($"整条太长了（{total} 字 > {w.MaxChars}），砍到 1-2 条短句");
        foreach (var l in lines.Where(l => l.Length > w.MaxLineChars))
            hard.Add($"这一行太长（{l.Length} 字 > {w.MaxLineChars}），改成短句");

        // 别重复自己：开头 4 字或结尾 4 字与最近 3 条相同
        var self = _cfg.Writer.BanRepeat ? selfRecentIn : new List<string>();
        foreach (var s in self)
        {
            var head = s.Length >= 4 ? s[..4] : s;
            var tail = s.Length >= 4 ? s[^4..] : s;
            if (head.Length > 0 && lines.Any(l => l.StartsWith(head, StringComparison.Ordinal)))
                hard.Add($"'{head}…' 这个开头刚用过，换一个");
            if (tail.Length > 0 && lines.Any(l => l.EndsWith(tail, StringComparison.Ordinal)))
                hard.Add($"'{tail}' 这个收尾刚用过，换一个");
        }

        // 别照抄意图：与意图的最长公共子串 ≥8 个汉字就算抄
        var intent = intentIn ?? "";
        if (intent.Length >= 8 && lines.Any(l => LongestCommonRun(l, intent) >= 8))
            hard.Add("别照抄意图的措辞，用自己的说法重写");

        if (lines.Count > Math.Max(1, w.MaxBubbles))
        {
            lines = lines.Take(Math.Max(1, w.MaxBubbles)).ToList();
            soft.Add("行数超过上限，已只保留前几条");
        }
        return lines;
    }

    private static List<string> SelfRecentOf(JsonNode pack) =>
        (pack["self_recent"] as JsonArray)?
            .Select(x => x?.GetValue<string>() ?? "")
            .Where(s => s.Length > 0)
            .ToList() ?? new List<string>();

    /// <summary>最长公共连续子串长度（短串 O(nm) 足够）。</summary>
    private static int LongestCommonRun(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var best = 0;
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            for (var j = 1; j <= b.Length; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    cur[j] = prev[j - 1] + 1;
                    if (cur[j] > best) best = cur[j];
                }
            }
            prev = cur;
        }
        return best;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}




