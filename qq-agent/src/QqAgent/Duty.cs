using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QqAgent;

/// <summary>值班脑的工具面（qq-agent.exe duty …）。一条命令完成一步，直播/ack/落库自动做，
/// 脑不需要（也不应该）自己拼 HTTP、自己写直播行、自己算去重。</summary>
internal static class Duty
{
    private const long SelfUid = 1000000001L;   // 占位：改成机器人自己的 QQ 号（daemon 读 config 时可覆盖）

    private sealed class Ctx
    {
        public required AppConfig Cfg;
        public required string LogDir;
        public required string DataDir;
        public required string RepoRoot;
        public required string PendingPath;
        public required string AckPath;
        public required string LivePath;
        public required string TurnsPath;
        public required string IntentPath;
        public required YunoClient Yuno;
        public required Brain Brain;
        public required MemeLexicon Memes;

        public string Resolve(string p) => DshBrain.Resolve(Cfg, p);
    }

    private static Ctx Load(string[] args)
    {
        var cfgPath = FindConfig(args);
        var cfg = AppConfig.Load(cfgPath);
        var logDir = Path.Combine(AppConfig.BaseDir, "logs");
        var dataDir = Path.Combine(AppConfig.BaseDir, "data");
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(dataDir);
        var repo = DshBrain.Resolve(cfg, cfg.AmyRuntime.ProjectRoot);
        return new Ctx
        {
            Cfg = cfg,
            LogDir = logDir,
            DataDir = dataDir,
            RepoRoot = repo,
            PendingPath = Path.Combine(logDir, "amy-pending.json"),
            AckPath = Path.Combine(logDir, "amy-ack.json"),
            LivePath = Path.Combine(logDir, "amy-live.log"),
            TurnsPath = Path.Combine(logDir, "amy-turns.jsonl"),
            IntentPath = Path.Combine(logDir, "amy-intent.txt"),
            Yuno = new YunoClient(cfg.Yuno),
            Brain = new Brain(cfg.Model),
            Memes = MemeLexicon.Load(Path.IsPathRooted(cfg.Memes.Path)
                ? cfg.Memes.Path
                : Path.Combine(AppConfig.BaseDir, cfg.Memes.Path)),
        };
    }

