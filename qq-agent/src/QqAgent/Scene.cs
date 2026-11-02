using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QqAgent;

/// <summary>现场包 v2 构建器：把「谁在说、上句是什么、图读没读、有哪些梗、要不要翻记忆」一次性算好，
/// 值班脑只读 amy-pending.json，不再自己翻 messages.jsonl / 调 Yuno / 数图片序号。</summary>
internal sealed class PackBuilder
{
    private readonly AppConfig _cfg;
    private readonly string _logDir;
    private readonly string _jsonlPath;
    private readonly string _dataDir;
    private readonly YunoClient _yuno;
    private readonly Func<string, long> _uidOf;

    private static readonly ConcurrentDictionary<string, (DateTime At, string Text)> MemoryCache = new();

    /// <summary>与实际落盘一致的序列化选项（缩进会显著增加字节数，预算必须按同一口径量）。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions WriteOpt = new() { WriteIndented = true };

    /// <summary>回忆线索：没有这些词就不预取 Yuno（默认 cue 门控）。</summary>
    private static readonly Regex CueRx = new(
        @"(上次|上回|之前|以前|那天|昨天|前天|还记得|你说过|以前你|那次|不是说过|又(来|是)|还记得吗)",
        RegexOptions.Compiled);

    public PackBuilder(AppConfig cfg, string logDir, string dataDir, YunoClient yuno, Func<string, long> uidOf)
    {
        _cfg = cfg;
        _logDir = logDir;
        _dataDir = dataDir;
        _jsonlPath = Path.Combine(logDir, "messages.jsonl");
        _yuno = yuno;
        _uidOf = uidOf;
    }

    public string JsonlPath => _jsonlPath;

