using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QqAgent;

/// <summary>窗口截图：PrintWindow(PW_RENDERFULLCONTENT) 优先，黑屏降级屏幕拷贝。存 PNG + 元数据侧车。</summary>
internal static class Snap
{
    public sealed record Shot(string Path, Native.RECT Rect, string Title, IntPtr Hwnd);

    /// <summary>截单个窗口。返回 null = 拿不到有效画面（窗口收进托盘/已销毁等）。</summary>
    public static Shot? Capture(IntPtr hwnd, Native.RECT rect, string title, string outDir)
    {
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        // 收进托盘时窗口会被移到 -16000（README 坑5），这种窗口截了也是废图
        if (w <= 0 || h <= 0 || rect.Left <= -10000) return null;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            bool printed;
            try { printed = Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }

            if (!printed || IsBlank(bmp))
            {
                // 个别硬件加速窗口 PrintWindow 给黑图，只能退回屏幕拷贝（要求窗口可见无遮挡）
                try { g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(w, h)); } catch { }
            }
        }
        if (IsBlank(bmp)) return null;

        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"snap-{NameOf(title, hwnd)}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        bmp.Save(path, ImageFormat.Png);
        File.WriteAllText(path + ".json", new JsonObject
        {
            ["hwnd"] = hwnd.ToInt64(),
            ["title"] = title,
            ["left"] = rect.Left,
            ["top"] = rect.Top,
            ["right"] = rect.Right,
            ["bottom"] = rect.Bottom,
        }.ToJsonString());
        return new Shot(path, rect, title, hwnd);
    }

    /// <summary>加载已有截图及其元数据（see --snap 用；坐标换算依赖窗口未移动过）。</summary>
    public static Shot? Load(string pngPath)
    {
        if (!File.Exists(pngPath) || !File.Exists(pngPath + ".json")) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(pngPath + ".json"));
            var rect = new Native.RECT
            {
                Left = node!["left"]!.GetValue<int>(),
                Top = node["top"]!.GetValue<int>(),
                Right = node["right"]!.GetValue<int>(),
                Bottom = node["bottom"]!.GetValue<int>(),
            };
            return new Shot(pngPath, rect, node["title"]?.GetValue<string>() ?? "", (IntPtr)(node["hwnd"]?.GetValue<long>() ?? 0));
        }
        catch { return null; }
    }

    /// <summary>采样判断是否全黑（PrintWindow 失败的典型表现）。</summary>
    private static bool IsBlank(Bitmap bmp)
    {
        int stepX = Math.Max(1, bmp.Width / 32), stepY = Math.Max(1, bmp.Height / 32);
        for (int x = stepX / 2; x < bmp.Width; x += stepX)
            for (int y = stepY / 2; y < bmp.Height; y += stepY)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 12 || p.G > 12 || p.B > 12) return false;
            }
        return true;
    }

    private static string NameOf(string title, IntPtr hwnd)
    {
        var sb = new StringBuilder();
        foreach (var c in title.Trim())
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|') continue;
            sb.Append(c);
            if (sb.Length >= 24) break;
        }
        return sb.Length > 0 ? sb.ToString() : $"hwnd-{hwnd}";
    }
}

/// <summary>视觉 grounding：截图 + 目标描述 → OpenAI 兼容视觉 API → 0-1000 归一化 bbox。</summary>
internal sealed class VisionClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly VisionConfig _cfg;

    public VisionClient(VisionConfig cfg) => _cfg = cfg;

    public void EnsureReady()
    {
        if (!_cfg.Enabled)
            throw new InvalidOperationException("Vision.Enabled=false（config.json），视觉定位未开启");
        if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
            throw new InvalidOperationException("Vision.ApiKey 未配置（需 bigmodel 按量 key；Coding Plan 套餐额度不可用于自建程序）");
    }

    /// <summary>2026-10-27 新增：看懂一张图（识图，不返回坐标）。把 PNG 发给视觉模型，拿一段中文描述。
    /// 用途：OCR 无文字、又必须理解画面时调用一次；平时一律先走 QQ 内置 OCR（快、免费、零幻觉）。</summary>
    public string Describe(string imagePath, string question)
    {
        EnsureReady();
        if (!File.Exists(imagePath)) throw new InvalidOperationException("图片不存在：" + imagePath);
        var q = string.IsNullOrWhiteSpace(question)
            ? "用中文简要描述这张图：是什么、画面上有什么、有没有文字；只写你确实看到的，看不清就说看不清。"
            : question.Trim();
        var payload = new JsonObject
        {
            ["model"] = _cfg.Model,
            ["temperature"] = 0.2,
            ["max_tokens"] = 400,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(imagePath)),
                            },
                        },
                        new JsonObject { ["type"] = "text", ["text"] = q },
                    },
                },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = Http.Send(req);
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"识图调用失败 {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] + "…" : body)}");
        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        return content.Trim();
    }

    /// <summary>返回 [x1,y1,x2,y2]（0-1000 归一化）；null = 模型未给出可解析的框。</summary>
    public double[]? Locate(string imagePath, string target)
    {
        EnsureReady();
        var payload = new JsonObject
        {
            ["model"] = _cfg.Model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(imagePath)),
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"Help me to locate the '{target}' in the screenshot and give me its bounding boxes. "
                                + "只输出一个数组，格式 [[x1,y1,x2,y2]]，坐标为 0-1000 归一化，不要输出其他文字。",
                        },
                    },
                },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = Http.Send(req);
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"视觉模型调用失败 {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] + "…" : body)}");

        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var m = Regex.Match(content,
            @"\[\[\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)\s*\]\]");
        if (!m.Success) return null;
        return new[]
        {
            double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value),
            double.Parse(m.Groups[3].Value), double.Parse(m.Groups[4].Value),
        };
    }
}

