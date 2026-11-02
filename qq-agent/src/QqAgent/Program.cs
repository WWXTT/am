using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QqAgent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // 2026-10-27：进程启动即声明 DPI 感知。否则从 cmd 拉起的 daemon 是 DPI-unaware，
        // GetWindowRect 拿到的是被系统虚拟化过的坐标（实测差 0.75 倍），win-lock 比对面永远不一致 → 每 12s 抖一次。
        Native.SetProcessDPIAware();
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";
        var rest = args.Skip(1).ToArray();

        // 2026-10-29：进程级兜底。之前 daemon 静默消失（UIA 抛 0x8000FFFF 冒到 Main 的 catch），
        // 控制台一闪而过、agent.log 里没有任何痕迹，只能靠"心跳停了"反推。现在一律落 logs\daemon-crash.log。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashDump("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashDump("UnobservedTask", e.Exception); e.SetObserved(); };

        try
        {
            return cmd switch
            {
                "probe" => Probe(),
                "watch" => Watch(rest),
                "send" => Send(rest),
                "sendimg" => SendImg(rest),
                "sendfile" => SendFileCli(rest),
                "filedlg" => FileDlgTest(rest),
                "sendvoice" => SendVoice(rest),
                "playwav" => PlayWav(rest),
                "spacetest" => SpaceTest(rest),
                "sendat" => SendAt(rest),
                "recall" => Recall(rest),
                "mic" => Mic(rest),
                "findchat" => FindChat(rest),
                "clickat" => ClickAt(rest),
                "hoverat" => HoverAt(rest),
                "rclickat" => RClickAt(rest),
                "wheel" => Wheel(rest),
                "movehwnd" => MoveHwnd(rest),
                "placewin" => PlaceWin(rest),
                "ocrfwd" => OcrFwd(rest),
                "ocrtest" => OcrTest(rest),
                "clickbtn" => ClickBtn(rest),
                "snap" => SnapCmd(rest),
                "see" => See(rest),
                "askimg" => AskImg(rest),
                "inspect" => Inspect(),
                "dump" => Dump(rest),
                "readfwd" => ReadFwd(rest),
                "readimg" => ReadImg(rest),
                "openimg" => OpenImg(rest),
                "closefwd" => CloseFwd(),
                "fwdprops" => FwdProps(),
                "layout" => Layout(rest),
                "vd" => VdCmd(rest),
                "sendq" => SendQuietCmd(rest),
                "sendemoji" => SendEmojiCmd(rest),
                "inputprobe" => InputProbe(),
                "selftest" => SelfTest(rest),
                "status" => StatusCmd(rest),
                "daemon" => Daemon(rest),
                "crashtest" => CrashTest(),
                "duty" => Duty.Run(rest),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            CrashDump("Main/" + cmd, ex);
            return 1;
        }
    }

    /// <summary>2026-10-29：验证崩溃现场落盘链路（故意抛异常）。它不是业务命令，只是自检用。</summary>
    private static int CrashTest()
        => throw new InvalidOperationException("crashtest：故意抛出的异常，用来验证 logs\\daemon-crash.log 是否写入");

    /// <summary>daemon 崩溃现场落盘目录：优先 cwd\logs（daemon.bat 会 cd 到 qq-agent），其次 exe 同目录的 logs。</summary>
    private static string ResolveLogDir()
    {
        try
        {
            var p = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            if (Directory.Exists(p)) return p;
        }
        catch { }
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "logs");
            if (Directory.Exists(p)) return p;
        }
        catch { }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>2026-10-29：把致命异常写成一份可读现场（类型/消息/完整堆栈），追加不覆盖。</summary>
    private static void CrashDump(string where, Exception? ex)
    {
        try
        {
            var path = Path.Combine(ResolveLogDir(), "daemon-crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid={Environment.ProcessId} where={where}");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine(new string('-', 80));
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            qq-agent <command>
              probe                    读取会话/聊天列表/消息（只读）
              readfwd [n] [--snap-img] [--chat 会话]
                                        读取第 n 条（1=最新）转发消息全文（点开→读→关）；
                                        --snap-img 附查看器整窗截图（转发图不落磁盘缓存，识图吃截图）
              readimg --ts <epoch秒> [--before 120] [--after 15] [--all] [--out 目录]
                                        按消息时间窗从 QQNT 图片缓存取原图（nt_data\Pic，魔数验格式，
                                        复制到 logs\img\，绝不动源文件）；UIA 的 pic-element 无文件属性，
                                        磁盘缓存是识图唯一通路
              openimg [n] [--chat 会话] [--keep]
                                        Invoke 打开消息区倒数第 n 张（1=最新）图片的查看器，截大图后关窗；
                                        纯净目标（整窗 snap 会混入会话列表预览缩略图等干扰源），
                                        且直发图也可能网络直渲不落盘——查看器截图是统一兜底
                                        （2026-10-26 起 OCR 链不用此路：右键「打开文件夹」开的是 Explorer）
              ocrfwd [N=40] [--pic n] [--line N --chat X [--img K]]
                                        图片批量文字识别（QQ 内置「提取文字」，零幻觉）。链路自含：
                                        自动归位 work 档 → 无查看器时双击开大图——直发图双击主窗
                                        倒数第 n 张（--pic，默认 1=最新）再「左翻」往前(更早)遍历；
                                        转发图包双击转发查看器首图再「右翻」往后(图包顺序)遍历
                                        （两通路方向相反，起点决定方向）→循环{提取文字→翻页}
                                        逐图输出 JSON lines；已有大图窗直接续跑（断点续遍历）
                                        加 --line N（messages.jsonl 行号）→ 识别完自动写
                                        ocr-ledger.jsonl 并把该行 Text 回写成 [图片|原文]；
                                        --chat X 校验会话，--img K 指定首图图序（默认 1）
              dump [--all]             转储 UIA 树（调试用；--all 含全部 QQ 顶层窗口）
              watch [seconds]          监听 QQ 窗口事件
              send <会话名> <文本>      切换会话并发送，随后校验
              sendimg <会话名> <图片路径>  切换会话并发送图片（剪贴板粘贴）
              sendvoice <会话名> <音频路径> [--vol 0.8] [--cap 58]
                                        切换会话并把音频以 QQ 语音条实录发送（立体声混音回环）
              sendat <成员名> <文本...> [--chat 会话]  @某人发文本（群聊）
              recall [--chat 会话] [--ts 片段]  撤销自己最后一条（或含片段的最新一条）消息
              mic list|on|off            录音端点管理：on=启用立体声混音并设默认（语音条前置）
              snap [--all] [--out 目录] [--config p]  截 QQ 窗口存 PNG（PrintWindow，可截 UIA 读不到的弹层）
              see "<目标>" [--snap 图] [--click] [--config p]  视觉定位目标坐标；--click 直接点击
              askimg --path <png> [--q "问题"] [--config p]  识图：要一段中文描述（只在 OCR 无文字时用，每轮 ≤1 次）
              selftest [--config p]    Yuno + 模型连通性自检
              status [--watch]         值班实时状态：daemon 心跳/当前会话/CLI 转录/日志尾（--watch 每 3s 刷新）
              daemon [--chat 名称] [--config p] [--dump-prompt]
              duty <子命令>            值班脑工具面（doctor/pack/say/chat/skip/ack/context/meme/state/roster/stats/prune）
                                       `duty help` 看全部；值班脑一律用 amy.cmd 调用
            """);
        return 2;
    }

    private static string ArgValue(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback;
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name);

    /// <summary>去掉 "--config &lt;path&gt;"：amy.cmd 每次都会追加，若被拼进正文/路径会发出去
    /// （2026-10-27 已踩：sendq 发出去的消息尾巴带上了 qq-agent\config.json）。</summary>
    private static string[] WithoutConfig(string[] args)
    {
        var list = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config") { i++; continue; }
            list.Add(args[i]);
        }
        return list.ToArray();
    }

    /// <summary>值班实时状态一览：daemon 心跳 / 运行中 amy 会话 / headless CLI 转录尾部 / agent.log 尾部。</summary>
    private static int StatusCmd(string[] args)
    {
        var watchMode = HasFlag(args, "--watch");
        do
        {
            if (watchMode) Console.Clear();
            PrintStatus();
            if (watchMode) Thread.Sleep(3000);
        } while (watchMode);
        return 0;
    }

    private static void PrintStatus()
    {
        var statusPath = Path.Combine("logs", "amy-status.json");
        if (!File.Exists(statusPath))
        {
            Console.WriteLine("找不到 logs\\amy-status.json：daemon 未启动过（注意须在 qq-agent\\ 目录下运行）");
            return;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(statusPath)); }
        catch (Exception ex) { Console.WriteLine("状态文件解析失败: " + ex.Message); return; }
        using var root = doc;

        Console.WriteLine("=== daemon ===");
        var pid = root.RootElement.TryGetProperty("pid", out var pe) && pe.TryGetInt32(out var pv) ? pv : 0;
        var procAlive = false;
        if (pid > 0) { try { Process.GetProcessById(pid); procAlive = true; } catch { } }
        var heartbeat = root.RootElement.TryGetProperty("heartbeat", out var hb) && hb.TryGetInt64(out var hbv)
            ? hbv : 0;
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - heartbeat;
        Console.WriteLine($"pid={pid} 进程{(procAlive ? "存活" : "不存在!")}  心跳 {age}s 前");

        var phase = root.RootElement.TryGetProperty("phase", out var ph) ? ph.GetString() : "idle";
        DateTime? started = null;
        if (phase == "running" && root.RootElement.TryGetProperty("running", out var run)
            && run.ValueKind == JsonValueKind.Object)
        {
            var info = run.TryGetProperty("info", out var i) ? i.GetString() : "";
            var elapsed = run.TryGetProperty("elapsed_s", out var e) && e.TryGetInt32(out var ev) ? ev : 0;
            if (run.TryGetProperty("started", out var st) && DateTime.TryParse(st.GetString(), out var sv))
                started = sv;
            Console.WriteLine($"值班脑进行中 [{info}]  已运行 {elapsed}s");
        }
        else if (root.RootElement.TryGetProperty("last", out var lastEl) && lastEl.ValueKind == JsonValueKind.Object
                 && lastEl.TryGetProperty("result", out var res) && !string.IsNullOrEmpty(res.GetString()))
        {
            Console.WriteLine($"值班脑: 空闲（上一轮 {res.GetString()}）");
        }
        else
        {
            Console.WriteLine("值班脑: 空闲（尚无记录）");
        }

        Console.WriteLine();
        Console.WriteLine("=== 值班脑转录（logs/amy-session.log）===");
        if (started is null)
        {
            Console.WriteLine("（空闲，无进行中会话）");
        }
        else
        {
            var lines = TailTranscript(started.Value);
            if (lines.Count == 0) Console.WriteLine("（会话刚拉起，暂无事件——DSH 事件通常几秒后开始落盘）");
            foreach (var l in lines) Console.WriteLine(l);
        }

        Console.WriteLine();
        Console.WriteLine("=== agent.log 尾部 ===");
        var logPath = Path.Combine("logs", "agent.log");
        if (File.Exists(logPath))
            foreach (var l in File.ReadLines(logPath).TakeLast(8)) Console.WriteLine(l);

        Console.WriteLine();
        Console.WriteLine("=== 上一轮归档（amy-session.log 末块）===");
        var sessPath = Path.Combine("logs", "amy-session.log");
        if (File.Exists(sessPath))
        {
            var text = File.ReadAllText(sessPath, Encoding.UTF8);
            var idx = text.LastIndexOf("=====", StringComparison.Ordinal);
            if (idx >= 0)
                foreach (var l in text[idx..].Split('\n').Take(10)) Console.WriteLine(l.TrimEnd('\r'));
        }
    }

    /// <summary>从值班脑会话归档（logs/amy-session.log）末块提取最近一轮的 DSH 事件行（NDJSON）。</summary>
    private static List<string> TailTranscript(DateTime startedAfter)
    {
        var result = new List<string>();
        try
        {
            var logFile = Path.Combine("logs", "amy-session.log");
            if (!File.Exists(logFile)) return result;
            var text = File.ReadAllText(logFile, Encoding.UTF8);
            var idx = text.LastIndexOf("=====", StringComparison.Ordinal);
            if (idx < 0) return result;

            foreach (var raw in text[idx..].Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("=====", StringComparison.Ordinal) ||
                    line.StartsWith("[stderr]", StringComparison.Ordinal)) continue;
                if (line.StartsWith('{'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        var type = r.TryGetProperty("type", out var te) ? te.GetString() : "";
                        switch (type)
                        {
                            case "tool_call":
                                result.Add("→ " + (r.TryGetProperty("tool", out var tn) ? tn.GetString() : "?"));
                                break;
                            case "tool_result":
                                result.Add("✓ " + (r.TryGetProperty("status", out var st) ? st.GetString() : ""));
                                break;
                            case "text":
                                var txt = r.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                                if (!string.IsNullOrWhiteSpace(txt))
                                    result.Add("◇ " + (txt!.Length > 80 ? txt[..80] + "…" : txt));
                                break;
                            case "error":
                                result.Add("× " + (r.TryGetProperty("message", out var me) ? me.GetString() : ""));
                                break;
                        }
                        continue;
                    }
                    catch { }
                }
                result.Add(line);
            }
        }
        catch { }
        return result.TakeLast(10).ToList();
    }
    private static string ConfigPath(string[] args)
    {
        var p = ArgValue(args, "--config", "config.json");
        return Path.IsPathRooted(p) ? p : Path.GetFullPath(p);
    }

    private static int Probe()
    {
        var ui = QqUi.Attach();
        Console.WriteLine($"window hwnd={ui.Hwnd}  header='{ui.Header()}'");
        Console.WriteLine("--- 会话列表 ---");
        foreach (var c in ui.Chats())
        {
            var mark = c.Selected ? "*" : " ";
            var unread = c.Unread ? " [未读]" : "";
            Console.WriteLine($"{mark} {c.Title,-24} {c.Time,-8} {c.Preview}{unread}");
        }
        Console.WriteLine("--- 当前会话消息（末 8 条）---");
        foreach (var m in ui.Messages().TakeLast(8))
        {
            var who = m.IsSelf ? "我" : (m.Sender.Length > 0 ? m.Sender : "?");
            var q = (m.QuoteFrom.Length > 0 || m.QuoteText.Length > 0)
                ? $"  ←引用[{m.QuoteFrom}: {m.QuoteText}]"
                : "";
            Console.WriteLine($"[{m.Time}] {who}: {m.Text.Replace("\n", " / ")}{q}");
        }
        return 0;
    }

    private static int Watch(string[] args)
    {
        int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;
        var ui = QqUi.Attach();
        using var spy = new WinEventSpy();
        spy.Start((int)PidOf(ui.Hwnd));
        Console.WriteLine($"watching hwnd={ui.Hwnd} for {seconds}s ...");

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var last = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            Native.DrainMessages();
            Thread.Sleep(50);
            if ((DateTime.UtcNow - last).TotalMilliseconds < 1000) continue;
            var n = spy.Drain(out var byType);
            if (n > 0)
            {
                var parts = byType.OrderByDescending(kv => kv.Value).Select(kv => $"0x{kv.Key:X4}x{kv.Value}");
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} events={n} {string.Join(' ', parts)}");
            }
            last = DateTime.UtcNow;
        }
        Console.WriteLine($"total events={spy.Count}");
        return 0;
    }

    private static int Send(string[] args)
    {
        args = WithoutConfig(args);
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: send <会话名> <文本>");
            return 2;
        }
        var chat = args[0];
        var text = string.Join(' ', args.Skip(1));

        var ui = QqUi.Attach();
        ui.EnsureForeground();
        if (!ui.OpenChat(chat))
        {
            Console.Error.WriteLine($"切换会话失败：'{chat}'，当前='{ui.Header()}'");
            return 1;
        }
        if (ui.Header() != chat)
        {
            Console.Error.WriteLine($"会话标题不符：期望='{chat}' 实际='{ui.Header()}'，中止");
            return 1;
        }

        ui.Send(text);
        Thread.Sleep(1200);
        var hit = ui.Messages().Any(m => m.IsSelf && m.Text.Contains(text));
        Console.WriteLine(hit ? $"sent ok -> '{text}'" : $"sent unverified -> '{text}'");
        return hit ? 0 : 3;
    }

    private static int SendImg(string[] args)
    {
        args = WithoutConfig(args);
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: sendimg <会话名> <图片路径>");
            return 2;
        }
        var chat = args[0];
        var path = Path.GetFullPath(string.Join(' ', args.Skip(1)));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"图片不存在: {path}");
            return 2;
        }

        var ui = QqUi.Attach();
        ui.EnsureForeground();
        if (!ui.OpenChat(chat))
        {
            Console.Error.WriteLine($"切换会话失败：'{chat}'，当前='{ui.Header()}'");
            return 1;
        }
        if (ui.Header() != chat)
        {
            Console.Error.WriteLine($"会话标题不符：期望='{chat}' 实际='{ui.Header()}'，中止");
            return 1;
        }

        int before = ui.Messages().Count(m => m.IsSelf);
        ui.ClearInput();
        ui.SendImageFile(path);
        Thread.Sleep(2000);
        foreach (var m in ui.Messages().TakeLast(3))
            Console.WriteLine($"[{m.Time}] {(m.IsSelf ? "我" : (m.Sender.Length > 0 ? m.Sender : "?"))}: {m.Text.Replace("\n", " / ")}");
        var ok = ui.Messages().Count(m => m.IsSelf) > before;
        Console.WriteLine(ok ? $"sent image ok -> '{path}'" : $"sent image unverified -> '{path}'");
        return ok ? 0 : 3;
    }

    private static int SendFileCli(string[] args)
    {
        args = WithoutConfig(args);
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: sendfile <会话名> <文件路径>");
            return 2;
        }
        var chat = args[0];
        var path = Path.GetFullPath(string.Join(' ', args.Skip(1)));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"文件不存在: {path}");
            return 2;
        }

        var ui = QqUi.Attach();
        ui.EnsureForeground();
        if (!ui.OpenChat(chat))
        {
            Console.Error.WriteLine($"切换会话失败：'{chat}'，当前='{ui.Header()}'");
            return 1;
        }
        if (ui.Header() != chat)
        {
            Console.Error.WriteLine($"会话标题不符：期望='{chat}' 实际='{ui.Header()}'，中止");
            return 1;
        }

        var name = Path.GetFileName(path);
        var before = ui.Messages().Count(m => m.IsSelf);   // 文件消息 UIA Text 为空，按 self 条数增量判定
        ui.SendFile(path);
        Thread.Sleep(1500);
        foreach (var m in ui.Messages().TakeLast(3))
            Console.WriteLine($"[{m.Time}] {(m.IsSelf ? "我" : (m.Sender.Length > 0 ? m.Sender : "?"))}: {m.Text.Replace("\n", " / ")}");
        var ok = ui.Messages().Count(m => m.IsSelf) > before;
        Console.WriteLine(ok ? $"sent file ok -> '{path}'" : $"sent file unverified -> '{path}'");
        return ok ? 0 : 3;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    /// <summary>2026-10-28 阶段3自测：不带参数只报进程 DPI 感知（验证 app.manifest 是否生效），零 QQ 交互；
    /// `filedlg open` 走到「对话框打开又取消」为止——点「文件」→ 双路探测老/新对话框 → WM_CLOSE/「取消」
    /// 关掉。绝不粘贴路径、绝不回车、绝不点「发送(1)」，不会发出任何东西。</summary>
    private static int FileDlgTest(string[] args)
    {
        var aware = -1;
        try { aware = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()); }
        catch (EntryPointNotFoundException) { Console.WriteLine("[filedlg] DPI 感知 API 不可用（旧系统？）"); }
        var label = aware switch
        {
            0 => "Unaware（manifest 未生效！）",
            1 => "SystemAware",
            2 => "PerMonitor/Aware（app.manifest 生效）",
            _ => $"invalid({aware})",
        };
        Console.WriteLine($"[filedlg] 进程 DPI 感知: {label}");
        if (args.Length == 0 || !args[0].Equals("open", StringComparison.OrdinalIgnoreCase))
            return aware == 2 ? 0 : 1;

        var ui = QqUi.Attach();
        ui.EnsureForeground();
        if (!ui.ClickButton("文件"))
        {
            Console.WriteLine("[filedlg] FAIL: 点不到「文件」按钮");
            return 1;
        }
        var (legacy, dlg) = ui.WaitForFileDialog(1500, 10000);
        if (legacy is null && dlg == IntPtr.Zero)
        {
            Console.WriteLine("[filedlg] FAIL: 老路径与新路径都没等到文件对话框");
            return 1;
        }
        if (legacy is not null)
        {
            Console.WriteLine("[filedlg] 检出老对话框（UIA「文件名」框）→ sendfile 会走老路径；尝试点「取消」关闭");
            ui.ClickButton("取消");
            return 0;
        }

        Native.GetWindowRect(dlg, out var r);
        Console.WriteLine($"[filedlg] 检出新对话框 hwnd={dlg} rect=({r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}) → sendfile 会走新兜底；WM_CLOSE 取消");
        Native.PostMessage(dlg, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(400);
            if (!Native.IsWindowVisible(dlg))
            {
                Console.WriteLine("[filedlg] 对话框已取消关闭，测试结束（未粘贴未发送）");
                return 0;
            }
        }
        Console.WriteLine("[filedlg] WARN: WM_CLOSE 后对话框仍在，请手动关闭");
        return 1;
    }

    private static int SelfTest(string[] args)
    {
        var cfg = AppConfig.Load(ConfigPath(args));
        var yuno = new YunoClient(cfg.Yuno);
        Console.WriteLine($"yuno health: {yuno.Healthy()}");

        var ctx = yuno.Context("你记得我喜欢什么吗", new[] { "ui:c2c:selftest" }, 0);
        Console.WriteLine($"yuno context: {(string.IsNullOrEmpty(ctx) ? "(empty)" : ctx.Length + " chars")}");

        var brain = new Brain(cfg.Model);
        var reply = brain.Chat("你是一个只回答一个词的助手。", new[] { ("user", "回复：可用") });
        Console.WriteLine($"model reply: {reply}");

        var v = cfg.Vision;
        if (!v.Enabled || string.IsNullOrWhiteSpace(v.ApiKey))
        {
            Console.WriteLine($"vision: disabled (Enabled={v.Enabled}, key={(string.IsNullOrWhiteSpace(v.ApiKey) ? "未配置" : "已配置")})");
        }
        else
        {
            // 白底图中央画红块让模型定位，bbox 中心应接近 (500,500)——连通性与坐标解析的最小可证伪测试
            using var bmp = new System.Drawing.Bitmap(100, 100);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                g.FillRectangle(System.Drawing.Brushes.Red, 35, 35, 30, 30);
            }
            var tmp = Path.Combine(Path.GetTempPath(), "qq-agent-selftest-vision.png");
            bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            try
            {
                var box = new VisionClient(v).Locate(tmp, "red square");
                Console.WriteLine(box is null
                    ? "vision: 调用成功但未解析到 bbox"
                    : $"vision: bbox(0-1000)=[{string.Join(',', box)}]（期望中心约 500,500）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("vision: " + ex.Message);
            }
        }
        return 0;
    }

    private static int Inspect()
    {
        var ui = QqUi.Attach();
        Console.WriteLine($"header='{ui.Header()}'");
        foreach (var (hwnd, _, _) in QqUi.Windows())
        {
            System.Windows.Automation.AutomationElement rootEl;
            try { rootEl = System.Windows.Automation.AutomationElement.FromHandle(hwnd); }
            catch { continue; }
            foreach (System.Windows.Automation.AutomationElement el in rootEl.FindAll(
                System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                string cls, name, type;
                try
                {
                    cls = el.Current.ClassName;
                    name = el.Current.Name ?? "";
                    type = el.Current.ControlType.ToString();
                }
                catch { continue; }
                if (name.Contains("发送") || name.Contains("文件名") || name.Contains("malfoid")
                    || name.Contains("取消") || name.Contains("留言"))
                {
                    var r = el.Current.BoundingRectangle;
                    Console.WriteLine($"hwnd={hwnd} {type,-10} cls='{cls}' name='{name}' rect=({(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height})");
                }
            }
        }
        return 0;
    }

    /// <summary>转储 UIA 树（默认主窗口；--all 含弹层/查看器等独立窗口）。</summary>
    private static int Dump(string[] args)
    {
        var all = HasFlag(args, "--all");
        int depthLimit = int.TryParse(ArgValue(args, "--depth", "40"), out var d) ? d : 40;

        var ui = QqUi.Attach();
        var targets = QqUi.Windows();
        if (!all) targets = targets.Where(t => t.Hwnd == ui.Hwnd).ToList();

        foreach (var (hwnd, rect, title) in targets)
        {
            System.Windows.Automation.AutomationElement rootEl;
            try { rootEl = System.Windows.Automation.AutomationElement.FromHandle(hwnd); }
            catch { continue; }
            Console.WriteLine($"=== hwnd={hwnd} title='{title}' rect=({rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top})");
            DumpEl(rootEl, 0, depthLimit);
        }
        return 0;
    }

    private static void DumpEl(System.Windows.Automation.AutomationElement el, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        foreach (System.Windows.Automation.AutomationElement child in el.FindAll(
            System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
        {
            string cls, name, type;
            System.Windows.Rect r;
            try
            {
                cls = child.Current.ClassName ?? "";
                name = child.Current.Name ?? "";
                type = child.Current.ControlType?.ToString() ?? "?";
                r = child.Current.BoundingRectangle;
            }
            catch { continue; }
            var nm = name.Replace("\r", " ").Replace("\n", "↵");
            if (nm.Length > 90) nm = nm[..90] + "…";
            Console.WriteLine($"{new string(' ', depth * 2)}{type,-12} cls='{cls}' name='{nm}' rect=({(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height})");
            DumpEl(child, depth + 1, maxDepth);
        }
    }

    /// <summary>读取转发消息全文：Invoke 开第 n 条（1=最新）卡片 → 读查看器 → 关窗。
    /// 全程静默（不动光标、不抢前台），输出前后光标/前台断言。</summary>
    /// <summary>读取转发消息全文（两进程接力）：进程A（本进程）Invoke 开查看器后退出；
    /// 静置后自启的进程B 全新连接首查全文并关窗。活 UIA 连接会抑制 Chromium 发布全量
    /// 文本，所以读取必须由新进程做（2026-10-25 定案）。</summary>
    private static int ReadFwd(string[] args)
    {
        // ---- 阶段 2：被进程A spawn 出来的新进程 ----
        var stage2Hwnd = ArgValue(args, "--stage2", "");
        if (stage2Hwnd.Length > 0)
        {
            if (!HasFlag(args, "--nowait"))
            {
                Console.WriteLine("[fwd2] 静置 90s（零连接，等 Chromium 发布全量文本）...");
                Thread.Sleep(90_000);
            }
            var hwnd = new IntPtr(long.Parse(stage2Hwnd));
            var title = ArgValue(args, "--title", "");
            int expected = int.TryParse(ArgValue(args, "--expected", "0"), out var ev) ? ev : 0;
            var fwd = Fwd.Read(hwnd, title, expected, HasFlag(args, "--keep"));

            var total2 = fwd.ExpectedCount > 0 ? $"{fwd.ExpectedCount}" : "?";
            Console.WriteLine($"=== 转发消息 '{fwd.Title}'（读得 {fwd.Messages.Count}/{total2} 条）===");
            foreach (var m in fwd.Messages)
            {
                var who = m.IsSelf ? "我" : (m.Sender.Length > 0 ? m.Sender : "?");
                Console.WriteLine($"[{m.Time}] {who}: {m.Text.Replace("\n", " / ")}");
            }
            if (!HasFlag(args, "--keep")) RestoreUi();   // 2026-10-27：读完收窗 + 主窗回档1
            return fwd.Messages.Count > 0 ? 0 : 1;
        }

        // ---- 阶段 1：开窗即退 ----
        // n = 首个位置参数整数；必须跳过带值 flag 的值（曾把 flag 的数值参数当成 n 越界）
        int n = 1;
        var valueFlags = new HashSet<string> { "--chat", "--stage2", "--title", "--expected" };
        for (int i = 0; i < args.Length; i++)
        {
            if (valueFlags.Contains(args[i])) { i++; continue; }
            if (int.TryParse(args[i], out var v)) { n = v; break; }
        }
        var keep = HasFlag(args, "--keep");
        var chatArg = ArgValue(args, "--chat", "");
        // --snap-img：转发图不落 nt_data 磁盘缓存（实证：查看器前台可见渲染 8s 仍零落盘，
        // 网络流直渲），改为对查看器窗口整体截图（PrintWindow 全内容），识图吃截图。
        // --keep 优先，二者同给时忽略截图。
        var snapImg = HasFlag(args, "--snap-img") && !keep;

        Native.GetCursorPos(out var c0);
        var fg0 = Native.GetForegroundWindow();

        var ui = QqUi.Attach();
        if (chatArg.Length > 0 && ui.Header() != chatArg)
        {
            if (!ui.OpenChat(chatArg))
            {
                Console.Error.WriteLine($"切换会话失败: '{chatArg}'，当前='{ui.Header()}'");
                return 1;
            }
        }
        var cards = ui.ForwardCards();
        Console.WriteLine($"可见转发卡片 {cards.Count} 张，读取倒数第 {n} 张");
        foreach (var c in cards)
            Console.WriteLine($"  card: '{c.Title}' 共{(c.Count > 0 ? c.Count.ToString() : "?")}条 y={c.Y:F0}");

        var (hwnd2, title2, expected2) = Fwd.Open(ui, n);
        // 2026-10-27：转发查看器也摆到 Amy 右半区（弹窗不占用户主屏）
        if (hwnd2 != IntPtr.Zero)
        {
            // 2026-10-28 owner：查看器摆左半屏，右半屏留给 QQ，不然一展开就被 QQ 压住
            var (fx, fy, fw, fh) = AmyLeftHalf(ui.Hwnd);
            QqUi.Place(hwnd2, fx, fy, fw, fh);
            Thread.Sleep(250);
        }

        Native.GetCursorPos(out var c1);
        var fg1 = Native.GetForegroundWindow();
        Console.WriteLine($"无感断言: cursor ({c0.X},{c0.Y})->({c1.X},{c1.Y})  fg {fg0}->{fg1}");

        if (keep)
        {
            Console.WriteLine($"[fwd1] keep-open: hwnd={hwnd2}（外部静置后可用 --stage2 读取）");
            return 0;
        }

        // 立即读取并关窗（正文一直在树里；旧版 90s 静置/两进程接力是为解析 bug 打的掩护，
        // 已随 ControlType 过滤修复一并废弃——2026-10-25）。
        // --snap-img 时传 keepOpen=true：读完后查看器还开着（沉底），截完图手动关
        var result = Fwd.Read(hwnd2, title2, expected2, snapImg);

        var total = result.ExpectedCount > 0 ? $"{result.ExpectedCount}" : "?";
        Console.WriteLine($"=== 转发消息 '{result.Title}'（读得 {result.Messages.Count}/{total} 条）===");
        foreach (var m in result.Messages)
        {
            var who = m.IsSelf ? "我" : (m.Sender.Length > 0 ? m.Sender : "?");
            Console.WriteLine($"[{m.Time}] {who}: {m.Text.Replace("\n", " / ")}");
        }

        string? snapPath = null;
        if (snapImg)
        {
            var vr = QqUi.Windows().FirstOrDefault(w => w.Hwnd == hwnd2);
            var shot = vr.Rect.Right > vr.Rect.Left
                ? Snap.Capture(hwnd2, vr.Rect, title2, ImgCache.DefaultOutDir())
                : null;
            Native.PostMessage(hwnd2, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(300);
            snapPath = shot?.Path;
            Console.WriteLine(snapPath is not null
                ? $"[fwd2] 查看器截图 -> {snapPath}（转发图不落盘，识图吃此截图）"
                : "[fwd2] 查看器截图失败（黑图/窗口不可用）");
            if (snapPath is not null)
                Console.WriteLine(new JsonObject
                {
                    ["images"] = new JsonArray(new JsonObject
                    {
                        ["path"] = snapPath,
                        ["format"] = "png",
                        ["source"] = "viewer-snap",
                        ["note"] = "转发图不落盘，此为查看器整窗截图（多图合一）",
                    }),
                }.ToJsonString());
        }
        RestoreUi();   // 2026-10-27：读转发同样收尾（关查看器 + 主窗回档1）
        return result.Messages.Count > 0 || snapPath is not null ? 0 : 1;
    }


    /// <summary>识图（2026-10-27）：askimg --path <png> [--q "问题"] [--config p]
    /// 把图片发给视觉模型要一段中文描述。**只在 OCR 无文字、又必须理解画面时用一次**；
    /// 取图推荐 openimg 先落 PNG，或直接用 logs\img\ 里现成的截图。</summary>
    private static int AskImg(string[] args)
    {
        var path = ArgValue(args, "--path", "");
        if (path.Length == 0)
        {
            Console.Error.WriteLine("用法: askimg --path <png> [--q \"想问什么\"] [--config p]");
            return 2;
        }
        if (!File.Exists(path)) { Console.Error.WriteLine($"图片不存在: {path}"); return 2; }
        var cfg = AppConfig.Load(ArgValue(args, "--config", "config.json"));
        var vision = new VisionClient(cfg.Vision);
        try
        {
            var answer = vision.Describe(path, ArgValue(args, "--q", ""));
            Console.WriteLine(new JsonObject
            {
                ["path"] = path,
                ["model"] = cfg.Vision.Model,
                ["text"] = answer,
            }.ToJsonString());
            return string.IsNullOrWhiteSpace(answer) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[askimg] " + ex.Message);
            return 1;
        }
    }
    /// <summary>识图链路（2026-10-25）：按消息 ts 在 QQNT 图片缓存时间窗匹配取原图。
    /// 消息 ts 与文件 mtime 实测亚秒级对齐（最松样本 Δ−68s，默认窗口 [−120s,+15s] 覆盖）；
    /// Ori 缺席回落 Thumb；扩展名会说谎一律魔数验格式；只复制不动源。单行 JSON 输出。</summary>
    private static int ReadImg(string[] args)
    {
        if (!long.TryParse(ArgValue(args, "--ts", ""), out var ts))
        {
            Console.Error.WriteLine("用法: readimg --ts <epoch秒> [--before 120] [--after 15] [--all] [--out 目录]");
            return 2;
        }
        int before = int.TryParse(ArgValue(args, "--before", "120"), out var b) ? b : 120;
        int after = int.TryParse(ArgValue(args, "--after", "15"), out var a) ? a : 15;
        var outDir = ArgValue(args, "--out", "");

        var roots = ImgCache.Roots();
        if (roots.Count == 0)
        {
            Console.Error.WriteLine("找不到 Pic 缓存根: Documents\\Tencent Files\\<账号>\\nt_qq\\nt_data\\Pic");
            return 3;
        }
        foreach (var r in roots) Console.Error.WriteLine($"pic root: {r}");

        var imgs = ImgCache.FindAndExport(ts, before, after,
            outDir.Length > 0 ? outDir : null, HasFlag(args, "--all") ? 8 : 1);

        // 2026-10-27：像素/宽高闸——消息气泡里的图有渲染尺寸，按比例过滤掉「隔壁那条」的缓存图。
        // 事故：哈马斯那条带图消息没落盘，readimg 按时间最近抓到了上一条 Hoshi 的竖图（猫），整句解读跑偏。
        int ew = int.TryParse(ArgValue(args, "--w", "0"), out var w0) ? w0 : 0;
        int eh = int.TryParse(ArgValue(args, "--h", "0"), out var h0) ? h0 : 0;
        var rejected = new List<ImgCache.Img>();
        if (ew > 0 && eh > 0)
        {
            var want = (double)ew / eh;
            var kept = new List<ImgCache.Img>();
            foreach (var i in imgs)
            {
                if (i.Width <= 0 || i.Height <= 0) { kept.Add(i); continue; }   // 读不出尺寸就不拦
                var got = (double)i.Width / i.Height;
                // 气泡是按等比缩放的：允许 12% 误差（含描边/圆角带来的取整）
                if (Math.Abs(got - want) / want <= 0.12) kept.Add(i); else rejected.Add(i);
            }
            imgs = kept;
        }

        foreach (var i in imgs)
            Console.WriteLine($"img[{i.Source}] Δ{i.DeltaMs}ms {i.Width}x{i.Height} {i.Format} {i.Bytes}B -> {i.OutPath}");
        foreach (var i in rejected)
            Console.Error.WriteLine($"[readimg] 宽高不符已丢弃 {i.Width}x{i.Height} (期望比例 {ew}x{eh})");
        Console.WriteLine(new JsonObject
        {
            ["found"] = imgs.Count > 0,
            ["count"] = imgs.Count,
            ["best"] = imgs.Count > 0 ? ImgJson(imgs[0]) : null,
            ["candidates"] = new JsonArray(imgs.Select(i => (JsonNode)ImgJson(i)).ToArray()),
            ["rejected_aspect"] = new JsonArray(rejected.Select(i => (JsonNode)ImgJson(i)).ToArray()),
        }.ToJsonString());
        return imgs.Count > 0 ? 0 : 2;
    }

    private static JsonObject ImgJson(ImgCache.Img i) => new()
    {
        ["path"] = i.OutPath,
        ["src"] = i.SrcPath,
        ["format"] = i.Format,
        ["source"] = i.Source,
        ["stem"] = i.Stem,
        ["bytes"] = i.Bytes,
        ["mtime"] = i.Mtime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        ["delta_ms"] = i.DeltaMs,
        ["w"] = i.Width,
        ["h"] = i.Height,
    };

    /// <summary>Invoke 打开消息区倒数第 n 张图的查看器，沉底后 PrintWindow 截大图再关窗（2026-10-26）。
    /// 动机：整窗 snap 混入会话列表预览缩略图等干扰目标（识图读到与消息无关的内容）；
    /// 且直发图也可能网络直渲不落盘，readimg 会 miss——查看器大图是纯净统一兜底。
    /// 查看器窗口无稳定标题（转发查看器标题=卡片标题，图片查看器不是），按 Invoke 前后差分识别新窗。</summary>
    private static int OpenImg(string[] args)
    {
        int n = 1;
        for (int i = 0; i < args.Length; i++)
            if (int.TryParse(args[i], out var v)) { n = v; break; }
        var chatArg = ArgValue(args, "--chat", "");

        var ui = QqUi.Attach();
        if (chatArg.Length > 0 && ui.Header() != chatArg)
        {
            if (!ui.OpenChat(chatArg))
            {
                Console.Error.WriteLine($"切换会话失败: '{chatArg}'，当前='{ui.Header()}'");
                return 1;
            }
        }

        var pics = ui.PicElements();
        Console.WriteLine($"可见图片 {pics.Count} 张，打开倒数第 {n} 张");
        if (pics.Count == 0) { Console.Error.WriteLine("当前会话没有可见图片消息"); return 1; }
        if (n > pics.Count) { Console.Error.WriteLine($"只有 {pics.Count} 张图，倒数第 {n} 张越界"); return 1; }
        var pic = pics[^n];

        // 跨进程拼坐标必踩时序坑（窗口移动/切窗后树坐标陈旧，2026-10-26 连续翻车），
        // 整条链必须本进程内一气呵成：滚入视口 → 右键 → 轮询菜单 → 点「打开文件夹」。
        // （pic-element 无 InvokePattern；双击会触发查看器又被第二击关掉——右键菜单是稳定通路）
        if (pic.TryGetCurrentPattern(System.Windows.Automation.ScrollItemPattern.Pattern, out var spo)
            && spo is System.Windows.Automation.ScrollItemPattern sip)
        {
            try { sip.ScrollIntoView(); Thread.Sleep(400); } catch { }
        }
        var pr = pic.Current.BoundingRectangle;
        if (pr.Width <= 0 || pr.Height <= 0)
        {
            Console.Error.WriteLine("图片元素不可见（rect 无效）");
            return 1;
        }
        int pcx = (int)(pr.X + pr.Width / 2), pcy = (int)(pr.Y + pr.Height / 2);
        ui.EnsureForeground();
        Native.SetCursorPos(pcx, pcy);
        Thread.Sleep(150);
        Native.mouse_event(Native.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        Native.mouse_event(Native.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        Console.WriteLine($"[img1] rclicked ({pcx},{pcy})");

        // 右键菜单进 UIA 树要 1~3s（recall 同款经验），轮询搜「打开文件夹」
        bool menuClicked = false;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            if (ui.ClickButton("打开文件夹")) { menuClicked = true; break; }
            Thread.Sleep(400);
        }
        if (!menuClicked) { Console.Error.WriteLine("右键菜单 4s 内未出现「打开文件夹」（落点不在图片上？）"); return 1; }
        Console.WriteLine("[img1] 已点「打开文件夹」，等 Explorer");

        // 等 Explorer 窗口（QQ 进程外的 CabinetWClass）
        IntPtr explorerHwnd = IntPtr.Zero;
        string explorerName = "";
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && explorerHwnd == IntPtr.Zero)
        {
            foreach (System.Windows.Automation.AutomationElement w in
                System.Windows.Automation.AutomationElement.RootElement.FindAll(
                    System.Windows.Automation.TreeScope.Children,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ClassNameProperty, "CabinetWClass")))
            {
                try
                {
                    var wr = w.Current.BoundingRectangle;
                    if (wr.Width <= 0) continue;
                    explorerHwnd = new IntPtr(w.Current.NativeWindowHandle);
                    explorerName = w.Current.Name ?? "";
                    Console.WriteLine($"[img1] explorer hwnd={explorerHwnd} name='{explorerName}'");
                    break;
                }
                catch { }
            }
            if (explorerHwnd == IntPtr.Zero) Thread.Sleep(400);
        }
        if (explorerHwnd == IntPtr.Zero) { Console.Error.WriteLine("Explorer 8s 内未出现"); return 1; }

        // 侦察模式：Explorer 开着就返回（结构侦察/人工接管）
        if (HasFlag(args, "--keep-folder"))
        {
            Console.WriteLine($"[img1] keep-folder: explorer hwnd={explorerHwnd}");
            Console.WriteLine(new JsonObject
            {
                ["found"] = true,
                ["explorer_hwnd"] = explorerHwnd.ToInt64(),
                ["explorer_name"] = explorerName,
                ["stage"] = "folder-opened",
            }.ToJsonString());
            return 0;
        }
        Thread.Sleep(800);   // 等 Explorer 完成文件定位高亮

        // 选中项（QQNT 定位高亮的那个）：Win11 Explorer 列表项按 ControlType=ListItem 匹配
        var explorer = System.Windows.Automation.AutomationElement.FromHandle(explorerHwnd);
        string fileName = "";
        foreach (System.Windows.Automation.AutomationElement e in
            explorer.FindAll(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.ListItem)))
        {
            bool sel = false;
            try
            {
                if (e.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out var sp2)
                    && sp2 is System.Windows.Automation.SelectionItemPattern sip2 && sip2.Current.IsSelected)
                    sel = true;
            }
            catch { }
            if (!sel) continue;
            var nm = e.Current.Name ?? "";
            if (nm.Length > 0) { fileName = nm; Console.WriteLine($"[img2] 选中项: {fileName}"); break; }
        }

        Native.PostMessage(explorerHwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        // 目录定位：Explorer 名（'Ori'/'Thumb'）有歧义——Pic\<月>\Ori 与 Emoji\emoji-recv\<月>\Ori
        // 同名（2026-10-26 实测：被 QQNT 归类为表情的图存 emoji-recv，readimg 只扫 Pic 必 miss）。
        // 用选中项文件名在双根下验证存在性，命中即真目录。
        string dirPath = "";
        var picRoot = ImgCache.Roots().FirstOrDefault() ?? "";
        if (picRoot.Length > 0 && explorerName.Length > 0)
        {
            var month = DateTime.Now.ToString("yyyy-MM");
            var ntData = Path.GetDirectoryName(picRoot) ?? "";   // ...\nt_data\Pic → ...\nt_data
            var candidates = new[]
            {
                Path.Combine(picRoot, month, explorerName),
                ntData.Length > 0 ? Path.Combine(ntData, "Emoji", "emoji-recv", month, explorerName) : "",
            };
            foreach (var cand in candidates)
            {
                if (cand.Length == 0 || !Directory.Exists(cand)) continue;
                if (fileName.Length > 0 && File.Exists(Path.Combine(cand, fileName)))
                {
                    dirPath = cand;
                    Console.WriteLine($"[img2] 目录(选中项命中): {dirPath}");
                    break;
                }
                if (fileName.Length == 0 && dirPath.Length == 0) dirPath = cand;   // 兜底先记着
            }
        }

        // 选中项缺席 → 目录内 mtime 最新文件（QQNT 刚触发的下载必然最新）
        if (fileName.Length == 0 && dirPath.Length > 0)
        {
            fileName = new DirectoryInfo(dirPath).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.Name ?? "";
            if (fileName.Length > 0) Console.WriteLine($"[img2] mtime 最新: {fileName}");
        }

        if (dirPath.Length == 0 || fileName.Length == 0)
        {
            Console.Error.WriteLine($"读取不全: dir='{dirPath}' file='{fileName}'（用 --keep-folder 侦察）");
            return 1;
        }
        var src = Path.Combine(dirPath, fileName);
        if (!File.Exists(src))
        {
            Console.Error.WriteLine($"文件不存在: {src}");
            return 1;
        }
        var outDir2 = ImgCache.DefaultOutDir();
        Directory.CreateDirectory(outDir2);
        var dst = Path.Combine(outDir2, "openimg-" + fileName);
        File.Copy(src, dst, true);
        Console.WriteLine($"[img2] 复制 -> {dst}");
        Console.WriteLine(new JsonObject
        {
            ["found"] = true,
            ["count"] = 1,
            ["best"] = new JsonObject
            {
                ["path"] = dst,
                ["src"] = src,
                ["source"] = "open-folder-copy",
                ["note"] = "右键打开文件夹后从 Explorer 定位复制的真图（纯净目标）",
            },
        }.ToJsonString());
        return 0;
    }

    /// <summary>移动任意顶层窗口到工作区右下（--hwnd 句柄，支持 0x 十六进制）。
    /// 查看器被用户窗口盖住导致滚轮落空时用（2026-10-26）。</summary>
    private static int MoveHwnd(string[] args)
    {
        Native.SetProcessDPIAware();
        var hwndArg = ArgValue(args, "--hwnd", "");
        if (hwndArg.Length == 0) { Console.Error.WriteLine("用法: movehwnd --hwnd <句柄>"); return 2; }
        var s = hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hwndArg[2..] : hwndArg;
        var style = hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer;
        var h = new IntPtr(long.Parse(s, style));
        var wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        Native.GetWindowRect(h, out var r);
        int mw = r.Right - r.Left, mh = r.Bottom - r.Top;
        int tx = wa.Right - mw - 8, ty = wa.Bottom - mh - 8;
        var xArg = ArgValue(args, "--x", "");
        var yArg = ArgValue(args, "--y", "");
        if (xArg.Length > 0 && yArg.Length > 0) { tx = int.Parse(xArg); ty = int.Parse(yArg); }
        Native.SetWindowPos(h, IntPtr.Zero, tx, ty, 0, 0,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
        Thread.Sleep(400);
        Native.GetWindowRect(h, out r);
        Console.WriteLine($"movehwnd: ({r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top})");
        return 0;
    }

    /// <summary>双击图片最小链路测试（管理员协议 2026-10-26）：双击倒数第 n 张开大图 → 1.2s →
    /// 点「提取文字」一次 → 4s → 点「左翻」（往前/更早）一次。仅此三动作，无归位/无收集/无收尾。
    /// 双击定位与 ocrfwd 主窗路径同款（滚动稳定后重取 rect+视口校验）。</summary>
    private static int OcrTest(string[] args)
    {
        int n = 1;
        for (int i = 0; i < args.Length; i++)
            if (int.TryParse(args[i], out var v)) { n = v; break; }
        var ui = QqUi.Attach();
        // 窗口归位是链路的一部分（2026-10-26 管理员定案）：与 ocrfwd 主窗路径同款前置
        Layout(new[] { "--work" });
        Thread.Sleep(600);
        var pics = ui.PicElements();
        if (pics.Count == 0) { Console.Error.WriteLine("没有可见图片"); return 1; }
        if (n > pics.Count) { Console.Error.WriteLine($"只有 {pics.Count} 张，倒数第 {n} 张越界"); return 1; }
        var pic = pics[^n];
        System.Windows.Automation.ScrollItemPattern? sip =
            pic.TryGetCurrentPattern(System.Windows.Automation.ScrollItemPattern.Pattern, out var spo)
            && spo is System.Windows.Automation.ScrollItemPattern s ? s : null;
        try { sip?.ScrollIntoView(); } catch { }
        Thread.Sleep(800);
        var pr = pic.Current.BoundingRectangle;
        var view = ui.MessageAreaRect();
        bool CenterInView() => view.HasValue
            && pr.X + pr.Width / 2 >= view.Value.Left && pr.X + pr.Width / 2 < view.Value.Right
            && pr.Y + pr.Height / 2 >= view.Value.Top && pr.Y + pr.Height / 2 < view.Value.Bottom;
        if (!CenterInView()) { try { sip?.ScrollIntoView(); } catch { } Thread.Sleep(600); pr = pic.Current.BoundingRectangle; }
        if (pr.Width <= 0 || pr.Height <= 0 || !CenterInView())
        {
            Console.Error.WriteLine($"图片不在消息视口 rect=({(int)pr.X},{(int)pr.Y} {(int)pr.Width}x{(int)pr.Height}) view={view}");
            return 1;
        }
        int mx = (int)(pr.X + pr.Width / 2), my = (int)(pr.Y + pr.Height / 2);
        Console.WriteLine($"[t] 双击倒数第 {n} 张 ({mx},{my})");
        ui.EnsureForeground();
        Native.SetCursorPos(mx, my);
        Thread.Sleep(150);
        for (int k = 0; k < 2; k++)
        {
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            if (k == 0)
            {
                Thread.Sleep(150);   // 双击间隔 ~150ms+MOVE（Chromium 认双击的时序）
                Native.mouse_event(Native.MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(20);
            }
        }
        Thread.Sleep(1200);   // 等大图窗开稳
        var ocr1 = ui.ClickButton("提取文字");
        Console.WriteLine(ocr1 ? "[t] 已点 提取文字" : "[t] 未找到 提取文字");
        Thread.Sleep(4000);   // OCR 渲染
        var flip = ui.ClickButton("左翻");
        Console.WriteLine(flip ? "[t] 已点 左翻" : "[t] 未找到 左翻");
        return ocr1 && flip ? 0 : 1;
    }

    /// <summary>窗口归位 CLI：placewin --hwnd &lt;句柄&gt; --x N --y N [--w N --h N]（w/h 省略=只移动）。
    /// 坐标依赖型流程（双击开大图、OCR 按钮点击）执行前先归位，防原点漂移。</summary>
    private static int PlaceWin(string[] args)
    {
        var hwndArg = ArgValue(args, "--hwnd", "");
        if (hwndArg.Length == 0 || ArgValue(args, "--x", "").Length == 0)
        {
            Console.Error.WriteLine("用法: placewin --hwnd <句柄> --x N --y N [--w N --h N]");
            return 2;
        }
        var s = hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hwndArg[2..] : hwndArg;
        var style = hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer;
        var h = new IntPtr(long.Parse(s, style));
        int x = int.Parse(ArgValue(args, "--x", "0")), y = int.Parse(ArgValue(args, "--y", "0"));
        int w = int.TryParse(ArgValue(args, "--w", "0"), out var wv) ? wv : 0;
        int hgt = int.TryParse(ArgValue(args, "--h", "0"), out var hv) ? hv : 0;
        if (!QqUi.Place(h, x, y, w, hgt)) { Console.Error.WriteLine("SetWindowPos 失败"); return 1; }
        Thread.Sleep(400);
        Native.GetWindowRect(h, out var r);
        Console.WriteLine($"placewin: ({r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top})");
        return 0;
    }

    /// <summary>图片批量 OCR（QQ 内置「提取文字」，零幻觉——外部视觉模型会编造图内文案，
    /// 2026-10-26 实锤）。进程内闭环：无查看器时双击开大图——转发图包双击转发查看器内首图，
    /// 直发图双击主窗倒数第 --pic 张（默认 1=最新；2026-10-26 管理员定案：主窗直发图同样双击开大图，
    /// openimg 右键「打开文件夹」链路不用于 OCR）→ 循环{提取文字→收集→右翻}一次性读完视口图序。
    /// 已有大图窗时直接续跑（断点续遍历）。输出逐图 JSON lines，回拼消息序由调用方对 messages.jsonl 完成。</summary>
    private static int OcrFwd(string[] args)
    {
        int count = 40, picIdx = 1;
        long bookLine = 0;      // --line N：自动入账到 messages.jsonl 第 N 行（1 基）
        int bookImg = 1;        // --img K：第一张图的图内序号（默认 1）
        var bookChat = "";      // --chat X：行号所属会话校验（防串会话回写）
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pic" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pv))
            {
                picIdx = pv; i++; continue;
            }
            if (args[i] == "--line" && i + 1 < args.Length && long.TryParse(args[i + 1], out var lv))
            {
                bookLine = lv; i++; continue;
            }
            if (args[i] == "--img" && i + 1 < args.Length && int.TryParse(args[i + 1], out var iv))
            {
                bookImg = iv; i++; continue;
            }
            if (args[i] == "--chat" && i + 1 < args.Length)
            {
                bookChat = args[i + 1]; i++; continue;
            }
            if (int.TryParse(args[i], out var v)) { count = v; continue; }   // 位置参数；不能 break，后面还有 --line/--chat
        }

        var ui = QqUi.Attach();
        ui.EnsureForeground();

        // 图片查看器（大图窗）识别：窗口树里有 name='图片查看器' 的节点
        IntPtr BigViewer()
        {
            foreach (var (hwnd, rect, title) in QqUi.Windows())
            {
                if (rect.Right - rect.Left < 400 || rect.Bottom - rect.Top < 300) continue;
                try
                {
                    var el = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                    if (QqUi.FindByName(el, "图片查看器") is not null) return hwnd;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        var big = BigViewer();
        // 主窗双击开的大图窗：起点=最新图，必须往前翻（左翻/上一张，往更早）；转发查看器=右翻遍历图包
        var fromMain = false;
        if (big == IntPtr.Zero)
        {
            // 找转发查看器（非主窗大 Chromium 窗，标题≠QQ），双击其视口内第一张图
            System.Windows.Automation.AutomationElement? firstPic = null;
            IntPtr fwdHwnd = IntPtr.Zero;
            foreach (var (hwnd, rect, title) in QqUi.Windows())
            {
                if (title == "QQ" || rect.Right - rect.Left < 400 || rect.Bottom - rect.Top < 300) continue;
                try
                {
                    var el = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                    foreach (System.Windows.Automation.AutomationElement p in
                        el.FindAll(System.Windows.Automation.TreeScope.Descendants,
                            System.Windows.Automation.Condition.TrueCondition))
                    {
                        string pcls;
                        try { pcls = p.Current.ClassName ?? ""; } catch { continue; }
                        if (!pcls.Contains("pic-element")) continue;
                        var pr = p.Current.BoundingRectangle;
                        if (pr.Width <= 0) continue;
                        if (pr.Y < rect.Top + 60 || pr.Y > rect.Bottom - 60) continue;   // 视口内
                        firstPic = p;
                        fwdHwnd = hwnd;
                        break;
                    }
                    if (firstPic is not null) break;
                }
                catch { }
            }
            // 双击开大图统一通路（150ms+MOVE 时序同款，2026-10-26 实测）；失败=IntPtr.Zero
            IntPtr DblClickAndWaitViewer(int x, int y)
            {
                Native.SetCursorPos(x, y);
                Thread.Sleep(150);
                for (int k = 0; k < 2; k++)
                {
                    Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(40);
                    Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    if (k == 0)
                    {
                        Thread.Sleep(150);   // 双击间隔必须 ~150ms+MOVE，短间隔 Chromium 不认（2026-10-26 实测）
                        Native.mouse_event(Native.MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(20);
                    }
                }
                var wait = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < wait)
                {
                    var b = BigViewer();
                    if (b != IntPtr.Zero) return b;
                    Thread.Sleep(400);
                }
                return IntPtr.Zero;
            }

            if (firstPic is not null)
            {
                var fr = firstPic.Current.BoundingRectangle;
                int dx = (int)(fr.X + fr.Width / 2), dy = (int)(fr.Y + fr.Height / 2);
                Console.WriteLine($"[ocr] 双击转发查看器 hwnd={fwdHwnd} 首图 ({dx},{dy})");
                big = DblClickAndWaitViewer(dx, dy);
            }
            else
            {
                // 窗口归位是链路的一部分（2026-10-26 管理员定案）：主窗被拖动/缩放后 rect 与点击全偏，
                // 双击前先自动归位 work 档，链路自含不再依赖调用方记得归位
                Layout(new[] { "--work" });
                Thread.Sleep(600);
                // 直发图兜底（2026-10-26 管理员定案）：主窗直发图同样双击开大图，起点=倒数第 picIdx 张（最新），
                // 翻页必须往前（左翻=上一张=更早的图）——「下一张」方向没有图，点了等于卡在原地重复提取（同日实锤）。
                var pics = ui.PicElements();
                if (pics.Count == 0)
                {
                    Console.Error.WriteLine("没有转发查看器，主窗也没有可见图片");
                    return 1;
                }
                if (picIdx > pics.Count)
                {
                    Console.Error.WriteLine($"主窗只有 {pics.Count} 张可见图，倒数第 {picIdx} 张越界");
                    return 1;
                }
                var pic = pics[^picIdx];
                System.Windows.Automation.ScrollItemPattern? sip =
                    pic.TryGetCurrentPattern(System.Windows.Automation.ScrollItemPattern.Pattern, out var spo)
                    && spo is System.Windows.Automation.ScrollItemPattern s ? s : null;
                try { sip?.ScrollIntoView(); } catch { }
                // 连发多图时滚动稳定要 800ms+，rect 必须稳定后重取——中途取的是陈旧值（2026-10-26 潮汐 7 连图实测点击落空）
                Thread.Sleep(800);
                var pr = pic.Current.BoundingRectangle;
                var view = ui.MessageAreaRect();
                bool CenterInView() => view.HasValue
                    && pr.X + pr.Width / 2 >= view.Value.Left && pr.X + pr.Width / 2 < view.Value.Right
                    && pr.Y + pr.Height / 2 >= view.Value.Top && pr.Y + pr.Height / 2 < view.Value.Bottom;
                if (!CenterInView())
                {
                    try { sip?.ScrollIntoView(); } catch { }
                    Thread.Sleep(600);
                    pr = pic.Current.BoundingRectangle;
                }
                if (pr.Width <= 0 || pr.Height <= 0 || !CenterInView())
                {
                    Console.Error.WriteLine($"图片元素不在消息视口内 rect=({(int)pr.X},{(int)pr.Y} {(int)pr.Width}x{(int)pr.Height}) view={view}");
                    return 1;
                }
                int mx = (int)(pr.X + pr.Width / 2), my = (int)(pr.Y + pr.Height / 2);
                Console.WriteLine($"[ocr] 双击主窗倒数第 {picIdx} 张图 ({mx},{my})");
                ui.EnsureForeground();
                big = DblClickAndWaitViewer(mx, my);
                fromMain = true;
            }
            if (big == IntPtr.Zero) { Console.Error.WriteLine("双击后图片查看器未出现"); return 1; }
        }
        Console.WriteLine($"[ocr] 图片查看器 hwnd={big}");
        // 归位到 Amy **左半区**（2026-10-28 owner：展开放左半屏，右半是 QQ，摆右半会被压住；按钮坐标全依赖窗口位置）
        var (vx, vy, vw, vh) = AmyLeftHalf(ui.Hwnd);
        QqUi.Place(big, vx, vy, vw, vh);
        Thread.Sleep(400);

        bool ClickBtnIn(IntPtr hwnd, string name)
        {
            try
            {
                var el = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                var btn = QqUi.FindByName(el, name);
                if (btn is null) return false;
                var r = btn.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) return false;
                Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                Thread.Sleep(130);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(40);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        string[] uiWords = { "查看器", "缩放", "关闭", "最小化", "最大化", "旋转", "编辑", "翻译",
            "提取文字", "另存为", "更多", "左翻", "右翻", "窗口控制", "图片原始大小", "转发", "多选", "引用" };

        List<string> Collect()
        {
            var seen = new HashSet<string>();
            var lines = new List<string>();
            try
            {
                var el = System.Windows.Automation.AutomationElement.FromHandle(big);
                foreach (System.Windows.Automation.AutomationElement e in
                    el.FindAll(System.Windows.Automation.TreeScope.Descendants,
                        System.Windows.Automation.Condition.TrueCondition))
                {
                    string nm;
                    try { nm = e.Current.Name ?? ""; } catch { continue; }
                    if (nm.Length < 4) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(nm, @"^[0-9.\s%]+$")) continue;   // 缩放比例"100%"等
                    string cls;
                    try { cls = e.Current.ClassName ?? ""; } catch { cls = ""; }
                    if (cls.Contains("image-viewer") || cls.Contains("window-control")) continue;
                    bool ui = false;
                    foreach (var w in uiWords) if (nm.Contains(w)) { ui = true; break; }
                    if (ui) continue;
                    if (seen.Add(nm)) lines.Add(nm);
                }
            }
            catch { }
            return lines;
        }

        int extracted = 0;
        var perImage = new List<List<string>>();   // 每张图的识别行（--line 入账用）
        for (int i = 1; i <= count; i++)
        {
            if (!ClickBtnIn(big, "提取文字"))
            {
                Console.Error.WriteLine($"[ocr] 第{i}张找不到「提取文字」按钮（查看器关了？），收尾");
                break;
            }
            Thread.Sleep(3200);   // OCR 处理
            var lines = Collect();
            perImage.Add(lines);
            Console.WriteLine(new JsonObject
            {
                ["i"] = i,
                ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()),
            }.ToJsonString());
            extracted++;
            if (i == count) break;
            var flip = fromMain ? "左翻" : "右翻";   // 主窗起点=最新图，往前(更早)翻；转发查看器按图包顺序右翻
            if (!ClickBtnIn(big, flip))
            {
                Console.WriteLine($"[ocr] 第{i}张后无「{flip}」（到底了），收尾");
                break;
            }
            Thread.Sleep(1500);
        }
        Console.WriteLine(new JsonObject { ["done"] = true, ["extracted"] = extracted }.ToJsonString());

        // --line N：识别完直接入账 + 回写原文层（2026-10-26：不再让值班脑手抄账本，抄错=丢内容）
        if (bookLine > 0)
        {
            try
            {
                var cfg = AppConfig.Load(ArgValue(args, "--config", "config.json"));
                var logDir = Path.Combine(AppConfig.BaseDir, "logs");
                var summary = OcrBook.Record(
                    Path.Combine(logDir, "messages.jsonl"),
                    Path.Combine(logDir, "ocr-ledger.jsonl"),
                    bookLine, bookImg, perImage, bookChat,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    fromMain ? "direct" : "fwd", "ocrfwd");
                Console.WriteLine("[ocr] " + summary);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[ocr] 入账失败：" + ex.Message);
                RestoreUi();
                return 4;
            }
        }
        RestoreUi();   // 2026-10-27：ESC 收图 → 关查看器 → 主窗回档1
        return extracted > 0 ? 0 : 1;
    }

    /// <summary>UI 收尾（2026-10-27 管理员定案）：识图/发语音/看转发展开过 QQ 之后，一律
    /// ESC 收浮层 → closefwd 清查看器 → 主窗放回**档1**（layout --std）。
    /// 每个"一次性动作"命令末尾自动调，不依赖调用方记得收尾。</summary>
    internal static void RestoreUi()
    {
        try
        {
            // 只对「不是主窗」的 QQ 窗口发 ESC（图片查看器/转发查看器）。给主窗发 ESC 会把当前会话关掉，
            // 面板变空 → 读不到图、daemon 还会被逼着切回配置里的默认会话（2026-10-27 实测踩到）。
            var mainHwnd = QqUi.Attach().Hwnd;
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != mainHwnd)
            {
                Native.GetWindowThreadProcessId(fg, out var fgPid);
                if (Process.GetProcessesByName("QQ").Any(p => (uint)p.Id == fgPid))
                {
                    Native.SendKeyScan(0x01, true);   // ESC 扫描码 0x01（查看器只认扫描码）
                    Thread.Sleep(60);
                    Native.SendKeyScan(0x01, false);
                    Thread.Sleep(350);
                }
            }
        }
        catch { }
        try { CloseFwd(); } catch { }
        // 档1 位置偶尔被 QQ 自己的窗口自整动画带偏，连放两次取稳
        try { Layout(new[] { "--std" }); Thread.Sleep(500); Layout(new[] { "--std" }); } catch { }
    }

    /// <summary>关闭遗留的转发查看器（标题非 QQ、内含 message-container 的大窗口）与图片查看器
    /// （ocrfwd 跑完不关大图窗——2026-10-26 补：closefwd 一并收掉，OCR 链自清洁）。</summary>
    private static int CloseFwd()
    {
        var ui = QqUi.Attach();
        int closed = 0;
        foreach (var (hwnd, rect, title) in QqUi.Windows())
        {
            if (title == "QQ" || (rect.Right - rect.Left) * (rect.Bottom - rect.Top) < 400 * 300) continue;
            try
            {
                var el = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                bool fwd = QqUi.FindByClassContains(el, "message-container") is not null
                    && QqUi.FindByName(el, "消息列表") is null;   // 主窗口特征，跳过
                bool bigv = QqUi.FindByName(el, "图片查看器") is not null;
                if (!fwd && !bigv) continue;
                Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Console.WriteLine($"closed viewer hwnd={hwnd} title='{title}'");
                closed++;
            }
            catch { }
        }
        Console.WriteLine(closed > 0 ? $"{closed} viewer(s) closed" : "no viewer found");
        return closed > 0 ? 0 : 1;
    }

    /// <summary>2026-10-27 owner 定案：小屏**右半**归 Amy（左半是桌宠，QQ 只占右半）。
    /// 返回主 QQ 窗所在显示器的右半矩形，坐标与 UIA/GetWindowRect 同一套（exe 的 DPI 虚拟化空间）。</summary>
    private static (int X, int Y, int W, int H) AmyHalf(IntPtr mainHwnd = default)
    {
        try
        {
            if (mainHwnd == IntPtr.Zero)
            {
                // 不能拿「最大的 QQ 窗」当主窗：QQ 会给查看器预开一个 1600×1200 的空窗，面积比主窗还大。
                try { mainHwnd = QqUi.Attach().Hwnd; } catch { }
            }
            if (mainHwnd != IntPtr.Zero)
            {
                var mon = Native.MonitorFromWindow(mainHwnd, Native.MONITOR_DEFAULTTONEAREST);
                var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
                {
                    int w = (mi.rcWork.Right - mi.rcWork.Left) / 2;
                    int h = mi.rcWork.Bottom - mi.rcWork.Top;
                    if (w > 300 && h > 300) return (mi.rcWork.Left + w, mi.rcWork.Top, w, h);
                }
            }
        }
        catch { }
        return (2560, 901, 1642, 1160);   // 兜底：旧 work 档
    }

    /// <summary>左半区（2026-10-28 owner：「展开后被 QQ 挡住了，把展开放到左半屏」）。
    /// 与 AmyHalf 同源：主窗所在屏的工作区左半；转发查看器/大图窗都摆这里，右侧留给 QQ。</summary>
    private static (int X, int Y, int W, int H) AmyLeftHalf(IntPtr mainHwnd = default)
    {
        try
        {
            if (mainHwnd == IntPtr.Zero)
            {
                try { mainHwnd = QqUi.Attach().Hwnd; } catch { }
            }
            if (mainHwnd != IntPtr.Zero)
            {
                var mon = Native.MonitorFromWindow(mainHwnd, Native.MONITOR_DEFAULTTONEAREST);
                var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
                {
                    int w = (mi.rcWork.Right - mi.rcWork.Left) / 2;
                    int h = mi.rcWork.Bottom - mi.rcWork.Top;
                    if (w > 300 && h > 300) return (mi.rcWork.Left, mi.rcWork.Top, w, h);
                }
            }
        }
        catch { }
        return (7680, 0, 1900, 2160);   // 兜底：小屏左半（2026-10-28 实测工作区 7680,0 3840x2160）
    }

    /// <summary>主窗维持 QQ **最小档**、靠右半区右缘居中（owner：QQ 别铺满，左半留给桌宠）。
    /// 先丢个 200×200 让 QQ 自己夹到最小尺寸，再按实测尺寸右对齐。</summary>
    private static void PlaceAmyMain(IntPtr hwnd)
    {
        var (hx, hy, hw, hh) = AmyHalf(hwnd);
        Native.GetWindowRect(hwnd, out var cur);
        int w = cur.Right - cur.Left, h = cur.Bottom - cur.Top;
        // 只有当前比最小档大时才做「丢 200×200 让 QQ 自己夹」——无谓的缩放会让 Chromium 重排，
        // 紧接着的双击/UIA rect 会拿到旧坐标（2026-10-27 实测：OCR 双击打空就死在这）。
        if (w > 1700 || h > 1250)
        {
            QqUi.Place(hwnd, hx, hy, 200, 200);
            Thread.Sleep(300);
            Native.GetWindowRect(hwnd, out cur);
            w = cur.Right - cur.Left; h = cur.Bottom - cur.Top;
        }
        if (w <= 0 || h <= 0) { w = 1640; h = 1160; }
        QqUi.Place(hwnd, hx + hw - w - 16, hy + (hh - h) / 2, w, h);
    }

    /// <summary>把残留的 QQ 弹窗（图片查看器/转发查看器/资料卡——QQ 会给它们预开一个空窗并扔在主屏）
    /// 统一搬到 Amy 右半区（2026-10-27 owner：弹窗也放我这边）。</summary>
    private static void ParkSideWindows(IntPtr mainHwnd)
    {
        try
        {
            var (hx, hy, hw, hh) = AmyLeftHalf(mainHwnd);
            foreach (var (h, rect, title) in QqUi.Windows())
            {
                if (h == mainHwnd) continue;
                if (!Native.IsWindowVisible(h)) continue;   // 隐藏的预开窗不管
                int w = rect.Right - rect.Left, ht = rect.Bottom - rect.Top;
                if (w < 400 || ht < 300) continue;   // 托盘图标/小浮层不动
                if (rect.Left >= hx && rect.Right <= hx + hw) continue;   // 已经在左半（QQ 占右半，2026-10-28）
                QqUi.Place(h, hx, hy, Math.Min(w, hw), Math.Min(ht, hh));
                Console.WriteLine($"[park] 弹窗 '{title}' ({rect.Left},{rect.Top} {w}x{ht}) -> 左半区");
            }
        }
        catch { }
    }

    /// <summary>把主窗**当前所在**显示器的工作区铺满（备用档；小屏时代主用 PlaceAmyMain）。</summary>
    private static void PlaceOnCurrentMonitor(IntPtr hwnd, int margin)
    {
        try
        {
            var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
            {
                int w = mi.rcWork.Right - mi.rcWork.Left - margin * 2;
                int h = mi.rcWork.Bottom - mi.rcWork.Top - margin * 2;
                if (w > 400 && h > 400)
                {
                    QqUi.Place(hwnd, mi.rcWork.Left + margin, mi.rcWork.Top + margin, w, h);
                    return;
                }
            }
        }
        catch { }
        QqUi.Place(hwnd, 2560, 901, 1642, 1160);   // 兜底：旧 work 档
    }

    /// <summary>主窗按当前实际尺寸纯移动锚到工作区右下角（不动尺寸、不抢前台、免 UIA）。
    /// QQ 有自己的最小尺寸与窗口自整行为，不做任何尺寸强设。</summary>
    private static int Layout(string[] args)
    {
        Native.SetProcessDPIAware();

        // --work：工作档（2026-10-26 管理员定档）：主窗 (2560,901 1642x1160)，
        // 屏内可见 ~1280px（会话列表+聊天区+输入区可达）——发语音（sendvoice 录音面板）、
        // 读转发（readfwd 卡片定位）用这个状态
        if (HasFlag(args, "--work"))
        {
            var pidsW = new HashSet<uint>(System.Diagnostics.Process.GetProcessesByName("QQ").Select(p => (uint)p.Id));
            var winsW = Native.TopLevelWindows(pidsW).Where(w => Native.IsWindowVisible(w.Hwnd)).ToList();
            if (winsW.Count == 0) { Console.Error.WriteLine("找不到 QQ 窗口"); return 1; }
            var hwndW = winsW.OrderByDescending(w => (long)(w.Rect.Right - w.Rect.Left) * (w.Rect.Bottom - w.Rect.Top)).First().Hwnd;
            PlaceAmyMain(hwndW);
            ParkSideWindows(hwndW);
            Thread.Sleep(400);
            Native.GetWindowRect(hwndW, out var rw);
            Console.WriteLine($"layout(work): ({rw.Left},{rw.Top} {rw.Right - rw.Left}x{rw.Bottom - rw.Top})");
            return 0;
        }

        // --std：标准文字回复档（2026-10-26 管理员定档）：主窗 (3348,1474 1642x1160)，
        // 屏内仅露左侧会话列表（三个会话可点），sendq 回复文字用这个状态。
        // （无参 layout = 右下完全展示档，暂无功能依赖，备用）
        if (HasFlag(args, "--std"))
        {
            var pidsStd = new HashSet<uint>(System.Diagnostics.Process.GetProcessesByName("QQ").Select(p => (uint)p.Id));
            var winsStd = Native.TopLevelWindows(pidsStd).Where(w => Native.IsWindowVisible(w.Hwnd)).ToList();
            if (winsStd.Count == 0) { Console.Error.WriteLine("找不到 QQ 窗口"); return 1; }
            var hwndStd = winsStd.OrderByDescending(w => (long)(w.Rect.Right - w.Rect.Left) * (w.Rect.Bottom - w.Rect.Top)).First().Hwnd;
            PlaceAmyMain(hwndStd);
            ParkSideWindows(hwndStd);
            Thread.Sleep(400);
            // 2026-10-27：档1 这个位置会让「消息区」整个落在屏外（屏 3840 宽，消息区 x≈3820..4622），
            // Chromium 就不再渲染新消息、daemon 读不到。放完再夹一次，保证消息区可见，其余行为不变。
            try { QqUi.Attach().EnsureChatAreaVisible(); } catch { }
            Native.GetWindowRect(hwndStd, out var rs);
            Console.WriteLine($"layout(std): ({rs.Left},{rs.Top} {rs.Right - rs.Left}x{rs.Bottom - rs.Top})");
            return 0;
        }

        // 免 UIA 找主窗：QQ 进程中最大的可见 Chrome_WidgetWin_1
        var pids = new HashSet<uint>(System.Diagnostics.Process.GetProcessesByName("QQ").Select(p => (uint)p.Id));
        var wins = Native.TopLevelWindows(pids).Where(w => Native.IsWindowVisible(w.Hwnd)).ToList();
        if (wins.Count == 0)
        {
            Console.Error.WriteLine("找不到 QQ 窗口");
            return 1;
        }
        var main = wins.OrderByDescending(w => (long)(w.Rect.Right - w.Rect.Left) * (w.Rect.Bottom - w.Rect.Top)).First();
        IntPtr hwnd = main.Hwnd;

        // 位置锁（2026-10-27 owner：「把 qq 固定住，不让我直接动」）：--pin 记下当前 rect，
        // daemon 每 12s 兜底把被拖走的窗口拉回；--unpin 删锁。
        // 注意 layout 走的是免 config 路径，AppConfig.BaseDir 还是 bin 目录 —— 用 daemon.bat 上溯定 logs。
        var lockPath = Path.Combine(Path.GetDirectoryName(ImgCache.DefaultOutDir())!, "win-lock.json");
        if (HasFlag(args, "--unpin"))
        {
            if (File.Exists(lockPath)) File.Delete(lockPath);
            Console.WriteLine("win-lock: 已解除（窗口可自由拖动）");
            return 0;
        }
        if (HasFlag(args, "--pin"))
        {
            Native.GetWindowRect(hwnd, out var pr);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            File.WriteAllText(lockPath, new JsonObject
            {
                ["left"] = pr.Left,
                ["top"] = pr.Top,
                ["width"] = pr.Right - pr.Left,
                ["height"] = pr.Bottom - pr.Top,
                ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }.ToJsonString());
            Console.WriteLine($"win-lock: 锁定 ({pr.Left},{pr.Top} {pr.Right - pr.Left}x{pr.Bottom - pr.Top}) -> {lockPath}");
            return 0;
        }

        if (HasFlag(args, "--sink"))
        {
            // Z 序沉底：完全被其他窗口盖住（隐藏形态）；无坐标操作，不影响输入可达性
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                0x0002 /*SWP_NOMOVE*/ | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Console.WriteLine($"sink: hwnd={hwnd} 已压到 Z 序最底");
            return 0;
        }
        var wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;

        Native.GetWindowRect(hwnd, out var r);
        int mw = r.Right - r.Left, mh = r.Bottom - r.Top;
        Native.SetWindowPos(hwnd, IntPtr.Zero, wa.Right - mw - 8, wa.Bottom - mh - 8, 0, 0,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
        Thread.Sleep(500);
        Native.GetWindowRect(hwnd, out r);
        Console.WriteLine($"layout(move): ({r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top})");
        return 0;
    }

    /// <summary>探测转发查看器里每条消息的深层无障碍属性（正文文本不进可见树时的取证）。</summary>
    private static int FwdProps()
    {
        var ui = QqUi.Attach();
        System.Windows.Automation.AutomationElement? viewer = null;
        foreach (var (hwnd, rect, title) in QqUi.Windows())
        {
            if (title == "QQ" || (rect.Right - rect.Left) < 400 || (rect.Bottom - rect.Top) < 300) continue;
            try
            {
                var el = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                if (QqUi.FindByName(el, "消息列表") is not null) continue;
                if (QqUi.FindByClassContains(el, "message-container") is null) continue;
                viewer = el;
                break;
            }
            catch { }
        }
        if (viewer is null)
        {
            Console.WriteLine("没有打开的转发查看器（先 readfwd --keep）");
            return 1;
        }

        int i = 0;
        foreach (System.Windows.Automation.AutomationElement el in viewer.FindAll(
            System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
        {
            string cls;
            try { cls = el.Current.ClassName ?? ""; }
            catch { continue; }
            if (!cls.Contains("message-container") || cls.Contains("msg-content-container")) continue;

            string sender = "";
            foreach (System.Windows.Automation.AutomationElement d in el.FindAll(
                System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                try
                {
                    if ((d.Current.ClassName ?? "") == "avatar-span") { sender = d.Current.Name ?? ""; break; }
                }
                catch { break; }
            }

            Console.WriteLine($"--- msg#{i++} sender='{sender}' cls='{cls}'");
            DumpProps(el, "  container");

            foreach (System.Windows.Automation.AutomationElement d in el.FindAll(
                System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                string dcls;
                try { dcls = d.Current.ClassName ?? ""; }
                catch { continue; }
                if (!dcls.Contains("msg-content-container")) continue;
                DumpProps(d, "  content ");
                foreach (System.Windows.Automation.AutomationElement c in d.FindAll(
                    System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
                {
                    try { DumpProps(c, "    child   "); } catch { }
                }
                break;
            }
            if (i >= 4) break;   // 前几条足够取证
        }
        return 0;
    }

    private static void DumpProps(System.Windows.Automation.AutomationElement el, string tag)
    {
        string cls, name, help;
        try
        {
            cls = el.Current.ClassName ?? "";
            name = (el.Current.Name ?? "").Replace("\n", "↵");
            help = (el.Current.HelpText ?? "").Replace("\n", "↵");
        }
        catch { return; }
        var line = $"{tag} cls='{cls}' name='{Trunc(name)}' help='{Trunc(help)}'";

        try
        {
            if (el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp) &&
                vp is System.Windows.Automation.ValuePattern v)
                line += $" value='{Trunc(v.Current.Value)}'";
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var tp) &&
                tp is System.Windows.Automation.TextPattern t)
                line += $" textpat='{Trunc(t.DocumentRange.GetText(-1))}'";
        }
        catch { }
        Console.WriteLine(line);
    }

    private static string Trunc(string s) => s.Length > 80 ? s[..80] + "…" : s;

    /// <summary>虚拟桌面：vd（状态）/ vd move [n=2]（QQ 主窗移到桌面 n，不够则创建）/ vd back。</summary>
    private static int VdCmd(string[] args)
    {
        Native.SetProcessDPIAware();
        args = WithoutConfig(args);
        var pids = new HashSet<uint>(System.Diagnostics.Process.GetProcessesByName("QQ").Select(p => (uint)p.Id));
        var wins = Native.TopLevelWindows(pids).Where(w => Native.IsWindowVisible(w.Hwnd)).ToList();
        if (wins.Count == 0)
        {
            Console.Error.WriteLine("找不到 QQ 窗口");
            return 1;
        }
        var main = wins.OrderByDescending(w => (long)(w.Rect.Right - w.Rect.Left) * (w.Rect.Bottom - w.Rect.Top)).First();

        List<Guid> desktops;
        try { desktops = Vd.Desktops(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"虚拟桌面 API 不可用: {ex.Message}");
            return 1;
        }

        var cur = Vd.WindowDesktop(main.Hwnd);
        Console.WriteLine($"desktops={desktops.Count}");
        for (int i = 0; i < desktops.Count; i++)
            Console.WriteLine($"  #{i + 1} {desktops[i]}{(desktops[i] == cur ? "  <- QQ" : "")}");

        if (args.Length == 0) return 0;
        if (args[0] is not ("move" or "back"))
        {
            Console.Error.WriteLine("用法: vd [move n|back]");
            return 2;
        }

        int n = args[0] == "back" ? 1 : args.Length > 1 && int.TryParse(args[1], out var v) ? v : 2;
        while (desktops.Count < n)
        {
            _ = Vd.Create();
            desktops = Vd.Desktops();
            Console.WriteLine($"created desktop #{desktops.Count}");
        }
        if (desktops[n - 1] == cur)
        {
            Console.WriteLine($"QQ 已在桌面 #{n}");
            return 0;
        }
        Vd.MoveWindow(main.Hwnd, desktops[n - 1]);
        Console.WriteLine($"QQ -> 桌面 #{n}（Win+Ctrl+←/→ 可切换查看）");
        return 0;
    }

    /// <summary>静默发送到当前会话（不发切换）：WM_PASTE + 回车，验证投递式输入链路。</summary>
    private static int SendQuietCmd(string[] args)
    {
        args = WithoutConfig(args);
        if (args.Length < 1)
        {
            Console.Error.WriteLine("用法: sendq <文本>（发到当前会话，请先用 probe 确认当前会话）");
            return 2;
        }
        var text = string.Join(' ', args
            .Where((a, i) => !a.StartsWith("--") && !(i > 0 && args[i - 1] == "--chat"))
            .ToArray());
        var chatArg = ArgValue(args, "--chat", "");

        var ui = QqUi.Attach();
        if (chatArg.Length > 0 && ui.Header() != chatArg)
        {
            if (!ui.OpenChat(chatArg))
            {
                Console.Error.WriteLine($"切换会话失败: '{chatArg}'，当前='{ui.Header()}'");
                return 1;
            }
        }
        var chat = ui.Header();
        Console.WriteLine($"[sendq] -> '{chat}': {text}");

        Native.GetCursorPos(out var c0);
        var fg0 = Native.GetForegroundWindow();
        ui.SendQuiet(text);
        Thread.Sleep(1500);
        Native.GetCursorPos(out var c1);
        var fg1 = Native.GetForegroundWindow();
        Console.WriteLine($"无感断言: cursor ({c0.X},{c0.Y})->({c1.X},{c1.Y})  fg {fg0}->{fg1}");

        var ok = ui.Messages().Any(m => m.IsSelf && m.Text.Contains(text));
        Console.WriteLine(ok ? $"sent ok (quiet) -> '{chat}'" : $"sent UNVERIFIED -> '{chat}'（posted 输入未被 Chromium 接收？）");
        return ok ? 0 : 3;
    }

    /// <summary>一键发表情（开发期实测 2026-10-26）：表情面板→我的收藏第 N 个→发送→回读。
    /// 用法：sendemoji [--chat 会话] [--index n] [--list] [--dry-run]</summary>
    private static int SendEmojiCmd(string[] args)
    {
        var chat = ArgValue(args, "--chat", "");
        var index = int.TryParse(ArgValue(args, "--index", "1"), out var i) ? i : 1;
        var list = HasFlag(args, "--list");
        var dry = HasFlag(args, "--dry-run");
        var tab = ArgValue(args, "--tab", "我的收藏");

        var ui = QqUi.Attach();
        if (chat.Length > 0 && ui.Header() != chat)
        {
            if (!ui.OpenChat(chat))
            {
                Console.Error.WriteLine($"切换会话失败: '{chat}'，当前='{ui.Header()}'");
                return 1;
            }
        }
        if (chat.Length > 0 && ui.Header() != chat)
        {
            Console.Error.WriteLine($"会话标题校验失败: '{ui.Header()}' != '{chat}'");
            return 1;
        }

        if (list) return EmojiSender.List(tab);

        var (ok, detail) = EmojiSender.Send(index, dry, tab);
        Console.WriteLine((ok ? "[sendemoji] ok " : "[sendemoji] FAIL ") + detail + $"  header='{ui.Header()}'");
        return ok ? 0 : 1;
    }
    /// <summary>探测输入面的 UIA 可用性：编辑区 ValuePattern/TextPattern、发送按钮 Invoke、
    /// Chromium 真输入子窗口（Chrome_RenderWidgetHostHWND）。</summary>
    private static int InputProbe()
    {
        var ui = QqUi.Attach();
        Console.WriteLine($"header='{ui.Header()}'");

        var editor = QqUi.FindByClassContains(ui.Root, "ProseMirror");
        Console.WriteLine($"editor: {(editor is null ? "NOT FOUND" : "found")}");
        if (editor is not null)
        {
            Console.WriteLine($"  ValuePattern: {Supports(editor, System.Windows.Automation.ValuePattern.Pattern)}");
            Console.WriteLine($"  TextPattern:  {Supports(editor, System.Windows.Automation.TextPattern.Pattern)}");
            Console.WriteLine($"  Invoke:       {Supports(editor, System.Windows.Automation.InvokePattern.Pattern)}");
            try { Console.WriteLine($"  value now:    '{(editor.Current.Name ?? "")}'"); } catch { }
        }

        var sendBtn = QqUi.FindByName(ui.Root, "发送");
        Console.WriteLine($"send-button: {(sendBtn is null ? "NOT FOUND" : "found")}");
        if (sendBtn is not null)
        {
            Console.WriteLine($"  Invoke:       {Supports(sendBtn, System.Windows.Automation.InvokePattern.Pattern)}");
            try
            {
                var r = sendBtn.Current.BoundingRectangle;
                Console.WriteLine($"  rect: ({(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height})");
            }
            catch { }
        }

        // Chromium 的真输入子窗口
        foreach (var (hwnd, _, _) in QqUi.Windows())
        {
            Native.EnumChildWindows(hwnd, (child, _) =>
            {
                var sb = new System.Text.StringBuilder(64);
                Native.GetClassName(child, sb, 64);
                var cls = sb.ToString();
                if (cls.StartsWith("Chrome_RenderWidgetHostHWND"))
                    Console.WriteLine($"render-child: hwnd={child} of {hwnd} (posted 输入应该发给它而不是主窗口)");
                return true;
            }, IntPtr.Zero);
        }
        return 0;
    }

    private static string Supports(System.Windows.Automation.AutomationElement el, System.Windows.Automation.AutomationPattern pattern)
    {
        try { return el.TryGetCurrentPattern(pattern, out var _) ? "YES" : "no"; }
        catch { return "err"; }
    }

    private static int FindChat(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("用法: findchat <号码或昵称>");
            return 2;
        }
        var keyword = string.Join(' ', args);
        var ui = QqUi.Attach();
        ui.EnsureForeground();
        var header = ui.OpenChatBySearch(keyword);
        Console.WriteLine(string.IsNullOrEmpty(header) ? "find failed" : $"opened chat '{header}'");
        return string.IsNullOrEmpty(header) ? 1 : 0;
    }

    /// <summary>音频端点管理：mic list / mic on / mic off——语音条通路的录音设备开关。
    /// on 幂等（启用立体声混音+设默认+满电平）；off 恢复机器原状（禁用回环端点）。</summary>
    private static int Mic(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
                Console.WriteLine("=== 录音端点 capture（* = 默认） ===");
                foreach (var e in AudioIo.List(1))
                    Console.WriteLine($"  {(e.IsDefault ? '*' : ' ')} state={e.State} {e.Name}");
                Console.WriteLine("=== 播放端点 render（* = 默认） ===");
                foreach (var e in AudioIo.List(0))
                    Console.WriteLine($"  {(e.IsDefault ? '*' : ' ')} state={e.State} {e.Name}");
                return 0;
            case "on":
            case "off":
            {
                var cfg = AppConfig.Load(ArgValue(args, "--config", "config.json"));
                Console.WriteLine(sub == "on"
                    ? AudioIo.EnsureCaptureReady(cfg.AmyRuntime.VoiceCaptureMatch)
                    : AudioIo.DisableCapture(cfg.AmyRuntime.VoiceCaptureMatch));
                return 0;
            }
            default:
                Console.Error.WriteLine("用法: mic list|on|off");
                return 2;
        }
    }

    /// <summary>sendvoice <会话> <音频路径> [--vol 0.8] [--cap 58] [--config p]：
    /// 把本地音频以 QQ 语音条发到指定会话。自动确保录音端点就绪；归一化转码后实录发送。</summary>
    private static int SendVoice(string[] args)
    {
        var pos = args.Where((a, i) => !a.StartsWith("--") && !(i > 0 && args[i - 1].StartsWith("--"))).ToArray();
        if (pos.Length < 2)
        {
            Console.Error.WriteLine("用法: sendvoice <会话名> <音频路径> [--vol 0.8] [--cap 58]");
            return 2;
        }
        var chat = pos[0];
        var path = pos[1];
        if (!File.Exists(path)) { Console.Error.WriteLine($"文件不存在: {path}"); return 2; }

        var cfg = AppConfig.Load(ArgValue(args, "--config", "config.json"));
        var amy = cfg.AmyRuntime;
        var vol = double.Parse(ArgValue(args, "--vol", amy.VoiceVolume.ToString("0.00")), System.Globalization.CultureInfo.InvariantCulture);
        var cap = double.Parse(ArgValue(args, "--cap", amy.VoiceMaxSeconds.ToString("0.#")), System.Globalization.CultureInfo.InvariantCulture);

        Console.WriteLine(AudioIo.EnsureCaptureReady(amy.VoiceCaptureMatch));

        var ui = QqUi.Attach();
        if (ui.Header() != chat && !ui.OpenChat(chat))
        {
            Console.Error.WriteLine($"切换会话失败: '{chat}'，当前='{ui.Header()}'");
            return 1;
        }
        Console.WriteLine($"[sendvoice] -> '{ui.Header()}': {Path.GetFileName(path)} vol={vol} cap={cap}s");

        var tmp = AudioIo.NormalizeToWav(path, vol);
        try
        {
            var before = ui.Messages().Count(m => m.IsSelf);
            ui.SendVoiceFile(tmp, amy.VoiceRenderMatch, cap);
            Thread.Sleep(1500);
            var ok = ui.Messages().Count(m => m.IsSelf) > before;
            Console.WriteLine(ok ? $"voice sent ok -> '{ui.Header()}'" : $"voice sent UNVERIFIED（回读未见新消息） -> '{ui.Header()}'");
            RestoreUi();   // 2026-10-27：录音面板收起 + 主窗回档1
            return 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>空格注入验证（2026-10-25）：开录音面板 → 逐秒 dump 面板状态（按住期间 UI 必变：
    /// 计时器/提示切换）→ 松开 → dump。用法 spacetest [--vk|--scan]（默认 --scan）。</summary>
    private static int SpaceTest(string[] args)
    {
        var useVk = HasFlag(args, "--vk");
        var useUnicode = HasFlag(args, "--unicode");
        var clickPanel = HasFlag(args, "--click");
        var ui = QqUi.Attach();
        ui.EnsureForeground();
        void Press(bool down)
        {
            if (useVk) QqUi.SendKeyTest(0x20, down);
            else if (useUnicode) Native.SendCharUnicode(' ');
            else Native.SendKeyScan(0x39, down);
        }
        if (clickPanel) { ui.ClickVoicePanelBodyForTest(); Thread.Sleep(400); }
        string PanelDump(string tag)
        {
            var lines = ui.DumpVoicePanelForTest();
            Console.WriteLine($"--- panel {tag} ---");
            foreach (var l in lines) Console.WriteLine("  " + l);
            return string.Join("|", lines);
        }
        var before = PanelDump("before");
        Press(true);
        var held1 = PanelDump("held+1s");
        Thread.Sleep(1000);
        var held2 = PanelDump("held+2s");
        Press(false);
        Thread.Sleep(800);
        var after = PanelDump("after-release");
        Console.WriteLine($"RESULT: changed_during_hold={(held1 != before || held2 != before)} released_changed={(after != held2)}");
        return 0;
    }

    /// <summary>playwav <音频路径> [--vol 0.8] [--cap 58]：归一化后 WASAPI 播放（语音通路自检，无 QQ 副作用）。</summary>
    private static int PlayWav(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (path is null || !File.Exists(path)) { Console.Error.WriteLine("用法: playwav <音频路径> [--vol 0.8] [--cap 58]"); return 2; }
        var cfg = AppConfig.Load(ArgValue(args, "--config", "config.json"));
        var amy = cfg.AmyRuntime;
        var vol = double.Parse(ArgValue(args, "--vol", amy.VoiceVolume.ToString("0.00")), System.Globalization.CultureInfo.InvariantCulture);
        var cap = double.Parse(ArgValue(args, "--cap", amy.VoiceMaxSeconds.ToString("0.#")), System.Globalization.CultureInfo.InvariantCulture);
        var tmp = AudioIo.NormalizeToWav(path, vol);
        try { AudioIo.PlayWavBlocking(tmp, amy.VoiceRenderMatch, cap); }
        finally { try { File.Delete(tmp); } catch { } }
        Console.WriteLine("playwav done");
        return 0;
    }

    /// <summary>sendat <成员名> <文本...> [--chat 会话]：@某人发文本（群聊）。</summary>
    private static int SendAt(string[] args)
    {
        args = WithoutConfig(args);
        var pos = args.Where((a, i) => !a.StartsWith("--") && !(i > 0 && args[i - 1] == "--chat")).ToArray();
        if (pos.Length < 2)
        {
            Console.Error.WriteLine("用法: sendat <成员名> <文本...> [--chat 会话名]");
            return 2;
        }
        var member = pos[0];
        var text = string.Join(' ', pos.Skip(1));
        var chatArg = ArgValue(args, "--chat", "");

        var ui = QqUi.Attach();
        if (chatArg.Length > 0 && ui.Header() != chatArg && !ui.OpenChat(chatArg))
        {
            Console.Error.WriteLine($"切换会话失败: '{chatArg}'，当前='{ui.Header()}'");
            return 1;
        }
        var chat = ui.Header();
        Console.WriteLine($"[sendat] -> '{chat}': @{member} {text}");
        ui.SendAt(member, text);
        Thread.Sleep(1500);
        var ok = ui.Messages().Any(m => m.IsSelf && m.Text.Contains(text[..Math.Min(15, text.Length)]));
        Console.WriteLine(ok ? $"sent ok -> '{chat}'" : "sent UNVERIFIED（回读未见，人工确认）");
        return 0;
    }

    /// <summary>recall [--chat 会话] [--ts <文本片段>]：撤销自己最后一条（或含片段的）消息。</summary>
    private static int Recall(string[] args)
    {
        var chatArg = ArgValue(args, "--chat", "");
        var ts = ArgValue(args, "--ts", "");
        var ui = QqUi.Attach();
        if (chatArg.Length > 0 && ui.Header() != chatArg && !ui.OpenChat(chatArg))
        {
            Console.Error.WriteLine($"切换会话失败: '{chatArg}'，当前='{ui.Header()}'");
            return 1;
        }
        var chat = ui.Header();
        var ok = ui.RecallLastOwn(ts);
        Console.WriteLine(ok ? $"recall 触发 -> '{chat}'" : "recall 失败（找不到消息/菜单）");
        if (ok)
        {
            Thread.Sleep(1200);
            var gone = !ui.Messages().Any(m => m.IsSelf && (ts.Length == 0 || m.Text.Contains(ts)));
            Console.WriteLine(gone ? "消息已消失=撤销成功" : "消息仍在=撤销可能失败（权限/超时窗）");
        }
        return ok ? 0 : 1;
    }

    private static int ClickBtn(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("用法: clickbtn <按钮名(精确)>");
            return 2;
        }
        var name = string.Join(' ', args);
        var ui = QqUi.Attach();
        ui.EnsureForeground();
        var ok = ui.ClickButton(name);
        Console.WriteLine(ok ? $"clicked '{name}'" : $"button '{name}' not found");
        return ok ? 0 : 1;
    }

    private static int ClickAt(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out var x) || !int.TryParse(args[1], out var y))
        {
            Console.Error.WriteLine("用法: clickat <x> <y> [--dbl]");
            return 2;
        }
        var dbl = HasFlag(args, "--dbl");
        var ui = QqUi.Attach();
        ui.EnsureForeground();
        ui.ClickPoint(x, y, dbl);
        Console.WriteLine($"{(dbl ? "dbl" : "")}clicked ({x},{y}), header='{ui.Header()}'");
        return 0;
    }

    /// <summary>纯移动鼠标到屏幕坐标（悬停触发 QQNT 图片工具条等浮层），不点击。</summary>
    private static int HoverAt(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out var x) || !int.TryParse(args[1], out var y))
        {
            Console.Error.WriteLine("用法: hoverat <x> <y>");
            return 2;
        }
        var ui = QqUi.Attach();
        ui.EnsureForeground();
        Native.SetCursorPos(x, y);
        Console.WriteLine($"hover ({x},{y}), header='{ui.Header()}'");
        return 0;
    }

    /// <summary>右键点屏幕坐标弹上下文菜单（recall 同款：菜单进树后用 dump/clickbtn 点菜单项）。</summary>
    private static int RClickAt(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out var x) || !int.TryParse(args[1], out var y))
        {
            Console.Error.WriteLine("用法: rclickat <x> <y>");
            return 2;
        }
        var ui = QqUi.Attach();
        ui.EnsureForeground();
        Native.SetCursorPos(x, y);
        Thread.Sleep(150);
        Native.mouse_event(Native.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        Native.mouse_event(Native.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        Console.WriteLine($"rclicked ({x},{y}), header='{ui.Header()}'");
        return 0;
    }

    /// <summary>滚轮。默认消息区中心+主窗前台；--at x,y 滚指定屏幕坐标；--hwnd &lt;句柄&gt;
    /// 先置前指定窗口（转发查看器等独立窗——EnsureForeground 只会拉起主窗，滚轮会打到主窗上，2026-10-26 踩坑）。</summary>
    private static int Wheel(string[] args)
    {
        int notches = 3;
        for (int i = 0; i < args.Length; i++)
            if (int.TryParse(args[i], out var v)) { notches = v; break; }
        var down = HasFlag(args, "--down");
        var atArg = ArgValue(args, "--at", "");
        var hwndArg = ArgValue(args, "--hwnd", "");
        var ui = QqUi.Attach();
        if (hwndArg.Length > 0)
        {
            var s = hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hwndArg[2..] : hwndArg;
            var style = hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer;
            var h = new IntPtr(long.Parse(s, style));
            Native.SetForegroundWindow(h);
            Thread.Sleep(250);
            Console.WriteLine($"[wheel] foreground -> hwnd {h}");
        }
        else ui.EnsureForeground();
        int cx, cy;
        if (atArg.Length > 0)
        {
            var p = atArg.Split(',');
            cx = int.Parse(p[0]); cy = int.Parse(p[1]);
        }
        else
        {
            var r = ui.MessageAreaRect() ?? throw new InvalidOperationException("找不到消息区");
            cx = (r.Left + r.Right) / 2; cy = (r.Top + r.Bottom) / 2;
        }
        Native.SetCursorPos(cx, cy);
        Thread.Sleep(80);
        Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0,
            unchecked((uint)((down ? -1 : 1) * 120 * notches)), UIntPtr.Zero);
        Thread.Sleep(350);
        Console.WriteLine($"wheel {(down ? "down" : "up")} {notches} notches @({cx},{cy}), header='{ui.Header()}'");
        return 0;
    }

    /// <summary>截 QQ 窗口：默认主窗口；--all 含搜索结果等独立弹层窗口。PNG + .json 元数据落 logs\。</summary>
    private static int SnapCmd(string[] args)
    {
        var all = HasFlag(args, "--all");
        var cfgPath = ConfigPath(args);
        var baseDir = File.Exists(cfgPath) ? Path.GetDirectoryName(cfgPath)! : AppContext.BaseDirectory;
        var outDir = ArgValue(args, "--out", "");
        if (outDir.Length == 0) outDir = Path.Combine(baseDir, "logs");

        var ui = QqUi.Attach();   // 顺带完成：最小化时恢复窗口、SetProcessDPIAware
        var targets = QqUi.Windows();
        if (!all) targets = targets.Where(t => t.Hwnd == ui.Hwnd).ToList();

        int saved = 0;
        foreach (var (hwnd, rect, title) in targets)
        {
            var shot = Snap.Capture(hwnd, rect, title, outDir);
            if (shot is null) continue;
            saved++;
            Console.WriteLine($"saved {shot.Path}");
            Console.WriteLine($"  title='{title}' hwnd=0x{hwnd.ToInt64():X} rect=({rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top})");
            Console.WriteLine($"  图内坐标(x,y) → 屏幕坐标 = ({rect.Left}+x, {rect.Top}+y)");
        }
        Console.WriteLine(saved > 0 ? $"{saved} window(s) captured" : "no window captured（QQ 窗口不可见？）");
        return saved > 0 ? 0 : 1;
    }

    /// <summary>视觉定位：snap → 视觉模型 grounding → 打印屏幕坐标；--click 则点击（UIA 读不到的界面用）。</summary>
    private static int See(string[] args)
    {
        if (args.Length < 1 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("用法: see \"<目标描述>\" [--snap 图路径] [--click] [--config p]");
            return 2;
        }
        var target = args[0];
        var click = HasFlag(args, "--click");
        var snapArg = ArgValue(args, "--snap", "");

        var cfg = AppConfig.Load(ConfigPath(args));
        var vision = new VisionClient(cfg.Vision);
        vision.EnsureReady();

        var ui = QqUi.Attach();
        Snap.Shot shot;
        if (snapArg.Length > 0)
        {
            var loaded = Snap.Load(snapArg);
            if (loaded is null)
            {
                Console.Error.WriteLine($"截图或元数据(.json)缺失: {snapArg}（--snap 假定窗口未移动，换新图请直接 see 不带 --snap）");
                return 2;
            }
            shot = loaded;
        }
        else
        {
            var wins = QqUi.Windows();
            var (hwnd, rect, title) = wins.FirstOrDefault(t => t.Hwnd == ui.Hwnd);
            var baseDir = AppConfig.BaseDir;
            var fresh = Snap.Capture(hwnd, rect, title, Path.Combine(baseDir, "logs"));
            if (fresh is null)
            {
                Console.Error.WriteLine("截图失败（窗口不可见？）");
                return 1;
            }
            shot = fresh;
            Console.WriteLine($"snap -> {shot.Path}");
        }

        var box = vision.Locate(shot.Path, target);
        if (box is null)
        {
            Console.Error.WriteLine($"模型未返回可解析的坐标框（目标='{target}'）");
            return 3;
        }

        int w = shot.Rect.Right - shot.Rect.Left, h = shot.Rect.Bottom - shot.Rect.Top;
        int ax = shot.Rect.Left + (int)Math.Round((box[0] + box[2]) / 2 / 1000.0 * w);
        int ay = shot.Rect.Top + (int)Math.Round((box[1] + box[3]) / 2 / 1000.0 * h);
        Console.WriteLine($"bbox(0-1000)=[{box[0]},{box[1]},{box[2]},{box[3]}] → 屏幕=({ax},{ay})  (图 {w}x{h} + 窗口左上 {shot.Rect.Left},{shot.Rect.Top})");

        if (click)
        {
            ui.ClickPoint(ax, ay);
            Console.WriteLine($"clicked ({ax},{ay})");
        }
        return 0;
    }

    private static int Daemon(string[] args)
    {
        var cfg = AppConfig.Load(ConfigPath(args));
        var chat = ArgValue(args, "--chat", cfg.Daemon.Chat);
        if (!string.IsNullOrWhiteSpace(chat)) cfg.Daemon.Chat = chat;
        if (HasFlag(args, "--dump-prompt")) cfg.Daemon.DumpPrompt = true;

        var ui = QqUi.Attach();
        ui.EnsureForeground();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var daemon = new Daemon(cfg, ui);
        daemon.Run(cts.Token);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static uint PidOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }
}






