using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QqAgent;

internal sealed class AppConfig
{
    public string SelfName { get; set; } = "Amy";
    public string PersonaPath { get; set; } = @"..\yuno\personas\amy\persona.md";

    public YunoConfig Yuno { get; set; } = new();
    public ModelConfig Model { get; set; } = new();
    public DaemonConfig Daemon { get; set; } = new();
    public VisionConfig Vision { get; set; } = new();
    public AmyRuntimeConfig AmyRuntime { get; set; } = new();
    /// <summary>声线模块：唯一出正文的地方（纯人设 + 现场 + 意图）。</summary>
    public WriterConfig Writer { get; set; } = new();
    /// <summary>现场包：now/ambient 分层、记忆门控、预算。</summary>
    public SceneConfig Scene { get; set; } = new();
    /// <summary>梗词库：命中注入、冷却、新词候选。</summary>
    public MemesConfig Memes { get; set; } = new();
    /// <summary>可选快车道：off=全部交给值班脑；auto=纯文字回合由 daemon 内嵌决定（声线仍走 Writer）。</summary>
    public TriageConfig Triage { get; set; } = new();
    /// <summary>行动力策略：谁能指挥本机操作、群友能用到哪些脚本、产物目录白名单。</summary>
    public ActionConfig Action { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string BaseDir { get; private set; } = AppContext.BaseDirectory;

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"配置文件不存在: {path}");
        BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
    }
}

internal sealed class YunoConfig
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8457";
    public string Token { get; set; } = "";
}

internal sealed class ModelConfig
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "deepseek-flash";
    public double Temperature { get; set; } = 0.8;
    public int MaxTokens { get; set; } = 400;
}

internal sealed class VisionConfig
{
    /// <summary>视觉定位（截图 grounding）总开关；关或没 key 时 see 子命令直接拒绝，不产生任何网络调用。</summary>
    public bool Enabled { get; set; }
    /// <summary>bigmodel 按量 key（Coding Plan 套餐额度不可用于自建程序，官方 FAQ 明确）。</summary>
    public string BaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "glm-4.6v";
}

internal sealed class DaemonConfig
{
    /// <summary>观察哪个会话；空 = 跟随当前会话。</summary>
    public string Chat { get; set; } = "";
    /// <summary>事件聚合窗口（毫秒）。</summary>
    public int DebounceMs { get; set; } = 600;
    /// <summary>送给模型的最近消息条数。</summary>
    public int ContextMessages { get; set; } = 30;
    /// <summary>是否允许在该会话主动发言（否则只记录不回复）。</summary>
    public bool ReplyEnabled { get; set; } = true;
    /// <summary>调试：打印完整 prompt。</summary>
    public bool DumpPrompt { get; set; }
    /// <summary>纯通知模式（2026-10-25 定案 v2）：daemon 退化为传感器——只检测新消息、落 messages.jsonl、
    /// 原子写 amy-pending.json 交给人工观察台（DSH GUI 会话 + duty-watch）消费；不做模型调用/发送/spawn。
    /// 值班会话用 logs\amy-busy.flag 与 daemon 互斥窗口操作；false = 旧快/慢车道行为。</summary>
    public bool NotifyOnly { get; set; }
}

