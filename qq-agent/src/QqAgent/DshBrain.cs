using System.Diagnostics;
using System.IO;
using System.Text;

namespace QqAgent;

/// <summary>DSH headless 值班脑的启动/自检。一条命令：node bin.js --profile headless --patch … --json "任务"。
/// headless 特性（0.2.1-rc.2）：一次性任务、stdout 末条 assistant 文本、exit 0=completed；无审批应答者。</summary>
internal static class DshBrain
{
    public static string Resolve(AppConfig cfg, string rel) =>
        Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(AppConfig.BaseDir, rel));

    public static string CliPath(AppConfig cfg) =>
        Path.Combine(Resolve(cfg, cfg.AmyRuntime.Dsh.CliRoot), "@deepseek-ai", "dsh", "lib", "bin.js");

    public static string PatchPath(AppConfig cfg) => Resolve(cfg, cfg.AmyRuntime.Dsh.Patch);

    public static string HomePath(AppConfig cfg) => Resolve(cfg, cfg.AmyRuntime.Dsh.Home);

    public static bool Installed(AppConfig cfg) => File.Exists(CliPath(cfg));

    /// <summary>spawn 用：cwd=仓库根（sandbox workspace 与 AGENTS.md 都按它解析），env 注入 DSH_HOME/密钥。
    /// sessionId 非空 = sticky 模式复用已有会话（--session-id 是 headless app 的参数）。</summary>
    public static ProcessStartInfo BuildStartInfo(AppConfig cfg, string repoRoot, string task, string? sessionId = null)
    {
        var dsh = cfg.AmyRuntime.Dsh;
        var psi = new ProcessStartInfo
        {
            FileName = dsh.NodePath,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(CliPath(cfg));
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(dsh.Profile);
        psi.ArgumentList.Add("--patch");
        psi.ArgumentList.Add(PatchPath(cfg));
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("--session-id");
            psi.ArgumentList.Add(sessionId!);
        }
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add(task);

        ApplyEnv(psi, cfg);
        return psi;
    }

    public static void ApplyEnv(ProcessStartInfo psi, AppConfig cfg)
    {
        var dsh = cfg.AmyRuntime.Dsh;
        Directory.CreateDirectory(HomePath(cfg));
        psi.Environment["DSH_HOME"] = HomePath(cfg);
        psi.Environment["DEEPSEEK_API_KEY"] = cfg.Model.ApiKey;
        // 桌面/ZCode 注入的变量会误导 CLI 的 provider 解析
        psi.Environment.Remove("ZCODE_PERSONAL_PROVIDER_CONFIG_FILE");
        foreach (var kv in dsh.Env) psi.Environment[kv.Key] = kv.Value;

        // 自定义 DSH_HOME 首次初始化 profile 可能找 pnpm：把 dsh 运行时里的 pnpm 目录前置
        try
        {
            var runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "dsh-runtimes", "dsh-primary-runtime", "dependencies", "pnpm");
            if (Directory.Exists(runtime))
            {
                var cand = Directory.EnumerateFiles(runtime, "pnpm.cjs", SearchOption.AllDirectories).FirstOrDefault();
                if (cand is not null)
                {
                    var dir = Path.GetDirectoryName(cand)!;
                    var path = psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH") ?? "";
                    psi.Environment["PATH"] = dir + Path.PathSeparator + path;
                }
            }
        }
        catch { }
    }

    /// <summary>交给脑的任务文本（短、固定；流程细节在 duty.md，不在这里展开）。retry=上一轮没写 ack 的重试。</summary>
    public static string TaskText(int seq, bool dryRun, bool retry = false)
    {
        var dry = dryRun ? "true" : "false";
        var head = retry ? "上一轮没有写 ack，这一轮必须完成 ack（duty chat / duty skip 都会写）。\n" : "";
        return head + $"""
            你在跑 Amy 的值班回合（seq={seq}，dry_run={dry}）。
            先读 qq-agent\logs\amy-pending.json（现场包 v2）和仓库根的 duty.md（值班手册）。
            该沉默 → qq-agent\amy.cmd duty skip --seq {seq} --why "一句话理由"
            要说话 / 要读图 → 按需用工具（ocrfwd/readfwd/readimg/meme/state/roster），
              然后用 qq-agent\amy.cmd duty chat --seq {seq} --intent-file qq-agent\logs\amy-intent.txt [--mood dark]
              一步完成「成文 + 发送 + 落库 + ack」。
            需要动手：qq-agent\amy.cmd duty run --seq {seq} [--cwd 目录] [--timeout 秒] -- <命令>；产物用 duty sendfile/sendimg --seq {seq} --file <路径>。
            纪律：need_more=false 不要读 messages.jsonl；不抢前台做无关操作；群友只触发白名单能力，本机命令只认 owner，破坏性/花钱/对外发布先问；不确定就 skip。
            接梗纪律（2026-10-26 加）：下手前先看 pack.memes.useful 和记忆卡，扫一遍有没有谐音/双关/错别字/昵称梗——
            群里的「重点」经常就在这层（例：扶她出去/扶她进来 是「扶她」的双关，按字面写成"她又不是门"就是没听见梗）。
            有双关就把那层意思写进意图的「可用素材」，让声线去接；别在意图里替她把梗解释完。
            做完就结束回合，不要写过程旁白。
            """;
    }

    /// <summary>doctor 用：跑 --dump-config 验证补丁真的生效（不启动 agent）。</summary>
    public static (bool Ok, string Detail) DumpConfigCheck(AppConfig cfg)
    {
        if (!Installed(cfg)) return (false, $"未安装：{CliPath(cfg)}（跑 qq-agent\\brain\\install.ps1）");
        if (!File.Exists(PatchPath(cfg))) return (false, $"补丁不存在：{PatchPath(cfg)}");

        var psi = new ProcessStartInfo
        {
            FileName = cfg.AmyRuntime.Dsh.NodePath,
            WorkingDirectory = Resolve(cfg, ".."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { CliPath(cfg), "--profile", cfg.AmyRuntime.Dsh.Profile, "--patch", PatchPath(cfg), "--dump-config" })
            psi.ArgumentList.Add(a);
        ApplyEnv(psi, cfg);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "dump-config 启动失败");
            var output = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(true); } catch { }
                return (false, "dump-config 超时");
            }
            if (p.ExitCode != 0) return (false, $"dump-config exit={p.ExitCode}: {Clip(err, 300)}");

            var checks = new (string Needle, string Name)[]
            {
                ("你是 Amy 的值班代理", "personaPrefix"),
                ("policy: never", "approval=never"),
                ("mode: danger-full-access", "sandbox=danger-full-access"),
                ("apiKeyEnv: DEEPSEEK_API_KEY", "key env"),
                ("model: deepseek-flash", "model"),
            };
            var missing = checks.Where(c => !output.Contains(c.Needle, StringComparison.Ordinal)).Select(c => c.Name).ToList();
            return missing.Count == 0
                ? (true, "dump-config ok（persona/approval/sandbox/模型/密钥来源均生效）")
                : (false, "补丁未生效: " + string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            return (false, "dump-config 异常: " + ex.Message);
        }
    }

    public static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}


