using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QqAgent;

internal sealed class Daemon
{
    private readonly AppConfig _cfg;
    private readonly QqUi _ui;
    private readonly YunoClient _yuno;
    private readonly Brain _brain;
    private readonly AmyRuntimeConfig _amy;
    private readonly string _persona;
    private readonly string _personaDark;
    private readonly string _logDir;
    private readonly Dictionary<string, ScopeInfo> _scopeMap = new();
    private readonly Dictionary<string, Dictionary<string, long>> _rosterMap = new();
    private readonly HashSet<string> _uidLogged = new();

    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    // 2026-10-27：去重窗口。签名不再含 m.Time（QQNT 重渲染时空/有值会翻转，同一条消息会被当新消息重投，
    // 现场包就出现"不存在的旧消息"）。改为同签名 6 小时内只收一次。
    // 2026-10-28 修「真重发被吞」：纯 6 小时窗口会把「同一人隔 19 分钟逐字重发的新气泡」也当重渲染吞掉
    // （12:49 優鬱重发需求实测未投递）。新增 _sigFloor 记每个签名上次处理时的「时间语境」（时间桶 epoch，
    // 无桶记当下时刻），命中去重后若「在列表末尾且桶更新」判为真重发放行，详见 Tick 内注释。
    private readonly Dictionary<string, DateTimeOffset> _seenAt = new();
    /// <summary>签名 → 上次处理时的时间桶 epoch 秒（当时无桶则记处理时刻）。末尾真重发判定用：当前桶必须严格比它新才放行。</summary>
    private readonly Dictionary<string, long> _sigFloor = new();

    private string _chat = "";
    private bool _primed;
    /// <summary>原文层最后一条的 ts（epoch 秒）：时间闸用它判断补出来的消息是新的还是旧的。</summary>
    private long _lastStoredTs;
    private DateTime _amyStarted;
    private string _amyInfo = "";
    private string _lastResult = "";
    private DateTime _statusLastWrite = DateTime.MinValue;
    private bool _unreadSwitch;
    /// <summary>主循环连续异常次数（2026-10-29）：成功跑完一轮就清零；连续多次说明 UIA 连接已废，日志里报出来好排查。</summary>
    private int PollFaultCount;

    /// <summary>快车道长任务（绘图/翻唱）后台完成后回主线程执行的收尾动作（发送/落库必须在主 STA 线程做 UIA）。</summary>
    private readonly ConcurrentQueue<Action> _mainQueue = new();
    private static readonly HttpClient HttpPing = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>headless Amy 会话进行中：挡住本进程一切 QQ 窗口操作（读消息/切会话），避免与 Amy 侧 send 抢焦点。</summary>
    private volatile bool _amyBusy;
    /// <summary>Amy 会话结束信号：队列里没有事件时（如 DryRun 未发送）也要补一次 tick 处理积压消息。</summary>
    private volatile bool _amyDoneSignal;
    private int _amySeq;

    /// <summary>常驻值班会话 id（logs\amy-session-id.txt）：spawn 一律 --resume 复用同一会话，不再每任务新建（2026-10-25 用户定案，会话列表曾被堆出 16 个）。</summary>
    private readonly string _amySessionIdPath;
    /// <summary>headless 进程互斥锁（logs\amy.lock 存 node PID，PID 存活即锁有效）：
    /// daemon 被关后 headless 会变孤儿继续跑，新 daemon 若再 spawn 就是两个 yolo 会话并行操作真机——锁跨 daemon 代际生效。</summary>
    private readonly string _amyLockPath;
    /// <summary>车道故障冷却截止：快车道模型失败或 headless 秒退后 N 分钟内新消息只落档不升级，防止链路故障时连环 spawn。</summary>
    private DateTime _laneCooldownUntil = DateTime.MinValue;
    private const int LaneCooldownMinutes = 5;

    /// <summary>NotifyOnly 模式：最近一次投递 (seq, 时间)；值班会话每处理完一轮写 logs\amy-ack.json。
    /// 投递超过 10 分钟未消费则告警一次（人工观察台没开/没处理），恢复消费后自动复位。</summary>
    private (int Seq, DateTime At) _lastNotify;
    private bool _notifyWarned;
    private readonly string _busyFlagPath;

    /// <summary>原文层行号基线：每条 messages.jsonl append 自增；现场包给每条消息钉行号（=OCR 账本主键）。</summary>
    private long _jsonlLines;
    /// <summary>现场包构建器（now/ambient/OCR 内联/梗/记忆门控/现场状态）。</summary>
    private readonly PackBuilder _pack;

    private sealed record ScopeInfo(string Kind, long Id);

    public Daemon(AppConfig cfg, QqUi ui)
    {
        _cfg = cfg;
        _ui = ui;
        _yuno = new YunoClient(cfg.Yuno);
        _brain = new Brain(cfg.Model);

        _logDir = Path.Combine(AppConfig.BaseDir, "logs");
        Directory.CreateDirectory(_logDir);

        var personaPath = ResolvePath(cfg.PersonaPath);
        if (File.Exists(personaPath))
        {
            _persona = File.ReadAllText(personaPath);
            Log($"persona loaded: {personaPath} ({_persona.Length} chars)");
        }
        else
        {
            _persona = "你是一个叫 Amy 的群聊机器人。";
            Log($"[WARN] persona not found: {personaPath}，回退内置人设");
        }

        var darkPath = ResolvePath(cfg.AmyRuntime.DarkPersonaPath);
        if (cfg.AmyRuntime.FastDark && File.Exists(darkPath))
        {
            _personaDark = File.ReadAllText(darkPath);
            Log($"dark persona loaded: {darkPath} ({_personaDark.Length} chars, 常驻全域 2026-10-26 定案)");
        }
        else
        {
            _personaDark = "";
            if (cfg.AmyRuntime.FastDark)
                Log($"dark persona 独立文件不存在: {darkPath}——阴暗面常驻不受影响（人设段落已并入 persona.md，2026-10-26 定案）");
        }

        LoadScopeMap();
        LoadRosterMap();

        _amy = cfg.AmyRuntime;
        _amySessionIdPath = Path.Combine(_logDir, "amy-session-id.txt");
        _amyLockPath = Path.Combine(_logDir, "amy.lock");
        _busyFlagPath = Path.Combine(_logDir, "amy-busy.flag");

        var jsonl = Path.Combine(_logDir, "messages.jsonl");
        _jsonlLines = Journal.CountLines(jsonl);
        _lastStoredTs = Journal.LastTs(jsonl);
        SeedSeen(jsonl);
        // 2026-10-27：种子已经建立基线，启动后不必再 prime。否则「启动 → 首个 tick 没有新消息 → _primed 仍为 false
        // → 下一条真正的新消息被当成 baseline 吞掉」，实测重启后 10 分钟内丢过一条（12:19 那次 primed 1 baseline）。
        _primed = true;
        _pack = new PackBuilder(cfg, _logDir, Path.Combine(AppConfig.BaseDir, "data"), _yuno, UidOf);
        Log($"原文层基线: messages.jsonl {_jsonlLines} 行；现场包 v2 就绪");

        if (_cfg.Daemon.NotifyOnly)
            Log("daemon mode: 纯通知（DSH GUI 观察台消费 amy-pending.json；本进程不调模型不发送不 spawn）");
        if (string.Equals(_amy.Mode, "dsh", StringComparison.OrdinalIgnoreCase))
            Log($"amy runtime: dsh headless（dryrun={_amy.DryRun}, timeout={_amy.TimeoutMinutes}m, cli={DshBrain.CliPath(_cfg)}）");
        else if (_amy.Enabled)
            Log($"amy runtime: embedded fastlane={_amy.FastLane}（dryrun={_amy.DryRun}；需要工具时升级 dsh 脑）");
        else
            Log("amy runtime: none（只落档，不回复）");

        // 值班脑补丁自检（沙箱外做；值班脑自己的 doctor 在沙箱里跑不了 dump-config）
        _ = Task.Run(() =>
        {
            try
            {
                var (ok, detail) = DshBrain.DumpConfigCheck(_cfg);
                var path = Path.Combine(_logDir, "brain-patch-check.json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new
                {
                    ok,
                    detail,
                    at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                }), Encoding.UTF8);
                File.Move(tmp, path, true);
                if (!ok) Log("[WARN] 值班脑补丁自检失败: " + detail);
                else Log("值班脑补丁自检通过");
            }
            catch (Exception ex)
            {
                Log("[WARN] 值班脑补丁自检异常: " + ex.Message);
            }
        });