    public JsonObject Build(string chat, string kind, long groupId, string[] scopes,
        long lineBefore, int seq, bool dryRun, string escalateHint)
    {
        var scene = _cfg.Scene;
        var take = Math.Max(8, scene.NowLines + scene.AmbientLines + 12);
        var tail = Journal.ReadTail(_jsonlPath, chat, take);
        var ledger = scene.OcrInline ? OcrLedger.Load(Path.Combine(_logDir, "ocr-ledger.jsonl")) : new OcrLedger();
        var memes = MemeLexicon.Load(Resolve(_cfg.Memes.Path));

        var incoming = tail.Where(l => l.Line > lineBefore && !l.IsSelf).ToList();
        var trigger = incoming.LastOrDefault() ?? tail.LastOrDefault(l => !l.IsSelf);

        // now = Amy 上次发言之后的原始现场（接不接、接哪句由值班 Amy 判断）；ambient = 更早、只用听懂梗
        var lastSelfIdx = tail.FindLastIndex(l => l.IsSelf);
        var segment = lastSelfIdx >= 0 ? tail.Skip(lastSelfIdx + 1).ToList() : tail.ToList();
        var now = segment.TakeLast(Math.Max(2, scene.NowLines)).ToList();
        var ambient = tail.Take(Math.Max(0, tail.Count - now.Count))
                          .TakeLast(Math.Max(0, scene.AmbientLines)).ToList();

        var pack = new JsonObject
        {
            ["v"] = 2,
            ["seq"] = seq,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["notify_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["dry_run"] = dryRun,
            ["chat"] = chat,
            ["kind"] = kind,
            ["group_id"] = groupId,
            ["scopes"] = ToArray(scopes),
            ["self_name"] = _cfg.SelfName,
            ["escalate_hint"] = escalateHint ?? "",
        };

        if (trigger is not null)
        {
            var tn = new JsonObject
            {
                ["line"] = trigger.Line,
                ["sender"] = trigger.Sender,
                ["uid"] = trigger.Uid > 0 ? trigger.Uid : _uidOf(trigger.Sender),
                ["text"] = trigger.Text,
                ["time"] = trigger.Time,
                ["pics"] = trigger.Pics,
            };
            if (trigger.PicW > 0 && trigger.PicH > 0)
            {
                tn["pic_w"] = trigger.PicW;
                tn["pic_h"] = trigger.PicH;
            }
            if (RefOf(trigger, tail) is { } tref) tn["ref"] = tref;
            pack["trigger"] = tn;
        }

        pack["incoming"] = LinesNode(incoming, ledger, tail);

        // 分层：now=要接的这一段，ambient=只用来听懂梗的背景
        pack["scene"] = new JsonObject
        {
            ["now"] = LinesNode(now, ledger, tail),
            ["ambient"] = LinesNode(ambient, ledger, tail),
            ["need_more"] = incoming.Count > Math.Max(12, scene.NowLines * 2),
        };

        // 曝光逻辑：每张图给 ocr_n（ocrfwd --pic n 用，1=最新），已入账的直接内联文字
        var imageLines = tail.Where(l => l.Pics > 0).ToList();
        var vision = new JsonArray();
        foreach (var l in now.Concat(ambient).Where(l => l.Pics > 0).DistinctBy(l => l.Line))
        {
            var idx = imageLines.FindIndex(x => x.Line == l.Line);
            var ocrText = ledger.TextOf(l.Line, l.Pics);
            var node = new JsonObject
            {
                ["line"] = l.Line,
                ["imgs"] = l.Pics,
                ["ocr_n"] = idx >= 0 ? imageLines.Count - idx : 0,
                ["has_ocr"] = ocrText is not null,
                ["sender"] = l.Sender,
            };
            if (ocrText is not null && scene.OcrInline) node["ocr_text"] = ocrText;
            if (l.PicW > 0 && l.PicH > 0) { node["pic_w"] = l.PicW; node["pic_h"] = l.PicH; }
            if (RefOf(l, tail) is { } vref) node["ref"] = vref;
            vision.Add(node);
        }
        pack["vision"] = vision;

        var roster = new JsonArray();
        foreach (var g in now.Concat(ambient).Where(l => !l.IsSelf).GroupBy(l => l.Sender))
        {
            roster.Add(new JsonObject
            {
                ["name"] = g.Key,
                ["uid"] = g.Last().Uid > 0 ? g.Last().Uid : _uidOf(g.Key),
                ["last_seen"] = g.Last().Time,
            });
        }
        pack["roster"] = roster;

        var selfRecent = tail.Where(l => l.IsSelf).TakeLast(3).Select(l => Clip(l.Text, 48)).ToList();
        pack["self_recent"] = ToArray(selfRecent);

        // 梗：命中现场才注入；冷却期只提示"刚用过"
        var sceneTexts = now.Select(l => l.Text).Concat(ambient.Select(l => l.Text)).ToList();
        if (trigger is not null) sceneTexts.Add(trigger.Text);
        var (useful, cooling) = memes.Match(sceneTexts, _cfg.Memes.CooldownHours);
        var memesNode = new JsonObject
        {
            ["useful"] = new JsonArray(useful.Take(5).Select(e => (JsonNode)new JsonObject
            {
                ["term"] = e.Term,
                ["meaning"] = Clip(e.Meaning, 60),
                ["examples"] = ToArray(e.Examples.Take(1)),
            }).ToArray()),
            ["cooling"] = ToArray(cooling.Take(6)),
        };
        var scan = Math.Max(0, _cfg.Memes.ScanLines);
        var recentForMining = scan > 0 ? Journal.ReadTail(_jsonlPath, chat, scan) : new List<JournalLine>();
        var candidates = memes.Detect(recentForMining, _cfg.Memes);
        memesNode["new_terms"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
        {
            ["term"] = c.Term,
            ["count"] = c.Count,
            ["senders"] = c.Senders,
            ["examples"] = ToArray(c.Examples),
        }).ToArray());
        pack["memes"] = memesNode;

        pack["memory"] = BuildMemory(trigger, scopes);

        var state = SceneStateStore.Load(Path.Combine(_logDir, "amy-scene-state.json"));
        if (state.TryGetValue(chat, out var st) && st.Topic.Length > 0)
        {
            var ageHours = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - st.Updated) / 3600.0;
            pack["scene_state"] = new JsonObject
            {
                ["topic"] = st.Topic,
                ["active_jokes"] = ToArray(st.Jokes),
                ["updated"] = DateTimeOffset.FromUnixTimeSeconds(st.Updated).ToLocalTime().ToString("HH:mm"),
                ["stale"] = ageHours > 2,
            };
        }

        pack["limits"] = new JsonObject
        {
            ["now"] = scene.NowLines,
            ["ambient"] = scene.AmbientLines,
            ["max_bytes"] = scene.MaxBytes,
        };