/// <summary>值班回复的运行时。Mode=dsh：spawn DSH headless 值班脑（现行）；embedded：daemon 内嵌模型直调（旧快车道，回退）；
/// none：只落档不处理（配合 Daemon.NotifyOnly 由 GUI 会话消费）。</summary>
internal sealed class AmyRuntimeConfig
{
    /// <summary>dsh | embedded | none（默认 none，config.json 显式开）。</summary>
    public string Mode { get; set; } = "none";
    public DshConfig Dsh { get; set; } = new();
    public bool Enabled { get; set; }
    /// <summary>快车道（2026-10-25）：普通文字消息 daemon 进程内直调模型（persona+Yuno 记忆）→ SendQuiet 静默发送
    /// → after_chat 落原文，全程无 spawn 无前台操作；模型输出 [ESCALATE] 才升级 headless Amy（复杂操作/工具带）。</summary>
    public bool FastLane { get; set; }
    /// <summary>快车道技能-绘图：[DRAW] → SD webui txt2img → 进程内贴图发送（服务未启动不自动拉起）。</summary>
    public bool FastDraw { get; set; }
    /// <summary>快车道技能-翻唱：[COVER] → amy_cover.py --search → 进程内 sendfile 发音频。</summary>
    public bool FastCover { get; set; }
    /// <summary>快车道技能-语音：[VOICE] 后跟要说的话 → GPT-SoVITS（gs_voice.py，柿本香里音色）→ sendfile 发 wav。
    /// 技能一律不发 ETA 预告、失败不发兜底文案（只报控制台/直播），成功才发产物本体（2026-10-25 用户定案）。</summary>
    public bool FastVoice { get; set; }
    /// <summary>语音条模式（2026-10-25）：[VOICE]/[COVER] 产物不再发文件卡片，而是驱动 QQ「语音消息」
    /// 按钮实录成真语音条——立体声混音（Realtek 回环）当录音设备，waveOut 把音频播进 Realtek 扬声器，
    /// QQ 录到即所得。官方客户端自身能力，无注入无协议工具。失败自动回落发文件。</summary>
    public bool VoiceAsPtt { get; set; } = true;
    /// <summary>语音条时长上限秒（QQ 普通账号硬上限 60s，留 2s 余量；SVIP 才有 3~5 分钟长语音）。</summary>
    public double VoiceMaxSeconds { get; set; } = 58;
    /// <summary>播放音量倍率（ffmpeg 内嵌进转码；QQ 录音链路有 AGC，太大易炸太小易糊。
    /// 2026-10-25 用户验收：0.8 偏大，降 30% 定档 0.56）。</summary>
    public double VoiceVolume { get; set; } = 0.56;
    /// <summary>录音端点匹配串：本机 Realtek「立体声混音」（模拟输出回环）。</summary>
    public string VoiceCaptureMatch { get; set; } = "立体声混音";
    /// <summary>播放端点匹配串：waveOut 定向到 Realtek（与录音回环同一物理声卡；匹配不到回落系统默认播放）。</summary>
    public string VoiceRenderMatch { get; set; } = "Realtek";
    /// <summary>翻唱超过语音上限时的策略：true=先发上限长度的语音条预览、紧跟着发完整音频文件；
    /// false=超长翻唱直接走文件卡片（不发预览）。语音技能不受此开关影响。</summary>
    public bool CoverVoicePreview { get; set; } = true;
    /// <summary>快车道阴暗面（2026-10-25）：输出协议加 [DARK] 正文——有人怼/挑衅/阴阳 Amy 本人时按阴暗面人设（贴吧对线风格）反击。</summary>
    public bool FastDark { get; set; }
    /// <summary>阴暗面人设文件（默认根目录 persona-dark.md，与 persona.md 分离互不影响）。</summary>
    public string DarkPersonaPath { get; set; } = @"..\persona-dark.md";
    /// <summary>SD webui（Automatic1111 API）地址。</summary>
    public string SdBaseUrl { get; set; } = "http://127.0.0.1:7860";
    /// <summary>演练模式：被调起的 Amy 只生成候选回复写 logs\amy-dryrun.txt，不发送、不 after_chat/add_fact。</summary>
    public bool DryRun { get; set; }
    public string NodePath { get; set; } = "node";
    /// <summary>[已废弃] ZCode CLI 路径：值班脑改为 DSH headless 后不再使用，仅留字段做旧配置兼容。</summary>
    public string ZcodeCjs { get; set; } = "";
    /// <summary>headless 会话工作目录（工程根；AGENTS.md 在该目录才自动加载）。</summary>
    public string ProjectRoot { get; set; } = "..";
    /// <summary>单次 Amy 会话超时（分钟）；超时 taskkill /T 杀进程树。</summary>
    public int TimeoutMinutes { get; set; } = 3;
    /// <summary>随 amy-pending.json 带给 Amy 的最近上下文条数。</summary>
    public int MaxContextMessages { get; set; } = 24;
}

/// <summary>DSH headless 值班脑的安装/启动参数。路径相对 qq-agent\（config.json 所在目录）。</summary>
internal sealed class DshConfig
{
    public string NodePath { get; set; } = "node";
    /// <summary>CLI 的 node_modules 根（默认 brain\node_modules）；入口 = &lt;CliRoot&gt;\@deepseek-ai\dsh\lib\bin.js。</summary>
    public string CliRoot { get; set; } = @"brain\node_modules";
    /// <summary>独立 DSH_HOME：会话/存储都在 brain 目录下，不污染用户 ~/.dsh，可整包清。</summary>
    public string Home { get; set; } = @"brain\dsh-home";
    /// <summary>脑的 profile 补丁（approval/sandbox/persona/模型）。</summary>
    public string Patch { get; set; } = @"brain\headless.patch.yml";
    public string Profile { get; set; } = "headless";
    /// <summary>fresh=每批新会话（默认，防历史漂移）；sticky=复用 logs\amy-session-id.txt。</summary>
    public string SessionMode { get; set; } = "fresh";
    /// <summary>额外环境变量（如 DSH_TELEMETRY_DISABLED=1）。</summary>
    public Dictionary<string, string> Env { get; set; } = new()
    {
        ["DSH_TELEMETRY_DISABLED"] = "1",
    };
}