        if (LockAlive(out var orphanPid))
            Log($"[WARN] 前代 headless Amy 孤儿仍在运行（pid={orphanPid}）：在它退出前拒绝新的 spawn，避免并行操作真机");
    }

    private static string ResolvePath(string p) =>
        Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppConfig.BaseDir, p));

    private void LoadScopeMap()
    {
        var path = Path.Combine(AppConfig.BaseDir, "data", "scope-map.json");
        if (!File.Exists(path))
        {
            Log("scope-map.json 不存在，暂用「会话标题」当 scope");
            return;
        }
        try
        {
            // scope-map.json 的键是小写 kind/id；positional record 参数默认按大小写敏感匹配，不加此项会整体反序列化成 null/0（2026-10-25 值班演练实测）
            var map = JsonSerializer.Deserialize<Dictionary<string, ScopeInfo>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (map is null) return;
            foreach (var kv in map) _scopeMap[kv.Key] = kv.Value;
            Log($"scope-map 已载入 {_scopeMap.Count} 条");
        }
        catch (Exception ex)
        {
            Log("scope-map 读取失败: " + ex.Message);
        }
    }

    /// <summary>昵称/群名片 → QQ 号（data/roster-map.json，按 scope 分组）。
    /// user_id=0 会让 Yuno 跳过印象卡、人物关系注入和按人归档，所以尽力解析。</summary>
    private void LoadRosterMap()
    {
        var path = Path.Combine(AppConfig.BaseDir, "data", "roster-map.json");
        if (!File.Exists(path))
        {
            Log("roster-map.json 不存在，user_id 将传 0（Yuno 印象卡/关系注入不生效）");
            return;
        }
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(File.ReadAllText(path));
            if (map is null) return;
            foreach (var kv in map) _rosterMap[kv.Key] = kv.Value;
            var total = _rosterMap.Values.Sum(v => v.Count);
            Log($"roster-map 已载入 {_rosterMap.Count} 个 scope / {total} 个名字");
        }
        catch (Exception ex)
        {
            Log("roster-map 读取失败: " + ex.Message);
        }
    }

    private long UidOf(string sender)
    {
        var (kind, id) = ScopeOf();
        if (kind == "c2c" && id > 0) return id;
        var key = id > 0 ? $"{kind}:{id}" : "";
        if (key.Length == 0 || !_rosterMap.TryGetValue(key, out var names)) return 0;
        if (names.TryGetValue(sender.Trim(), out var uid)) return uid;
        return 0;
    }

    private void LogUidOnce(string sender, long uid)
    {
        var sig = $"{_chat}|{sender}|{uid}";
        if (!_uidLogged.Add(sig)) return;
        Log(uid > 0 ? $"uid resolved: '{sender}' -> {uid}" : $"uid miss: '{sender}' -> 0（补 data/roster-map.json）");
    }

    public void Run(CancellationToken ct)
    {
        Native.DisableConsoleQuickEdit();   // 2026-10-27：控制台被点一下进"选择"模式会把整个 daemon 写阻塞
        StartLiveTail();
        // 2026-10-29：进程级起止留痕。以前 daemon 静默消失时 agent.log 里连"死"的痕迹都没有，
        // 无法区分「崩溃」和「用户关窗口」——启动写 pid、正常退出写 STOPPED，缺失即异常终止。
        Log($"daemon START pid={Environment.ProcessId} ver={typeof(Daemon).Assembly.GetName().Version}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log($"daemon STOPPED pid={Environment.ProcessId}（进程退出）");
        Log($"yuno healthy = {_yuno.Healthy()}");

        if (!string.IsNullOrWhiteSpace(_cfg.Daemon.Chat) && _ui.OpenChat(_cfg.Daemon.Chat))
            Log($"opened chat '{_cfg.Daemon.Chat}'");

        _chat = WaitHeader();
        Log($"watching chat '{_chat}'  group={_ui.IsGroup()}");

        using var spy = new WinEventSpy();
        spy.Start((int)GetPid(_ui.Hwnd));

        var lastEvent = DateTime.UtcNow;
        var pending = 0;
        // 2026-10-27：WinEvent 只是"多数情况够用"的唤醒源。窗口在屏幕外/被遮挡/长时间不重绘时
        // Chromium 会降频甚至不派发 UIA 事件，新消息就静默丢掉（用户反馈"不点 QQ 就不投递"）。
        // 兜底：每 12s 强制重绘 + 主动读一遍（读是幂等的，去重靠签名），不抢前台、不动焦点。
        var lastPoll = DateTime.UtcNow;
        var pollOnly = false;
        _ui.EnsureChatAreaVisible();   // 2026-10-27：消息区在屏外就不渲染，新消息读不到 → 先把窗口平移进来

        while (!ct.IsCancellationRequested)
        {
          // 2026-10-29：整轮兜底。Tick() 里本来有 try，但循环体其余部分（spy.Drain / _ui.Header() /
          // EnforceWinLock / ScrollMessagesToBottom 等）只要抛一次 UIA COM 异常，就会穿出 Run() →
          // Program.Main 的 catch → 进程静默退出。现在一律就地接住、落盘、睡 500ms 继续跑。
          try
          {
            // 快车道长任务（绘图/翻唱）的收尾动作回主线程执行（UIA 全部留在主 STA 线程）
            while (_mainQueue.TryDequeue(out var act))
            {
                try { act(); }
                catch (Exception ex) { Log("main-queue action failed: " + ex.Message); }
            }

            Native.DrainMessages();
            Thread.Sleep(60);

            // Amy 会话刚结束：即使没有新事件也补一次 tick，处理它忙期间到达的消息
            if (_amyDoneSignal)
            {
                _amyDoneSignal = false;
                pending += 1;
            }

            var drained = spy.Drain(out _);
            if (drained > 0)
            {
                lastEvent = DateTime.UtcNow;
                pending += drained;
            }

            // 无事件时也定期找未读，避免漏掉别的会话；Amy 忙时跳过（不能抢窗口焦点）
            if (pending == 0 && !_amyBusy && !SessionBusy() && (DateTime.UtcNow - lastEvent).TotalMilliseconds > 3000)
            {
                if (TrySwitchToUnread()) pending = 1;
            }

            // 兜底轮询（见上）：每 12s 叫醒渲染 + 主动读一遍
            if (!_amyBusy && !SessionBusy() && (DateTime.UtcNow - lastPoll).TotalSeconds >= 12)
            {
                lastPoll = DateTime.UtcNow;
                _ui.EnsureChatAreaVisible();      // 用户挪过窗口就再拉回来
                EnforceWinLock();                 // 位置锁：用户拖走了就按 win-lock.json 拉回
                _ui.ScrollMessagesToBottom();     // 用户往上翻过 → 拉回底部，否则新消息不进树
                _ui.NudgeRepaint();
                pending += 1;
                pollOnly = true;
            }

            // NotifyOnly 失联检测：投递超时未被人工观察台消费 → 告警一次（会话没开/没处理/卡死）
            if (_cfg.Daemon.NotifyOnly && !_notifyWarned && _lastNotify.Seq > 0
                && (DateTime.Now - _lastNotify.At).TotalMinutes > 10 && ReadAckSeq() < _lastNotify.Seq)
            {
                _notifyWarned = true;
                Log($"[WARN] notify#{_lastNotify.Seq} 已投递超过 10 分钟未被观察台消费（ack seq={ReadAckSeq()}）：检查 DSH 会话是否在值守/是否已布置 watch");
            }

            if (pending > 0 && (DateTime.UtcNow - lastEvent).TotalMilliseconds >= _cfg.Daemon.DebounceMs)
            {
                var n = pending;
                pending = 0;
                var quiet = pollOnly;
                pollOnly = false;
                if (!quiet) Log($"events={n} -> tick");
                try { Tick(); }
                catch (Exception ex) { Log("tick failed: " + ex.Message); }
            }

            if ((DateTime.UtcNow - _statusLastWrite).TotalSeconds > (_amyBusy ? 2 : 20))
                WriteStatus();

            var hdr = _ui.Header();
            if (!string.IsNullOrEmpty(hdr) && hdr != _chat)
            {
                _chat = hdr;
                _primed = false;
                Log($"chat changed -> '{_chat}' group={_ui.IsGroup()}");
            }
            if (PollFaultCount > 0) PollFaultCount = 0;
          }
          catch (Exception ex)
          {
              Log("[FATAL-LOOP] " + ex.GetType().Name + " hr=0x" + ex.HResult.ToString("X8") + ": " + ex.Message);
              CrashLog("main-loop", ex);
              PollFaultCount++;
              if (PollFaultCount == 10)
                  Log("[WARN] 主循环连续 10 轮异常：UIA 事件源可能已失效，建议重启 daemon（本进程会继续重试）");
              try { Thread.Sleep(500); } catch { }
          }
        }
    }

    private string WaitHeader()
    {
        var h = _ui.Header();
        for (int i = 0; i < 20 && string.IsNullOrEmpty(h); i++)
        {
            Thread.Sleep(250);
            h = _ui.Header();
        }
        if (string.IsNullOrEmpty(h))
            throw new InvalidOperationException("会话标题为空：QQ 主窗口处于空面板，请先手动点开一个会话");
        return h;
    }

    /// <summary>把有未读的会话切过来；返回是否发生了切换。</summary>
    private bool TrySwitchToUnread()
    {
        try
        {
            var unread = _ui.Chats().Where(c => c.Unread && c.Title != _chat).ToList();
            if (unread.Count == 0) return false;

            var target = unread[0];
            if (!_ui.OpenChat(target.Title)) return false;

            _chat = target.Title;
            _primed = false;
            _unreadSwitch = true;
            Log($"switched to unread '{_chat}' preview='{target.Preview}'");
            return true;
        }
        catch (Exception ex)
        {
            Log("switch failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>2026-10-27：窗口位置锁。logs\win-lock.json 存在时，用户把 QQ 拖走/改尺寸，
    /// 每 12s 兜底轮询按锁定值拉回（用户：「把 qq 固定住，不让我直接动」）。删掉文件即解锁。</summary>
    private (long Left, long Top, long W, long H, DateTime Mtime)? _pin;

    private void EnforceWinLock()
    {
        try
        {
            var path = Path.Combine(_logDir, "win-lock.json");
            if (!File.Exists(path)) { _pin = null; return; }
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_pin is null || _pin.Value.Mtime != mtime)
            {
                var n = JsonNode.Parse(File.ReadAllText(path));
                _pin = (
                    (long)(n?["left"]?.GetValue<long>() ?? 0),
                    (long)(n?["top"]?.GetValue<long>() ?? 0),
                    (long)(n?["width"]?.GetValue<long>() ?? 0),
                    (long)(n?["height"]?.GetValue<long>() ?? 0),
                    mtime);
                Log($"win-lock 载入：({_pin.Value.Left},{_pin.Value.Top} {_pin.Value.W}x{_pin.Value.H})");
            }
            var p = _pin.Value;
            Native.GetWindowRect(_ui.Hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            var same = Math.Abs(r.Left - p.Left) <= 2 && Math.Abs(r.Top - p.Top) <= 2
                       && (p.W <= 0 || Math.Abs(w - p.W) <= 2) && (p.H <= 0 || Math.Abs(h - p.H) <= 2);
            if (same) return;
            QqUi.Place(_ui.Hwnd, (int)p.Left, (int)p.Top, (int)p.W, (int)p.H);
            Log($"win-lock：窗口被挪到 ({r.Left},{r.Top} {w}x{h})，已拉回 ({p.Left},{p.Top} {p.W}x{p.H})");
        }
        catch { }
    }

    private void Tick()
    {
        // Amy 会话进行中：不读消息、不 Mark（消息留给它结束后合并处理），也不动窗口。
        // NotifyOnly 模式下值班会话用 logs\amy-busy.flag 声明占用（10 分钟防僵死兜底），语义相同
        if ((_amy.Enabled && _amyBusy) || SessionBusy())
        {
            Log("amy busy, tick deferred");
            return;
        }

        var msgs = _ui.Messages();
        // 2026-10-28 阶段2：文件气泡探针（空正文气泡的结构 dump，同形状去重），写 agent.log 供下轮细化判定
        foreach (var probe in _ui.DrainFileProbes())
            Log(probe);
        if (msgs.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(_cfg.Daemon.Chat) && _ui.OpenChat(_cfg.Daemon.Chat))
                Log($"reopened chat '{_cfg.Daemon.Chat}'");
            return;
        }

        // 2026-10-27 时间闸：QQ 的 HH:mm 分隔条划出「时间桶」，桶头时间比现在早 20 分钟以上的整桶当历史
        // （晚渲染 / 切会话补齐 / @ 气泡首帧文本不全）。
        // 不用位置判定：UI 是虚拟列表，OCR 开图、切会话时节点会回收重排，新消息可能排在已入库消息之前，
        // 12:30 就这样把东哥哥两条新消息判成历史吞掉了（原文层和投递都没有）。同一句 6 小时内重发仍会漏，属既有设计。
        // 2026-10-27 追加：**列表最后一条永远不按历史处理**——新消息一定在末尾，宁可多投一次也别吞掉。
        // 2026-10-27 再修：只有「桶时间 ≤ 原文层最后一条的时间」才算历史。渲染卡住一段时间后一口气补出来的
        // 新消息，桶虽然旧、但比库里最后一条新，必须照投（17:54 那批 16:07 的消息就是被时间闸吞掉的）。
        var lastIdx = -1;
        for (var i = msgs.Count - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(msgs[i].Text)) { lastIdx = i; break; }

        var staleBucket = false;
        long bucketTs = 0;
        var fresh = new List<MessageItem>();
        var backfilled = 0;
        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (string.IsNullOrWhiteSpace(m.Text)) continue;
            // 当前气泡是否自带分隔条时间标签：随循环每条重置，防止继承上一条气泡的桶
            var hasOwnBucket = !string.IsNullOrWhiteSpace(m.Time);
            if (hasOwnBucket)
            {
                staleBucket = LooksStale(m.Time);
                bucketTs = BucketTs(m.Time);
            }
            var sig = SigOf(m);
            if (SeenRecently(sig))
            {
                // 2026-10-28 修「真重发被去重吞掉」（12:30 需求已投，12:49:57 同人逐字重发被 6 小时窗口吞掉、永不投递）。
                // 已见签名默认仍按重渲染抑制；但同时满足 ①在列表末尾（i == lastIdx，新气泡一定出现在末尾）
                // ②气泡自带分隔条时间标签（hasOwnBucket）③该桶比此签名上次处理时（_sigFloor）严格更新
                // → 判为「同一人逐字重发的新气泡」，放行投递。
                // 反面＝重渲染，仍被抑制：旧气泡重读时位置不在末尾；末尾气泡每 tick 被重读时桶不变；
                // 无自有时间标签（分隔条不在视野内/被回收）时一律抑制——末尾气泡每 tick 都会被 OCR 重读，
                // 无桶可证「新」就放行会让同一条消息每个 tick 重复投递。
                // 2026-10-28 13:14:54 实测：[图片] 因 m.Time 为空、桶是继承来的（上一条气泡的分隔条）而放行，
                // 虽是真新图但机制上可把旧的末尾气泡误当重发放行，故收紧为只认气泡自带时间标签，
                // m.Time 为空时一律按重渲染抑制。代价：无分隔条/同桶内的快速重发仍会漏，
                // 此情形与重渲染在现有信号下不可区分，保守取舍（宁可漏，不可重投）。
                if (i != lastIdx || !hasOwnBucket || !_sigFloor.TryGetValue(sig, out var floor) || bucketTs <= floor)
                    continue;
                Log($"重发放行: {m.Sender}: {Clip(m.Text, 30)}（桶 {m.Time}）");
            }
            var nowUtc = DateTimeOffset.UtcNow;
            Mark(sig);
            _seenAt[sig] = nowUtc;
            // 2026-10-28：记录该签名上次处理的「时间语境」。有桶记桶（epoch 秒）；无桶记当下时刻——
            // 它比任何随后才滚回视野的旧分隔条都新，保证无桶场景下永不被「桶更新」误放行。
            _sigFloor[sig] = bucketTs > 0 ? bucketTs : nowUtc.ToUnixTimeSeconds();
            if (staleBucket && i < lastIdx && bucketTs <= _lastStoredTs)
            {
                backfilled++;
                if (backfilled <= 3) Log($"历史回填跳过: {m.Sender}: {Clip(m.Text, 30)}（桶 {m.Time}）");
                continue;
            }
            fresh.Add(m);
        }
        if (backfilled > 0) Log($"历史回填 {backfilled} 条（时间桶早于 20 分钟且不在末尾，只标记不投递）");

        if (!_primed)
        {
            // 会话刚切进来（或 header 抖了一下）。2026-10-27 修：以前这里无条件把第一条 fresh 当基线吞掉，
            // 用户来回点会话时就丢消息（18:38 一次 primed(1 baseline) 吞掉了群里正在聊的一条）。
            // 现在只有「整屏都是没见过的历史」（>=5 条）才当基线；一两条当新消息投递，旧的由时间闸过滤。
            if (msgs.Count == 0) return;
            _primed = true;
            var fromUnread = _unreadSwitch;
            _unreadSwitch = false;

            if (fresh.Count >= 5)
            {
                Log($"primed ({fresh.Count} baseline)");
                return;
            }
            if (fromUnread)
            {
                var lastIncoming = fresh.LastOrDefault(m => !m.IsSelf);
                if (lastIncoming is null) { Log("unread baseline: no incoming"); return; }
                Log($"unread baseline: {fresh.Count} msgs, handle last");
                fresh = new List<MessageItem> { lastIncoming };
            }
        }

        if (fresh.Count == 0) return;
        Log($"new messages: {fresh.Count}");

        var lineBefore = _jsonlLines;
        foreach (var m in fresh)
            AppendJsonl("messages.jsonl", new
            {
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                chat = _chat,
                m.Time,
                m.Sender,
                uid = m.IsSelf ? 1000000001L : UidOf(m.Sender),   // 2026-10-26：原文层逐条钉 uid，认人免查证（1000000001 为占位，改成自己的号）
                m.Text,
                m.IsSelf,
                pics = m.Element is null ? 0 : QqUi.PicCount(m.Element),   // 现场包算 ocr_n / 账本校验用
                pic_w = m.PicW,                                              // 气泡渲染宽高：readimg --w/--h 宽高闸
                pic_h = m.PicH,
                quote_from = m.QuoteFrom,                                   // 引用标识：被引作者（"这张图"指谁）
                quote_text = m.QuoteText,                                   // 引用标识：被引正文（空=引的是纯图消息）
            });

        var incoming = fresh.Where(m => !m.IsSelf).ToList();
        if (incoming.Count == 0) return;
        if (!_cfg.Daemon.ReplyEnabled)
        {
            Log("reply disabled by config");
            return;
        }
        // 2026-10-28 用户定案：去掉会话黑白名单——能不能收/回由 QQ 自身关系决定
        // （加群/退群、私人拉黑），Amy 不再维护 Daemon.Chats / DarkChats。

        // 现场包 + 大脑路由：NotifyOnly=人工观察台（DSH GUI 会话消费）；dsh=自动值班脑；embedded=旧回退
        if (_cfg.Daemon.NotifyOnly)
        {
            WritePack(incoming, lineBefore, "");
            return;
        }

        if (string.Equals(_amy.Mode, "dsh", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.Now < _laneCooldownUntil)
            {
                Log($"lane cooldown 剩余 {(_laneCooldownUntil - DateTime.Now).TotalSeconds:F0}s，{incoming.Count} 条消息只落档不处理");
                return;
            }
            var seq = WritePack(incoming, lineBefore, "");
            if (seq > 0) SpawnDshBrain(seq);
            return;
        }

        if (_amy.Enabled)
        {
            // 回退路径：旧快车道内嵌直调；需要工具时同样升级到 DSH 值班脑（不再 spawn ZCode）
            if (DateTime.Now < _laneCooldownUntil)
            {
                Log($"lane cooldown 剩余 {(_laneCooldownUntil - DateTime.Now).TotalSeconds:F0}s，{incoming.Count} 条消息只落档不处理");
                return;
            }
            if (_amy.FastLane)
            {
                var (handled, hint) = TryFastLane(msgs, incoming);
                if (!handled)
                {
                    var seq = WritePack(incoming, lineBefore, hint);
                    if (seq > 0) SpawnDshBrain(seq);
                }
            }
            else
            {
                var seq = WritePack(incoming, lineBefore, "");
                if (seq > 0) SpawnDshBrain(seq);
            }
            return;
        }

        var reply = GenerateReply(msgs, incoming);
        if (string.IsNullOrWhiteSpace(reply)) return;

        if (reply.Trim() is "[SILENT]" or "SILENT")
        {
            Log("[SILENT] 决定不说话");
            return;
        }

        var header = _ui.Header();
        if (header != _chat)
        {
            Log($"abort send: chat changed ('{_chat}' -> '{header}')");
            return;
        }

        _ui.Send(reply);
        Log($"sent: {reply.Replace("\n", " / ")}");

        var (kind, id) = ScopeOf();
        var last = incoming[^1];
        var payload = string.Join("\n", incoming.Select(m => $"{m.Sender}: {m.Text}"));
        var uid = UidOf(last.Sender);
        LogUidOnce(last.Sender, uid);
        _yuno.AfterChat(
            kind: kind,
            groupId: kind == "group" ? id : 0,
            userId: uid,
            sender: last.Sender,
            text: payload,
            msgTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            reply: reply);
    }

    private (string Kind, long Id) ScopeOf()
    {
        var kind = _ui.IsGroup() ? "group" : "c2c";
        if (_scopeMap.TryGetValue(_chat, out var info)) return (info.Kind, info.Id);
        return (kind, 0);
    }

    private string[] ScopesFor()
    {
        var (kind, id) = ScopeOf();
        if (id > 0)
            return kind == "group"
                ? new[] { $"group:{id}", $"group_all:{id}" }
                : new[] { $"c2c:{id}" };
        return new[] { $"ui:group:{_chat}", $"ui:c2c:{_chat}" };
    }

    /// <summary>history 消息 → (role, content) 对话序列，内嵌直调与快车道共用。</summary>
    private List<(string Role, string Content)> BuildConvo(IReadOnlyList<MessageItem> msgs)
    {
        var history = msgs.TakeLast(Math.Max(4, _cfg.Daemon.ContextMessages)).ToList();
        var convo = new List<(string Role, string Content)>();
        foreach (var m in history)
        {
            if (string.IsNullOrWhiteSpace(m.Text)) continue;
            if (m.IsSelf) convo.Add(("assistant", m.Text));
            else convo.Add(("user", $"{m.Sender}: {m.Text}"));
        }
        if (convo.Count == 0 || convo[^1].Role == "assistant")
            convo.Add(("user", "（现在没有新的对你说的话，你可以选择沉默。）"));
        return convo;
    }

    /// <summary>阴暗面常驻（2026-10-26 管理员定案：去掉独立文件与 DarkChats 白名单限制）：
    /// FastDark 开即全域启用。阴暗面人设段落已并入 persona.md（persona-dark.md 已删除），
    /// 模型从 persona.md 直接读到；_personaDark 仅在旧文件存在时兼容追加。</summary>
    private bool DarkActive() => _amy.FastDark;

    private string BuildSystemPrompt(string memory, string instructions, bool dark = false)
    {
        var sys = new StringBuilder();
        sys.AppendLine(_persona);
        sys.AppendLine();
        sys.AppendLine($"# 当前场景\n你正在 QQ 会话「{_chat}」里，你的昵称是 {_cfg.SelfName}。");
        if (!string.IsNullOrWhiteSpace(memory))
        {
            sys.AppendLine();
            sys.AppendLine("# 长期记忆上下文（Yuno）");
            sys.AppendLine(memory);
        }
        if (dark && _personaDark.Length > 0)
        {
            sys.AppendLine();
            sys.AppendLine("# 阴暗面人设（只在你决定用 [DARK] 反击时启用，平时你仍是上面的 Amy）");
            sys.AppendLine(_personaDark);
        }
        sys.AppendLine();
        sys.AppendLine(instructions);
        return sys.ToString();
    }

    private string GenerateReply(IReadOnlyList<MessageItem> msgs, List<MessageItem> incoming)
    {
        var uid = UidOf(incoming[^1].Sender);
        LogUidOnce(incoming[^1].Sender, uid);
        var mem = _yuno.Context(incoming[^1].Text, ScopesFor(), uid) ?? "";
        var sys = BuildSystemPrompt(mem, "只输出你要说出口的正文，不要加前缀、不要解释。若你决定此刻不说话，只输出 [SILENT]。");
        var convo = BuildConvo(msgs);

        if (_cfg.Daemon.DumpPrompt)
        {
            Log("===== SYSTEM =====\n" + sys);
            foreach (var (r, c) in convo) Log($"[{r}] {c}");
            Log("===== END =====");
        }

        return _brain.Chat(sys, convo);
    }

    /// <summary>快车道决策：同一模型一次调用完成「回复什么 + 要不要升级」。返回正文、"[SILENT] 理由"、"[ESCALATE] 原因"、
    /// "[DRAW] 英文提示词"或"[COVER] 歌名"（技能标记仅在对应开关打开时告知模型）。</summary>
    private string FastDecide(IReadOnlyList<MessageItem> msgs, List<MessageItem> incoming, long uid)
    {
        var mem = _yuno.Context(incoming[^1].Text, ScopesFor(), uid) ?? "";
        var skills = new StringBuilder();
        var n = 3;
        if (DarkActive())
        {
            skills.AppendLine($"{n}. [DARK] 后跟反击正文（按上方阴暗面人设的贴吧风格写，两三句内收尾）——先把这段情景读懂，只有当整段读下来确实是在点名针对你、挑事而不是打趣/吐槽时；没人针对你绝不用；");
            n++;
        }
        if (_amy.FastDraw)
        {
            skills.AppendLine($"{n}. [DRAW] 后跟英文 Stable Diffusion 提示词——对方想要你画图/生图/来张图时；");
            n++;
        }
        if (_amy.FastCover)
        {
            skills.AppendLine($"{n}. [COVER] 后跟歌名——对方想听你唱歌/翻唱某首歌时；");
            n++;
        }
        if (_amy.FastVoice)
        {
            skills.AppendLine($"{n}. [VOICE] 后跟要说的中文话（不要标记不要引号；写 ≤12 字的短句——组织内容时就按短句说，超长会被系统自动断句拆成多条语音）；对方让你发语音/说句话听听，或你判断这句话用语音说更有味道时；");
            n++;
        }
        var escNo = n.ToString();
        var sys = BuildSystemPrompt(mem,
            "你是 Amy 的快车道：能用纯文本或已接入的技能回应当前会话。\n" +
            "只输出下面几种之一，不要加前缀、不要解释：\n" +
            "1. 直接要说的正文（普通闲聊/接话，一两句即可，符合人设语气）；\n" +
            "2. [SILENT] 加一句话理由（不值得说话时）；\n" +
            (skills.Length > 0 ? skills.ToString() : "") +
            escNo + ". [ESCALATE] 加一句话原因——当且仅当对方要求已接入技能以外的、纯文本做不到的事：发图片素材/表情包/GIF/文件、读转发卡片内容、搜索外部资料、多步复杂任务、明确点名要你（Amy 本人）动手操作。\n" +
            "拿不准算 1；能用一句话接住的绝不升级；要图要歌直接用对应标记，别嘴上答应了却不干活。", DarkActive());
        var convo = BuildConvo(msgs);

        if (_cfg.Daemon.DumpPrompt)
        {
            Log("===== FAST SYSTEM =====\n" + sys);
            foreach (var (r, c) in convo) Log($"[{r}] {c}");
            Log("===== END =====");
        }

        return _brain.Chat(sys, convo);
    }

    /// <summary>快车道：daemon 进程内直调模型 → SendQuiet 静默发送 → after_chat 落原文，全程无 spawn 无前台操作。
    /// 返回 (handled=true 已处理完毕)；(false, 原因) = 模型判定 [ESCALATE] 或调用失败，应交回 headless Amy。</summary>
    private (bool Handled, string Hint) TryFastLane(IReadOnlyList<MessageItem> msgs, List<MessageItem> incoming)
    {
        var seq = ++_amySeq;
        var last = incoming[^1];
        var uid = UidOf(last.Sender);
        LogUidOnce(last.Sender, uid);
        var preview = last.Text.Length <= 40 ? last.Text : last.Text[..40] + "…";
        var trigger = preview.Replace("\n", " ");

        _amyBusy = true;
        _amyStarted = DateTime.Now;
        _amyInfo = $"fastlane chat='{_chat}' trigger='{trigger}'";
        WriteStatus();
        Live("fast", seq, $"START chat='{_chat}' trigger='{trigger}'");
        var sw = Stopwatch.StartNew();

        string decision;
        try { decision = FastDecide(msgs, incoming, uid); }
        catch (Exception ex)
        {
            // 模型链路挂了：降级升级给 headless Amy（另一家模型/带工具）；同时进入冷却，
            // 冷却期内后续消息只落档——链路故障时每条消息各 spawn 一个会话的堆积放大器就此切断
            var why = $"快车道模型调用失败: {ex.Message}";
            _laneCooldownUntil = DateTime.Now.AddMinutes(LaneCooldownMinutes);
            Live("fast", seq, $"ESCALATE: {why}（车道进入{LaneCooldownMinutes}分钟冷却）");
            Log($"fast#{seq} escalate -> amy: {why}；冷却 {LaneCooldownMinutes}m");
            _amyBusy = false;
            _amyDoneSignal = true;
            WriteStatus();
            return (false, why);
        }

        if (decision.StartsWith("[ESCALATE]", StringComparison.OrdinalIgnoreCase))
        {
            var why = decision["[ESCALATE]".Length..].Trim();
            if (why.Length == 0) why = "快车道判定需要工具";
            Live("fast", seq, $"DECIDE: ESCALATE {why}");
            Log($"fast#{seq} escalate -> amy: {why}");
            _amyBusy = false;
            _amyDoneSignal = true;
            WriteStatus();
            return (false, why);
        }

        // 空输出（实测 deepseek-flash 对纯"[图片]"类无文字素材会返回空）按沉默处理，绝不当正文发送
        if (string.IsNullOrWhiteSpace(decision) || decision.Trim() is "[SILENT]" or "SILENT"
            || decision.StartsWith("[SILENT]", StringComparison.OrdinalIgnoreCase))
        {
            Live("fast", seq, $"DECIDE: {decision.Trim()}");
            Log($"fast#{seq} [SILENT]");
            _amyBusy = false;
            _amyDoneSignal = true;
            WriteStatus();
            return (true, "");
        }

        // —— 阴暗面对线（[DARK] 后跟正文）：本质仍是纯文本直发，剥掉标记走下方正常发送/落库，直播与日志带 DARK 标记便于观察对线
        var dark = false;
        if (decision.StartsWith("[DARK]", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = decision["[DARK]".Length..].Trim();
            if (DarkActive())
            {
                dark = true;
                decision = stripped;
                if (decision.Length == 0)
                {
                    Live("fast", seq, "DECIDE: [DARK] 空正文 → 沉默");
                    Log($"fast#{seq} [DARK] empty -> silent");
                    _amyBusy = false;
                    _amyDoneSignal = true;
                    WriteStatus();
                    return (true, "");
                }
            }
            else if (stripped.Length > 0)
            {
                // 白名单外会话的指令从未告知 [DARK]，模型仍输出（如有人骗标记）→ 防御性剥掉按普通正文发
                decision = stripped;
                Log($"fast#{seq} [DARK] 出现在非阴暗面会话 '{_chat}'，已剥标记按正文处理");
            }
        }

        // —— 技能标记（绘图/翻唱）：DryRun 只记候选；正式态先发 ETA 告知，接口后台跑，产物回主线程发送
        string? skill = null;
        string arg = "";
        if (decision.StartsWith("[DRAW]", StringComparison.OrdinalIgnoreCase) && _amy.FastDraw)
        {
            skill = "draw";
            arg = decision["[DRAW]".Length..].Trim();
        }
        else if (decision.StartsWith("[COVER]", StringComparison.OrdinalIgnoreCase) && _amy.FastCover)
        {
            skill = "cover";
            arg = decision["[COVER]".Length..].Trim();
        }
        else if (decision.StartsWith("[VOICE]", StringComparison.OrdinalIgnoreCase) && _amy.FastVoice)
        {
            skill = "voice";
            arg = decision["[VOICE]".Length..].Trim().Trim('"');
        }
        if (skill is not null)
        {
            if (arg.Length == 0)
                arg = skill switch
                {
                    "draw" => "cute anime girl",
                    "voice" => "嘿嘿，测试一下语音",
                    _ => "未指定歌名",
                };
            if (_amy.DryRun)
            {
                try { File.AppendAllText(Path.Combine(_logDir, "amy-dryrun.txt"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] fast#{seq} chat='{_chat}'\n{decision}\n\n", Encoding.UTF8); } catch { }
                Live("fast", seq, $"DECIDE: {skill.ToUpper()} (dryrun) '{arg}'");
                _amyBusy = false;
                _amyDoneSignal = true;
                WriteStatus();
                return (true, "");
            }

            var skillHeader = _ui.Header();
            if (skillHeader != _chat)
            {
                Log($"fast#{seq} abort skill: chat changed ('{_chat}' -> '{skillHeader}')");
                Live("fast", seq, "END aborted（会话已切换）");
                _amyBusy = false;
                _amyDoneSignal = true;
                WriteStatus();
                return (true, "");
            }

            // 预告不进群（2026-10-25 用户定案）：只报控制台/直播，直接开跑。
            // 预计耗时：生图 ~144s/张、翻唱流水线 1-4 分钟、语音 10-20s。
            Live("fast", seq, $"DECIDE: {skill.ToUpper()} '{arg}'（无预告直跑）");

            var (kind, id) = ScopeOf();
            var chat = _chat;
            var sender = last.Sender;
            var payload = string.Join("\n", incoming.Select(m => $"{m.Sender}: {m.Text}"));
            _amyInfo = $"fast-{skill} '{arg}' chat='{chat}'";
            WriteStatus();
            Log($"fast#{seq} {skill} job start: '{arg}'");

            _ = Task.Run(() =>
            {
                string? product = null;
                string? err = null;
                try { product = skill switch { "draw" => DrawImage(arg), "voice" => GenVoice(arg), _ => RunCover(arg) }; }
                catch (Exception ex) { err = ex.Message; }
                var p = product;
                var e = err;
                _mainQueue.Enqueue(() => FinishSkillJob(seq, skill!, arg, p, e, chat, kind, id, uid, sender, payload));
            });
            // busy 保持到 FinishSkillJob——期间 daemon 不碰窗口，新消息留待结束合并处理
            return (true, "");
        }

        // —— 纯文本回复：DryRun 只落候选，不发送不写库
        if (_amy.DryRun)
        {
            try { File.AppendAllText(Path.Combine(_logDir, "amy-dryrun.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] fast#{seq} chat='{_chat}'{(dark ? " [DARK]" : "")}\n{decision}\n\n", Encoding.UTF8); } catch { }
            Live("fast", seq, $"DECIDE: {(dark ? "DARK " : "")}REPLY (dryrun) {decision.Replace("\n", " ")[..Math.Min(30, decision.Length)]}…");
            _amyBusy = false;
            _amyDoneSignal = true;
            WriteStatus();
            return (true, "");
        }

        var header = _ui.Header();
        if (header != _chat)
        {
            Log($"fast#{seq} abort send: chat changed ('{_chat}' -> '{header}')");
            Live("fast", seq, "END aborted（会话已切换）");
            _amyBusy = false;
            _amyDoneSignal = true;
            WriteStatus();
            return (true, "");
        }

        var brief = decision.Replace("\n", " ");
        brief = brief.Length <= 20 ? brief : brief[..20] + "…";
        try
        {
            _ui.SendQuiet(decision);
            Thread.Sleep(1200);
            var verified = _ui.Messages().Any(m => m.IsSelf && m.Text.Contains(decision.Length > 40 ? decision[..40] : decision));
            Live("fast", seq, $"{(dark ? "DARK " : "")}SEND: {(verified ? "ok" : "unverified")} 回复「{brief}」");
            Log($"fast#{seq} {(dark ? "[DARK] " : "")}sent: {decision.Replace("\n", " / ")}");

            var (kind, id) = ScopeOf();
            var payload = string.Join("\n", incoming.Select(m => $"{m.Sender}: {m.Text}"));
            _yuno.AfterChat(
                kind: kind,
                groupId: kind == "group" ? id : 0,
                userId: uid,
                sender: last.Sender,
                text: payload,
                msgTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                reply: decision);
            Live("fast", seq, $"AFTER_CHAT: ok 原文已落库");
        }
        catch (Exception ex)
        {
            Log($"fast#{seq} send/after_chat failed: {ex.Message}");
            Live("fast", seq, $"END error {ex.Message}");
        }

        Live("fast", seq, $"END exit=0 耗时{sw.Elapsed.TotalSeconds:F0}s");
        _lastResult = $"fast#{seq} 耗时 {sw.Elapsed.TotalSeconds:F0}s";
        _amyBusy = false;
        _amyDoneSignal = true;
        WriteStatus();
        return (true, "");
    }

    /// <summary>把值班实时状态写到 logs\amy-status.json（心跳+运行中会话+上轮结果），供 status 命令观察。</summary>
    private void WriteStatus()
    {
        try
        {
            object? running = _amyBusy ? new
            {
                seq = _amySeq,
                dry_run = _amy.DryRun,
                chat = _chat,
                info = _amyInfo,
                started = _amyStarted.ToString("HH:mm:ss"),
                elapsed_s = (int)(DateTime.Now - _amyStarted).TotalSeconds,
            } : null;
            object? last = string.IsNullOrEmpty(_lastResult) ? null : new { result = _lastResult };
            var payload = new
            {
                pid = Environment.ProcessId,
                heartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                phase = _amyBusy ? "running" : "idle",
                running,
                last,
            };
            File.WriteAllText(Path.Combine(_logDir, "amy-status.json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
            _statusLastWrite = DateTime.UtcNow;
        }
        catch { }
    }

    /// <summary>值班直播日志单行追加：daemon 窗口（StartLiveTail 转发）与 tail -f 共用的唯一实时事件流。</summary>
    private void Live(string actor, int seq, string line)
    {
        try { File.AppendAllText(Path.Combine(_logDir, "amy-live.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {actor}#{seq} {line}\n", Encoding.UTF8); }
        catch { }
    }

    /// <summary>写现场包 v2（atomic temp+Move）：NotifyOnly（人工观察台）与 dsh（自动值班脑）共用同一格式。
    /// 返回 seq；构建/写入失败返回 0（本轮不 spawn，消息已落原文层，下轮合并处理）。</summary>
    private int WritePack(List<MessageItem> incoming, long lineBefore, string escalateHint)
    {
        var seq = ++_amySeq;
        var (kind, id) = ScopeOf();
        JsonObject pack;
        try
        {
            pack = _pack.Build(_chat, kind, id, ScopesFor(), lineBefore, seq, _amy.DryRun, escalateHint);
        }
        catch (Exception ex)
        {
            Log($"pack#{seq} 构建失败，本轮不投递: {ex.Message}");
            Live("notify", seq, "PACK-FAIL " + ex.Message);
            return 0;
        }

        var pendingPath = Path.Combine(_logDir, "amy-pending.json");
        var tmp = pendingPath + ".tmp";
        var json = pack.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, pendingPath, true);
        }
        catch (Exception ex)
        {
            Log($"pack#{seq} 写 pending 失败，本轮放弃: {ex.Message}");
            Live("notify", seq, "PACK-FAIL " + ex.Message);
            return 0;
        }

        var last = incoming.Count > 0 ? incoming[^1] : null;
        var preview = last is null ? "(无新消息)" : last.Text.Length <= 40 ? last.Text : last.Text[..40] + "…";
        var bytes = Encoding.UTF8.GetByteCount(json);
        Log($"pack#{seq} 已投递: chat='{_chat}' {incoming.Count} 条 trigger='{preview.Replace("\n", " ")}' ({bytes}B, mode={_amy.Mode})");
        Live("notify", seq, $"NOTIFY: {incoming.Count} 条新消息已投递 chat='{_chat}' trigger='{preview.Replace("\n", " ")}'");
        _lastNotify = (seq, DateTime.Now);
        _notifyWarned = false;
        _lastResult = $"pack#{seq} 已投递 {incoming.Count} 条";
        _amyStarted = DateTime.Now;
        _amyInfo = $"pack chat='{_chat}' trigger='{preview.Replace("\n", " ")}'";
        WriteStatus();
        return seq;
    }

    /// <summary>spawn DSH headless 值班脑：一批消息一个会话，跑完即退。
    /// 只负责启动与观测——判断/取证/成文/发送/落库/ack 全由脑调 `duty` 完成。
    /// 锁/冷却/超时沿用旧 headless 语义（amy.lock 跨代际互斥，秒退进 5 分钟冷却）。</summary>
    private void SpawnDshBrain(int seq, bool retry = false)
    {
        if (LockAlive(out var lockPid))
        {
            Log($"dsh#{seq} spawn 拒绝：上一轮脑仍在运行（pid={lockPid}），本轮消息已落原文层");
            Live("amy", seq, "END skipped（上一轮脑还在跑，拒绝并行）");
            return;
        }

        var repo = ResolvePath(_amy.ProjectRoot);
        var task = DshBrain.TaskText(seq, _amy.DryRun, retry);
        var sticky = string.Equals(_amy.Dsh.SessionMode, "sticky", StringComparison.OrdinalIgnoreCase);
        var resumeId = sticky ? LoadSessionId() : null;

        Process? p;
        try
        {
            var psi = DshBrain.BuildStartInfo(_cfg, repo, task, resumeId);
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log($"dsh#{seq} spawn 失败: {ex.Message}");
            Live("amy", seq, "BRAIN-ERR " + DshBrain.Clip(ex.Message, 160));
            _laneCooldownUntil = DateTime.Now.AddMinutes(LaneCooldownMinutes);
            return;
        }
        if (p is null)
        {
            Log($"dsh#{seq} spawn 失败: Process.Start 返回 null");
            Live("amy", seq, "BRAIN-ERR start-null");
            return;
        }

        try { File.WriteAllText(_amyLockPath, $"{p.Id}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", Encoding.UTF8); } catch { }

        _amyBusy = true;
        _amyStarted = DateTime.Now;
        _amyInfo = $"dsh#{seq} chat='{_chat}'{(resumeId is null ? "" : " resume")}";
        WriteStatus();
        Live("amy", seq, $"START seq={seq} pid={p.Id} dry={_amy.DryRun}{(retry ? " retry" : "")}{(resumeId is null ? "" : " resume=" + resumeId)}");

        var sw = Stopwatch.StartNew();
        var sessionLog = Path.Combine(_logDir, "amy-session.log");
        _ = Task.Run(() =>
        {
            var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
            var errTask = Task.Run(() => p.StandardError.ReadToEnd());
            var timedOut = !p.WaitForExit(Math.Max(1, _amy.TimeoutMinutes) * 60_000);
            if (timedOut)
            {
                try { Process.Start("taskkill", $"/PID {p.Id} /T /F")?.WaitForExit(); } catch { }
                Log($"dsh#{seq} 超时（>{_amy.TimeoutMinutes}m），已杀进程树");
            }

            var stdout = "";
            var stderr = "";
            try { stdout = outTask.Result; } catch { }
            try { stderr = errTask.Result; } catch { }

            if (!timedOut)
            {
                Log($"dsh#{seq} exit={p.ExitCode} 耗时 {sw.Elapsed.TotalSeconds:F0}s" +
                    (stderr.Length > 0 ? " stderr=" + DshBrain.Clip(stderr.Replace("\n", " ⏎ "), 200) : ""));
                if (p.ExitCode != 0 && sw.Elapsed.TotalSeconds < 60)
                {
                    _laneCooldownUntil = DateTime.Now.AddMinutes(LaneCooldownMinutes);
                    Live("amy", seq, $"BRAIN-ERR exit={p.ExitCode} 秒退，车道冷却 {LaneCooldownMinutes}m");
                }
            }

            var sid = ParseSessionId(stdout);
            if (sid is not null)
            {
                try { File.WriteAllText(_amySessionIdPath, sid, Encoding.UTF8); } catch { }
                if (sid != resumeId) Log($"dsh#{seq} 会话 id: {sid}");
            }

            _lastResult = timedOut
                ? $"dsh#{seq} 超时被杀（>{_amy.TimeoutMinutes}m）"
                : $"dsh#{seq} exit={p.ExitCode} 耗时 {sw.Elapsed.TotalSeconds:F0}s";
            Live("amy", seq, $"END {(timedOut ? "timeout" : "exit=" + p.ExitCode)} 耗时{sw.Elapsed.TotalSeconds:F0}s");
            try
            {
                File.AppendAllText(sessionLog,
                    $"===== dsh#{seq} {DateTime.Now:yyyy-MM-dd HH:mm:ss} exit={(timedOut ? "timeout" : p.ExitCode)} dry_run={_amy.DryRun} =====\n" +
                    $"{stdout}\n[stderr]\n{stderr}\n\n", Encoding.UTF8);
            }
            catch { }

            // ack 守卫：脑正常退出却没写 ack = 本轮没被消费；重试一次（最多一次），再不行只告警
            var acked = ReadAckSeq() >= seq;
            if (!acked && !retry && !timedOut && p.ExitCode == 0)
            {
                Log($"dsh#{seq} 正常退出但未写 ack，重试一次");
                Live("amy", seq, "BRAIN-NOACK 未写 ack，重试一次");
                Task.Run(() =>
                {
                    Thread.Sleep(1500);
                    try { SpawnDshBrain(seq, retry: true); }
                    catch (Exception ex) { Log("dsh retry failed: " + ex.Message); }
                });
            }
            else if (!acked)
            {
                Log($"[WARN] dsh#{seq} 未写 ack（exit={(timedOut ? "timeout" : p.ExitCode.ToString())}, retry={retry}）：本轮未消费，消息已落原文层");
                Live("amy", seq, "BRAIN-NOACK 未消费（消息保留下轮合并）");
            }

            ReleaseLock();
            _amyBusy = false;
            _amyDoneSignal = true;
            WriteStatus();
        });
    }
    /// <summary>值班会话处理中（logs\amy-busy.flag 存在且 10 分钟内刷新过）。超过 10 分钟视作僵死忽略——
    /// 宁可 daemon 恢复检测也别被卡死的会话永远堵住；flag 由值班会话负责touch与删除。</summary>
    private bool SessionBusy()
    {
        try
        {
            if (!File.Exists(_busyFlagPath)) return false;
            return (DateTime.Now - File.GetLastWriteTime(_busyFlagPath)).TotalMinutes < 10;
        }
        catch { return false; }
    }

    /// <summary>值班会话的消费水位（logs\amy-ack.json 的 seq）；0=从未消费。</summary>
    private int ReadAckSeq()
    {
        try
        {
            var p = Path.Combine(_logDir, "amy-ack.json");
            if (!File.Exists(p)) return 0;
            using var d = JsonDocument.Parse(File.ReadAllText(p));
            return d.RootElement.TryGetProperty("seq", out var v) && v.TryGetInt32(out var s) ? s : 0;
        }
        catch { return 0; }
    }

    /// <summary>amy.lock 是否有活着的持有者（node PID 存活=锁有效；进程没了=锁自然失效）。</summary>
    private bool LockAlive(out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(_amyLockPath)) return false;
            if (!int.TryParse(File.ReadAllText(_amyLockPath).Split('\n')[0].Trim(), out pid) || pid <= 0) return false;
            var holder = Process.GetProcessById(pid); // 进程不存在会抛，视作锁失效
            return !holder.HasExited && holder.ProcessName == "node";
        }
        catch { return false; }
    }

    private void ReleaseLock()
    {
        try { File.Delete(_amyLockPath); } catch { }
    }

    /// <summary>读常驻值班会话 id；非法/缺失返回 null（spawn 走新建）。</summary>
    private string? LoadSessionId()
    {
        try
        {
            var s = File.Exists(_amySessionIdPath) ? File.ReadAllText(_amySessionIdPath).Trim() : "";
            return s.StartsWith("sess_") ? s : null;
        }
        catch { return null; }
    }

    /// <summary>从 DSH --json 的 NDJSON 事件流里解析会话 id（sticky 模式存档用）。</summary>
    /// <summary>从 DSH --json 的 NDJSON 事件流里解析会话 id（sticky 模式存档用）。
    /// 事件行可能混有普通日志，逐行找 sessionId / session_id。</summary>
    private static string? ParseSessionId(string stdout)
    {
        foreach (var raw in stdout.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                foreach (var name in new[] { "sessionId", "session_id" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var v) &&
                        v.GetString() is { Length: > 0 } id && id.StartsWith("session", StringComparison.Ordinal))
                        return id;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>后台线程 tail logs\amy-live.log：新行原样打到本进程 console，daemon 窗口即值班直播台
    /// （2026-10-25 定案：daemon 本体=可见 cmd 窗口，窗口活着=值班中，关窗即停）。
    /// LiveAppend 与 headless Amy 的 bash echo 都以整行+\n 追加，leftover 只防御极端交错。</summary>
    private void StartLiveTail()
    {
        var path = Path.Combine(_logDir, "amy-live.log");
        var t = new Thread(() =>
        {
            long pos = 0;
            try
            {
                if (File.Exists(path))
                {
                    // 回放尾部几行作上下文，再从当前末尾开始追新；FileShare.ReadWrite 与写入者共存
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(Math.Max(0, fs.Length - 8192), SeekOrigin.Begin);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    foreach (var l in sr.ReadToEnd().Replace("\r", "").Split('\n')
                                 .Where(s => s.Length > 0).TakeLast(6))
                        Console.WriteLine(l);
                    pos = fs.Length;
                }
            }
            catch { }

            var leftover = "";
            while (true)
            {
                Thread.Sleep(400);
                try
                {
                    if (!File.Exists(path)) continue;
                    var len = new FileInfo(path).Length;
                    if (len <= pos) continue;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        leftover += sr.ReadToEnd();
                    }
                    pos = len;
                    var parts = leftover.Split('\n');
                    leftover = parts[^1].Length > 65536 ? "" : parts[^1];
                    foreach (var l in parts[..^1])
                    {
                        var s = l.TrimEnd('\r');
                        if (s.Length > 0) Console.WriteLine(s);
                    }
                }
                catch { }
            }
        })
        { IsBackground = true, Name = "amy-live-tail" };
        t.Start();
    }

    /// <summary>长任务收尾（主线程）：产物发送（异常重试一次）+ after_chat 落原文 + 直播收束；失败只报控制台不进群（2026-10-25 定案）。</summary>
    private void FinishSkillJob(int seq, string skill, string arg, string? product, string? err,
        string chat, string kind, long id, long uid, string sender, string payload)
    {
        var sw = Stopwatch.StartNew();
        string reply;
        try
        {
            var header = _ui.Header();
            if (header != chat && !_ui.OpenChat(chat))
            {
                Log($"fast#{seq} abort finish: 无法回到会话 '{chat}'（当前 '{header}'），产物未发送");
                Live("fast", seq, $"END aborted（无法回到会话 '{chat}'）");
                _amyBusy = false;
                _amyDoneSignal = true;
                WriteStatus();
                return;
            }

            if (product is null)
            {
                // 失败不进群（2026-10-25 用户定案）：只报控制台/直播，不发兜底文案。
                reply = "";
                Live("fast", seq, $"FAIL: {skill} 失败（只报控制台，不发群）: {err}");
                Log($"fast#{seq} {skill} failed: {err}");
                _lastResult = $"fast#{seq} {skill} 失败";
            }
            else
            {
                var (sent, verified) = SendProduct(seq, product, skill);
                if (!sent)
                {
                    reply = "";
                    Live("fast", seq, $"FAIL: 产物发送失败（只报控制台，不发群）: {product}");
                    Log($"fast#{seq} send asset failed twice: {product}");
                    _lastResult = $"fast#{seq} {skill} 产物发送失败";
                }
                else
                {
                    reply = skill switch
                    {
                        "draw" => $"[AI绘图: {arg}]",
                        "voice" => $"[语音: {(arg.Length <= 20 ? arg : arg[..20] + "…")}]",
                        _ => $"[翻唱「{arg}」: {Path.GetFileName(product)}]",
                    };
                    var label = product.Contains('|')
                        ? $"{product.Split('|').Length}段语音"
                        : Path.GetFileName(product);
                    Live("fast", seq, $"SEND: {(verified ? "ok" : "unverified(属预期)")} {label}");
                    Log($"fast#{seq} asset sent ({(verified ? "verified" : "unverified")}): {product}");
                    _lastResult = $"fast#{seq} {skill} 完成 耗时{sw.Elapsed.TotalSeconds:F0}s";
                }
            }

            // 成败都落原文：失败时群里没发任何消息，reply 留空（不发兜底文本，2026-10-25 定案）
            try
            {
                _yuno.AfterChat(
                    kind: kind,
                    groupId: kind == "group" ? id : 0,
                    userId: uid,
                    sender: sender,
                    text: payload,
                    msgTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    reply: reply);
            }
            catch (Exception ex) { Log($"fast#{seq} after_chat failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Log($"fast#{seq} finish failed: {ex.Message}");
            Live("fast", seq, $"END error {ex.Message}");
        }
        Live("fast", seq, $"END {(product is null ? "error" : "exit=0")} 耗时{sw.Elapsed.TotalSeconds:F0}s");
        _amyBusy = false;
        _amyDoneSignal = true;
        WriteStatus();
    }

    /// <summary>技能产物发送总入口（2026-10-25）：[VOICE]/[COVER] 且 VoiceAsPtt=true 时优先语音条
    /// （立体声混音回环实录），失败回落文件卡片；其余（图）走原路径。
    /// voice 多段（GenVoice 断句产物，'|' 拼接）逐段独立发送。</summary>
    private (bool Sent, bool Verified) SendProduct(int seq, string product, string skill)
    {
        if (skill == "voice" && product.Contains('|'))
        {
            var anySent = false;
            var allVerified = true;
            foreach (var seg in product.Split('|'))
            {
                var r = SendProduct(seq, seg, skill);
                anySent |= r.Sent;
                allVerified &= r.Verified;
            }
            Log($"fast#{seq} voice sent {product.Split('|').Length} segments");
            return (anySent, allVerified);
        }
        if (_amy.VoiceAsPtt && (skill == "voice" || skill == "cover"))
        {
            var v = TrySendVoice(product, skill);
            if (v.Sent) return v;
            Log($"fast#{seq} voice-as-ptt 失败，回落文件发送: {product}");
        }
        return TrySendAsset(product, skill == "draw");
    }

    /// <summary>语音条发送（一次尝试）：归一化转码 → 确保录音端点就绪 → 实录发送；
    /// 翻唱超上限且 CoverVoicePreview=true 时先发上限长度语音预览、紧跟完整文件。
    /// 内部异常返回 (false,false) 由调用方回落，不抛出。</summary>
    private (bool Sent, bool Verified) TrySendVoice(string path, string skill)
    {
        try
        {
            Console.WriteLine(AudioIo.EnsureCaptureReady(_amy.VoiceCaptureMatch));
            var tmp = AudioIo.NormalizeToWav(path, _amy.VoiceVolume);
            try
            {
                var dur = AudioIo.WavDuration(tmp);
                var cap = _amy.VoiceMaxSeconds;
                if (dur > cap + 1 && skill == "cover" && !_amy.CoverVoicePreview)
                {
                    Log($"cover {dur:F0}s 超语音上限且未开预览，走文件: {path}");
                    return (false, false);
                }
                var before = _ui.Messages().Count(m => m.IsSelf);
                _ui.SendVoiceFile(tmp, _amy.VoiceRenderMatch, cap);
                var previewOnly = dur > cap + 1;
                if (previewOnly)
                {
                    Thread.Sleep(1000);
                    _ui.SendFile(path);   // 语音只是前奏预览，完整版仍以文件送达
                }
                Thread.Sleep(1500);
                var ok = previewOnly
                    ? _ui.Messages().Any(m => m.IsSelf && m.Text.Contains(Path.GetFileName(path)))
                    : _ui.Messages().Count(m => m.IsSelf) > before;
                return (true, ok);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch (Exception ex)
        {
            Log($"voice ptt attempt failed: {ex.Message}");
            return (false, false);
        }
    }

    /// <summary>进程内发送产物文件；抛异常（找不到按钮/对话框未出现等）重试一次。verified=false 是 UIA 回读限制（属预期，实际已发出）。</summary>
    private (bool Sent, bool Verified) TrySendAsset(string path, bool isImage)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (isImage)
                {
                    var before = _ui.Messages().Count(m => m.IsSelf);
                    _ui.SendImageFile(path);
                    Thread.Sleep(2000);
                    var ok = _ui.Messages().Count(m => m.IsSelf) > before;
                    return (true, ok);
                }
                _ui.SendFile(path);
                Thread.Sleep(1500);
                var hit = _ui.Messages().Any(m => m.IsSelf && m.Text.Contains(Path.GetFileName(path)));
                return (true, hit);
            }
            catch (Exception ex)
            {
                Log($"send asset attempt {attempt} failed: {ex.Message}");
            }
        }
        return (false, false);
    }

    /// <summary>SD webui txt2img（anything-v5 基参 512×768×22 步，fp32≈144s），产物落 Sound\fast-draw\；服务未启动直接抛错（不自动拉起）。</summary>
    private string DrawImage(string prompt)
    {
        var baseU = _amy.SdBaseUrl.TrimEnd('/');
        try
        {
            using var ping = HttpPing.GetAsync(baseU + "/sdapi/v1/options").Result;
            if (!ping.IsSuccessStatusCode)
                throw new InvalidOperationException($"SD 服务响应 {(int)ping.StatusCode}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SD 绘图服务未启动（{_amy.SdBaseUrl} 无响应），喊管理员开一下喵", ex);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        var payload = new
        {
            prompt,
            negative_prompt = "lowres, bad anatomy, bad hands, text, error, missing fingers, extra digits, worst quality, low quality, jpeg artifacts, watermark",
            steps = 22,
            width = 512,
            height = 768,
            cfg_scale = 7,
        };
        using var resp = http.PostAsJsonAsync(baseU + "/sdapi/v1/txt2img", payload).Result;
        var body = resp.Content.ReadAsStringAsync().Result;
        if (!resp.IsSuccessStatusCode)
            // SD API 错误响应的真实原因在尾部 errors 字段，头部截断会切掉
            throw new InvalidOperationException($"生图失败 {(int)resp.StatusCode}: {(body.Length > 300 ? body[^300..] : body)}");
        var b64 = JsonNode.Parse(body)?["images"]?[0]?.GetValue<string>()
            ?? throw new InvalidOperationException("生图响应无 images");

        var dir = Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "..", "Sound", "fast-draw"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"fast-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(b64));
        return path;
    }

    /// <summary>amy_cover.py --search 关键词搜索翻唱（默认 --mix 混伴奏版优先发）；解析 stdout 尾部 JSON summary 取产物路径。</summary>
    private string RunCover(string song)
    {
        var script = Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "..", "bot-workspace", "amy_cover.py"));
        if (!File.Exists(script))
            throw new InvalidOperationException($"找不到翻唱脚本 {script}");

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(song);
        psi.ArgumentList.Add("--search");
        psi.ArgumentList.Add("--mix");
        // 35s 默认是 CPU 时代遗留；SVC 已 GPU 化（ZLUDA 16x），90s 段几分钟内可出
        psi.ArgumentList.Add("--dur");
        psi.ArgumentList.Add("90");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("python 启动失败");
        var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
        var errTask = Task.Run(() => p.StandardError.ReadToEnd());
        if (!p.WaitForExit(15 * 60_000))
        {
            try { Process.Start("taskkill", $"/PID {p.Id} /T /F")?.WaitForExit(); } catch { }
            throw new InvalidOperationException("翻唱超时（>15 分钟）已终止");
        }

        var stdout = outTask.Result;
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var t = line.Trim();
            if (!t.StartsWith('{')) continue;
            try
            {
                using var j = JsonDocument.Parse(t);
                if (!j.RootElement.TryGetProperty("products", out var prods)) continue;
                string? Pick(string name) => prods.TryGetProperty(name, out var v) ? v.GetString() : null;
                var file = Pick("mixed") ?? Pick("vocal_only");
                if (file is not null && File.Exists(file)) return file;
            }
            catch { }
        }
        // 完整输出落盘留证；摘要取尾部（traceback 的异常行在末尾），头部 200 字符只会截到 HF Warning/进度条噪音
        var failLog = Path.Combine(_logDir, $"cover-fail-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try { File.WriteAllText(failLog, $"exit={p.ExitCode}\n---stdout---\n{stdout}\n---stderr---\n{errTask.Result}"); } catch { }
        var tail = (p.ExitCode != 0 && errTask.Result.Length > 0 ? errTask.Result : stdout).Trim();
        if (tail.Length > 200) tail = tail[^200..];
        throw new InvalidOperationException($"翻唱失败(exit={p.ExitCode}): {tail}（完整输出 {failLog}）");
    }

    /// <summary>gen_voice.py 语音合成（ChatTTS + ZLUDA GPU，cand_01 青春女声，秒级/条）。
    /// 2026-10-25 听感定案：短句（≤12 字）渲染质量好，长句必拆——文本按句读标点切段、
    /// 无标点长段按字数硬切，每段独立生成一个 wav，多段以 '|' 拼接返回（SendProduct 逐段发送）。
    /// 最多 6 段；超时 2 分钟兜杀（按段计，每段独立计时）。</summary>
    private string GenVoice(string text)
    {
        var parts = SplitVoiceText(text);
        if (parts.Count == 0) throw new InvalidOperationException("语音文本为空");
        var proj = Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "..", "bot-workspace", "chattts-restore"));
        var py = Path.Combine(proj, ".venv", "Scripts", "python.exe");
        var script = Path.Combine(proj, "gen_voice.py");
        if (!File.Exists(py) || !File.Exists(script))
            throw new InvalidOperationException($"语音环境不完整: {proj}");

        var dir = Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "..", "Sound", "fast-voice"));
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var paths = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var path = Path.Combine(dir, $"voice-{stamp}-{i + 1}.wav");
            RunVoiceGen(py, script, proj, parts[i], path, $"{i + 1}/{parts.Count}");
            paths.Add(path);
        }
        return string.Join("|", paths);
    }

    /// <summary>语音文本拆段：按句读标点切，段内仍超 12 字按字数硬切；上限 6 段（约 72 字，超出丢弃）。</summary>
    private static List<string> SplitVoiceText(string text)
    {
        const int maxLen = 12, maxParts = 6;
        var parts = new List<string>();
        foreach (var raw in text.Split(new[] { '，', '。', '！', '？', '；', '、', '~', ',', '.', '!', '?', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Trim();
            if (seg.Length == 0) continue;
            for (var i = 0; i < seg.Length; i += maxLen)
            {
                if (parts.Count >= maxParts) return parts;
                parts.Add(seg.Substring(i, Math.Min(maxLen, seg.Length - i)));
            }
        }
        return parts;
    }

    /// <summary>跑一次 gen_voice.py（单段）。失败写 voice-fail 日志并抛出。</summary>
    private void RunVoiceGen(string py, string script, string proj, string seg, string path, string tag)
    {
        var psi = new ProcessStartInfo
        {
            FileName = py,
            WorkingDirectory = proj,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(seg);
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("python 启动失败");
        var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
        var errTask = Task.Run(() => p.StandardError.ReadToEnd());
        if (!p.WaitForExit(120_000))
        {
            try { Process.Start("taskkill", $"/PID {p.Id} /T /F")?.WaitForExit(); } catch { }
            throw new InvalidOperationException($"语音生成超时（>2 分钟）已终止 [{tag}]");
        }
        if (!File.Exists(path) || new FileInfo(path).Length < 10_000)
        {
            var failLog = Path.Combine(_logDir, $"voice-fail-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            try { File.WriteAllText(failLog, $"exit={p.ExitCode} seg='{seg}'\n---stdout---\n{outTask.Result}\n---stderr---\n{errTask.Result}"); } catch { }
            var tail = (p.ExitCode != 0 && errTask.Result.Length > 0 ? errTask.Result : outTask.Result).Trim();
            if (tail.Length > 200) tail = tail[^200..];
            throw new InvalidOperationException($"语音生成失败 [{tag}](exit={p.ExitCode}): {tail}（完整输出 {failLog}）");
        }
        Log($"voice gen ok [{tag}] '{seg}' -> {Path.GetFileName(path)}");
    }

    private string BuildAmyPrompt(string pendingPath) =>
        "值班调起：QQ 会话有新消息，你是被哨兵(qq-agent daemon)拉起的 Amy 本人。" +
        $"先读 {pendingPath}（触发消息、上下文、scopes、dry_run 标志、群号；若有 escalate_hint 说明快车道处理不了才升级到你，原因见该字段），再按 AGENTS.md §4 值班流程处理：" +
        "0) 全程直播：下面每完成一步，立刻用 Bash 追加一行到 qq-agent\\logs\\amy-live.log（单行格式 `[HH:mm:ss] amy#<seq> <STEP>: <一句话摘要>`，seq 取自 pending 文件，STEP ∈ CONTEXT/DECIDE/SEND/AFTER_CHAT/ADD_FACT；DECIDE 行必须写 REPLY 或 [SILENT] 加一句话理由），写完这行再进行下一步——这是管理员实时观察你的唯一窗口，漏写视同事故；" +
        "1) POST /amy/context 注入记忆（Bearer token 在 qq-agent\\config.json 的 Yuno.Token，scopes 用文件里给的）；" +
        "2) 按 persona.md 人设判断要不要回复——不值得回就直接结束（DECIDE 行写 [SILENT] 与理由）" +
        (DarkActive() ? "；只有在整段情景读下来确实是在点名针对你、挑事而不是打趣/吐槽时，才改按 persona.md 的阴暗面段落反击（两三句内收尾）" : "") + "；" +
        "3) 要回复就用 qq-agent.exe sendq \"<文本>\" --chat \"<会话名>\" 发送（静默发送、发完自动沉底，无感断言 exit=3 仅代表未回读验证不算失败；exe 路径见 AGENTS.md §6，会话名用文件里的 chat；SEND 行写 exit 码与回复前 20 字）；" +
        "4) 发送成功才 POST /amy/after_chat 落原文（参数从 pending 文件取，reply=你实际发出的文本）；" +
        "5) 按 §4 自行判断本轮是否值得 /amy/add_fact，不确定就不存（ADD_FACT 行写存了几条或 skip 理由）。" +
        (_amy.DryRun
            ? " 本次 dry_run=true：禁止发送、禁止 after_chat/add_fact，直播只写 CONTEXT 与 DECIDE 两行，再把候选回复（或 [SILENT] 与一句话理由）写入 qq-agent\\logs\\amy-dryrun.txt 后结束。"
            : "");

    /// <summary>2026-10-27：重启后把原文层最近 400 条灌进去重窗口。
    /// 否则"窗口滚动/重渲染把旧消息重新加载"会被当成新消息再投一遍（现场包出现幽灵消息）。</summary>
    private void SeedSeen(string jsonlPath)
    {
        try
        {
            if (!File.Exists(jsonlPath)) return;
            var lines = File.ReadAllLines(jsonlPath);
            var skip = Math.Max(0, lines.Length - 400);
            var now = DateTimeOffset.UtcNow;
            var n = 0;
            for (var i = skip; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var chat = doc.RootElement.TryGetProperty("chat", out var c) ? c.GetString() : null;
                    var sender = doc.RootElement.TryGetProperty("Sender", out var s) ? s.GetString() : null;
                    var text = doc.RootElement.TryGetProperty("Text", out var t) ? t.GetString() : null;
                    var ts = doc.RootElement.TryGetProperty("ts", out var tv) && tv.TryGetInt64(out var lineTs) ? lineTs : 0;
                    if (string.IsNullOrEmpty(chat) || string.IsNullOrEmpty(text)) continue;
                    var seedSig = $"{chat}|{sender}|{SigText(text)}";
                    _seenAt[seedSig] = now;   // 2026-10-27：与运行时签名完全一致（空白折叠 + 图片/转发只认类型头）
                    // 2026-10-28：种子必须同步记 _sigFloor＝原文层落库时刻（秒精度），否则启动后末尾历史气泡
                    // 会因「桶(分钟精度) > 缺省 0」被当成真重发补投，破坏启动防补投。落库时刻 ≥ 该气泡自身
                    // 桶时间，重渲染桶不比它新 → 抑制；之后的真重发桶必然更晚 → 仍能放行。无 ts 老行给
                    // long.MaxValue：时间不可考，永不放行（保守）。
                    _sigFloor[seedSig] = ts > 0 ? ts : long.MaxValue;
                    n++;
                }
                catch { }
            }
            Log($"去重窗口种子: {n} 条（原文层最近 {lines.Length - skip} 行）");
        }
        catch (Exception ex)
        {
            Log("seed seen failed: " + ex.Message);
        }
    }

    /// <summary>时间桶是否过期：分隔条上的 HH:mm 比现在早 20 分钟以上（跨天/未来时间不倒扣）。</summary>
    private static bool LooksStale(string label)
    {
        if (!TimeSpan.TryParseExact(label.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var t))
            return false;
        var now = DateTime.Now;
        var delta = now - (now.Date + t);
        if (delta < TimeSpan.Zero) delta += TimeSpan.FromDays(1);   // 跨零点
        if (delta > TimeSpan.FromHours(12)) return false;            // 看着像"未来"的时间，别拦
        return delta > TimeSpan.FromMinutes(20);
    }

    /// <summary>去重签名：空白折叠后再拼，避免同一气泡首帧/补全两种渲染各占一个签名。
    /// 图片与转发卡片只认「类型头」——OCR 回写会把 `[图片]` 改成 `[图片|…498 字]`，
    /// 转发卡片加载中/加载完文字也不同，签名跟着变就会把同一气泡当新消息再投一次（2026-10-27 实测）。
    /// 2026-10-28 阶段2：文件同理——文件名是晚到的 UIA 文本，首帧可能只有 [文件]，只认类型头防同气泡双投；
    /// 代价：同人同桶连发两个不同文件、或用户手打 "[文件" 开头的真文本，会共用签名（与 [图片] 同款取舍）。</summary>
    private string SigOf(MessageItem m) => $"{_chat}|{m.Sender}|{SigText(m.Text)}";

    private static string SigText(string? text)
    {
        var t = CollapseWs(text);
        if (t.StartsWith("[图片", StringComparison.Ordinal)) return "[图片]";
        if (t.StartsWith("[文件", StringComparison.Ordinal)) return "[文件]";
        if (t.StartsWith("[聊天记录", StringComparison.Ordinal))
        {
            var end = t.IndexOf(']');
            if (end > 0) return t[..(end + 1)];
        }
        return t;
    }

    /// <summary>时间桶 → epoch 秒（按今天算；标签比现在「晚」超过 12 小时就按昨天，处理跨零点）。</summary>
    private static long BucketTs(string label)
    {
        if (!TimeSpan.TryParseExact(label.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var t))
            return 0;
        var now = DateTime.Now;
        var stamp = now.Date + t;
        if (stamp - now > TimeSpan.FromHours(12)) stamp = stamp.AddDays(-1);
        return new DateTimeOffset(stamp).ToUnixTimeSeconds();
    }

    private static string CollapseWs(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string Clip(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Replace('\n', ' ');
        return t.Length <= n ? t : t[..n] + "…";
    }

    private bool SeenRecently(string sig)
        => _seenAt.TryGetValue(sig, out var at) && (DateTimeOffset.UtcNow - at) < TimeSpan.FromHours(6);

    private void Mark(string sig)
    {
        if (!_seen.Add(sig)) return;
        _seenOrder.Enqueue(sig);
        while (_seenOrder.Count > 3000)
        {
            var old = _seenOrder.Dequeue();
            _seen.Remove(old);
            _seenAt.Remove(old);
            _sigFloor.Remove(old);   // 2026-10-28：与 _seenAt 同生命周期淘汰，防字典无界增长
        }
    }

    private void AppendJsonl(string name, object obj)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj);
            File.AppendAllText(Path.Combine(_logDir, name), json + Environment.NewLine, Encoding.UTF8);
            if (name == "messages.jsonl")
            {
                _jsonlLines++;
                // 记住最后一行的 ts：时间闸靠它区分"补出来的新消息"和"重渲染的旧消息"
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ts", out var t)) _lastStoredTs = t.GetInt64();
                }
                catch { }
            }
        }
        catch { }
    }

    private void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {msg}";
        Console.WriteLine(line);
        try
        {
            File.AppendAllText(Path.Combine(_logDir, "agent.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// 致命异常落盘（2026-10-29 新增）。
    /// 事故背景：UIA 在切会话/发文件时抛 COM 灾难性故障（0x8000FFFF），异常从主循环冒到 Program.Main
    /// 的 catch，进程直接没了；agent.log 里没有半行记录，只能靠"心跳停了"猜。
    /// 这里把异常类型、HResult 和完整堆栈写进 daemon-crash.log，重启后的排查有据可查。
    /// </summary>
    private void CrashLog(string where, Exception ex)
    {
        try
        {
            var path = Path.Combine(_logDir, "daemon-crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid={Environment.ProcessId} where={where} type={ex.GetType().FullName} hr=0x{ex.HResult:X8}");
            sb.AppendLine("thread=" + Environment.CurrentManagedThreadId + "  chat='" + _chat + "'");
            sb.AppendLine(ex.ToString());
            sb.AppendLine(new string('-', 80));
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static uint GetPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }
}