        TrimToBudget(pack, scene.MaxBytes);
        return pack;
    }

    /// <summary>记忆读取：**默认一回合什么都不带**（人物形象/印象卡在开局 `group-pack.md` 里加载一次，
    /// 不再逐回合投递——2026-10-28 owner 定案）；`MemoryGate=always` 才回带当前对话者档案卡（调试/兜底），
    /// `cue` 只保留"上次那个事"类向量检索。</summary>
    private JsonObject BuildMemory(JournalLine? trigger, string[] scopes)
    {
        var mode = (_cfg.Scene.MemoryGate ?? "cue").ToLowerInvariant();
        var node = new JsonObject
        {
            ["included"] = false,
            ["card"] = "",
            ["cards"] = new JsonArray(),
            ["reason"] = new JsonArray(),
            ["scope"] = scopes.Length > 0 ? scopes[0] : "",
            ["uid"] = 0,
        };
        if (trigger is null) { node["reason"]!.AsArray().Add("no_trigger"); return node; }

        var uid = trigger.Uid > 0 ? trigger.Uid : _uidOf(trigger.Sender);
        node["uid"] = uid;
        var scope = scopes.Length > 0 ? scopes[0] : "";
        var reasons = node["reason"]!.AsArray();

        // ---- 档案层：只在 always 下回带（默认不投；人物形象开局由 group-pack.md 一次性加载） ----
        if (uid > 0 && scope.Length > 0 && mode == "always")
        {
            var card = GetCardCached(scope, uid);
            if (!string.IsNullOrWhiteSpace(card))
            {
                node["card"] = Clip(card!, 900);
                node["included"] = true;
                reasons.Add("card");
            }
            else
            {
                reasons.Add("card_missing");
            }
        }

        if (mode == "off")
        {
            reasons.Add("gate_off");
            return node;
        }

        // ---- 向量层：只在明确的回忆线索 / 被点名问句时兜底 ----
        var cue = CueRx.IsMatch(trigger.Text);
        var named = trigger.Text.Contains(_cfg.SelfName, StringComparison.OrdinalIgnoreCase);
        var asked = trigger.Text.Contains('?') || trigger.Text.Contains('？') || trigger.Text.Contains("吗");
        var include = mode == "always" || cue || (named && asked);
        if (!include)
        {
            reasons.Add("no_cue");
            return node;
        }

        var cacheKey = $"vec|{trigger.Chat}|{uid}|{trigger.Text.GetHashCode()}";
        var now = DateTime.UtcNow;
        if (MemoryCache.TryGetValue(cacheKey, out var hit) && (now - hit.At).TotalSeconds < 60)
        {
            if (hit.Text.Length > 0)
            {
                node["cards"] = ToArray(new[] { hit.Text });
                node["included"] = true;
                reasons.Add("vector_cache");
            }
            else reasons.Add("vector_miss");
            return node;
        }

        // 预取不能拖住 spawn：Yuno 卡住时 3s 放弃，按无记忆降级
        string text;
        try
        {
            var call = Task.Run(() => _yuno.Context(Clip(trigger.Text, 200), scopes, uid,
                _cfg.Scene.MemoryTopK, _cfg.Scene.MemoryBudget) ?? "");
            text = call.Wait(3000) ? call.Result : "";
        }
        catch { text = ""; }
        MemoryCache[cacheKey] = (now, text);
        if (MemoryCache.Count > 64) MemoryCache.Clear();

        if (text.Length == 0)
        {
            reasons.Add("vector_miss");
            return node;
        }
        node["included"] = true;
        reasons.Add(cue ? "cue" : "named_question");
        node["cards"] = ToArray(new[] { Clip(text, Math.Max(200, _cfg.Scene.MemoryBudget)) });
        return node;
    }

    /// <summary>印象卡缓存（120s）：同一个人连续发言不重复打 Yuno。</summary>
    private string? GetCardCached(string scope, long uid)
    {
        var key = $"card|{scope}|{uid}";
        var now = DateTime.UtcNow;
        if (MemoryCache.TryGetValue(key, out var hit) && (now - hit.At).TotalSeconds < 120) return hit.Text;
        string? card;
        try
        {
            var call = Task.Run(() => _yuno.Card(scope, uid));
            card = call.Wait(2000) ? call.Result : null;
        }
        catch { card = null; }
        MemoryCache[key] = (now, card ?? "");
        return card;
    }


    private static JsonArray LinesNode(IEnumerable<JournalLine> lines, OcrLedger ledger, List<JournalLine> all)
    {
        var arr = new JsonArray();
        foreach (var l in lines)
        {
            var n = l.ToSceneNode();
            var ocr = l.Pics > 0 ? ledger.TextOf(l.Line, l.Pics) : null;
            if (ocr is not null) n["text"] = $"[图片|{ocr}]";
            if (n["ref"] is JsonObject rf && FindQuoted(l, all) is { } hit)
            {
                // 引用解析到具体行号/ts：话里说「这张图」时，图属于被引的那条，不属于当前这条
                rf["line"] = hit.Line;
                rf["ts"] = hit.Ts;
                rf["pics"] = hit.Pics;
                rf["sender"] = hit.Sender;
            }
            arr.Add(n);
        }
        return arr;
    }

    /// <summary>2026-10-27：引用标识。有引文→按「作者+引文」找最近一条；只引作者（引的是纯图消息）→
    /// 优先该作者最近的一条带图消息。找不到就只留 from/text，让值班脑自己去认，别瞎配图。</summary>
    private static JsonObject? RefOf(JournalLine l, List<JournalLine> all)
    {
        if (l.QuoteFrom.Length == 0 && l.QuoteText.Length == 0) return null;
        var rf = new JsonObject { ["from"] = l.QuoteFrom, ["text"] = l.QuoteText };
        var hit = FindQuoted(l, all);
        if (hit is not null)
        {
            rf["line"] = hit.Line;
            rf["ts"] = hit.Ts;
            rf["pics"] = hit.Pics;
            rf["sender"] = hit.Sender;
        }
        return rf;
    }

    private static JournalLine? FindQuoted(JournalLine cur, List<JournalLine> all)
    {
        var from = cur.QuoteFrom;
        if (from.Length == 0) return null;
        var q = cur.QuoteText;
        JournalLine? hit = null;
        JournalLine? hitWithPic = null;
        foreach (var x in all)
        {
            if (x.Line >= cur.Line) break;
            if (x.Sender != from) continue;
            if (q.Length > 0)
            {
                if (x.Text.Contains(q, StringComparison.Ordinal)) hit = x;
                continue;
            }
            hit = x;
            if (x.Pics > 0) hitWithPic = x;
        }
        return hitWithPic ?? hit;
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var s in items) arr.Add(s);
        return arr;
    }

    private static void TrimToBudget(JsonObject pack, int maxBytes)
    {
        if (maxBytes <= 0) return;
        for (var guard = 0; guard < 600; guard++)
        {
            var bytes = Encoding.UTF8.GetByteCount(pack.ToJsonString(WriteOpt));
            if (bytes <= maxBytes) break;
            if (pack["scene"] is not JsonObject scene) break;
            var memesNode = pack["memes"] as JsonObject;
            // 先砍"线索类"（候选新词/梗例句），再砍背景，最后才动 now
            if (memesNode?["new_terms"] is JsonArray nt && nt.Count > 0) { nt.RemoveAt(nt.Count - 1); continue; }
            if (memesNode?["useful"] is JsonArray useful && useful.Count > 3) { useful.RemoveAt(useful.Count - 1); continue; }
            if (pack["incoming"] is JsonArray inc && inc.Count > 2) { inc.RemoveAt(0); continue; }
            if (scene["ambient"] is JsonArray amb && amb.Count > 0) { amb.RemoveAt(0); continue; }
            if (scene["now"] is JsonArray now && now.Count > 2) { now.RemoveAt(0); continue; }
            if (pack["roster"] is JsonArray roster && roster.Count > 2) { roster.RemoveAt(roster.Count - 1); continue; }
            if (pack["memory"] is JsonObject mem && mem["cards"] is JsonArray cards && cards.Count > 0) { cards.RemoveAt(cards.Count - 1); continue; }
            if (pack["vision"] is JsonArray vision && vision.Count > 1) { vision.RemoveAt(vision.Count - 1); continue; }
            if (memesNode?["useful"] is JsonArray u2 && u2.Count > 0) { u2.RemoveAt(u2.Count - 1); continue; }
            break;
        }
    }


    private string Resolve(string p) =>
        Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppConfig.BaseDir, p));

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}




