using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QqAgent;

internal sealed class YunoClient
{
    // 2026-10-28：系统代理被打开但 7890 上没进程在听时，.NET 默认走 WinINET 代理 → 连本机 Yuno 也拒绝。
    // Yuno 是本机服务、DeepSeek 是直连可达的国内 API，两边都显式绕过代理。
    private static HttpClient NewHttp(int seconds) =>
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(seconds) };

    private static readonly HttpClient Http = NewHttp(30);
    private readonly YunoConfig _cfg;

    public YunoClient(YunoConfig cfg) => _cfg = cfg;

    private HttpRequestMessage Request(string path, object? body)
    {
        var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, _cfg.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(_cfg.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.Token);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return req;
    }

    public string? Context(string query, string[] scopes, long userId, int topK = 5, int budget = 2000)
    {
        try
        {
            using var req = Request("/amy/context", new
            {
                query,
                scopes,
                user_id = userId,
                top_k = topK,
                budget,
            });
            using var resp = Http.Send(req);
            if (!resp.IsSuccessStatusCode) return null;
            var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var node = JsonNode.Parse(text);
            return node?["context"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读某人的印象卡（Yuno GET /amy/card；档案层，比向量检索便宜且精确）。</summary>
    public string? Card(string scope, long userId)
    {
        try
        {
            using var req = Request($"/amy/card?scope={Uri.EscapeDataString(scope)}&uid={userId}", null);
            using var resp = Http.Send(req);
            if (!resp.IsSuccessStatusCode) return null;
            var node = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var text = node?["card"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>显式写入一条事实（「记住 X」/值班脑判断值得长存时）。</summary>
    public bool AddFact(string scope, string key, string fact, double importance = 0.7, double confidence = 0.85)
    {
        try
        {
            using var req = Request("/amy/add_fact", new
            {
                scope,
                key,
                fact,
                importance,
                confidence,
            });
            using var resp = Http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void AfterChat(string kind, long groupId, long userId, string sender, string text, long msgTime, string reply)
    {
        try
        {
            using var req = Request("/amy/after_chat", new
            {
                kind,
                group_id = groupId,
                user_id = userId,
                sender,
                text,
                msg_time = msgTime,
                reply,
            });
            using var _ = Http.Send(req);
        }
        catch { }
    }

    public bool Healthy()
    {
        try
        {
            using var req = Request("/health", null);
            using var resp = Http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

/// <summary>模型一次回复：正文 + 结束原因（"length" = 被 max_tokens 截断，正文可能为空或半句）。</summary>
internal sealed record ChatReply(string Text, string? FinishReason);

internal sealed class Brain
{
    private static readonly HttpClient Http = NewHttp(90);

    private static HttpClient NewHttp(int seconds) =>
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(seconds) };
    private readonly ModelConfig _cfg;

    public Brain(ModelConfig cfg) => _cfg = cfg;

    public string Chat(string systemPrompt, IEnumerable<(string Role, string Content)> messages,
        double? temperature = null, int? maxTokens = null)
        => ChatRaw(systemPrompt, messages, temperature, maxTokens).Text;

    public ChatReply ChatRaw(string systemPrompt, IEnumerable<(string Role, string Content)> messages,
        double? temperature = null, int? maxTokens = null)
    {
        var arr = new JsonArray();
        arr.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        foreach (var (role, content) in messages)
            arr.Add(new JsonObject { ["role"] = role, ["content"] = content });

        var payload = new JsonObject
        {
            ["model"] = _cfg.Model,
            ["messages"] = arr,
            ["temperature"] = temperature ?? _cfg.Temperature,
            ["max_tokens"] = maxTokens ?? _cfg.MaxTokens,
            ["stream"] = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = Http.Send(req);
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"模型调用失败 {(int)resp.StatusCode}: {Truncate(body, 300)}");

        var node = JsonNode.Parse(body);
        var choice = node?["choices"]?[0];
        var text = choice?["message"]?["content"]?.GetValue<string>() ?? "";
        var finish = choice?["finish_reason"]?.GetValue<string>();
        return new ChatReply(text.Trim(), finish);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