internal sealed class WriterConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>true=值班脑不得自写正文，必须走 writer（应急 --text-file 除外）。</summary>
    public bool Required { get; set; } = true;
    public string PersonaPath { get; set; } = @"..\persona.md";
    public double Temperature { get; set; } = 0.95;
    /// <summary>正文预算。注意 deepseek-flash 会先出思维链，160 会被 reasoning 吃光导致空正文（实测 27% 空回）。</summary>
    public int MaxTokens { get; set; } = 800;
    /// <summary>单条上限字数（超了守卫重写）。</summary>
    public int MaxLineChars { get; set; } = 40;
    /// <summary>整条回复上限字数。</summary>
    public int MaxChars { get; set; } = 120;
    /// <summary>最多分几泡发送（QQ 里连发两条更像人）。</summary>
    public int MaxBubbles { get; set; } = 2;
    public int BubbleDelayMinMs { get; set; } = 350;
    public int BubbleDelayMaxMs { get; set; } = 900;
    /// <summary>与最近 3 条自己说过的话/结尾颜文字重复 → 重写。</summary>
    public bool BanRepeat { get; set; } = true;
    /// <summary>AI 腔/客服腔正则命中 → 重写。</summary>
    public bool BanAIisms { get; set; } = true;
    public int RetryOnViolation { get; set; } = 1;
}

internal sealed class SceneConfig
{
    /// <summary>「要接的这一段」条数。</summary>
    public int NowLines { get; set; } = 8;
    /// <summary>「背景」条数（只用来听懂梗）。</summary>
    public int AmbientLines { get; set; } = 24;
    /// <summary>现场包字节上限（超了先砍 ambient）。</summary>
    public int MaxBytes { get; set; } = 12000;
    /// <summary>off=不预取记忆；cue=命中回忆线索/被@问句才预取；always=每回合都取。</summary>
    public string MemoryGate { get; set; } = "cue";
    public int MemoryTopK { get; set; } = 3;
    public int MemoryBudget { get; set; } = 700;
    /// <summary>把 OCR 账本里已识别的文字直接内联进现场包。</summary>
    public bool OcrInline { get; set; } = true;
}

internal sealed class MemesConfig
{
    public string Path { get; set; } = @"data\memes.json";
    /// <summary>同一个梗用过后多少小时内不再注入（只进 cooling 提示）。</summary>
    public int CooldownHours { get; set; } = 12;
    public int CandidateTop { get; set; } = 5;
    public int MinCount { get; set; } = 3;
    public int MinSenders { get; set; } = 2;
    public int ScanLines { get; set; } = 200;
}

internal sealed class TriageConfig
{
    /// <summary>off（默认，全部走值班脑）| auto（纯文字回合内嵌决定 + 同一 writer）。</summary>
    public string Mode { get; set; } = "off";
}

/// <summary>行动力策略（2026-10-26）：群友只能触发白名单能力；本机 shell 只认 owner；产物只能从白名单目录发。</summary>
internal sealed class ActionConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>owner 的 QQ 号：可要求跑本机命令（L2）。这里只是占位，实际由 config.json 的 Action.OwnerUids 决定。</summary>
    public List<long> OwnerUids { get; set; } = new() { 1000000000 };
    /// <summary>单条本机命令超时秒数（超时 taskkill /T）。</summary>
    public int MaxSeconds { get; set; } = 120;
    /// <summary>可发给群里的产物目录（相对仓库根或绝对路径）。白名单外的一律拒绝，防泄露任意文件。</summary>
    public List<string> SendDirs { get; set; } = new() { "Sound", "qq-agent\\logs\\img", "bot-workspace\\archive" };
    /// <summary>群友（非 owner）可跑的白名单命令：格式 "相对cwd|命令行前缀"。不带 shell 元字符。</summary>
    public List<string> GroupAllowedCommands { get; set; } = new()
    {
        @"bot-workspace|python amy_cover.py",
        @"bot-workspace\chattts-restore|.venv\Scripts\python.exe gen_voice.py",
    };
    /// <summary>非 owner 触发时拒绝的危险片段（小写匹配）。</summary>
    public List<string> DenyPatterns { get; set; } = new()
    {
        "format c:", "format d:", "format /fs", "shutdown /", "shutdown.exe", "stop-computer",
        "restart-computer", "diskpart", "bcdedit", "vssadmin delete", "reg delete hklm",
        "cipher /w", "remove-item -recurse c:\\", "rm -rf /",
    };
    /// <summary>owner 也硬拒的不可逆磁盘/系统破坏（防手滑；不含关机/重启）。</summary>
    public List<string> OwnerHardDenyPatterns { get; set; } = new()
    {
        "format c:", "format d:", "format /fs", "diskpart clean", "cipher /w",
        "remove-item -recurse c:\\windows", "vssadmin delete shadows",
    };
}






