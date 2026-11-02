using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QqAgent;

/// <summary>messages.jsonl 的一行（原文层）。Line = 1 基行号，OCR 账本用它当主键。</summary>
internal sealed record JournalLine(
    long Line, long Ts, string Chat, string Time, string Sender, long Uid, string Text, bool IsSelf, int Pics,
    string QuoteFrom = "", string QuoteText = "", int PicW = 0, int PicH = 0)
{
    /// <summary>现场包里的一行：省掉 chat/ts 等冗余字段。引用（回复）单独给 ref，别让模型自己去猜图属于谁。</summary>
    public JsonObject ToSceneNode()
    {
        var node = new JsonObject
        {
            ["line"] = Line,
            ["t"] = Time,
            ["sender"] = Sender,
            ["uid"] = Uid,
            ["text"] = Text,
            ["is_self"] = IsSelf,
            ["pics"] = Pics,
        };
        if (PicW > 0 && PicH > 0)
        {
            node["pic_w"] = PicW;
            node["pic_h"] = PicH;
        }
        if (QuoteFrom.Length > 0 || QuoteText.Length > 0)
            node["ref"] = new JsonObject { ["from"] = QuoteFrom, ["text"] = QuoteText };
        return node;
    }
}

internal static class Journal
{
    /// <summary>读 messages.jsonl，返回指定会话的最近 n 行（带 1 基行号）。文件按天增长，这里全量扫一遍但只留尾部窗口。</summary>
    public static List<JournalLine> ReadTail(string path, string chat, int n)
    {
        var result = new Queue<JournalLine>(Math.Max(4, n));
        if (!File.Exists(path) || n <= 0) return result.ToList();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        long lineNo = 0;
        while (sr.ReadLine() is { } raw)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = TryParse(lineNo, raw);
            if (line is null) continue;
            if (line.Chat != chat) continue;
            result.Enqueue(line);
            while (result.Count > n) result.Dequeue();
        }
        return result.ToList();
    }

    /// <summary>当前文件行数（daemon 启动时同步一次基线，之后每条 appends 自增）。</summary>
    public static long CountLines(string path)
    {
        if (!File.Exists(path)) return 0;
        long n = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        while (sr.ReadLine() is not null) n++;
        return n;
    }

    /// <summary>原文层最后一条的 ts（epoch 秒）；读不到返回 0。时间闸用它判断"补出来的消息是新的还是旧的"。</summary>
    public static long LastTs(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            string? last = null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                while (sr.ReadLine() is { } line)
                    if (!string.IsNullOrWhiteSpace(line)) last = line;
            }
            if (last is null) return 0;
            using var doc = JsonDocument.Parse(last);
            return doc.RootElement.TryGetProperty("ts", out var ts) ? ts.GetInt64() : 0;
        }
        catch { return 0; }
    }

    public static JournalLine? TryParse(long lineNo, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var chat = Str(r, "chat");
            var text = Str(r, "Text");
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new JournalLine(
                lineNo,
                Long(r, "ts"),
                chat,
                Str(r, "Time"),
                Str(r, "Sender"),
                Long(r, "uid"),
                text,
                Bool(r, "IsSelf"),
                (int)Long(r, "pics"),
                Str(r, "quote_from"),
                Str(r, "quote_text"),
                (int)Long(r, "pic_w"),
                (int)Long(r, "pic_h"));
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>OCR 账本写入 + 原文层回写（2026-10-26 新增：`ocrfwd --line N` 自动入账）。
/// 目的：识别结果不再靠值班脑手抄，避免"13 行文字被记成无文字"这类事故。</summary>
internal static class OcrBook
{
    /// <summary>把一次 ocrfwd 的结果写进账本，并把 messages.jsonl 第 line 行的 Text 回写成 [图片|OCR文字]。
    /// texts = 每张图的识别行（按图序）；imgStart = 第一张图的图内序号（默认 1）。
    /// 返回给调用方打印的一行摘要。失败抛 InvalidOperationException（带原因，不静默）。</summary>
    public static string Record(string jsonlPath, string ledgerPath, long line, int imgStart,
        IReadOnlyList<List<string>> texts, string? expectChat, long nowTs, string via, string readBy)
    {
        if (line <= 0) throw new InvalidOperationException("--line 必须是 messages.jsonl 的 1 基行号");
        if (!File.Exists(jsonlPath)) throw new InvalidOperationException("找不到原文层：" + jsonlPath);
        if (texts.Count == 0) throw new InvalidOperationException("没有识别结果可入账");

        var raw = File.ReadAllLines(jsonlPath, Encoding.UTF8);
        if (line > raw.Length) throw new InvalidOperationException($"行号 {line} 越界（messages.jsonl 只有 {raw.Length} 行）");
        var idx = (int)line - 1;
        var obj = JsonNode.Parse(raw[idx])?.AsObject()
                  ?? throw new InvalidOperationException($"第 {line} 行不是合法 JSON");
        var chat = obj["chat"]?.GetValue<string>() ?? "";
        var sender = obj["Sender"]?.GetValue<string>() ?? "";
        var uid = obj["uid"]?.GetValue<long>() ?? 0;
        var ts = obj["ts"]?.GetValue<long>() ?? nowTs;
        var pics = Math.Max(1, obj["pics"]?.GetValue<int>() ?? texts.Count);

        if (!string.IsNullOrEmpty(expectChat) && !string.Equals(chat, expectChat, StringComparison.Ordinal))
            throw new InvalidOperationException($"会话不符：第 {line} 行属于「{chat}」，不是「{expectChat}」（停止回写）");

        // 1) 账本：追加（同主键后写覆盖前写）
        var ledgerLines = new List<string>();
        var byImg = new Dictionary<int, string>();
        for (var k = 0; k < texts.Count; k++)
        {
            var img = imgStart + k;
            var text = texts[k].Count > 0 ? string.Join("／", texts[k]).Trim() : "无文字";
            byImg[img] = text;
            var entry = new JsonObject
            {
                ["line"] = line,
                ["img"] = img,
                ["chat"] = chat,
                ["ts"] = ts,
                ["sender"] = sender,
                ["uid"] = uid,
                ["via"] = via,
                ["read_by"] = readBy,
                ["read_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["text"] = text,
            };
            ledgerLines.Add(entry.ToJsonString());
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        File.AppendAllLines(ledgerPath, ledgerLines, Encoding.UTF8);

        // 2) 原文层：按图序拼出 [图片|...]，只改 Text，不动别的行
        var parts = new List<string>();
        for (var img = 1; img <= Math.Max(pics, imgStart + texts.Count - 1); img++)
            parts.Add(byImg.TryGetValue(img, out var t) && t.Length > 0 ? t : "无文字");
        obj["Text"] = "[图片|" + string.Join("／", parts) + "]";
        raw[idx] = obj.ToJsonString();

        // 3) 原子替换（写临时文件再 move，避免半截文件）
        var tmp = jsonlPath + ".ocr.tmp";
        File.WriteAllLines(tmp, raw, Encoding.UTF8);
        File.Move(tmp, jsonlPath, true);

        var total = byImg.Values.Sum(v => v.Length);
        return $"ledger+原文已回写：line={line} img={imgStart}..{imgStart + texts.Count - 1} chat={chat} 文字 {total} 字";
    }
}

/// <summary>OCR 账本（logs/ocr-ledger.jsonl）：主键 = messages.jsonl 行号 + 消息内图序。</summary>
internal sealed class OcrLedger
{
    private readonly Dictionary<long, Dictionary<int, string>> _map = new();

    public static OcrLedger Load(string path)
    {
        var ledger = new OcrLedger();
        if (!File.Exists(path)) return ledger;
        try
        {
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("line", out var l) || !l.TryGetInt64(out var line)) continue;
                    if (!r.TryGetProperty("img", out var i) || !i.TryGetInt32(out var img)) continue;
                    var text = r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? (t.GetString() ?? "") : "";
                    if (line <= 0 || img <= 0) continue;
                    if (!ledger._map.TryGetValue(line, out var imgs))
                    {
                        imgs = new Dictionary<int, string>();
                        ledger._map[line] = imgs;
                    }
                    imgs[img] = text;
                }
                catch { /* 单行坏账跳过，账本其余行仍可用 */ }
            }
        }
        catch { }
        return ledger;
    }

    public bool TryGet(long line, int img, out string text)
    {
        text = "";
        return _map.TryGetValue(line, out var imgs) && imgs.TryGetValue(img, out text!);
    }

    /// <summary>该行所有图的识别文字（按 img 序拼接）；未入账返回 null。</summary>
    public string? TextOf(long line, int pics)
    {
        if (!_map.TryGetValue(line, out var imgs) || imgs.Count == 0) return null;
        var parts = new List<string>();
        for (var i = 1; i <= Math.Max(pics, imgs.Count); i++)
            if (imgs.TryGetValue(i, out var t) && t.Length > 0) parts.Add(t);
        return parts.Count == 0 ? null : string.Join("／", parts);
    }
}

/// <summary>现场状态（logs/amy-scene-state.json）：值班脑每回合可写一行「现在在聊什么」，下一回合直接看到。</summary>
internal static class SceneStateStore
{
    public sealed class Entry
    {
        public string Topic { get; set; } = "";
        public List<string> Jokes { get; set; } = new();
        public long Updated { get; set; }
    }

    private static readonly JsonSerializerOptions Opt = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, Entry>();
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path, Encoding.UTF8), Opt)
                   ?? new Dictionary<string, Entry>();
        }
        catch
        {
            return new Dictionary<string, Entry>();
        }
    }

    public static void Set(string path, string chat, string topic, IEnumerable<string> jokes)
    {
        var all = Load(path);
        all[chat] = new Entry
        {
            Topic = topic.Trim(),
            Jokes = jokes.Where(j => !string.IsNullOrWhiteSpace(j)).Select(j => j.Trim()).Take(6).ToList(),
            Updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Opt), Encoding.UTF8);
        File.Move(tmp, path, true);
    }
}