    private static string FindConfig(string[] args)
    {
        var explicitPath = ArgValue(args, "--config", "");
        if (explicitPath.Length > 0) return Path.GetFullPath(explicitPath);
        var candidates = new List<string>();
        var cwd = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(cwd, "config.json"));
        candidates.Add(Path.Combine(cwd, "qq-agent", "config.json"));
        candidates.Add(Path.Combine(baseDir, "config.json"));
        candidates.Add(Path.Combine(cwd, "..", "config.json"));
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        throw new FileNotFoundException("找不到 config.json（用 --config 指定，或从 qq-agent/ 或仓库根运行）");
    }

    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();
        try
        {
            if (sub == "help" || sub is "-h" or "--help") return Help();
            var ctx = Load(rest);
            var cleaned = StripPair(rest, "--config");
            return sub switch
            {
                "doctor" => Doctor(ctx),
                "pack" => PackCmd(ctx, cleaned),
                "say" => SayCmd(ctx, cleaned),
                "chat" => ChatCmd(ctx, cleaned),
                "skip" => SkipCmd(ctx, cleaned),
                "ack" => AckCmd(ctx, cleaned),
                "context" => ContextCmd(ctx, cleaned),
                "after-chat" => AfterChatCmd(ctx, cleaned),
                "add-fact" => AddFactCmd(ctx, cleaned),
                "meme" => MemeCmd(ctx, cleaned),
                "state" => StateCmd(ctx, cleaned),
                "roster" => RosterCmd(ctx, cleaned),
                "stats" => StatsCmd(ctx, cleaned),
                "prune" => PruneCmd(ctx, cleaned),
                "live" => LiveCmd(ctx, cleaned),
                "card" => CardCmd(ctx, cleaned),
                "run" => RunCmd(ctx, cleaned),
                "sendfile" => SendArtifactCmd(ctx, cleaned, "sendfile", "file"),
                "sendimg" => SendArtifactCmd(ctx, cleaned, "sendimg", "img"),
                "action-log" => ActionLogCmd(ctx, cleaned),
                "sticker" => StickerCmd(ctx, cleaned),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[duty] ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            qq-agent duty <command>
              doctor                                       全链体检（DSH 安装/补丁/密钥/Yuno/QQ/daemon/pending/ack/词库/人设漂移）
              pack [--seq N] [--chat X] [--rebuild] [--out f]  打印现场包（默认读 pending；--rebuild 从 jsonl 重建）
              say --intent "…"|--intent-file f [--re <line>] [--mood dark] [--n 3]   只成文，不发送（给出候选）
              chat --seq N --intent "…"|--intent-file f [--re <line>] [--mood dark] [--no-send] [--try 2]
                                                   一步：成文→守卫→发送→after_chat→ack→直播
                                                   --re = 你选定的回话锚点（本包内的行号）；不传只回最新一条
              skip --seq N --why "…"                本回合不说话（直播 + ack）
              ack --seq N                           只写消费水位
              context --seq N                       调 /amy/context 打印记忆上下文
              after-chat --seq N --reply-file f     兜底落库（chat 已自动做，一般不用）
              add-fact --scope S --key K --fact "…" [--importance 0.7] [--confidence 0.85]
              meme list|add|prune|bump              · add <term> --meaning "…" [--examples "a;b"] [--scope S]
                                                    · bump <file|text>（发送后记账，chat 已自动做）
              state set --chat X --topic "…" [--jokes "a,b"] | state get [--chat X]
              roster add "<昵称>" <QQ> [--scope group:123456789]
              stats [--last 20]                     最近回合时延/守卫命中统计
              prune [--sessions 2d] [--snapshots 7d]  清 DSH 会话与旧截图
              card --uid N [--scope group:…] [--seq N]  读某人印象卡（档案层；比向量检索便宜）
              run --seq N [--cwd d] [--timeout s] [--dry-run] -- <命令…>
                                                   本机行动：owner 全开，群友仅 Action.GroupAllowedCommands 白名单
              sendfile --seq N --file P [--dry-run]     发文件到本轮会话（白名单目录）
              sendimg  --seq N --file P [--dry-run]     发图片（白名单目录）
              action-log [--last N]                 看行动记录（logs/amy-actions.jsonl）
              sticker --seq N [--name 备注 | --index n] [--list] [--dry-run]
                                                   一键发表情（--name 按备注匹配，推荐；--index 第 n 个，默认 1）；--list 只列
              live "<STEP>: msg" [--seq N]          手写一行直播
            """);
        return 2;
    }

    // ===================== 发送/落库 =====================

    private static int ChatCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        if (seq <= 0) { Console.Error.WriteLine("[duty chat] 需要 --seq N"); return 2; }
        var pack = ReadPending(ctx);
        var (ok, p, err) = VerifySeq(ctx, pack, seq);
        if (!ok) { Console.Error.WriteLine("[duty chat] " + err); return 2; }
        pack = p;

        var noSend = HasFlag(args, "--no-send");
        var dryRun = BoolOf(pack, "dry_run");
        var chat = pack["chat"]?.GetValue<string>() ?? "";
        var mood = ArgValue(args, "--mood", "normal");
        var intent = ResolveIntent(ctx, args);
        if (intent.Length == 0 && !HasFlag(args, "--text-file"))
        {
            Console.Error.WriteLine("[duty chat] 需要 --intent / --intent-file（或应急 --text-file）");
            return 2;
        }
        var (anchor, anchorErr) = ResolveAnchor(pack, args);
        if (anchorErr is not null) { Console.Error.WriteLine("[duty chat] " + anchorErr); return 2; }

        var sw = Stopwatch.StartNew();
        var bubbles = new List<string>();
        var violations = new List<string>();
        var raw = "";

        var textFile = ArgValue(args, "--text-file", "");
        if (textFile.Length > 0)
        {
            if (!File.Exists(textFile)) { Console.Error.WriteLine($"[duty chat] 文件不存在: {textFile}"); return 2; }
            bubbles = File.ReadAllText(textFile, Encoding.UTF8)
                .Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).Take(2).ToList();
        }
        else if (!ctx.Cfg.Writer.Enabled)
        {
            Console.Error.WriteLine("[duty chat] Writer 已关闭：必须用 --text-file 提供正文");
            return 2;
        }
        else
        {
            var writer = new Writer(ctx.Cfg, Writer.LoadPersona(ctx.Cfg), ctx.Brain);
            Writer.Outcome outcome;
            try
            {
                outcome = writer.Compose(pack, intent, mood, 1 + Math.Max(0, ctx.Cfg.Writer.RetryOnViolation), force: true, anchor);
            }
            catch (Exception ex)
            {
                Live(ctx, seq, "BRAIN-ERR", "声线模块失败：" + ex.Message);
                Console.Error.WriteLine("[duty chat] 声线模块失败: " + ex.Message);
                return 1;
            }
            if (outcome.Result is null)
            {
                if (outcome.Failure is not null)
                    Live(ctx, seq, "WRITER-EMPTY", "声线没出正文：" + outcome.Failure);
                else
                    Live(ctx, seq, "SKIP", "writer 判断不说（[SILENT]）");
                if (!WriteAck(ctx, seq)) return AckFail(ctx, seq);
                return 0;
            }
            var result = outcome.Result;
            bubbles = result.Bubbles;
            violations = result.Violations;
            raw = result.Raw;
        }
        var writerMs = sw.ElapsedMilliseconds;

        if (bubbles.Count == 0) { Live(ctx, seq, "SKIP", "正文为空"); if (!WriteAck(ctx, seq)) return AckFail(ctx, seq); return 0; }

        if (noSend || dryRun)
        {
            AppendDryRun(ctx, seq, chat, bubbles, violations);
            Live(ctx, seq, "SAY", $"(dry) 《{string.Join("》/《", bubbles)}》{ViolationNote(violations)}");
            if (!WriteAck(ctx, seq)) return AckFail(ctx, seq);
            WriteTurn(ctx, seq, pack, writerMs, 0, bubbles, violations, intent, dry: true);
            return 0;
        }

        // —— 发送：唯一发送入口，先核会话，再逐泡发 ——
        var ui = QqUi.Attach();
        if (ui.Header() != chat && !ui.OpenChat(chat))
        {
            Live(ctx, seq, "SEND-FAIL", $"切换会话失败 current='{ui.Header()}' want='{chat}'");
            return 1;
        }
        if (ui.Header() != chat)
        {
            Live(ctx, seq, "SEND-FAIL", $"会话标题校验失败 current='{ui.Header()}' want='{chat}'（拒绝发送）");
            return 1;
        }

        var sentIdx = 0;
        var sendOk = true;
        foreach (var b in bubbles)
        {
            ui.SendQuiet(b);
            var verified = VerifySent(ui, b);
            if (!verified)
            {
                // 二次机会：回读失败常是渲染慢，再等 2s 复查
                Thread.Sleep(2000);
                verified = VerifySent(ui, b);
            }
            sentIdx++;
            if (!verified)
            {
                sendOk = false;
                Live(ctx, seq, "SEND-FAIL", $"第 {sentIdx} 泡未回读（可能未送达）：《{b}》");
                break;
            }
            if (sentIdx < bubbles.Count)
            {
                var min = Math.Max(0, ctx.Cfg.Writer.BubbleDelayMinMs);
                var max = Math.Max(min, ctx.Cfg.Writer.BubbleDelayMaxMs);
                Thread.Sleep(Random.Shared.Next(min, max + 1));
            }
        }

        var gotText = string.Join("\n", bubbles.Take(sentIdx));
        var sentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (sendOk)
        {
            Live(ctx, seq, "SEND", $"ok x{sentIdx} 《{gotText.Replace("\n", "》/《")}》");
            var payload = BuildAfterChatText(pack);
            var kind = pack["kind"]?.GetValue<string>() ?? "group";
            var gid = pack["group_id"]?.GetValue<long>() ?? 0;
            var uid = pack["trigger"]?["uid"]?.GetValue<long>() ?? 0;
            var sender = pack["trigger"]?["sender"]?.GetValue<string>() ?? "";
            if (kind == "group" && gid > 0)
                ctx.Yuno.AfterChat(kind, gid, uid, sender, payload, sentAt, gotText);
            else if (kind == "c2c" && uid > 0)
                ctx.Yuno.AfterChat(kind, 0, uid, sender, payload, sentAt, gotText);
            else
                Live(ctx, seq, "AFTER_CHAT", "跳过（scope 未解析）");
            if (kind == "group" && gid > 0) Live(ctx, seq, "AFTER_CHAT", "双边原文落库 ok");
            ctx.Memes.Bump(bubbles);
        }

        WriteAck(ctx, seq);   // 发送已成功：ack 失败只告警不改变结果
        WriteTurn(ctx, seq, pack, writerMs, sentAt, bubbles, violations, intent, dry: false);
        if (violations.Count > 0) Live(ctx, seq, "GUARD", "降级发送：" + string.Join("；", violations));
        return sendOk ? 0 : 3;
    }

    private static bool VerifySent(QqUi ui, string bubble)
    {
        try
        {
            var key = bubble.Trim();
            if (key.Length == 0) return true;
            return ui.Messages().Any(m => m.IsSelf && m.Text.Contains(key, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    private static void AppendDryRun(Ctx ctx, int seq, string chat, List<string> bubbles, List<string> violations)
    {
        try
        {
            var path = Path.Combine(ctx.LogDir, "amy-dryrun.txt");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] duty#{seq} chat='{chat}'\n{string.Join("\n", bubbles)}\n" +
                (violations.Count > 0 ? $"[guard] {string.Join("；", violations)}\n" : "") + "\n",
                Encoding.UTF8);
        }
        catch { }
    }

    private static string ViolationNote(List<string> v) => v.Count == 0 ? "" : "（守卫降级：" + string.Join("；", v) + "）";

    private static string BuildAfterChatText(JsonNode pack)
    {
        var incoming = pack["incoming"] as JsonArray;
        if (incoming is null) return pack["trigger"]?["text"]?.GetValue<string>() ?? "";
        return string.Join("\n", incoming.Select(m => $"{m?["sender"]}: {m?["text"]}"));
    }

    private static int SkipCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        var why = ArgValue(args, "--why", "（未写理由）");
        if (seq <= 0) { Console.Error.WriteLine("[duty skip] 需要 --seq N"); return 2; }
        var (ok, _, err) = VerifySeq(ctx, ReadPending(ctx), seq);
        if (!ok) { Console.Error.WriteLine("[duty skip] " + err); return 2; }
        Live(ctx, seq, "SKIP", why);
        if (!WriteAck(ctx, seq)) return AckFail(ctx, seq);
        var pack = ReadPending(ctx);
        WriteTurn(ctx, seq, pack, 0, 0, new List<string>(), new List<string> { "skip:" + why }, "", dry: BoolOf(pack, "dry_run"));
        return 0;
    }

    private static int AckCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        if (seq <= 0) { Console.Error.WriteLine("[duty ack] 需要 --seq N"); return 2; }
        WriteAck(ctx, seq);   // 发送已成功：ack 失败只告警不改变结果
        Live(ctx, seq, "ACK", "消费水位已写");
        return 0;
    }

    // ===================== 现场包 =====================

    private static JsonNode? ReadPending(Ctx ctx)
    {
        try
        {
            return File.Exists(ctx.PendingPath)
                ? JsonNode.Parse(File.ReadAllText(ctx.PendingPath, Encoding.UTF8))
                : null;
        }
        catch { return null; }
    }

    private static (bool Ok, JsonNode Pack, string Error) VerifySeq(Ctx ctx, JsonNode? pack, int seq)
    {
        if (pack is null) return (false, new JsonObject(), "amon-pending.json 不存在（daemon 还没投递？）");
        var got = (int)(pack["seq"]?.GetValue<long>() ?? 0);
        if (got != seq) return (false, pack, $"pending seq={got} != 请求 seq={seq}（这条已被更新的消息覆盖，处理最新投递）");
        return (true, pack, "");
    }

    private static int PackCmd(Ctx ctx, string[] args)
    {
        JsonNode? pack = null;
        if (!HasFlag(args, "--rebuild")) pack = ReadPending(ctx);
        if (pack is null)
        {
            var chat = ArgValue(args, "--chat", ctx.Cfg.Daemon.Chat);
            var builder = new PackBuilder(ctx.Cfg, ctx.LogDir, ctx.DataDir, ctx.Yuno, _ => 0);
            var scopes = ScopeOf(ctx, chat);
            var gid = scopes.Length > 0 && scopes[0].StartsWith("group:", StringComparison.Ordinal)
                ? ParseLong(scopes[0]["group:".Length..]) : 0;
            pack = builder.Build(chat, gid > 0 ? "group" : "c2c", gid, scopes, long.MaxValue,
                (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000), ctx.Cfg.AmyRuntime.DryRun, "");
        }
        var json = pack.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var outPath = ArgValue(args, "--out", "");
        if (outPath.Length > 0)
        {
            File.WriteAllText(outPath, json, Encoding.UTF8);
            Console.WriteLine($"[duty pack] {Encoding.UTF8.GetByteCount(json)} bytes -> {outPath}");
        }
        else
        {
            Console.WriteLine(json);
            Console.Error.WriteLine($"[duty pack] bytes={Encoding.UTF8.GetByteCount(json)}");
        }
        return 0;
    }

    private static int SayCmd(Ctx ctx, string[] args)
    {
        var packFile = ArgValue(args, "--pack", "");
        JsonNode? pack = packFile.Length > 0 && File.Exists(packFile)
            ? JsonNode.Parse(File.ReadAllText(packFile, Encoding.UTF8))
            : ReadPending(ctx);
        if (pack is null)
        {
            var chat = ArgValue(args, "--chat", ctx.Cfg.Daemon.Chat);
            var scopes = ScopeOf(ctx, chat);
            var gid = scopes.Length > 0 && scopes[0].StartsWith("group:", StringComparison.Ordinal)
                ? ParseLong(scopes[0]["group:".Length..]) : 0;
            pack = new PackBuilder(ctx.Cfg, ctx.LogDir, ctx.DataDir, ctx.Yuno, _ => 0)
                .Build(chat, gid > 0 ? "group" : "c2c", gid, scopes, long.MaxValue, 1, true, "");
        }
        var intent = ResolveIntent(ctx, args);
        if (intent.Length == 0) { Console.Error.WriteLine("[duty say] 需要 --intent 或 --intent-file"); return 2; }
        var (anchor, anchorErr) = ResolveAnchor(pack, args);
        if (anchorErr is not null) { Console.Error.WriteLine("[duty say] " + anchorErr); return 2; }
        var mood = ArgValue(args, "--mood", "normal");
        var n = Math.Max(1, ParseInt(ArgValue(args, "--n", "1")));
        var persona = Writer.LoadPersona(ctx.Cfg);
        var writer = new Writer(ctx.Cfg, persona, ctx.Brain);
        for (var i = 1; i <= n; i++)
        {
            var sw = Stopwatch.StartNew();
            var outcome = writer.Compose(pack, intent, mood, 1 + Math.Max(0, ctx.Cfg.Writer.RetryOnViolation), force: false, anchor);
            sw.Stop();
            var result = outcome.Result;
            if (result is null)
            {
                var tag = outcome.Failure is null ? "[SILENT]" : "[EMPTY: " + outcome.Failure + "]";
                Console.WriteLine($"--- #{i} {tag} ({sw.ElapsedMilliseconds}ms) ---");
                continue;
            }
            Console.WriteLine($"--- #{i} ({sw.ElapsedMilliseconds}ms) ---");
            foreach (var b in result.Bubbles) Console.WriteLine(b);
            if (result.Violations.Count > 0) Console.WriteLine("[guard] " + string.Join("；", result.Violations));
        }
        return 0;
    }

    private static string ResolveIntent(Ctx ctx, string[] args)
    {
        var intent = ArgValue(args, "--intent", "");
        if (intent.Length == 0)
        {
            var file = ArgValue(args, "--intent-file", "");
            if (file.Length > 0 && File.Exists(file)) intent = File.ReadAllText(file, Encoding.UTF8).Trim();
        }
        return intent;
    }

    /// <summary>--re &lt;行号|段&gt;：值班 Amy 选定的回话锚点（必须是本包里真实存在的行）。
    /// 支持 `573` / `573-575` / `573,575`，多行必须是**同一个人**连着说的话（一次只回一个人；
    /// 他那几句当一整句话看）。不传就只给最新一条，但 prompt 已改成「不一定冲你」
    /// （2026-10-28 定案：投递层不再替 Amy 判断"这句是冲你说的"，见 Writer.UserPrompt）。</summary>
    private static (JsonArray? Nodes, string? Error) ResolveAnchor(JsonNode pack, string[] args)
    {
        var raw = ArgValue(args, "--re", "").Trim();
        if (raw.Length == 0) return (null, null);

        var wanted = new SortedSet<long>();
        foreach (var part in raw.Split(',', ';', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            var dash = token.IndexOf('-');
            if (dash > 0)
            {
                var lo = ParseLong(token[..dash]);
                var hi = ParseLong(token[(dash + 1)..]);
                if (lo <= 0 || hi < lo)
                    return (null, $"--re '{token}' 不是有效行号段（写法：573 或 573-575 或 573,575）");
                if (hi - lo > 20) return (null, $"--re '{token}' 跨度太大（一次最多 20 句）");
                for (var l = lo; l <= hi; l++) wanted.Add(l);
            }
            else
            {
                var l = ParseLong(token);
                if (l <= 0)
                    return (null, $"--re '{token}' 不是有效行号（写法：573 或 573-575 或 573,575）");
                wanted.Add(l);
            }
        }
        if (wanted.Count == 0) return (null, "--re 没解析出行号");

        var nodes = new List<JsonNode>();
        var missing = new List<long>();
        foreach (var line in wanted)
        {
            var node = FindPackLine(pack, line);
            if (node is null) missing.Add(line); else nodes.Add(node);
        }
        if (missing.Count > 0)
            return (null, $"--re {string.Join(",", missing)} 不在本包（trigger/incoming/scene.now/ambient）里；先 `duty pack` 核对行号，别猜");

        // 一次只回一个人：同一个人的多句可以一起锚，混进别人就拒绝（防"一条回复回两个人"）
        string SenderKey(JsonNode n)
        {
            var uid = n["uid"] is JsonValue uv && uv.TryGetValue<long>(out var u) ? u : 0;
            return uid > 0 ? $"uid:{uid}" : "name:" + (n["sender"]?.GetValue<string>() ?? "");
        }
        var first = nodes[0];
        var key0 = SenderKey(first);
        var stranger = nodes.FirstOrDefault(n => SenderKey(n) != key0);
        if (stranger is not null)
            return (null, $"一次只回一个人：锚点里混了 {first["sender"]} 和 {stranger["sender"]} 两个说话人");

        var arr = new JsonArray();
        foreach (var n in nodes) arr.Add(n.DeepClone());
        return (arr, null);
    }

    private static JsonNode? FindPackLine(JsonNode pack, long line)
    {
        static JsonNode? Find(JsonArray? arr, long line)
        {
            if (arr is null) return null;
            foreach (var n in arr)
                if (n?["line"] is JsonValue v && v.TryGetValue<long>(out var l) && l == line) return n;
            return null;
        }

        if (pack["trigger"] is { } t && t["line"] is JsonValue tv && tv.TryGetValue<long>(out var tl) && tl == line)
            return t;
        return Find(pack["incoming"] as JsonArray, line)
            ?? Find(pack["scene"]?["now"] as JsonArray, line)
            ?? Find(pack["scene"]?["ambient"] as JsonArray, line);
    }

    // ===================== 行动（L2 本机操作 / L1 产物发送） =====================

    /// <summary>一键发表情：固定能力（L1，任何群友可请求），不探索、不思考。
    /// 依赖 EmojiSender：点表情→我的收藏第 N 个→点发送→回读。</summary>
    private static int StickerCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        if (seq <= 0) { Console.Error.WriteLine("[duty sticker] 需要 --seq N"); return 2; }
        var (ok, pack, err) = VerifySeq(ctx, ReadPending(ctx), seq);
        if (!ok) { Console.Error.WriteLine("[duty sticker] " + err); return 2; }
        if (!ctx.Cfg.Action.Enabled) { Live(ctx, seq, "ACT-DENY", "Action.Enabled=false"); return 3; }

        var uid = pack["trigger"]?["uid"]?.GetValue<long>() ?? 0;
        var chat = pack["chat"]?.GetValue<string>() ?? "";
        var index = Math.Max(1, ParseInt(ArgValue(args, "--index", "1")));
        var name = ArgValue(args, "--name", "");
        var dry = HasFlag(args, "--dry-run");
        var tab = ArgValue(args, "--tab", "我的收藏");
        var tier = ctx.Cfg.Action.OwnerUids.Contains(uid) ? "owner" : "group";

        var ui = QqUi.Attach();
        if (!ui.PinForOperation())
        {
            Live(ctx, seq, "SEND-STICKER-FAIL", "QQ 不在当前桌面/无法置前");
            return 1;
        }
        if (ui.Header() != chat && !ui.OpenChat(chat))
        {
            Live(ctx, seq, "SEND-STICKER-FAIL", $"切换会话失败 current='{ui.Header()}' want='{chat}'");
            return 1;
        }
        if (ui.Header() != chat)
        {
            Live(ctx, seq, "SEND-STICKER-FAIL", $"会话标题校验失败 current='{ui.Header()}' want='{chat}'");
            return 1;
        }

        if (HasFlag(args, "--list"))
        {
            Live(ctx, seq, "STICKER-LIST", "列表情面板");
            return EmojiSender.List(tab);
        }

        var (sent, detail) = EmojiSender.Send(index, dry, tab, name.Length > 0 ? name : null);
        Live(ctx, seq, sent ? "SEND-STICKER" : "SEND-STICKER-FAIL", detail);
        LogAction(ctx, seq, uid, tier, name.Length > 0 ? $"send:sticker name={name}" : $"send:sticker index={index}", "", dry, sent ? 0 : 1, 0, detail, "");
        Console.WriteLine(detail);
        return sent ? 0 : 1;
    }
    /// <summary>本机命令：owner 任意（危险片段除外）；群友只能跑白名单脚本（无 shell 元字符）。
    /// 用法：duty run --seq N [--cwd 相对目录] [--timeout 秒] [--dry-run] -- <命令...></summary>
    private static int RunCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        if (seq <= 0) { Console.Error.WriteLine("[duty run] 需要 --seq N"); return 2; }
        var (ok, pack, err) = VerifySeq(ctx, ReadPending(ctx), seq);
        if (!ok) { Console.Error.WriteLine("[duty run] " + err); return 2; }
        var uid = pack["trigger"]?["uid"]?.GetValue<long>() ?? 0;
        var chat = pack["chat"]?.GetValue<string>() ?? "";
        var a = ctx.Cfg.Action;
        if (!a.Enabled) { Live(ctx, seq, "ACT-DENY", "行动力已关闭"); return 3; }

        var rest = args.SkipWhile(x => x != "--").Skip(1).ToArray();
        if (rest.Length == 0) { Console.Error.WriteLine("[duty run] 用法：duty run --seq N [--cwd d] [--timeout s] [--dry-run] -- <命令...>"); return 2; }
        var cmd = string.Join(' ', rest).Trim();
        var cwdRel = ArgValue(args, "--cwd", "").Trim();
        var timeout = Math.Clamp(ParseInt(ArgValue(args, "--timeout", a.MaxSeconds.ToString())), 1, 600);
        var dry = HasFlag(args, "--dry-run");

        var cwd = ResolveActionCwd(ctx, cwdRel);
        if (cwd is null) { Live(ctx, seq, "ACT-DENY", $"cwd 越界：{cwdRel}"); return 3; }

        var tier = DecideActionTier(ctx, uid, cwdRel, cmd, out var why);
        if (tier == "deny")
        {
            Live(ctx, seq, "ACT-DENY", why);
            LogAction(ctx, seq, uid, "deny", cmd, cwdRel, dry, 3, 0, why, "");
            Console.WriteLine("[deny] " + why);
            return 3;
        }

        if (dry)
        {
            Live(ctx, seq, "ACT-DRY", $"[{tier}] cwd={(cwdRel.Length == 0 ? "." : cwdRel)} {Clip(cmd, 120)}");
            LogAction(ctx, seq, uid, tier, cmd, cwdRel, true, 0, 0, "dry-run", "");
            Console.WriteLine($"[dry-run] tier={tier} cwd={cwd}\n  {cmd}");
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = ShellExe,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(cmd);

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            var finished = p.WaitForExit(timeout * 1000);
            if (!finished)
            {
                try { Process.Start("taskkill", $"/PID {p.Id} /T /F")?.WaitForExit(); } catch { }
                exitCode = -9;
                stdout = SafeResult(outTask);
                stderr = SafeResult(errTask) + $"\n[timeout {timeout}s，已杀进程树]";
            }
            else
            {
                exitCode = p.ExitCode;
                stdout = SafeResult(outTask);
                stderr = SafeResult(errTask);
            }
        }
        catch (Exception ex)
        {
            exitCode = -1;
            stdout = "";
            stderr = ex.Message;
        }
        sw.Stop();

        var ms = sw.ElapsedMilliseconds;
        Live(ctx, seq, "ACT", $"[{tier}] {Clip(cmd, 80)} → exit={exitCode} {ms}ms");
        LogAction(ctx, seq, uid, tier, cmd, cwdRel, false, exitCode, ms, stdout, stderr);
        Console.WriteLine($"[{tier}] exit={exitCode} {ms}ms");
        if (stdout.Length > 0) Console.WriteLine("--- stdout ---\n" + Tail(stdout, 4000));
        if (stderr.Length > 0) Console.WriteLine("--- stderr ---\n" + Tail(stderr, 2000));
        return exitCode == 0 ? 0 : 1;
    }

    /// <summary>发文件/图片到 pending 的会话：群友只能发白名单目录里的产物（owner 不限）。</summary>
    private static int SendArtifactCmd(Ctx ctx, string[] args, string exeCmd, string label)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        if (seq <= 0) { Console.Error.WriteLine($"[duty {label}] 需要 --seq N"); return 2; }
        var (ok, pack, err) = VerifySeq(ctx, ReadPending(ctx), seq);
        if (!ok) { Console.Error.WriteLine($"[duty {label}] " + err); return 2; }
        var uid = pack["trigger"]?["uid"]?.GetValue<long>() ?? 0;
        var chat = pack["chat"]?.GetValue<string>() ?? "";
        var dry = HasFlag(args, "--dry-run");
        var file = ArgValue(args, "--file", "");
        if (file.Length == 0) { Console.Error.WriteLine($"[duty {label}] 需要 --file <路径>"); return 2; }
        var full = Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(ctx.RepoRoot, file));
        if (!File.Exists(full)) { Live(ctx, seq, "ACT-DENY", $"文件不存在：{file}"); Console.Error.WriteLine("[duty] 文件不存在: " + full); return 2; }

        var a = ctx.Cfg.Action;
        var owner = a.OwnerUids.Contains(uid);
        if (!owner && !UnderSendDirs(ctx, full))
        {
            Live(ctx, seq, "ACT-DENY", $"产物目录不在白名单：{file}");
            LogAction(ctx, seq, uid, "deny", $"send:{label} {full}", "", dry, 3, 0, "send dir not allowed", "");
            Console.WriteLine("[deny] 该路径不在 Action.SendDirs 白名单，拒绝发送");
            return 3;
        }
        if (dry)
        {
            Live(ctx, seq, "ACT-DRY", $"send:{label} {Path.GetFileName(full)}");
            LogAction(ctx, seq, uid, owner ? "owner" : "group", $"send:{label} {full}", "", true, 0, 0, "dry-run", "");
            Console.WriteLine($"[dry-run] {(owner ? "owner" : "group")} send {label} -> {chat}: {full}");
            return 0;
        }

        var exe = Path.Combine(AppConfig.BaseDir, "src", "QqAgent", "bin", "Release", "net10.0-windows", "qq-agent.exe");
        if (!File.Exists(exe)) { Console.Error.WriteLine("[duty] qq-agent.exe 不存在"); return 1; }
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(exeCmd);
        psi.ArgumentList.Add(chat);
        psi.ArgumentList.Add(full);
        int exitCode; string stdout; string stderr;
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("start null");
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            var done = p.WaitForExit(120_000);
            if (!done) { try { Process.Start("taskkill", $"/PID {p.Id} /T /F")?.WaitForExit(); } catch { } exitCode = -9; }
            else exitCode = p.ExitCode;
            stdout = SafeResult(o); stderr = SafeResult(e);
        }
        catch (Exception ex) { exitCode = -1; stdout = ""; stderr = ex.Message; }
        sw.Stop();
        Live(ctx, seq, "SEND-" + label.ToUpperInvariant(), $"{Path.GetFileName(full)} → exit={exitCode}");
        LogAction(ctx, seq, uid, owner ? "owner" : "group", $"send:{label} {full}", "", false, exitCode, sw.ElapsedMilliseconds, stdout, stderr);
        if (stdout.Length > 0) Console.WriteLine(Tail(stdout, 1500));
        if (stderr.Length > 0) Console.Error.WriteLine(Tail(stderr, 800));
        return exitCode == 0 ? 0 : 1;
    }

    private static int ActionLogCmd(Ctx ctx, string[] args)
    {
        var last = Math.Clamp(ParseInt(ArgValue(args, "--last", "20")), 1, 500);
        var path = Path.Combine(ctx.LogDir, "amy-actions.jsonl");
        if (!File.Exists(path)) { Console.WriteLine("(还没有行动记录：logs/amy-actions.jsonl)"); return 0; }
        foreach (var line in File.ReadLines(path, Encoding.UTF8).TakeLast(last)) Console.WriteLine(line);
        return 0;
    }

    /// <summary>tier：owner=本机操作全开（危险片段仍拒）；group=白名单脚本；deny=拒绝。</summary>
    private static string DecideActionTier(Ctx ctx, long uid, string cwdRel, string cmd, out string why)
    {
        why = "";
        var a = ctx.Cfg.Action;
        var lower = cmd.ToLowerInvariant();
        var isOwner = uid > 0 && a.OwnerUids.Contains(uid);
        // owner 只硬拦磁盘擦除类；非 owner 用完整危险片段表（含关机/重启等）
        foreach (var bad in (isOwner ? a.OwnerHardDenyPatterns : a.DenyPatterns))
            if (lower.Contains(bad.ToLowerInvariant())) { why = $"命中危险片段：{bad.Trim()}"; return "deny"; }
        if (isOwner) return "owner";

        // 非 owner：只允许白名单脚本，且不能带 shell 元字符
        if (cmd.IndexOfAny(new[] { ';', '|', '&', '>', '<', '\n', '\r', '`' }) >= 0)
        {
            why = "群友命令含 shell 元字符，拒绝（只接受白名单脚本的简单参数）";
            return "deny";
        }
        var normCwd = NormalizeRel(cwdRel);
        foreach (var entry in a.GroupAllowedCommands)
        {
            var parts = entry.Split('|', 2);
            if (parts.Length != 2) continue;
            if (!string.Equals(NormalizeRel(parts[0]), normCwd, StringComparison.OrdinalIgnoreCase)) continue;
            if (!cmd.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase)) continue;
            return "group";
        }
        why = $"非 owner（uid={uid}）只能跑白名单脚本；本机命令需 owner 明确要求";
        return "deny";
    }

    private static string? ResolveActionCwd(Ctx ctx, string cwdRel)
    {
        var root = Path.GetFullPath(ctx.RepoRoot);
        var full = cwdRel.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, cwdRel));
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return Directory.Exists(full) ? full : null;
    }

    private static bool UnderSendDirs(Ctx ctx, string full)
    {
        var root = Path.GetFullPath(ctx.RepoRoot);
        foreach (var d in ctx.Cfg.Action.SendDirs)
        {
            var dir = Path.IsPathRooted(d) ? Path.GetFullPath(d) : Path.GetFullPath(Path.Combine(root, d));
            if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.Equals(dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizeRel(string p) =>
        (p ?? "").Trim().TrimStart('\\', '/').TrimEnd('\\', '/').Replace('/', '\\');

    private static void LogAction(Ctx ctx, int seq, long uid, string tier, string cmd, string cwd,
        bool dry, int exit, long ms, string stdout, string stderr)
    {
        try
        {
            var row = new JsonObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["seq"] = seq,
                ["uid"] = uid,
                ["tier"] = tier,
                ["cwd"] = cwd,
                ["cmd"] = cmd,
                ["dry"] = dry,
                ["exit"] = exit,
                ["ms"] = ms,
                ["stdout"] = Tail(stdout ?? "", 2000),
                ["stderr"] = Tail(stderr ?? "", 1000),
            };
            File.AppendAllText(Path.Combine(ctx.LogDir, "amy-actions.jsonl"), row.ToJsonString() + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private static string SafeResult(Task<string> t)
    {
        try { return t.Wait(5000) ? t.Result : ""; } catch { return ""; }
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];

    /// <summary>行动命令的 shell：优先 pwsh（若 PATH 里有），否则 Windows PowerShell 5.1。</summary>
    private static readonly string ShellExe = ResolveShell();

    private static string ResolveShell()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, "pwsh.exe"))) return "pwsh";
        }
        catch { }
        return "powershell";
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    // ===================== 记忆 =====================

    /// <summary>读某人印象卡（档案层）。--seq N 时从 pending 取当前触发者；否则 --uid/--scope。</summary>
    private static int CardCmd(Ctx ctx, string[] args)
    {
        long uid = ParseLong(ArgValue(args, "--uid", "0"));
        var scope = ArgValue(args, "--scope", "");
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        if (seq > 0)
        {
            var pack = ReadPending(ctx);
            var (ok, p, err) = VerifySeq(ctx, pack, seq);
            if (!ok) { Console.Error.WriteLine("[duty card] " + err); return 2; }
            uid = p["trigger"]?["uid"]?.GetValue<long>() ?? 0;
            scope = (p["scopes"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? scope;
        }
        if (uid <= 0) { Console.Error.WriteLine("[duty card] 需要 --uid N（或 --seq N 取当前触发者）"); return 2; }
        if (scope.Length == 0) scope = ScopeOf(ctx, ctx.Cfg.Daemon.Chat).FirstOrDefault() ?? "";
        if (scope.Length == 0) { Console.Error.WriteLine("[duty card] 缺少 scope（scope-map 里没有该会话？用 --scope 指定）"); return 2; }
        var card = ctx.Yuno.Card(scope, uid);
        if (card is null)
        {
            if (!ctx.Yuno.Healthy()) { Console.Error.WriteLine("[duty card] Yuno 不可用"); return 1; }
            Console.WriteLine($"({scope} / {uid} 没有印象卡)");
            return 0;
        }
        Console.WriteLine(card);
        return 0;
    }
    private static int ContextCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        var pack = ReadPending(ctx);
        if (seq > 0) { var (ok, p, err) = VerifySeq(ctx, pack, seq); if (!ok) { Console.Error.WriteLine(err); return 2; } pack = p; }
        if (pack is null) { Console.Error.WriteLine("[duty context] 无 pending"); return 2; }
        var trigger = pack["trigger"];
        var query = ArgValue(args, "--query", trigger?["text"]?.GetValue<string>() ?? "");
        var uid = trigger?["uid"]?.GetValue<long>() ?? 0;
        var scopes = (pack["scopes"] as JsonArray)?.Select(s => s?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? Array.Empty<string>();
        var text = ctx.Yuno.Context(query, scopes, uid, ctx.Cfg.Scene.MemoryTopK, ctx.Cfg.Scene.MemoryBudget);
        Console.WriteLine(text ?? "(null：Yuno 不可用或无命中)");
        return 0;
    }

    private static int AfterChatCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        var pack = ReadPending(ctx);
        var (ok, p, err) = VerifySeq(ctx, pack, seq);
        if (!ok) { Console.Error.WriteLine(err); return 2; }
        var replyFile = ArgValue(args, "--reply-file", "");
        if (!File.Exists(replyFile)) { Console.Error.WriteLine("[duty after-chat] 缺 --reply-file"); return 2; }
        var reply = File.ReadAllText(replyFile, Encoding.UTF8).Trim();
        var kind = p["kind"]?.GetValue<string>() ?? "group";
        var gid = p["group_id"]?.GetValue<long>() ?? 0;
        var uid = p["trigger"]?["uid"]?.GetValue<long>() ?? 0;
        var sender = p["trigger"]?["sender"]?.GetValue<string>() ?? "";
        ctx.Yuno.AfterChat(kind, gid, uid, sender, BuildAfterChatText(p),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), reply);
        WriteAck(ctx, seq);   // 发送已成功：ack 失败只告警不改变结果
        Live(ctx, seq, "AFTER_CHAT", "兜底落库 ok");
        return 0;
    }

    private static int AddFactCmd(Ctx ctx, string[] args)
    {
        var scope = ArgValue(args, "--scope", "");
        var key = ArgValue(args, "--key", "");
        var fact = ArgValue(args, "--fact", "");
        if (scope.Length == 0 || fact.Length == 0) { Console.Error.WriteLine("[duty add-fact] 需要 --scope 与 --fact"); return 2; }
        var ok = ctx.Yuno.AddFact(scope, key, fact,
            ArgValue(args, "--importance", "0.7").AsDouble(), ArgValue(args, "--confidence", "0.85").AsDouble());
        Console.WriteLine(ok ? "add_fact ok" : "add_fact failed（Yuno 不可用或参数被拒）");
        return ok ? 0 : 1;
    }

    // ===================== 词库 / 现场状态 / 花名册 =====================

    private static int MemeCmd(Ctx ctx, string[] args)
    {
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (action)
        {
            case "list":
                foreach (var e in ctx.Memes.Entries.OrderByDescending(e => e.Uses).ThenBy(e => e.Term))
                    Console.WriteLine($"{e.Term}\tuses={e.Uses}\tlast={(e.LastUsed > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.LastUsed).ToLocalTime().ToString("MM-dd HH:mm") : "never")}\t{e.Meaning}");
                Console.WriteLine($"共 {ctx.Memes.Entries.Count} 条");
                return 0;
            case "add":
                var term = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
                var meaning = ArgValue(args, "--meaning", "");
                var examples = ArgValue(args, "--examples", "").Split(';', StringSplitOptions.RemoveEmptyEntries);
                var scope = ArgValue(args, "--scope", ScopeOf(ctx, ctx.Cfg.Daemon.Chat).FirstOrDefault() ?? "");
                if (term.Length == 0) { Console.Error.WriteLine("[duty meme add] 需要 <term> --meaning …"); return 2; }
                var ok = ctx.Memes.TryAdd(term, meaning, examples, scope, out var msg);
                Console.WriteLine(msg);
                return ok ? 0 : 1;
            case "prune":
                Console.WriteLine($"removed {ctx.Memes.Prune()}");
                return 0;
            case "bump":
                var file = ArgValue(args, "--file", "");
                var text = file.Length > 0 && File.Exists(file)
                    ? File.ReadAllText(file, Encoding.UTF8)
                    : string.Join(' ', args.Skip(1).Where(a => !a.StartsWith("--")));
                ctx.Memes.Bump(new[] { text });
                Console.WriteLine("bump ok");
                return 0;
            default:
                Console.Error.WriteLine("用法: duty meme list|add|prune|bump");
                return 2;
        }
    }

    private static int StateCmd(Ctx ctx, string[] args)
    {
        var path = Path.Combine(ctx.LogDir, "amy-scene-state.json");
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "get";
        if (action == "get")
        {
            var chat = ArgValue(args, "--chat", ctx.Cfg.Daemon.Chat);
            var all = SceneStateStore.Load(path);
            if (!all.TryGetValue(chat, out var e)) { Console.WriteLine("(无现场状态)"); return 0; }
            Console.WriteLine($"topic: {e.Topic}");
            if (e.Jokes.Count > 0) Console.WriteLine("jokes: " + string.Join(", ", e.Jokes));
            Console.WriteLine("updated: " + DateTimeOffset.FromUnixTimeSeconds(e.Updated).ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            return 0;
        }
        if (action == "set")
        {
            var chat = ArgValue(args, "--chat", ctx.Cfg.Daemon.Chat);
            var topic = ArgValue(args, "--topic", "");
            if (topic.Length == 0) { Console.Error.WriteLine("[duty state set] 需要 --topic"); return 2; }
            var jokes = ArgValue(args, "--jokes", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            SceneStateStore.Set(path, chat, topic, jokes);
            Console.WriteLine("state ok");
            return 0;
        }
        Console.Error.WriteLine("用法: duty state get|set");
        return 2;
    }

    private static int RosterCmd(Ctx ctx, string[] args)
    {
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var path = Path.Combine(ctx.DataDir, "roster-map.json");
        var map = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(File.ReadAllText(path, Encoding.UTF8))
                      ?? map;
            }
            catch { }
        }
        if (action == "list")
        {
            foreach (var kv in map) Console.WriteLine($"{kv.Key}: {kv.Value.Count} 个名字");
            return 0;
        }
        if (action != "add") { Console.Error.WriteLine("用法: duty roster list|add"); return 2; }
        var name = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        var qq = args.Skip(1).Where(a => !a.StartsWith("--")).Skip(1).FirstOrDefault() ?? "";
        var scope = ArgValue(args, "--scope", "group:123456789");
        if (name.Length == 0 || !long.TryParse(qq, out var qqId)) { Console.Error.WriteLine("[duty roster add] 需要 \"<昵称>\" <QQ>"); return 2; }
        if (!map.TryGetValue(scope, out var names)) { names = new Dictionary<string, long>(StringComparer.Ordinal); map[scope] = names; }
        names[name] = qqId;
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine($"roster {scope}: {name} -> {qqId}（重启 daemon 生效）");
        return 0;
    }

    // ===================== 体检 / 统计 / 清理 =====================

    private static int Doctor(Ctx ctx)
    {
        var fails = 0;
        var warns = 0;
        void Ok(string name, string detail = "") => Console.WriteLine($"  [ok]   {name}{(detail.Length > 0 ? "  " + detail : "")}");
        void Warn(string name, string detail) { warns++; Console.WriteLine($"  [warn] {name}  {detail}"); }
        void Fail(string name, string detail) { fails++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }

        Console.WriteLine("== Amy 值班脑体检 ==");
        Console.WriteLine($"config: {Path.Combine(AppConfig.BaseDir, "config.json")}");

        var dsh = ctx.Cfg.AmyRuntime.Dsh;
        if (ctx.Cfg.AmyRuntime.Mode != "dsh")
            Warn("runtime mode", $"AmyRuntime.Mode={ctx.Cfg.AmyRuntime.Mode}（当前不是 dsh；自动值班脑不会启动）");
        else Ok("runtime mode", "dsh");

        if (ctx.Cfg.Daemon.NotifyOnly)
            Fail("模式闸", "Daemon.NotifyOnly=true：daemon 只投递不 spawn，需要 DSH GUI 会话值守；自动脑模式下请设 false");
        else Ok("模式闸", "NotifyOnly=false");

        // DSH 安装 + 版本
        if (!DshBrain.Installed(ctx.Cfg)) Fail("DSH CLI", $"未安装 {DshBrain.CliPath(ctx.Cfg)}（跑 qq-agent\\brain\\install.ps1）");
        else
        {
            var pkg = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(DshBrain.CliPath(ctx.Cfg))!)!, "package.json");
            var ver = File.Exists(pkg) ? (JsonNode.Parse(File.ReadAllText(pkg))?["version"]?.GetValue<string>() ?? "?") : "?";
            Ok("DSH CLI", $"{ver} @ {DshBrain.CliPath(ctx.Cfg)}");
        }

        var (dumpOk, dumpDetail) = DshBrain.DumpConfigCheck(ctx.Cfg);
        if (dumpOk) Ok("脑补丁", dumpDetail);
        else
        {
            // 值班脑自身跑在 DSH 沙箱里时无法再 spawn node 做 dump-config（Access denied）；
            // daemon 启动时会在沙箱外验证并写缓存，这里读缓存降级为 warn 而不是 FAIL。
            var cached = ReadPatchCache(ctx);
            if (cached.Ok) Warn("脑补丁", $"{dumpDetail}；采用 daemon 缓存校验：{cached.Detail}");
            else Fail("脑补丁", $"{dumpDetail}；缓存校验也不通过：{cached.Detail}");
        }

        if (string.IsNullOrWhiteSpace(ctx.Cfg.Model.ApiKey)) Fail("模型密钥", "config.json → Model.ApiKey 为空（headless 只读启动环境 DEEPSEEK_API_KEY）");
        else Ok("模型密钥", $"len={ctx.Cfg.Model.ApiKey.Length}（spawn 时注入 DEEPSEEK_API_KEY）");

        // Yuno
        if (ctx.Yuno.Healthy()) Ok("Yuno :8457", "health ok");
        else Warn("Yuno :8457", "不可用：记忆预取/落库会静默降级");

        // QQ
        var qq = Process.GetProcessesByName("QQ");
        var main = qq.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (main is null) Warn("QQ", $"进程 {qq.Length} 个，无主窗口（未登录/收托盘）");
        else Ok("QQ", $"pid={main.Id} window={main.MainWindowTitle}");
        foreach (var p in qq) p.Dispose();

        // daemon 心跳
        var statusPath = Path.Combine(ctx.LogDir, "amy-status.json");
        if (!File.Exists(statusPath)) Warn("daemon", "没有 logs/amy-status.json（daemon 没跑过）");
        else
        {
            try
            {
                var st = JsonNode.Parse(File.ReadAllText(statusPath, Encoding.UTF8));
                var hb = st?["heartbeat"]?.GetValue<long>() ?? 0;
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hb;
                if (age > 60) Warn("daemon", $"心跳 {age}s 前（可能已停）pid={st?["pid"]}"); else Ok("daemon", $"idle, heartbeat {age}s");
            }
            catch { Warn("daemon", "amy-status.json 解析失败"); }
        }

        // pending / ack
        var ackSeq = ReadAckSeq(ctx);
        if (!File.Exists(ctx.PendingPath)) Ok("pending", "无未消费投递");
        else
        {
            try
            {
                var p = JsonNode.Parse(File.ReadAllText(ctx.PendingPath, Encoding.UTF8));
                var seq = (int)(p?["seq"]?.GetValue<long>() ?? 0);
                var notifyAt = p?["notify_at"]?.GetValue<string>() ?? "?";
                if (seq > ackSeq) Warn("pending", $"seq={seq} 未消费（投递于 {notifyAt}，ack={ackSeq}）");
                else Ok("pending", $"seq={seq} 已消费（ack={ackSeq}）");
            }
            catch { Warn("pending", "解析失败"); }
        }

        // 人设漂移
        var rootPersona = DshBrain.Resolve(ctx.Cfg, ctx.Cfg.Writer.PersonaPath);
        var yunoPersona = DshBrain.Resolve(ctx.Cfg, @"..\yuno\personas\amy\persona.md");
        if (File.Exists(rootPersona) && File.Exists(yunoPersona))
        {
            var a = File.ReadAllText(rootPersona, Encoding.UTF8);
            var b = File.ReadAllText(yunoPersona, Encoding.UTF8);
            if (a == b) Ok("人设一致", $"{a.Length} chars");
            else Warn("人设漂移", $"根 {a.Length} chars ≠ yuno 副本 {b.Length} chars（跑 qq-agent\\tools\\sync-persona.ps1）");
        }
        else Warn("人设", "persona.md 缺失（根或 yuno 副本）");

        Ok("词库", $"{ctx.Memes.Entries.Count} 条 @ {ctx.Cfg.Memes.Path}");

        // DSH 会话堆积
        var home = DshBrain.HomePath(ctx.Cfg);
        if (Directory.Exists(home))
        {
            var files = Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories).ToList();
            Ok("脑会话目录", $"{files.Count} 个文件（duty prune --sessions 2d 清理）");
        }
        else Warn("脑会话目录", $"不存在 {home}（首次 spawn 自动创建）");

        // 双大脑检查：自动脑模式下若还有人工值守在占窗（amy-busy.flag 新鲜）→ 告警
        var busyFlag = Path.Combine(ctx.LogDir, "amy-busy.flag");
        var guiBusy = File.Exists(busyFlag) && (DateTime.Now - File.GetLastWriteTime(busyFlag)).TotalMinutes < 10;
        if (!ctx.Cfg.Daemon.NotifyOnly && guiBusy)
            Warn("双大脑", "检测到 amy-busy.flag（人工值守正在处理）：自动脑模式下请关掉 GUI 值守台，避免双回复");
        else if (ctx.Cfg.AmyRuntime.Mode == "dsh" && !ctx.Cfg.Daemon.NotifyOnly)
            Ok("单大脑", "自动脑（dsh headless）");
        Console.WriteLine($"== 结果：{(fails == 0 ? "通过" : "有 FAIL")}  失败={fails} 警告={warns} ==");
        return fails == 0 ? 0 : 1;
    }

    private static int StatsCmd(Ctx ctx, string[] args)
    {
        var last = Math.Max(1, ParseInt(ArgValue(args, "--last", "20")));
        if (!File.Exists(ctx.TurnsPath)) { Console.WriteLine("(还没有回合记录：logs/amy-turns.jsonl)"); return 0; }
        var rows = File.ReadLines(ctx.TurnsPath, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => { try { return JsonNode.Parse(l); } catch { return null; } })
            .Where(n => n is not null).Select(n => n!)
            .TakeLast(last).ToList();
        if (rows.Count == 0) { Console.WriteLine("(无记录)"); return 0; }

        var lat = new List<double>();
        foreach (var r in rows)
        {
            var notify = r["notify_ts"]?.GetValue<long>() ?? 0;
            var sent = r["sent_ts"]?.GetValue<long>() ?? 0;
            if (notify > 0 && sent > notify) lat.Add(sent - notify);
        }
        double Pct(List<double> xs, double p)
        {
            if (xs.Count == 0) return 0;
            var s = xs.OrderBy(x => x).ToList();
            var i = (int)Math.Ceiling(p / 100.0 * s.Count) - 1;
            return s[Math.Clamp(i, 0, s.Count - 1)];
        }
        var guardHits = rows.Sum(r => (r["violations"] as JsonArray)?.Count ?? 0);
        var cardHits = rows.Count(r => r["memory"]?["card"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb);
        var vecHits = rows.Count(r => r["memory"]?["vector"] is JsonValue vv && vv.TryGetValue<bool>(out var vb) && vb);
        var dry = rows.Count(r => BoolOf(r, "dry"));
        Console.WriteLine($"回合 {rows.Count}（dry={dry}）  notify→sent: p50={Pct(lat, 50):F0}s p95={Pct(lat, 95):F0}s n={lat.Count}");
        Console.WriteLine($"守卫命中 {guardHits} 次；记忆：印象卡 {cardHits}/{rows.Count}，向量 {vecHits}/{rows.Count}；最近 {Math.Min(5, rows.Count)} 回合：");
        foreach (var r in rows.TakeLast(5))
            Console.WriteLine($"  seq={r["seq"]} writer={r["writer_ms"]}ms bubbles={(r["bubbles"] as JsonArray)?.Count ?? 0} sent_ts={r["sent_ts"]} dry={r["dry"]}");
        return 0;
    }

    private static int PruneCmd(Ctx ctx, string[] args)
    {
        var sessDays = ParseDays(ArgValue(args, "--sessions", "2d"));
        var snapDays = ParseDays(ArgValue(args, "--snapshots", "7d"));
        var removed = 0;
        var home = DshBrain.HomePath(ctx.Cfg);
        if (Directory.Exists(home) && sessDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-sessDays);
            foreach (var f in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTime(f) >= cutoff) continue;
                    File.Delete(f);
                    removed++;
                }
                catch { }
            }
            foreach (var d in Directory.EnumerateDirectories(home, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
        }
        var snaps = 0;
        if (snapDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-snapDays);
            foreach (var f in Directory.EnumerateFiles(ctx.LogDir, "snap-*.png*"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) { File.Delete(f); snaps++; } } catch { }
            }
        }
        Console.WriteLine($"prune: 脑会话文件 {removed} 个（>{sessDays}d），截图 {snaps} 个（>{snapDays}d）");
        return 0;
    }

    private static int LiveCmd(Ctx ctx, string[] args)
    {
        var seq = ParseInt(ArgValue(args, "--seq", "0"));
        var text = string.Join(' ', args.Where(a => !a.StartsWith("--") && a != seq.ToString()));
        if (text.Length == 0) { Console.Error.WriteLine("用法: duty live \"<STEP>: msg\" [--seq N]"); return 2; }
        var step = text.Contains(':') ? text[..text.IndexOf(':')].Trim() : "NOTE";
        var msg = text.Contains(':') ? text[(text.IndexOf(':') + 1)..].Trim() : text;
        Live(ctx, seq, step, msg);
        return 0;
    }

    // ===================== 公共小件 =====================

    private static void Live(Ctx ctx, int seq, string step, string msg)
    {
        try
        {
            File.AppendAllText(ctx.LivePath,
                $"[{DateTime.Now:HH:mm:ss}] amy#{seq} {step}: {msg}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    private static int AckFail(Ctx ctx, int seq)
    {
        Console.Error.WriteLine("[duty] ack 写入失败：logs 目录不可写？(" + ctx.AckPath + ")");
        return 1;
    }

    private static bool WriteAck(Ctx ctx, int seq)
    {
        try
        {
            var tmp = ctx.AckPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                seq,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }), Encoding.UTF8);
            File.Move(tmp, ctx.AckPath, true);
        }
        catch { return false; }
        return true;
    }

    private static int ReadAckSeq(Ctx ctx)
    {
        try
        {
            if (!File.Exists(ctx.AckPath)) return 0;
            var n = JsonNode.Parse(File.ReadAllText(ctx.AckPath, Encoding.UTF8));
            return (int)(n?["seq"]?.GetValue<long>() ?? 0);
        }
        catch { return 0; }
    }

    private static void WriteTurn(Ctx ctx, int seq, JsonNode pack, long writerMs, long sentTs,
        List<string> bubbles, List<string> violations, string intent, bool dry)
    {
        try
        {
            var row = new JsonObject
            {
                ["seq"] = seq,
                ["notify_ts"] = pack["ts"]?.GetValue<long>() ?? 0,
                ["pack_at"] = pack["notify_at"]?.GetValue<string>() ?? "",
                ["writer_ms"] = writerMs,
                ["sent_ts"] = sentTs,
                ["chat"] = pack["chat"]?.GetValue<string>() ?? "",
                ["bubbles"] = new JsonArray(bubbles.Select(b => (JsonNode)b!).ToArray()),
                ["violations"] = new JsonArray(violations.Select(v => (JsonNode)v!).ToArray()),
                ["intent"] = intent,
                ["memory"] = new JsonObject
                {
                    ["card"] = (pack["memory"]?["card"]?.GetValue<string>() ?? "").Length > 0,
                    ["vector"] = (pack["memory"]?["cards"] as JsonArray)?.Count > 0,
                    ["reason"] = pack["memory"]?["reason"]?.DeepClone() ?? new JsonArray(),
                },
                ["dry"] = dry,
            };
            File.AppendAllText(ctx.TurnsPath, row.ToJsonString() + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>daemon 在沙箱外做的补丁 dump-config 校验缓存（logs/brain-patch-check.json）。</summary>
    private static (bool Ok, string Detail) ReadPatchCache(Ctx ctx)
    {
        try
        {
            var path = Path.Combine(ctx.LogDir, "brain-patch-check.json");
            if (!File.Exists(path)) return (false, "无缓存（daemon 启动后会写）");
            var n = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            var ok = n?["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            var detail = n?["detail"]?.GetValue<string>() ?? "";
            var at = n?["at"]?.GetValue<string>() ?? "";
            return (ok, $"{detail} @ {at}");
        }
        catch (Exception ex) { return (false, "缓存读取失败: " + ex.Message); }
    }
    private static string[] ScopeOf(Ctx ctx, string chat)
    {
        var path = Path.Combine(ctx.DataDir, "scope-map.json");
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path, Encoding.UTF8));
            if (map is null || !map.TryGetValue(chat, out var el)) return Array.Empty<string>();
            var kind = el.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            var id = el.TryGetProperty("id", out var i) && i.TryGetInt64(out var v) ? v : 0;
            if (id <= 0) return Array.Empty<string>();
            return kind == "group"
                ? new[] { $"group:{id}", $"group_all:{id}" }
                : new[] { $"c2c:{id}" };
        }
        catch { return Array.Empty<string>(); }
    }

    private static string ArgValue(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback;
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name);

    /// <summary>去掉 "name value" 形式的参数对（--config 之类不参与位置参数解析）。</summary>
    private static string[] StripPair(string[] args, string name)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name) { i++; continue; }
            list.Add(args[i]);
        }
        return list.ToArray();
    }

    private static bool BoolOf(JsonNode? node, string name)
    {
        try { return node?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b; }
        catch { return false; }
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    private static long ParseLong(string s) => long.TryParse(s, out var v) ? v : 0;

    private static double AsDouble(this string s) => double.TryParse(s, out var v) ? v : 0;

    private static int ParseDays(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim().TrimEnd('d', 'D');
        return int.TryParse(s, out var v) ? v : 0;
    }
}















