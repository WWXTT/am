using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Forms;

namespace QqAgent;

internal sealed record ChatItem(string Title, string Preview, string Time, bool Selected, bool Unread, AutomationElement Element);

    internal sealed record MessageItem(string Time, string Sender, string Text, bool IsSelf, double Y, bool IsForward = false, AutomationElement? Element = null, string QuoteFrom = "", string QuoteText = "", int PicW = 0, int PicH = 0);

/// <summary>转发卡片（UIA 里 cls=forward-msg）：标题 + 预览行 + 总条数（来自「查看N条转发消息」）。</summary>
internal sealed record ForwardCard(string Title, IReadOnlyList<string> PreviewLines, int Count, AutomationElement Element, double Y = 0);

/// <summary>转发查看器里读出的一条消息。Y=容器顶部坐标，用于乱序合并后恢复原顺序。</summary>
internal sealed record ForwardMessage(string Time, string Sender, string Text, bool IsSelf, double Y = 0);

internal sealed record ForwardContent(string Title, IReadOnlyList<ForwardMessage> Messages, int ExpectedCount);

internal sealed class QqUi
{
    private static readonly Regex TimeRx = new(@"^\d{1,2}:\d{2}$|^\d{2}/\d{2}$|^(昨天|前天)", RegexOptions.Compiled);
    // QQNT 会话条目未读徽标：ClassName 为空、Name 形如「1条未读」；旧版才是 q-badge 类名（2026-10-25）
    private static readonly Regex UnreadRx = new(@"^\d+\s*条未读$", RegexOptions.Compiled);
    private static readonly Regex FwdCountRx = new(@"查看(\d+)条转发消息", RegexOptions.Compiled);
    private static readonly HashSet<uint> Pids = new();

    private IntPtr _hwnd;
    private AutomationElement _root;

    private QqUi(IntPtr hwnd, AutomationElement root)
    {
        _hwnd = hwnd;
        _root = root;
    }

    public IntPtr Hwnd => _hwnd;
    public AutomationElement Root => _root;

    public static List<(IntPtr Hwnd, Native.RECT Rect, string Title)> Windows()
    {
        Pids.Clear();
        foreach (var p in Process.GetProcessesByName("QQ")) Pids.Add((uint)p.Id);
        return Native.TopLevelWindows(Pids);
    }

    private static bool IsBig(Native.RECT r) => r.Right - r.Left >= 400 && r.Bottom - r.Top >= 300;

    public static QqUi Attach()
    {
        Native.SetProcessDPIAware();

        var wins = Windows();
        if (wins.Count == 0)
            throw new InvalidOperationException(
                $"找不到 QQ 窗口：QQ 是否在运行？（QQ 进程 {Pids.Count} 个，Chrome_WidgetWin_1 顶层窗 0 个）");

        // 主窗口通常标题为 QQ；托盘/最小化时窗口会被移到屏幕外，需要先恢复再量尺寸
        var ordered = wins
            .OrderByDescending(w => w.Title == "QQ")
            .ThenByDescending(w => (long)(w.Rect.Right - w.Rect.Left) * (w.Rect.Bottom - w.Rect.Top))
            .ToList();

        AutomationElement? fallback = null;
        IntPtr fallbackHwnd = IntPtr.Zero;
        AutomationElement? msgOnly = null;
        IntPtr msgOnlyHwnd = IntPtr.Zero;

        foreach (var win in ordered)
        {
            var hwnd = win.Hwnd;
            var rect = win.Rect;
            if (!IsBig(rect))
            {
                Native.ShowWindow(hwnd, 9);   // SW_RESTORE
                Thread.Sleep(250);
                Native.GetWindowRect(hwnd, out rect);
                if (!IsBig(rect)) continue;
            }

            AutomationElement el;
            try { el = AutomationElement.FromHandle(hwnd); } catch { continue; }

            // 主窗=会话列表+消息区同窗；双击弹出的独立聊天窗只有消息区。
            // 必须优先绑主窗，否则 Header/Messages 读一处、OpenChat 点另一处（2026-10-25 事故）
            var hasChatList = FindByName(el, "会话列表") is not null;
            var hasMsgList = FindByName(el, "消息列表") is not null;
            if (hasChatList && hasMsgList)
                return new QqUi(hwnd, el);

            if (msgOnly is null && hasMsgList)
            {
                msgOnly = el;
                msgOnlyHwnd = hwnd;
            }

            if (fallback is null && hasChatList)
            {
                fallback = el;
                fallbackHwnd = hwnd;
            }
        }

        if (msgOnly is not null)
            return new QqUi(msgOnlyHwnd, msgOnly);
        if (fallback is not null)
            return new QqUi(fallbackHwnd, fallback);

        throw new InvalidOperationException(
            "QQ 窗口里既没有「会话列表」也没有「消息列表」。请用 --force-renderer-accessibility 启动 QQ。");
    }

    public static AutomationElement? FindByName(AutomationElement scope, string name)
    {
        var cond = new PropertyCondition(AutomationElement.NameProperty, name);
        return scope.FindFirst(TreeScope.Descendants, cond);
    }

    public static AutomationElement? FindByClass(AutomationElement scope, string className)
    {
        var cond = new PropertyCondition(AutomationElement.ClassNameProperty, className);
        return scope.FindFirst(TreeScope.Descendants, cond);
    }

    public AutomationElement? MessageList() => FindByName(_root, "消息列表") ?? FindInAnyWindow("消息列表");

    /// <summary>窗口归位公用方法：把任意顶层窗口设到指定位置/大小（DPI-aware，不动 Z 序不抢前台）。
    /// 一切依赖屏幕坐标的流程（双击开大图、点 OCR 按钮）执行前必须先归位，否则原点漂移全链失效
    /// （2026-10-26 定案）。w/h 传 0 表示只移动不改尺寸。</summary>
    public static bool Place(IntPtr hwnd, int x, int y, int w, int h)
    {
        Native.SetProcessDPIAware();
        uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        if (w <= 0 || h <= 0) flags |= Native.SWP_NOSIZE;
        return Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, flags);
    }

    /// <summary>2026-10-27：消息区整个落在屏幕外时，Chromium 不会继续渲染新消息（虚拟列表不追加），
    /// daemon 就"一条也读不到"，等用户滚动/点击才一次性补上（实测：窗口停在 3348,1474，
    /// 消息区 3820..4622 超出 3840 屏宽）。
    /// 只要求露一条缝（默认 100x200）就够渲染 —— QQ 可以几乎整窗移出屏幕当隐藏用。只平移，不改尺寸、不激活。</summary>
    public bool EnsureChatAreaVisible(int minVisibleX = 100, int minVisibleY = 200)
    {
        try
        {
            var area = MessageAreaRect();
            if (area is null) return false;
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) return false;
            int sRight = vx + vw, sBottom = vy + vh;
            int overlapX = Math.Min(area.Value.Right, sRight) - Math.Max(area.Value.Left, vx);
            int overlapY = Math.Min(area.Value.Bottom, sBottom) - Math.Max(area.Value.Top, vy);
            if (overlapX >= minVisibleX && overlapY >= minVisibleY) return false;
            int dx = 0, dy = 0;
            if (overlapX < minVisibleX) dx = (sRight - minVisibleX) - area.Value.Right;
            if (overlapY < minVisibleY) dy = (sBottom - minVisibleY) - area.Value.Bottom;
            if (dx == 0 && dy == 0) return false;
            if (!Native.GetWindowRect(_hwnd, out var wr)) return false;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, wr.Left + dx, wr.Top + dy, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Console.WriteLine($"[clamp] 消息区露得不够（{overlapX}x{overlapY}）→ 平移 ({dx},{dy})");
            return true;
        }
        catch { return false; }
    }

    /// <summary>把消息列表滚到最底（最新在底部）。读消息前调用：用户手动往上翻过之后，
    /// 新消息不会进 UIA 树，daemon 会误以为"没有新消息"。</summary>
    public bool ScrollMessagesToBottom()
    {
        var ml = MessageList();
        if (ml is null) return false;
        try
        {
            if (ml.TryGetCurrentPattern(System.Windows.Automation.ScrollPattern.Pattern, out var pat)
                && pat is System.Windows.Automation.ScrollPattern sp)
            {
                if (sp.Current.VerticallyScrollable && sp.Current.VerticalScrollPercent < 99)
                {
                    sp.SetScrollPercent(System.Windows.Automation.ScrollPattern.NoScroll, 100);
                    Thread.Sleep(250);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>消息列表区域矩形（wheel 命令定位滚动锚点用）。</summary>
    public Native.RECT? MessageAreaRect()
    {
        var ml = MessageList();
        if (ml is null) return null;
        try
        {
            var r = ml.Current.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return null;
            return new Native.RECT { Left = (int)r.X, Top = (int)r.Y, Right = (int)(r.X + r.Width), Bottom = (int)(r.Y + r.Height) };
        }
        catch { return null; }
    }

    private AutomationElement? FindInAnyWindow(string name)
    {
        foreach (var (hwnd, _, _) in Windows())
        {
            try
            {
                var el = AutomationElement.FromHandle(hwnd);
                var hit = FindByName(el, name);
                if (hit is null) continue;
                _hwnd = hwnd;
                _root = el;
                return hit;
            }
            catch { }
        }
        return null;
    }

    /// <summary>当前会话是不是群：群聊有「群公告 / 群聊成员」面板，私聊没有。</summary>
    public bool IsGroup()
    {
        foreach (var name in new[] { "群公告", "群聊成员" })
        {
            if (FindByName(_root, name) is not null) return true;
        }
        return false;
    }

    public string Header()
    {
        for (int i = 0; i < 8; i++)
        {
            var el = FindByClass(_root, "chat-header__contact-name");
            var name = el?.Current.Name ?? "";
            if (!string.IsNullOrEmpty(name)) return name;
            var other = FindInAnyWindow("消息列表");
            if (other is not null)
            {
                el = FindByClass(_root, "chat-header__contact-name");
                name = el?.Current.Name ?? "";
                if (!string.IsNullOrEmpty(name)) return name;
            }
            Thread.Sleep(120);
        }
        return "";
    }

    /// <summary>操作前锁定：临时置顶 + 确保在前台（**不动窗口位置/大小**）。
    /// 2026-10-28 修「发表情/读图时窗口漂到主屏」：旧版无条件按 PrimaryScreen 把窗口摆到右下角，
    /// 本机三屏（11520x2160，右屏 x≈9864）上等于每次操作都把 QQ 拽回主屏；现在只在窗口跑出可视区时才夹回
    /// 它自己那块屏，且保持原尺寸。</summary>
    public bool PinForOperation()
    {
        try
        {
            if (!OnCurrentDesktop()) return false;
            if (Native.IsIconic(_hwnd)) Native.ShowWindow(_hwnd, 9);
            Native.ShowWindow(_hwnd, 5);
            var scr = System.Windows.Forms.Screen.FromHandle(_hwnd) ?? System.Windows.Forms.Screen.PrimaryScreen!;
            var wa = scr.WorkingArea;
            if (Native.GetWindowRect(_hwnd, out var r))
            {
                var w = r.Right - r.Left;
                var hgt = r.Bottom - r.Top;
                var visW = Math.Min(r.Right, wa.Right) - Math.Max(r.Left, wa.Left);
                var visH = Math.Min(r.Bottom, wa.Bottom) - Math.Max(r.Top, wa.Top);
                var onScreen = w > 200 && hgt > 200 && visW >= w / 2 && visH >= hgt / 2;
                if (onScreen)
                {
                    Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
                }
                else
                {
                    var nx = Math.Min(Math.Max(r.Left, wa.Left), Math.Max(wa.Left, wa.Right - w));
                    var ny = Math.Min(Math.Max(r.Top, wa.Top), Math.Max(wa.Top, wa.Bottom - hgt));
                    Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, nx, ny, w, hgt, Native.SWP_SHOWWINDOW);
                    Console.WriteLine($"[pin] 窗口出界，已夹回 ({nx},{ny} {w}x{hgt})");
                }
            }
            // 前台锁下 SetForegroundWindow 常失败：用 AttachThreadInput+Alt 抖动强制；仍失败也继续
            // （窗口已置顶，第一次物理点击会把它激活），但不静默假装成功。
            var forced = Native.ForceForeground(_hwnd);
            Thread.Sleep(400);
            if (!forced || Native.GetForegroundWindow() != _hwnd)
                Console.WriteLine("[pin] warn: 无法确认 QQ 已在前台（已置顶，首次点击会激活）");
            return true;
        }
        catch { return false; }
    }

    /// <summary>操作结束取消临时置顶（不改变位置/大小）。</summary>
    public void UnpinAfterOperation()
    {
        try
        {
            Native.SetWindowPos(_hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch { }
    }
    /// <summary>2026-10-27：窗口挪到屏幕外/长时间不重绘时，Chromium 会降频（甚至不派发 UIA 事件），
    /// 新消息就读不到——表现是"用户不点 QQ 哨兵就不投递"。定期强制重绘一次把渲染叫醒。
    /// 只发 WM_PAINT 级别的重绘，不抢前台、不动焦点、不改 z 序。</summary>
    public void NudgeRepaint()
    {
        try
        {
            Native.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero,
                Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
        }
        catch { }
    }

    /// <summary>把窗口夹回虚拟屏内（至少露出 keepVisible 像素），避免整窗在屏幕外导致不渲染。
    /// 只移动、不改尺寸、不激活。</summary>
    public bool ClampOnScreen(int keepVisible = 120)
    {
        try
        {
            if (!Native.GetWindowRect(_hwnd, out var r)) return false;
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) return false;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            int x = r.Left, y = r.Top;
            int minX = vx - w + keepVisible, maxX = vx + vw - keepVisible;
            int minY = vy - h + keepVisible, maxY = vy + vh - keepVisible;
            x = Math.Max(minX, Math.Min(maxX, x));
            y = Math.Max(minY, Math.Min(maxY, y));
            if (x == r.Left && y == r.Top) return false;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Console.WriteLine($"[clamp] window ({r.Left},{r.Top}) -> ({x},{y})，保证屏内可见 {keepVisible}px");
            return true;
        }
        catch { return false; }
    }

    public void EnsureForeground()
    {
        if (Native.IsIconic(_hwnd)) Native.ShowWindow(_hwnd, 9);
        Native.ShowWindow(_hwnd, 5);
        Native.SetForegroundWindow(_hwnd);
        Thread.Sleep(400);
    }

    /// <summary>QQ 主窗是否在当前虚拟桌面。不在时一切物理点击/置前都必须禁止——
    /// 坐标会落到用户当前桌面的其他窗口上（2026-10-25 虚拟桌面隔离后引入的约束）。</summary>
    public bool OnCurrentDesktop()
    {
        try { return Vd.WindowOnCurrentDesktop(_hwnd); }
        catch { return true; }   // VD API 不可用（如 Win10 旧版）时按无隔离处理
    }

    public void RequireForeground()
    {
        if (!OnCurrentDesktop())
            throw new InvalidOperationException("QQ 在其他虚拟桌面，禁止置前/物理点击（会误点用户桌面）");
        for (int i = 0; i < 4; i++)
        {
            if (Native.ForceForeground(_hwnd)) return;
            Thread.Sleep(200);
        }
        throw new InvalidOperationException("QQ 未能置前，中止发送（防止误发到其他窗口）");
    }

    /// <summary>轮到指定标题的窗口（独立聊天窗口也会被正确识别）。</summary>
    public bool Locate(string title, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            foreach (var (hwnd, _, _) in Windows())
            {
                try
                {
                    var el = AutomationElement.FromHandle(hwnd);
                    if (FindByName(el, "消息列表") is null) continue;
                    var hdr = FindByClass(el, "chat-header__contact-name");
                    if (hdr is null || hdr.Current.Name != title) continue;
                    _hwnd = hwnd;
                    _root = el;
                    return true;
                }
                catch { }
            }
            Thread.Sleep(120);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    public IReadOnlyList<ChatItem> Chats()
    {
        var list = FindInAnyWindow("会话列表") is not null
            ? FindByName(_root, "会话列表")
            : null;
        if (list is null) return Array.Empty<ChatItem>();

        var result = new List<ChatItem>();
        foreach (AutomationElement item in list.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            var cls = item.Current.ClassName;
            if (!cls.Contains("recent-contact-item")) continue;

            var texts = Texts(item).ToList();
            string title = texts.FirstOrDefault() ?? "";
            string time = texts.FirstOrDefault(t => TimeRx.IsMatch(t)) ?? "";
            string preview = texts.Where(t => t != title && t != time).FirstOrDefault() ?? "";
            bool selected = cls.Contains("--selected");

            bool unread = false;
            foreach (AutomationElement d in item.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var badgeName = d.Current.Name ?? "";
                if ((d.Current.ClassName.Contains("q-badge") && !string.IsNullOrWhiteSpace(badgeName))
                    || UnreadRx.IsMatch(badgeName))
                {
                    unread = true;
                    break;
                }
            }

            result.Add(new ChatItem(title, preview, time, selected, unread, item));
        }
        return result;
    }

    private static IEnumerable<string> Texts(AutomationElement scope)
    {
        var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        foreach (AutomationElement t in scope.FindAll(TreeScope.Descendants, cond))
        {
            var n = t.Current.Name;
            if (!string.IsNullOrWhiteSpace(n)) yield return n;
        }
    }

    /// <summary>消息区里全部 pic-element（图片消息元素），按视觉位置从上到下排序。
    /// openimg 用它定位要打开的图（Invoke 开查看器）。</summary>
    public IReadOnlyList<AutomationElement> PicElements()
    {
        var ml = MessageList();
        if (ml is null) return Array.Empty<AutomationElement>();
        var found = new List<(AutomationElement El, double Top)>();
        foreach (AutomationElement el in ml.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string cls;
            try { cls = el.Current.ClassName ?? ""; } catch { continue; }
            if (!cls.Contains("pic-element")) continue;
            double top = 0;
            try { top = el.Current.BoundingRectangle.Top; } catch { }
            found.Add((el, top));
        }
        return found.OrderBy(t => t.Top).Select(t => t.El).ToList();
    }

    public IReadOnlyList<MessageItem> Messages()
    {
        var ml = MessageList();
        if (ml is null) return Array.Empty<MessageItem>();

        var all = ml.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var list = new List<MessageItem>();
        string pendingTime = "";

        foreach (AutomationElement el in all)
        {
            string cls;
            try { cls = el.Current.ClassName; } catch { continue; }

            if (cls.Contains("message__timestamp"))
            {
                pendingTime = Texts(el).FirstOrDefault() ?? pendingTime;
                continue;
            }

            if (!cls.Contains("message-container")) continue;
            if (cls.Contains("msg-content-container")) continue;
            // 系统提示（入群/违规/撤回等）不算发言人
            if (cls.Contains("--sys") || cls.Contains("system")) continue;

            bool isSelf = cls.Contains("--self");

            string sender = "";
            bool isForward = false;
            string quoteFrom = "";
            string quoteText = "";
            StringBuilder sb = new();

            foreach (AutomationElement d in el.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string dcls;
                try { dcls = d.Current.ClassName; } catch { continue; }
                if (sender.Length == 0 && dcls.Equals("avatar-span", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(d.Current.Name))
                    sender = d.Current.Name;

                // 引用（回复某条消息）：作者 + 被引正文单独钉住，供现场包传「引用标识」。
                // 正文 Text 维持原样（引用文字仍会被 msg-content-container 的 Texts 收进去），
                // 避免改格式导致去重签名变化、把已有消息重投一遍（2026-10-27 教训）。
                if (dcls.Contains("reply-element"))
                {
                    if (quoteFrom.Length == 0)
                    {
                        var rt = Texts(d).ToList();
                        if (rt.Count > 0)
                        {
                            quoteFrom = rt[0];
                            quoteText = string.Join(" ", rt.Skip(1)).Trim();
                        }
                    }
                    continue;
                }

                // 转发消息卡片不在 msg-content-container 里，单独按卡片摘要读
                if (dcls.Contains("forward-msg"))
                {
                    isForward = true;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(FormatForwardSummary(Texts(d).ToList()));
                    continue;
                }

                if (!dcls.Contains("msg-content-container")) continue;
                foreach (var t in Texts(d))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(t);
                }
            }

            double y = 0;
            try { y = el.Current.BoundingRectangle.Y; } catch { }

            // 气泡里那张图的渲染尺寸：给 readimg 做宽高闸，防止按时间最近抓到隔壁消息的图
            int pw = 0, ph = 0;
            var hasPic = HasPic(el);
            if (hasPic)
            {
                foreach (AutomationElement p in el.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string pcls;
                    try { pcls = p.Current.ClassName; } catch { continue; }
                    if (!pcls.Contains("pic-element")) continue;
                    var r = p.Current.BoundingRectangle;
                    if (r.Width > 0 && r.Height > 0) { pw = (int)r.Width; ph = (int)r.Height; break; }
                }
            }

            // 2026-10-28 阶段2：文件气泡的 UIA Text 为空且无 pic-element，旧逻辑这里合成 ""，daemon 在
            // IsNullOrWhiteSpace 处整条丢弃（12:49:57 優鬱的 xls 全程无记录）。改为保守合成 [文件]/[文件] 文件名，
            // 之后走与文本消息完全相同的闸（时间桶/去重/priming/白名单）。
            string text;
            if (sb.Length > 0) text = sb.ToString();
            else if (hasPic) text = "[图片]";
            else text = SynthesizeFileText(el, sender);
            list.Add(new MessageItem(pendingTime, sender, text, isSelf, y, isForward, el, quoteFrom, quoteText, pw, ph));
            pendingTime = "";
        }
        return list;
    }

    // ---- 2026-10-28 阶段2：文件气泡（空正文）保守识别 + file-probe 探针 ----
    // 文件气泡的文件名不在 msg-content-container 的 Text 里（实测整条 UIA Text 为空被丢弃），真实内部
    // 结构未知：先用「class 词根含 file/attach/download」保守判定，能读到以扩展名结尾的真实文本节点才带
    // 文件名（拿不到就只给 [文件]，绝不臆造）；每个空正文气泡留一条探针行供下一轮按真实结构细化。

    /// <summary>文件名候选：以「点+1~10 位字母数字扩展名」结尾（204.8 KB / 已下载 这类文本不会命中）。</summary>
    private static readonly Regex FileExtRx = new(@"\.[A-Za-z][A-Za-z0-9]{1,9}$", RegexOptions.Compiled);

    private readonly List<string> _fileProbes = new();
    private readonly HashSet<string> _fileProbeShapes = new();
    /// <summary>探针按结构形状去重后仍封顶，防长会话/名字多变时刷屏（封顶后静默，不另告警）。</summary>
    private const int FileProbeCap = 40;

    /// <summary>取走并清空本轮解析攒下的 file-probe 行（daemon 写 agent.log；CLI 路径不取，自然丢弃）。</summary>
    public IReadOnlyList<string> DrainFileProbes()
    {
        if (_fileProbes.Count == 0) return Array.Empty<string>();
        var lines = _fileProbes.ToArray();
        _fileProbes.Clear();
        return lines;
    }

    /// <summary>class 串（QQNT 常为多 class 空格拼接）按 - _ . 空格 切词后含 file/attach/download 词根。
    /// token 级匹配，避免 "profile"（含 file 子串）这类误命中。</summary>
    private static bool FileClassHit(string cls)
    {
        foreach (var tok in cls.ToLowerInvariant().Split('-', '_', '.', ' '))
        {
            if (tok == "file" || tok == "files" || tok.StartsWith("attach", StringComparison.Ordinal)
                || tok.StartsWith("download", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>空正文气泡 → 文件合成文本；不是文件气泡返回 ""（维持原丢弃行为）。
    /// 文件名只认真实存在的扩展名结尾文本节点（先命中元素内找、再全气泡找，跳过昵称），拿不到就只给 [文件]。</summary>
    private string SynthesizeFileText(AutomationElement el, string sender)
    {
        AutomationElement? hit = null;
        string hitCls = "";
        foreach (AutomationElement d in el.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string dcls;
            try { dcls = d.Current.ClassName ?? ""; } catch { continue; }
            if (!FileClassHit(dcls)) continue;
            hit = d;
            hitCls = dcls;
            break;
        }

        string name = "";
        if (hit is not null)
        {
            foreach (var t in Texts(hit).Concat(Texts(el)))
            {
                if (string.IsNullOrWhiteSpace(t) || t == sender || !FileExtRx.IsMatch(t)) continue;
                name = t.Trim();
                break;
            }
        }

        // 探针：dump 气泡前 5 个直接子节点的 class/name（命中 class、文件名命中与否一并记），
        // 同形状只记一次、封顶 FileProbeCap。no-class 行用于发现「真实文件 class 不含这些词根」的漏判。
        var kids = new List<string>();
        try
        {
            foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                if (kids.Count >= 5) break;
                string ccls, cnm;
                try { ccls = c.Current.ClassName ?? ""; cnm = c.Current.Name ?? ""; }
                catch { continue; }
                if (cnm.Length > 40) cnm = cnm[..40];
                kids.Add($"cls='{ccls}'/name='{cnm}'");
            }
        }
        catch { }
        var probe = hit is null
            ? $"file-probe: no-class（空正文无图、未命中文件词根，维持丢弃） name='' kids=[{string.Join(", ", kids)}]"
            : $"file-probe: class='{hitCls}' name='{(name.Length > 40 ? name[..40] : name)}' kids=[{string.Join(", ", kids)}]";
        if (_fileProbeShapes.Count < FileProbeCap && _fileProbeShapes.Add(probe))
            _fileProbes.Add(probe);

        if (hit is null) return "";
        return name.Length > 0 ? $"[文件] {name}" : "[文件]";
    }

    // ===== 转发消息（合并转发）阅读 =====

    /// <summary>转发卡片文本 → 摘要：[聊天记录|标题|共N条] + 预览行（卡片 UIA 树自带，不用点开）。</summary>
    private static string FormatForwardSummary(List<string> texts)
    {
        string title = texts.FirstOrDefault() ?? "";
        int count = 0;
        foreach (var t in texts)
        {
            var m = FwdCountRx.Match(t);
            if (m.Success) { count = int.Parse(m.Groups[1].Value); break; }
        }
        var lines = texts.Where(t => t != title && !FwdCountRx.IsMatch(t)).ToList();
        var head = $"[聊天记录|{title}|共{(count > 0 ? count.ToString() : "?")}条]";
        return lines.Count == 0 ? head : $"{head} {string.Join(" / ", lines)}";
    }

    /// <summary>当前会话可见的转发卡片（按出现顺序）。主消息列表是虚拟列表，只有视口内的卡片在树里。</summary>
    public IReadOnlyList<ForwardCard> ForwardCards()
    {
        var result = new List<ForwardCard>();
        var ml = MessageList();
        if (ml is null) return result;

        foreach (AutomationElement el in ml.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ClassNameProperty, "forward-msg")))
        {
            var texts = Texts(el).ToList();
            string title = texts.FirstOrDefault() ?? "";
            int count = 0;
            foreach (var t in texts)
            {
                var m = FwdCountRx.Match(t);
                if (m.Success) { count = int.Parse(m.Groups[1].Value); break; }
            }
            var lines = texts.Where(t => t != title && !FwdCountRx.IsMatch(t)).ToList();
            double y = 0;
            try { y = el.Current.BoundingRectangle.Y; } catch { }
            result.Add(new ForwardCard(title, lines, count, el, y));
        }
        return result.OrderBy(c => c.Y).ToList();
    }

    /// <summary>类名包含匹配（QQNT 的 ClassName 常为多 class 拼接，PropertyCondition 精确匹配不上）。</summary>
    public static AutomationElement? FindByClassContains(AutomationElement scope, string part)
    {
        foreach (AutomationElement el in scope.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try { if ((el.Current.ClassName ?? "").Contains(part)) return el; }
            catch { }
        }
        return null;
    }

    /// <summary>读取第 indexFromEnd 条转发消息（1=最新一条）：点卡片 → 等查看器窗口 → 读全部 → 关窗。
    /// 会短暂抢占前台并移动鼠标；只应在没有并发发送动作时调用。</summary>
    public ForwardContent ReadForward(int indexFromEnd = 1, bool keepOpen = false, bool instant = false)
    {
        var cards = ForwardCards();
        if (cards.Count == 0)
            throw new InvalidOperationException("当前会话没有可见的转发消息卡片（旧消息可能需先滚到可视区）");
        if (indexFromEnd < 1 || indexFromEnd > cards.Count)
            throw new InvalidOperationException($"转发卡片序号越界：{indexFromEnd}（当前可见 1..{cards.Count}）");
        var card = cards[^indexFromEnd];

        // 查看器 = QQ 顶层窗口（标题≠'QQ'、内含 message-container、无「消息列表」）。
        // 主列表是虚拟列表且用户可能正在操作窗口：每次尝试都重新定位卡片再点，
        // 检测优先新出现的 hwnd，QQ 复用旧 hwnd 时退回标题匹配
        AutomationElement? viewer = null;
        IntPtr viewerHwnd = IntPtr.Zero;
        Native.RECT viewerRect = default;
        string viewerTitle = card.Title;

        for (int attempt = 0; attempt < 2 && viewer is null; attempt++)
        {
            if (attempt > 0) Thread.Sleep(500);

            var cardsNow = ForwardCards();
            if (cardsNow.Count == 0 || cardsNow.Count < indexFromEnd)
                throw new InvalidOperationException($"转发卡片不见了（当前可见 {cardsNow.Count} 张）");
            card = cardsNow[^indexFromEnd];
            viewerTitle = card.Title;

            var before = Windows().Select(w => w.Hwnd).ToHashSet();

            var r = card.Element.Current.BoundingRectangle;
            // 卡片在虚拟列表的树里但矩形可能失真/在可视区外：滚进视口拿到新鲜矩形
            var wr = RectOf(_hwnd) ?? new Native.RECT();
            if (r.Width <= 0 || r.Height <= 0 || r.Y + r.Height > wr.Bottom - 40 || r.Y < wr.Top + 40)
            {
                Console.WriteLine($"[fwd] card out of viewport (y={r.Y:F0}, win {wr.Top}..{wr.Bottom}); scrolling");
                var scrolled = ScrollCardIntoView(indexFromEnd);
                if (scrolled is not null)
                {
                    card = scrolled;
                    r = card.Element.Current.BoundingRectangle;
                }
                else
                    Console.WriteLine("[fwd] scroll unavailable; trying Invoke anyway");
            }
            if (r.Width <= 0 || r.Height <= 0)
                throw new InvalidOperationException("转发卡片矩形为空（不可见）");

            // 只走 Invoke：零坐标、零前台、零鼠标（QQ 在别的虚拟桌面时物理点击不可能，
            // 且物理点击在窗口被移动/缩放后屡次落空——2026-10-25 实测）
            try
            {
                if (card.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var ip) &&
                    ip is InvokePattern inv)
                {
                    inv.Invoke();
                    Console.WriteLine("[fwd] card invoked");
                }
                else
                    throw new InvalidOperationException("转发卡片不支持 InvokePattern（重开会话或重启 QQ 试试）");
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Invoke 转发卡片失败: " + ex.Message);
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (viewer is null && DateTime.UtcNow < deadline)
            {
                foreach (var (hwnd, rect, title) in Windows())
                {
                    if (title == "QQ" || !IsBig(rect)) continue;
                    if (before.Contains(hwnd) && !(card.Title.Length > 0 && title == card.Title)) continue;
                    AutomationElement rootEl;
                    try { rootEl = AutomationElement.FromHandle(hwnd); } catch { continue; }
                    if (FindByName(rootEl, "消息列表") is not null) continue;   // 主窗口特征，不认
                    if (FindByClassContains(rootEl, "message-container") is null) continue;
                    viewer = rootEl;
                    viewerHwnd = hwnd;
                    viewerRect = rect;
                    if (title.Length > 0) viewerTitle = title;
                    break;
                }
                if (viewer is null) Thread.Sleep(200);
            }
        }
        if (viewer is null)
            throw new InvalidOperationException(
                "转发查看器 20s 内未出现：点击未生效（窗口被移动？），或该卡片的查看器已被打开（先 closefwd）");

        var scroll = FindByClassContains(viewer, "q-scroll-view") ?? viewer;
        var msgs = ParseViewerMessages(scroll);
        Console.WriteLine($"[fwd] initial: {msgs.Count} msgs, needMore={NeedsMoreScroll(msgs, card.Count)}");

        if (keepOpen && instant)
        {
            Console.WriteLine("[fwd] --instant: 跳过等待，保持打开（供外部静置后一次性读取）");
            return new ForwardContent(viewerTitle, msgs, card.Count);
        }

        // 文本物化规律（2026-10-25 实测定案）：查看器离屏消息的正文要静置数十秒后
        // Chromium 才发布进无障碍树；期间任何轮询查询都会打断发布——只能干等，
        // 等完一次性读（滚轮/拉高/失焦/TreeWalker/密集轮询均无效，全部实测）
        if (NeedsMoreScroll(msgs, card.Count))
        {
            Console.WriteLine("[fwd] idle 75s（零查询，等 Chromium 发布全量文本）...");
            Thread.Sleep(75000);
            scroll = FindByClassContains(viewer, "q-scroll-view") ?? viewer;
            msgs = Union(msgs, ParseViewerMessages(scroll));
            Console.WriteLine($"[fwd] after idle#1: {msgs.Count} msgs, needMore={NeedsMoreScroll(msgs, card.Count)}");

            if (NeedsMoreScroll(msgs, card.Count))
            {
                Console.WriteLine("[fwd] idle 45s more...");
                Thread.Sleep(45000);
                scroll = FindByClassContains(viewer, "q-scroll-view") ?? viewer;
                msgs = Union(msgs, ParseViewerMessages(scroll));
                Console.WriteLine($"[fwd] after idle#2: {msgs.Count} msgs, needMore={NeedsMoreScroll(msgs, card.Count)}");
            }
        }

        // 文本发布后同一消息会出现新旧两个签名（占位符版+真文版）：
        // 同 (Time,Sender) 下已有真文版的，剔除其占位符版；再按容器 Y 恢复原顺序
        var stale = new HashSet<ForwardMessage>();
        foreach (var g in msgs.GroupBy(m => $"{m.Time}|{m.Sender}"))
        {
            var list = g.ToList();
            if (list.Any(m => !IsPlaceholder(m.Text)))
                foreach (var pl in list.Where(m => IsPlaceholder(m.Text)))
                    stale.Add(pl);
        }
        msgs.RemoveAll(m => stale.Contains(m));
        msgs.Sort((a, b) => a.Y.CompareTo(b.Y));

        if (!keepOpen) CloseViewer(viewerHwnd, viewer);
        else Console.WriteLine($"[keep-open] viewer hwnd={viewerHwnd} title='{viewerTitle}'");

        return new ForwardContent(viewerTitle, msgs, card.Count);
    }

    /// <summary>解析转发查看器消息区：message-container(发送人 avatar-span + 正文 msg-content-container)，
    /// 回复消息的 reply-element 与嵌套 forward-msg 单独格式化。</summary>
    internal static List<ForwardMessage> ParseViewerMessages(AutomationElement scope)
    {
        var result = new List<ForwardMessage>();
        string pendingTime = "";

        foreach (AutomationElement el in scope.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string cls, name;
            try { cls = el.Current.ClassName ?? ""; name = el.Current.Name ?? ""; }
            catch { continue; }

            // 时间戳是滚动区里裸露的 Text（cls=''，形如 01:03 / 昨天 20:11）
            if (cls.Length == 0 && name.Length > 0 && TimeRx.IsMatch(name))
            {
                pendingTime = name;
                continue;
            }

            if (!cls.Contains("message-container")) continue;
            if (cls.Contains("--sys") || cls.Contains("system")) continue;
            bool isSelf = cls.Contains("--self");

            string sender = "";
            var body = new StringBuilder();
            var quote = "";

            foreach (AutomationElement d in el.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string dcls;
                try { dcls = d.Current.ClassName ?? ""; } catch { continue; }

                if (sender.Length == 0 && dcls.Equals("avatar-span", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(d.Current.Name))
                    sender = d.Current.Name;

                // 回复引用：首个文本=被引作者，其余=被引正文；置于正文前（与 UI 顺序一致）
                if (dcls.Contains("reply-element"))
                {
                    var rt = Texts(d).ToList();
                    var author = rt.Count > 0 ? rt[0] : "";
                    var qtext = string.Join("", rt.Skip(1)).Trim();
                    quote = $"[回复 {author}: {qtext}]";
                    continue;
                }

                // 嵌套转发卡片：读卡片摘要，不再递归点开
                if (dcls.Contains("forward-msg"))
                {
                    if (body.Length > 0) body.Append('\n');
                    body.Append(FormatForwardSummary(Texts(d).ToList()));
                    continue;
                }

                if (!dcls.Contains("msg-content-container")) continue;
                // 正文按直接子节点读，跳过 reply-element（其文本已单独收集），避免引用混入正文
                foreach (AutomationElement c in d.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    string ccls;
                    try { ccls = c.Current.ClassName ?? ""; } catch { continue; }
                    if (ccls.Contains("reply-element") || ccls.Contains("forward-msg")) continue;
                    if (ccls.Contains("pic-element"))
                    {
                        if (body.Length > 0) body.Append('\n');
                        body.Append("[图片]");
                        continue;
                    }
                    // 取子节点 Name 而非 ControlType==Text 过滤——正文节点不一定是 Text 类型
                    // （2026-10-25 实测：Texts() 过滤会漏掉全部正文，直接取 Name 才读得到）
                    var nm = "";
                    try { nm = c.Current.Name ?? ""; } catch { }
                    if (!string.IsNullOrWhiteSpace(nm))
                    {
                        if (body.Length > 0) body.Append('\n');
                        body.Append(nm);
                        continue;
                    }
                    foreach (var t in Texts(c))
                    {
                        if (body.Length > 0) body.Append('\n');
                        body.Append(t);
                    }
                }
            }

            var text = new StringBuilder();
            if (quote.Length > 0) text.Append(quote).Append('\n');
            if (body.Length > 0) text.Append(body);
            if (text.Length == 0)
                text.Append(HasPic(el) ? "[图片]" : "[非文本消息]");
            double my = 0;
            try { my = el.Current.BoundingRectangle.Top; } catch { }
            result.Add(new ForwardMessage(pendingTime, sender, text.ToString(), isSelf, my));
            pendingTime = "";
        }
        return result;
    }

    private static string Sig(ForwardMessage m) => $"{m.Time}|{m.Sender}|{m.Text}";

    /// <summary>占位符文本：正文未发布时的 [非文本消息] / 只有引用没有正文。</summary>
    private static bool IsPlaceholder(string text) =>
        text == "[非文本消息]" || text.EndsWith("\n");

    /// <summary>还要不要继续读：条数没到卡片标注值；有疑似未发布的空正文；引用后没有正文。</summary>
    internal static bool NeedsMoreScroll(List<ForwardMessage> msgs, int expected)
    {
        if (expected > 0 && msgs.Count < expected) return true;
        return msgs.Any(m => m.Text.Contains("[非文本消息]") || m.Text.EndsWith("\n"));
    }

    /// <summary>按签名去重合并（保序）；已发布过的节点滚走后仍留在树里，但长列表可能被回收，合并不丢。</summary>
    private static List<ForwardMessage> Union(List<ForwardMessage> a, IEnumerable<ForwardMessage> b)
    {
        var seen = a.Select(Sig).ToHashSet();
        var res = new List<ForwardMessage>(a);
        res.AddRange(b.Where(m => seen.Add(Sig(m))));
        return res;
    }

    /// <summary>反复解析直到不再新增消息（文本节点晚于容器发布）。</summary>
    private static List<ForwardMessage> Settle(AutomationElement scroll, List<ForwardMessage> cur, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            Thread.Sleep(450);
            var merged = Union(cur, ParseViewerMessages(scroll));
            if (merged.Count == cur.Count) return merged;
            cur = merged;
        }
        return cur;
    }

    private static Native.RECT? RectOf(IntPtr hwnd) =>
        Native.GetWindowRect(hwnd, out var r) ? r : null;

    /// <summary>把倒数第 indexFromEnd 张转发卡片滚进主窗视口（纯 UIA，零鼠标）。
    /// ScrollItemPattern 是 Chromium 原生实现，不会被 QQ 的「钉到底部」弹回。
    /// 每轮重新枚举（虚拟列表回收条目的矩形会失真）。</summary>
    private ForwardCard? ScrollCardIntoView(int indexFromEnd)
    {
        for (int i = 0; i < 10; i++)
        {
            var wrOpt = RectOf(_hwnd);
            if (wrOpt is null) return null;
            var wr = wrOpt.Value;

            var cards = ForwardCards();
            if (cards.Count < indexFromEnd) return null;
            var card = cards[^indexFromEnd];
            var r = card.Element.Current.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return null;

            bool above = r.Y < wr.Top + 80;
            bool below = r.Y + r.Height > wr.Bottom - 80;
            if (!above && !below) return card;

            try
            {
                if (card.Element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var pat) &&
                    pat is ScrollItemPattern sip)
                {
                    sip.ScrollIntoView();
                    Thread.Sleep(500);
                    continue;   // 下一轮用新鲜矩形复核
                }
            }
            catch { }
            return null;   // 不支持 ScrollItem 就算了（Invoke 不要求卡片在视口内）
        }
        return null;
    }

    /// <summary>TreeWalker 逐节点走树（读屏器式访问），返回节点数；收集所有非空 Name。</summary>
    private static int WalkTree(AutomationElement el, int depth, int maxDepth, HashSet<string> names)
    {
        if (depth > maxDepth) return 0;
        int count = 0;
        AutomationElement? child = null;
        try { child = TreeWalker.RawViewWalker.GetFirstChild(el); } catch { }
        while (child is not null)
        {
            count++;
            try
            {
                var n = child.Current.Name;
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
            catch { }
            count += WalkTree(child, depth + 1, maxDepth, names);
            var next = child;
            child = null;
            try { child = TreeWalker.RawViewWalker.GetNextSibling(next); } catch { }
        }
        return count;
    }

    /// <summary>静默滚轮：WM_MOUSEWHEEL 直接 PostMessage 到目标窗口（cua-driver 同款
    /// pid-scoped event posting 思路）——不动光标、不抢前台、不打扰用户鼠标。</summary>
    private static void PostWheel(IntPtr hwnd, bool down, int notches)
    {
        Native.GetClientRect(hwnd, out var cr);
        int cx = Math.Max(1, (cr.Right - cr.Left) / 2);
        int cy = Math.Max(1, (cr.Bottom - cr.Top) / 2);
        var wp = (IntPtr)((uint)(ushort)(short)(down ? -120 : 120) << 16);
        var lp = (IntPtr)((cy << 16) | (uint)cx);
        for (int i = 0; i < notches; i++)
        {
            Native.PostMessage(hwnd, Native.WM_MOUSEWHEEL, wp, lp);
            Thread.Sleep(110);
        }
    }

    /// <summary>对查看器滚一个来回（下到底再回顶；偶数轮反向），逼 Chromium 把滚过的
    /// 消息行渲染出来（正文文本随之进无障碍树）。</summary>
    private static void WheelPass(IntPtr hwnd, int notches, int round)
    {
        bool downFirst = round % 2 == 1;
        PostWheel(hwnd, downFirst, notches);
        PostWheel(hwnd, !downFirst, notches);
    }

    /// <summary>在窗口矩形中心滚轮（down=false 向上）。</summary>
    private static void WheelScroll(Native.RECT r, int notches, bool down)
    {
        Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        Thread.Sleep(80);
        Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0,
            unchecked((uint)((down ? -1 : 1) * 120 * notches)), UIntPtr.Zero);
        Thread.Sleep(350);
    }

    /// <summary>消息容器里是否有图片（pic-element，图片消息的正文节点）。</summary>
    private static bool HasPic(AutomationElement el) => PicCount(el) > 0;

    /// <summary>消息内图片元素数（多图消息一条消息多个 pic-element）。现场包用它算 ocr_n 与账本校验。</summary>
    public static int PicCount(AutomationElement el)
    {
        var n = 0;
        try
        {
            foreach (AutomationElement d in el.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                try { if ((d.Current.ClassName ?? "").Contains("pic-element")) n++; } catch { }
            }
        }
        catch { }
        return n;
    }

    /// <summary>关查看器：先 WM_CLOSE（不动焦点），没关掉再点窗口右上「关闭」。</summary>
    private static void CloseViewer(IntPtr hwnd, AutomationElement viewer)
    {
        Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(150);
            if (!Native.IsWindowVisible(hwnd)) return;
        }
        try
        {
            var close = FindByName(viewer, "关闭");
            if (close is null) return;
            var r = close.Current.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            Thread.Sleep(120);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(300);
        }
        catch { }
    }

    /// <summary>键盘切会话：原子焦点定向 + Ctrl+Tab 轮换，Header 命中即停。零点击零置前。</summary>
    private bool OpenChatByKeyboard(string title)
    {
        var cur = Header();
        if (cur == title) return true;
        FocusWithoutRaiseAtomic();
        for (int i = 0; i < 12; i++)
        {
            SendKey(0x11, down: true);
            SendKey(0x09, down: true);
            Thread.Sleep(40);
            SendKey(0x09, down: false);
            SendKey(0x11, down: false);
            Thread.Sleep(800);
            var h = Header();
            if (h == title)
            {
                Console.WriteLine($"[openchat] keyboard-switched to '{title}'");
                return true;
            }
        }
        return false;
    }

    public bool OpenChat(string title)
    {
        // 静默优先序：键盘 Ctrl+Tab 轮换 → SelectionItem → 物理点击（仅同桌面）
        if (OpenChatByKeyboard(title)) return true;

        // 优先 SelectionItemPattern（纯 UIA、零点击）；不支持或没切过去才物理点击
        // （聊天条目的 Invoke 会触发独立聊天窗口——坑 #4，禁用）
        try
        {
            var item = Chats().FirstOrDefault(c => c.Title == title);
            if (item is not null)
            {
                if (item.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) &&
                    sp is SelectionItemPattern sel)
                {
                    sel.Select();
                    Thread.Sleep(700);
                    if (Locate(title, 1500) || Header() == title)
                    {
                        Console.WriteLine($"[openchat] selected '{title}' via SelectionItem");
                        return true;
                    }
                    Console.WriteLine("[openchat] SelectionItem 未生效，回退物理点击");
                }
                else
                    Console.WriteLine("[openchat] 聊天条目不支持 SelectionItem，回退物理点击");
            }
        }
        catch { }

        // 兜底：物理单击（要求 QQ 在当前桌面且可置前）
        // 先把窗口摆稳并真正拿到前台，再取坐标——失焦状态下第一次点击只会激活窗口
        RequireForeground();
        Thread.Sleep(250);

        var chat = Chats().FirstOrDefault(c => c.Title == title);
        if (chat is null) return false;

        // 用真实单击选中会话（Invoke 会触发 QQ 的「独立聊天窗口」，主窗口反而变空）
        for (int attempt = 0; attempt < 2; attempt++)
        {
            // Chats() 枚举耗时期间前台可能被原窗口收回，点击前必须重新验证，否则点击只被当作激活
            RequireForeground();
            var r = chat.Element.Current.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return false;

            Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
            Thread.Sleep(150);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(700);

            if (Locate(title, 1500) || Header() == title) return true;
        }
        return false;
    }

    /// <summary>搜索打开会话：点搜索框 → 粘贴关键词 → 点结果条目。返回新会话标题（空=失败）。</summary>
    public string OpenChatBySearch(string keyword)
    {
        RequireForeground();
        string before = Header();
        var search = FindByName(_root, "搜索")
            ?? throw new InvalidOperationException("找不到搜索框（Name='搜索'）");

        var r = search.Current.BoundingRectangle;
        RequireForeground();
        Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        Thread.Sleep(150);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(600);

        // 搜索框里的残留关键词会与新粘贴叠加导致无结果——先全选再粘贴覆盖（2026-10-25 实测）
        const byte VK_CONTROL = 0x11, VK_A = 0x41;
        RequireForeground();
        Native.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_A, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.keybd_event(VK_A, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Native.keybd_event(VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(150);

        SetClipboardText(keyword);
        Thread.Sleep(150);
        RequireForeground();
        SendCtrlV();

        // 结果面板可能在独立弹层窗口（不在主窗口树内），轮询扫所有 QQ 顶层窗口
        AutomationElement? hit = null;
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (hit is null && DateTime.UtcNow < deadline)
        {
            foreach (var (hwnd, _, _) in Windows())
            {
                AutomationElement rootEl;
                try { rootEl = AutomationElement.FromHandle(hwnd); } catch { continue; }
                foreach (AutomationElement el in rootEl.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string name, cls;
                    try { name = el.Current.Name ?? ""; cls = el.Current.ClassName ?? ""; }
                    catch { continue; }
                    if (cls.Contains("search")) continue;   // 跳过搜索框自身
                    // 结果条目是「找好友：xxx」格式，号码输入时条目名可能只含昵称不含关键词
                    if ((name.Contains(keyword) || name.Contains("找好友")) && name.Length < 120)
                    {
                        hit = el;
                        Console.WriteLine($"[search-hit] hwnd={hwnd} cls='{cls}' name='{name}'");
                        break;
                    }
                }
                if (hit is not null) break;
            }
            if (hit is null) Thread.Sleep(500);
        }
        if (hit is null) return "";

        RequireForeground();
        var hr = hit.Current.BoundingRectangle;
        Native.SetCursorPos((int)(hr.X + hr.Width / 2), (int)(hr.Y + hr.Height / 2));
        Thread.Sleep(150);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(1500);

        var header = Header();
        return (string.IsNullOrEmpty(header) || header == before) ? "" : header;
    }

    /// <summary>通过输入框「文件」按钮 + Windows 文件对话框发送文件（wav/zip 等剪贴板 drop 粘贴 QQNT 输入框不收）。
    /// 2026-10-25 定案铁律（全链实测）：提交必须回车不点「确认」（CommonDlg 原生解析完整路径，点按钮走
    /// QQ 注入逻辑会把整串路径当字面文件名）；找对话框节点必须逐层递归；失败必须抛错、绝不谎报 unverified。
    /// 2026-10-28 阶段3：QQ 升级后弹 Win11 新版对话框（#32770「请选择」），文件名框不进 UIA 树
    /// （用户整树 dump 实证），老路径的「文件名」Edit 永远找不到。双路分发：先老路径短等 1.5s
    /// （老系统/老对话框仍走原逻辑），没等到走新兜底（相对坐标点文件名框，tools/send-file-dpi.ps1
    /// 同款，2026-10-28 用户实测可发）。两条路都打日志说明走了哪条。</summary>
    public void SendFile(string path)
    {
        RequireForeground();
        // 跨窗口精确名匹配点击（与 clickbtn CLI 同一已实证路径；主窗树 FindByName 命中的同名元素不可靠）
        if (!ClickButton("文件"))
            throw new InvalidOperationException("找不到「文件」按钮");
        Thread.Sleep(1000);

        var (legacyEdit, newDlg) = WaitForFileDialog(1500, 10000);
        if (legacyEdit is null && newDlg == IntPtr.Zero)
            throw new InvalidOperationException(
                "文件对话框未出现（老路径没有「文件名」输入框，新路径没有 #32770「请选择」窗）");

        if (legacyEdit is not null)
        {
            // ---- 老对话框（CommonDlg，文件名框在 UIA 树里）：2026-10-25 原逻辑 ----
            Console.WriteLine("[sendfile] 老对话框路径：UIA「文件名」框");

            // 文件名框聚焦必须 UIA SetFocus——物理点击首次只激活窗口不聚焦子控件，粘贴必落空（铁律 1）
            legacyEdit.SetFocus();
            Thread.Sleep(400);
            SetClipboardText(path);
            Thread.Sleep(150);
            SendCtrlV();
            Thread.Sleep(600);

            // 粘贴读回验证：落空时回车=打开错误目标
            string? cur = null;
            try { cur = legacyEdit.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) ? ((ValuePattern)vp).Current.Value : null; }
            catch { }
            if (string.IsNullOrEmpty(cur))
                throw new InvalidOperationException($"文件对话框路径未填入（粘贴落空，实际='{cur}'），已中止，对话框需手动关闭");

            SendEnter();   // 走 CommonDlg 原生路径解析；勿点「确认」按钮（铁律 2）

            // 打开成功 = 对话框关闭；仍挂着=发送失败（必须报错，不能谎报 unverified）
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(500);
                if (FindFileDialogNameEdit() is null) goto dialogClosed;
            }
            throw new InvalidOperationException("文件对话框未随回车关闭，文件可能未发出（请检查 QQ 输入框）");
        }

        SendFileNewDialog(newDlg, path);

    dialogClosed:
        // 回车只关对话框；文件贴进输入框后还要点「发送(1)」（2026-10-25 实测，且可能要点两次）
        Thread.Sleep(800);
        for (int i = 0; i < 8; i++)
        {
            if (ClickButton("发送(1)")) break;
            Thread.Sleep(500);
        }
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(600);
            if (!ClickButton("发送(1)")) return;   // 按钮消失=已发出
        }
        throw new InvalidOperationException("文件已在输入框但「发送(1)」多次点击未消失，请人工检查");
    }

    /// <summary>2026-10-28 阶段3：双路等文件对话框。legacyFirstMs 内只找老 CommonDlg 的「文件名」框
    /// （老系统不受新逻辑影响）；没等到再双路轮询到 totalMs（自调用起算）——老框晚到也接得住，
    /// 检出 #32770「请选择」即走新兜底。返回值两者至多一个非空。</summary>
    public (AutomationElement? LegacyEdit, IntPtr NewDlg) WaitForFileDialog(int legacyFirstMs, int totalMs)
    {
        var legacy = WaitFileDialogNameEdit(legacyFirstMs);
        if (legacy is not null) return (legacy, IntPtr.Zero);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < totalMs)
        {
            var dlg = FindChooseDialog();
            if (dlg != IntPtr.Zero) return (null, dlg);
            legacy = FindFileDialogNameEdit();
            if (legacy is not null) return (legacy, IntPtr.Zero);
            Thread.Sleep(400);
        }
        return (null, IntPtr.Zero);
    }

    /// <summary>QQ 进程的顶层 #32770「请选择」窗口（Win11 新版文件对话框）。老 CommonDlg 不在此列
    /// ——它挂在 Chrome_WidgetWin_1 里（FindFileDialogNameEdit 才看得到）。按进程过滤，不会误抓
    /// 其他应用的「请选择」窗。</summary>
    private static IntPtr FindChooseDialog()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("QQ")) pids.Add((uint)p.Id);
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var cls = new StringBuilder(64);
            Native.GetClassName(h, cls, 64);
            if (cls.ToString() != "#32770") return true;
            var title = new StringBuilder(128);
            Native.GetWindowText(h, title, 128);
            if (title.ToString() != "请选择" || !Native.IsWindowVisible(h)) return true;
            found = h;
            return false;   // 命中即停
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Win11 新版文件对话框填路径：文件名框不进 UIA（2026-10-28 用户整树 dump 实证），
    /// 只能按对话框相对坐标 (0.469w, 0.895h) 点聚焦 → 粘贴完整路径 → 回车
    /// （tools/send-file-dpi.ps1 同款，用户实测成功发出两个文件）。坐标一致性依赖进程 PerMonitorV2
    /// （app.manifest，阶段3）：GetWindowRect 与 SetCursorPos 同为物理像素。
    /// 读回验证降级：ValuePattern 读不回路径，成功判据=回车后对话框关闭；坐标点偏/粘贴落空时
    /// 对话框不会关，照样抛错（不谎报 unverified）。</summary>
    private static void SendFileNewDialog(IntPtr dlg, string path)
    {
        Console.WriteLine($"[sendfile] 新对话框路径：hwnd={dlg}（#32770 请选择，文件名框不进 UIA）");
        Native.SetForegroundWindow(dlg);
        Thread.Sleep(400);
        if (!Native.GetWindowRect(dlg, out var r))
            throw new InvalidOperationException("读不到文件对话框窗口矩形");

        var bx = (int)(r.Left + 0.469 * (r.Right - r.Left));
        var by = (int)(r.Top + 0.895 * (r.Bottom - r.Top));
        Console.WriteLine($"[sendfile] 对话框 rect=({r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top})，相对坐标点文件名框 ({bx},{by})");
        Native.SetCursorPos(bx, by);
        Thread.Sleep(200);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(90);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(500);

        SetClipboardText(path);
        Thread.Sleep(150);
        SendCtrlV();
        Thread.Sleep(900);

        Console.WriteLine("[sendfile] 读回验证降级：新对话框读不回路径，以回车后对话框关闭为成功判据");
        SendEnter();   // 与老路径同铁律：回车提交，不点「确认/打开」按钮
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(500);
            if (!Native.IsWindowVisible(dlg))
            {
                Console.WriteLine("[sendfile] 新对话框已随回车关闭（路径视为填入成功）");
                return;
            }
        }
        throw new InvalidOperationException("新对话框未随回车关闭（相对坐标点偏或粘贴落空），文件未选上，对话框需手动关闭");
    }

    // ---- 语音条（2026-10-25）：驱动 QQ「语音消息」实录，把本地 wav 变成真语音条 ----
    // 前置：AudioIo.EnsureCaptureReady 已把「立体声混音」设为默认录音设备（Realtek 回环），
    // 本机无实体麦克风，QQ 录到的=Realtek 扬声器播放的内容。

    /// <summary>把归一化后的 PCM wav 以「语音消息」实录发送（2026-10-25 实测流程）：
    /// 点「语音消息」→ 输入区被 audio-msg-input 面板替换（提示「按住空格键开始说话」）
    /// → 前台确保后按住空格 → waveOut 同步播放（录音=播放内容，capSeconds 截断）
    /// → 松开空格停止 → 语音落入输入框预览 → 点「发送」。
    /// 任一步失败先 Esc/退出退出录音面板（绝不把半成品发出去）再抛出。</summary>
    public void SendVoiceFile(string wavPath, string renderMatch, double capSeconds)
    {
        RequireForeground();
        if (!VoicePanelActive())   // 面板已开着别重点按钮（会切换回去）
        {
            if (!ClickButton("语音消息"))
                throw new InvalidOperationException("找不到「语音消息」按钮（输入工具栏）");
            if (!WaitVoicePanel(4000))
                throw new InvalidOperationException("点击「语音消息」后未出现录音面板（audio-msg-input）");
        }
        Console.WriteLine("[voice] panel ok");
        try
        {
            // Chromium 键盘路由的关键：UIA SetFocus 到面板元素（sendq/sendat 同款——
            // 不做这步键盘事件根本不进页面，空格全部落空；2026-10-25 用户定判"没真实按住空格"）
            FindByClassContains(_root, "audio-msg-input")?.SetFocus();
            Thread.Sleep(250);
            FocusWithoutRaiseAtomic();
            Thread.Sleep(200);
            var gi = new Native.GUITHREADINFO();
            gi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>();
            _ = Native.GetGUIThreadInfo(0, ref gi);
            Console.WriteLine($"[voice] focus={gi.hwndFocus} qq={_hwnd}");
            if (gi.hwndFocus != _hwnd)
                throw new InvalidOperationException($"键盘焦点未能定向到 QQ（{gi.hwndFocus}），空格会落空");
            Native.SendKeyScan(0x39, true);   // SPACE 扫描码 0x39 按下 = 开始说话
            Console.WriteLine("[voice] space down(scan)");
            try
            {
                // 关键配方（2026-10-25 实测定判）：按住期间必须 ~100ms 周期补发扫描码 keydown
                // （模拟 OS 自动重复流）——孤立单次 keydown 不被 QQNT 认定为"按住"，录音不启动。
                // 播放并行跑，播完立刻停补发。
                var playTask = Task.Run(() => AudioIo.PlayWavBlocking(wavPath, renderMatch, capSeconds));
                while (!playTask.Wait(100))
                    Native.SendKeyScan(0x39, true);
                playTask.Wait();
                Console.WriteLine("[voice] play done");
                Thread.Sleep(400);   // 话尾，掐太短会吞最后一个字
            }
            finally { Native.SendKeyScan(0x39, false); }   // 松开 = 停止；播放抛异常也必须松键
            Console.WriteLine("[voice] space up");
            Thread.Sleep(1000);   // 等 ptt-element 语音预览落进输入框
        }
        catch
        {
            TryCancelVoicePanel();
            throw;
        }
        // 预览在输入框里：悬浮「发送」键优先，回车兜底（与贴图发送同策略）
        var sent = ClickSend();
        Console.WriteLine($"[voice] clicksend={sent}");
        if (!sent)
        {
            RequireForeground();
            SendEnter();
            Console.WriteLine("[voice] enter fallback");
        }
        Thread.Sleep(1200);
    }

    /// <summary>测试钩子：vk 版空格注入（spacetest 对照用）。</summary>
    internal static void SendKeyTest(ushort vk, bool down) => SendKey(vk, down);

    /// <summary>测试钩子：点一下录音面板本体（Chromium 把页面焦点给被点元素）。</summary>
    internal void ClickVoicePanelBodyForTest()
    {
        var panel = FindByClassContains(_root, "audio-msg-input");
        if (panel is null) return;
        var r = panel.Current.BoundingRectangle;
        Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        Thread.Sleep(150);
        var down = new Native.INPUT { type = 0 };
        down.mi.dwFlags = Native.MOUSEEVENTF_LEFTDOWN;
        var up = new Native.INPUT { type = 0 };
        up.mi.dwFlags = Native.MOUSEEVENTF_LEFTUP;
        _ = Native.SendInput(2, new[] { down, up }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>测试钩子：编辑器状态（is-empty 变化=空格进了编辑器而不是面板句柄）。</summary>
    internal string EditorStateForTest()
    {
        var ed = FindByClassContains(_root, "ProseMirror");
        if (ed is null) return "(no editor)";
        try { return ed.Current.ClassName; } catch { return "?"; }
    }

    /// <summary>测试钩子：dump 录音面板子树（spacetest 观察按住期间 UI 变化）。</summary>
    internal List<string> DumpVoicePanelForTest()
    {
        var lines = new List<string>();
        var panel = FindByClassContains(_root, "audio-msg-input");
        if (panel is null) { lines.Add("(no panel)"); return lines; }
        var all = panel.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement e in all)
        {
            try
            {
                var n = e.Current.Name;
                var cls = e.Current.ClassName;
                if (!string.IsNullOrEmpty(n) || cls.Contains("micro"))
                    lines.Add($"cls='{cls}' name='{n}'");
            }
            catch { }
        }
        return lines;
    }

    /// <summary>录音面板判定：输入区被 audio-msg-input 替换（面板类名是铁标记，
    /// 比按钮名/工具栏消失更稳——2026-10-25 实测面板文本为「按住空格键开始说话…」）。</summary>
    private bool VoicePanelActive() => FindByClassContains(_root, "audio-msg-input") is not null;

    private bool WaitVoicePanel(int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (VoicePanelActive()) return true;
            Thread.Sleep(250);
        }
        return VoicePanelActive();
    }

    /// <summary>退出录音面板：Esc（面板自述快捷键），兜底点「退出」链接。</summary>
    private void TryCancelVoicePanel()
    {
        try
        {
            RequireForeground();
            Native.SendKeyScan(0x01, true);   // ESC 扫描码 0x01（面板只认扫描码）
            Thread.Sleep(60);
            Native.SendKeyScan(0x01, false);
            Thread.Sleep(400);
            if (!VoicePanelActive()) return;
            ClickButton("退出");
        }
        catch (Exception ex) { Console.WriteLine($"[voice] cancel failed: {ex.Message}"); }
    }

    // ---- @提及（2026-10-25）：真键序 @ + 粘贴名片名过滤 + 回车选中（人类路径）----
    // 成员面板（at-picker）对 UIA 树不可见（全树 dump 无此元素），无法按名点击；
    // 人工流程是「打 @ → 打名字 → 面板高亮唯一项 → 回车」——粘贴名字同样触发过滤。

    /// <summary>@某人并发文本。判据：选中回车后面板关闭且消息未发出（编辑器仍有内容）；
    /// 若面板根本没开，回车会把纯文本 "@名字" 直接发出去——调用方应立刻撤回并视为失败。</summary>
    public void SendAt(string member, string text)
    {
        ClearInput();
        FocusWithoutRaiseAtomic();
        Thread.Sleep(300);
        var gi = new Native.GUITHREADINFO();
        gi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>();
        _ = Native.GetGUIThreadInfo(0, ref gi);
        Console.WriteLine($"[sendat] focus={gi.hwndFocus} qq={_hwnd}");
        if (gi.hwndFocus != _hwnd)
            throw new InvalidOperationException($"键盘焦点未能定向到 QQ（{gi.hwndFocus}），中止");

        var before = Messages().Count(m => m.IsSelf);

        // 真键序 @：Shift+2（中文 IME 态直出符号）
        SendKey(0x10, true);
        SendKey(0x32, true);
        Thread.Sleep(60);
        SendKey(0x32, false);
        SendKey(0x10, false);
        Thread.Sleep(700);

        // 逐字真键序输入名片名（KEYEVENTF_UNICODE）：at-picker 按键入过滤并高亮唯一匹配。
        // 粘贴会关掉面板（2026-10-25 实测），必须走键盘路径。
        foreach (var ch in member)
        {
            Native.SendCharUnicode(ch);
            Thread.Sleep(90);
        }
        Thread.Sleep(900);   // 等过滤稳定

        // 回车 = 选中高亮成员；若面板未开则此回车发出纯文本 "@名字"
        SendEnter();
        Thread.Sleep(900);

        var sentOut = Messages().Count(m => m.IsSelf) > before;
        if (sentOut)
            throw new InvalidOperationException("@面板未生效：回车把纯文本直接发出了（调用方应撤回该消息）");

        // mention chip 已落入编辑器，续贴正文再发（纯粘贴！全选粘贴会覆盖掉 chip）
        SetClipboardText(" " + text);
        Thread.Sleep(150);
        SendPasteOnly();
        Thread.Sleep(400);
        SendEnter();
    }

    // ---- 撤回（2026-10-25）：悬停到自己消息气泡（右侧头像往左一小截）→ 右键 → 点「撤回」 ----
    // 用户实测定档：鼠标必须落在发出的句子上才出菜单；点容器左侧空白区无菜单。
    // 菜单项 QQNT 叫「撤回」（「撤销」是错的），两个都试。

    /// <summary>撤回自己最后一条消息（contains 非空时只撤含该片段的最新一条）。
    /// 私聊 2 分钟 / 群管理员窗口内有效。</summary>
    public bool RecallLastOwn(string contains = "")
    {
        RequireForeground();
        var mine = Messages()
            .Where(m => m.IsSelf && (contains.Length == 0 || m.Text.Contains(contains)))
            .OrderBy(m => m.Y)
            .LastOrDefault();
        if (mine is null || mine.Element is null) return false;

        // 定位点=头像左侧一点：自消息头像在最右、气泡紧贴其左——比容器中心可靠
        System.Windows.Rect avatar;
        try
        {
            var av = mine.Element.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "avatar-span"));
            avatar = av.Count > 0 ? av[0].Current.BoundingRectangle : mine.Element.Current.BoundingRectangle;
        }
        catch { return false; }
        if (avatar.Width <= 0) return false;
        var cx = (int)(avatar.X - 70);            // 头像往左 70 + 再向下 50（用户定档 2026-10-25：短消息气泡也稳选中）
        var cy = (int)(avatar.Y + avatar.Height / 2 + 50);
        Console.WriteLine($"[recall] target text='{(mine.Text.Length > 20 ? mine.Text[..20] : mine.Text)}' click=({cx},{cy})");

        // 清场：残留菜单先 ESC 掉（编辑器空态 ESC 无副作用）
        Native.SendKeyScan(0x01, true); Thread.Sleep(40); Native.SendKeyScan(0x01, false);
        Thread.Sleep(200);

        Native.SetCursorPos(cx, cy);
        Thread.Sleep(120);   // 只做定位停留（长悬停会弹悬浮件截胡右键）
        RightClickSendInput();
        // 轮询等菜单进 UIA 树（渲染需时，之前 800ms 一次性搜太早全 miss——2026-10-25 用户定判）
        AutomationElement? item = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            item = FindInAnyWindow("撤回") ?? FindInAnyWindow("撤销");
            if (item is not null) break;
            Thread.Sleep(250);
        }
        if (item is null)
        {
            Console.WriteLine("[recall] 菜单 3s 内未出现在 UIA 树");
            // 兜底清场：ESC 关掉可能已开但不可见的菜单
            Native.SendKeyScan(0x01, true); Thread.Sleep(40); Native.SendKeyScan(0x01, false);
            return false;
        }
        var r = item.Current.BoundingRectangle;
        Console.WriteLine($"[recall] menu item rect=({(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height})");
        Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        Thread.Sleep(200);
        var down = new Native.INPUT { type = 0 };
        down.mi.dwFlags = Native.MOUSEEVENTF_LEFTDOWN;
        var up = new Native.INPUT { type = 0 };
        up.mi.dwFlags = Native.MOUSEEVENTF_LEFTUP;
        _ = Native.SendInput(2, new[] { down, up }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        Thread.Sleep(1200);
        // 结果由调用方回读验证（消息消失=成功）
        return true;
    }

    /// <summary>SendInput 结构化右键（mouse_event 版在 Chromium 渲染区不弹菜单，2026-10-25 实测）。</summary>
    private static void RightClickSendInput()
    {
        var down = new Native.INPUT { type = 0 };
        down.mi.dwFlags = Native.MOUSEEVENTF_RIGHTDOWN;
        var up = new Native.INPUT { type = 0 };
        up.mi.dwFlags = Native.MOUSEEVENTF_RIGHTUP;
        _ = Native.SendInput(2, new[] { down, up }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>按顺序尝试多个候选按钮名，命中即点。</summary>
    private bool ClickFirstButton(params string[] names)
    {
        foreach (var n in names)
            if (ClickButton(n)) return true;
        return false;
    }


    /// <summary>在 QQ 宿主树里逐层递归找文件对话框的「文件名」编辑框（CommonDlg 的 DUI 树对 Descendants 查询不完整）。
    /// 2026-10-28 阶段3：timeoutMs 为轮询预算（500ms 一轮）——老路径在 SendFile 里只给 1.5s，
    /// 没等到就交给新对话框兜底（WaitForFileDialog），不再干等 5s。</summary>
    private AutomationElement? WaitFileDialogNameEdit(int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var hit = FindFileDialogNameEdit();
            if (hit is not null) return hit;
            Thread.Sleep(500);
        } while (sw.ElapsedMilliseconds < timeoutMs);
        return null;
    }

    private AutomationElement? FindFileDialogNameEdit()
    {
        foreach (var (hwnd, _, _) in Windows())
        {
            try
            {
                var rootEl = AutomationElement.FromHandle(hwnd);
                // Descendants 条件查询对 CommonDlg 的 DUI 树不完整（2026-10-25 实测
                // PS/FindByName 全部 miss 而递归 dump 可见），必须逐层递归
                var stack = new Stack<AutomationElement>();
                stack.Push(rootEl);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    try
                    {
                        if (cur.Current.ControlType == ControlType.Edit &&
                            cur.Current.Name.Contains("文件名"))
                            return cur;
                        var kids = cur.FindAll(TreeScope.Children, Condition.TrueCondition);
                        foreach (AutomationElement k in kids)
                            stack.Push(k);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>按精确 Name 找按钮并点击（跨 QQ 全部顶层窗口；用于弹层按钮如「发送(1)」）。</summary>
    public bool ClickButton(string name)
    {
        foreach (var (hwnd, _, _) in Windows())
        {
            AutomationElement rootEl;
            try { rootEl = AutomationElement.FromHandle(hwnd); }
            catch { continue; }
            var btn = FindByName(rootEl, name);
            if (btn is null) continue;
            try
            {
                var r = btn.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) continue;
                Console.WriteLine($"[click-btn] hwnd={hwnd} rect=({(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height})");
                Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                Thread.Sleep(150);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>双击元素中心（真鼠标：Chromium 不吃 posted 消息，pic-element 又无 InvokePattern，
    /// 双击=打开图片查看器）。短暂抢前台。两次按压间隔 ~110ms，落在系统双击窗口内。
    /// 先 ScrollIntoView：窗口移动后 Chromium a11y 树的 rect 可能陈旧（2026-10-26 实测双击落到
    /// 头像上弹出资料卡），滚动会强制布局重算并发布新坐标。</summary>
    public void DblClickElement(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(System.Windows.Automation.ScrollItemPattern.Pattern, out var spo)
            && spo is System.Windows.Automation.ScrollItemPattern sip)
        {
            try { sip.ScrollIntoView(); Thread.Sleep(400); } catch { }
        }
        var r = el.Current.BoundingRectangle;
        if (r.Width <= 0 || r.Height <= 0)
            throw new InvalidOperationException("目标元素不可见（坐标无效，先滚动进视口）");
        RequireForeground();
        Native.SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        Thread.Sleep(150);
        for (int i = 0; i < 2; i++)
        {
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            if (i == 0) Thread.Sleep(60);
        }
    }

    /// <summary>点屏幕坐标（用于无障碍树读不到的弹层，坐标来自截图证据）。</summary>
    public void ClickPoint(int x, int y, bool dbl = false)
    {
        RequireForeground();
        Native.SetCursorPos(x, y);
        Thread.Sleep(150);
        int clicks = dbl ? 2 : 1;
        for (int i = 0; i < clicks; i++)
        {
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            if (i == 0 && dbl) Thread.Sleep(90);   // 双击间隔，落在系统双击窗口内
        }
        Thread.Sleep(800);
    }

    // ===== 输入与发送（小窗适配）=====
    // 小窗（≈820x580 以下）里「发送」按钮会被裁掉：输入框定位直接找 ProseMirror 编辑区，
    // 发送一律回车（文字/图片回车即发；「我的手机」弹悬浮发送键的场景仍先找新键）

    /// <summary>输入框矩形：优先 ProseMirror 编辑区；兜底旧的「发送按钮左上方」相对定位。</summary>
    private System.Windows.Rect? InputRect()
    {
        try
        {
            var editor = FindByClassContains(_root, "ProseMirror");
            var r = editor?.Current.BoundingRectangle;
            if (r is { Width: > 0, Height: > 0 }) return r;
        }
        catch { }

        var sendBtn = FindByName(_root, "发送");
        if (sendBtn is null) return null;
        try
        {
            var s = sendBtn.Current.BoundingRectangle;
            if (s.Width <= 0) return null;
            return new System.Windows.Rect(s.X - 380, s.Y - 70, 200, 40);
        }
        catch { return null; }
    }

    /// <summary>点输入框并取得焦点（粘贴前必须重新置前，防止 CtrlV 落进别的窗口）。
    /// 小窗下输入框可能被压扁，点击坐标钳进主窗内。</summary>
    private void ClickInput()
    {
        var r = InputRect() ?? throw new InvalidOperationException("找不到输入框（ProseMirror 与发送按钮都不在树里）");
        RequireForeground();
        int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + Math.Min(r.Height / 2, 40));
        if (RectOf(_hwnd) is { } mw)
        {
            cx = Math.Clamp(cx, mw.Left + 20, mw.Right - 20);
            cy = Math.Clamp(cy, mw.Top + 70, mw.Bottom - 70);
        }
        Native.SetCursorPos(cx, cy);
        Thread.Sleep(180);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(400);
    }

    public void Send(string text)
    {
        ClickInput();
        SetClipboardText(text);
        Thread.Sleep(200);
        RequireForeground();
        SendCtrlV();
        Thread.Sleep(400);
        SendEnter();
    }

    /// <summary>静默发送 v7：原子化焦点定向（attach→SetFocus→detach 零休眠，危险窗口 ~1ms）
    /// + UIA 编辑区焦点 + SendInput 键序 + 发送按钮 Invoke 兜底。
    /// 铁律：AttachThreadInput 的绑定窗口内绝不允许 Sleep/IO/分配——进程死在绑定窗口内
    /// 会锁死整机输入队列（2026-10-25 事故）。绑定解除后的任何时点被杀都无害。</summary>
    public void SendQuiet(string text)
    {
        var prevFg = Native.GetForegroundWindow();
        SetClipboardText(text);
        Thread.Sleep(80);

        FocusWithoutRaiseAtomic();

        // 安全闸：全局键盘焦点确在 QQ 才发 SendInput，否则按键会进用户正在用的窗口。
        // 焦点定向最多 3 次重试（2026-10-26 潮汐实测：用户刚点走窗口时首次定向常被夺回；
        // 用户持续打字时三次都失败=放弃，安全语义不变）
        // 2026-10-27：小屏归 Amy 后 owner 要求"输入快点，别和我打字串上"——首轮等待 400→250，
        // 后段各睡眠同步收紧，整段占键盘时间约 1.9s→1.2s。
        var gi = new Native.GUITHREADINFO();
        gi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>();
        for (int attempt = 1; ; attempt++)
        {
            Thread.Sleep(250);   // 已解绑，此处被杀无害
            _ = Native.GetGUIThreadInfo(0, ref gi);
            Console.WriteLine($"[sendq] focus: global={gi.hwndFocus} qq={_hwnd} attempt={attempt}");
            if (gi.hwndFocus == _hwnd) break;
            if (attempt >= 3)
            {
                // 2026-10-27：小屏归 Amy 后"偷焦点"不再是打扰（窗口在她自己半屏），
                // 静默定向失败就抬一次前台再定向一次，别让值班整轮发不出去。
                try { RequireForeground(); } catch { }
                Thread.Sleep(120);
                _ = Native.GetGUIThreadInfo(0, ref gi);
                if (gi.hwndFocus == _hwnd) break;
                throw new InvalidOperationException($"键盘焦点未能定向到 QQ（{gi.hwndFocus}），中止");
            }
            FocusWithoutRaiseAtomic();
        }

        // Web 焦点到输入框（OS 焦点在窗口、页面焦点不在输入框时粘贴无反应）
        try { FindByClassContains(_root, "ProseMirror")?.SetFocus(); } catch { }
        Thread.Sleep(180);

        // Ctrl+A 全选清残留 → Ctrl+V 覆盖粘贴 → Enter（真输入事件；Chromium 无视一切 posted 消息——2026-10-25 实测）
        // 输入框有用户打到一半的字符时，不带全选的粘贴会拼接成脏消息（zhu 前缀事故）
        SendKey(0x11, true);
        SendKey(0x41, true);
        Thread.Sleep(40);
        SendKey(0x41, false);
        SendKey(0x11, false);
        Thread.Sleep(50);
        SendKey(0x11, true);
        SendKey(0x56, true);
        Thread.Sleep(40);
        SendKey(0x56, false);
        SendKey(0x11, false);
        Thread.Sleep(350);
        SendKey(0x0D, true);
        Thread.Sleep(40);
        SendKey(0x0D, false);

        // 还原现场：SetFocus 的激活副作用可能把 QQ 提到前台——沉底回去，并把前台
        // 还给用户原窗口（发送全程用户视角应无感）
        Thread.Sleep(150);
        if (Native.GetForegroundWindow() == _hwnd)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                0x0002 | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (prevFg != IntPtr.Zero && prevFg != _hwnd)
                Native.SetForegroundWindow(prevFg);
        }
    }

    /// <summary>原子焦点定向：绑定窗口内零休眠零分配，微秒级完成全部 attach/SetFocus/detach。</summary>
    private void FocusWithoutRaiseAtomic()
    {
        var fg = Native.GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0u : Native.GetWindowThreadProcessId(fg, out _);
        var qqThread = Native.GetWindowThreadProcessId(_hwnd, out _);
        var me = Native.GetCurrentThreadId();
        if (fgThread != 0) Native.AttachThreadInput(me, fgThread, true);
        Native.AttachThreadInput(me, qqThread, true);
        Native.SetFocus(_hwnd);
        Native.AttachThreadInput(me, qqThread, false);
        if (fgThread != 0) Native.AttachThreadInput(me, fgThread, false);
    }

    private static void SendKey(ushort vk, bool down)
    {
        var inp = new Native.INPUT { type = 1 };
        inp.ki.wVk = vk;
        inp.ki.dwFlags = down ? 0u : Native.KEYEVENTF_KEYUP;
        _ = Native.SendInput(1, new[] { inp }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    private static void SetClipboardText(string text)
    {
        // 2026-10-28 15:00 实盘：.NET 的 Clipboard.SetDataObject（OLE 路径）从 14:57 起持续抛
        // 「Requested Clipboard operation did not succeed」，连试 13 次全败；同一时刻 clip.exe 与裸 Win32
        // OpenClipboard+SetClipboardData 写入完全正常（读回也正常）。故先走 Win32 写 CF_UNICODETEXT，
        // 失败再退回 .NET 老路径，两条都败才算真失败。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (TrySetClipboardTextWin32(text)) return;
            try
            {
                Clipboard.SetDataObject(text, true, 10, 100);
                return;
            }
            catch { Thread.Sleep(120); }
        }
        throw new InvalidOperationException("剪贴板写入失败");
    }

    // ---- 裸 Win32 剪贴板（只写 CF_UNICODETEXT，绕开 OLE）----
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private static bool TrySetClipboardTextWin32(string text)
    {
        IntPtr hMem = IntPtr.Zero;
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)(uint)bytes.Length);
                if (hMem == IntPtr.Zero) return false;
                var p = GlobalLock(hMem);
                if (p == IntPtr.Zero) return false;
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, p, bytes.Length);
                GlobalUnlock(hMem);
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
                hMem = IntPtr.Zero;   // 所有权已交给系统，不能再 GlobalFree
                return true;
            }
            finally { CloseClipboard(); }
        }
        catch { return false; }
        finally { if (hMem != IntPtr.Zero) GlobalFree(hMem); }
    }

    private static void SendCtrlV()
    {
        const byte VK_CONTROL = 0x11, VK_V = 0x56, VK_A = 0x41;
        // 先全选再粘贴：输入框有残留字符（用户打到一半）时，直接 Ctrl+V 会拼接成脏消息（2026-10-25 实测 zhu 前缀事故）
        Native.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_A, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.keybd_event(VK_A, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Native.keybd_event(VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(60);
        Native.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        Native.keybd_event(VK_V, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Native.keybd_event(VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>纯粘贴（不做 Ctrl+A 全选）：编辑器已有内容需要保留时用——
    /// 全选粘贴会覆盖掉刚插入的 @mention chip（2026-10-25 实测 v4 发出去 chip 消失）。</summary>
    private static void SendPasteOnly()
    {
        const byte VK_CONTROL = 0x11, VK_V = 0x56;
        Native.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        Native.keybd_event(VK_V, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Native.keybd_event(VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SendEnter()
    {
        Native.keybd_event(Native.VK_RETURN, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        Native.keybd_event(Native.VK_RETURN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public void SendImageFile(string path)
    {
        ClickInput();

        var regularBtns = SendButtonRects("粘贴前");
        SetClipboardImageAndFile(path);
        Thread.Sleep(200);
        // 剪贴板写入耗时期间前台可能被 VS 等窗口抢回（2026-10-25 实测两次粘贴落空），
        // 粘贴前必须重新置前
        RequireForeground();
        SendCtrlV();
        Thread.Sleep(2500);   // 文件粘贴要等预览出现
        // 普通会话粘贴后回车即可发；「我的手机」等特例会弹悬浮发送键——
        // 有新键点新键，没有就回车（小窗无发送按钮时也走回车，2026-10-25 定案）
        if (!ClickSend(regularBtns))
        {
            RequireForeground();
            SendEnter();
        }
    }

    /// <summary>枚举所有可见「发送」键的位置签名（"x,y"），tag 仅用于日志。</summary>
    private HashSet<string> SendButtonRects(string tag)
    {
        var set = new HashSet<string>();
        var btns = _root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "发送"));
        int i = 0;
        foreach (AutomationElement b in btns)
        {
            try
            {
                var r = b.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) continue;
                set.Add($"{(int)r.X},{(int)r.Y}");
                Console.WriteLine($"[send-btn {tag} #{i}] cls='{b.Current.ClassName}' rect=({(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height})");
            }
            catch { }
            i++;
        }
        return set;
    }

    /// <summary>点悬浮发送键：排除粘贴前就存在的常驻键，只点新出现的。</summary>
    public bool ClickSend(HashSet<string>? excludeRects = null)
    {
        var btns = _root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "发送"));
        AutomationElement? target = null;
        int i = 0;
        foreach (AutomationElement b in btns)
        {
            try
            {
                var r = b.Current.BoundingRectangle;
                Console.WriteLine($"[send-btn 粘贴后 #{i}] cls='{b.Current.ClassName}' rect=({(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height})");
                if (r.Width <= 0 || r.Height <= 0) { i++; continue; }
                var key = $"{(int)r.X},{(int)r.Y}";
                if (excludeRects is not null && excludeRects.Contains(key)) { i++; continue; }
                target = b;
            }
            catch { }
            i++;
        }
        if (target is null) return false;

        RequireForeground();
        var rect = target.Current.BoundingRectangle;
        Native.SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
        Thread.Sleep(150);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        return true;
    }

    /// <summary>清空输入框（清掉上次失败残留的图片预览/文字）。</summary>
    public void ClearInput()
    {
        if (InputRect() is null) return;
        ClickInput();

        const byte VK_CONTROL = 0x11, VK_A = 0x41, VK_DELETE = 0x2E;
        Native.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_A, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.keybd_event(VK_A, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Native.keybd_event(VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(120);
        Native.keybd_event(VK_DELETE, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.keybd_event(VK_DELETE, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(200);
    }

    // 位图（CF_DIB）与文件拖放（CF_HDROP）同时上剪贴板：QQ 输入框对两种格式的
    // 接收行为不同版本有差异，双格式保证预览能生成（2026-10-25 实测单格式都曾落空）
    private static void SetClipboardImageAndFile(string path)
    {
        using var img = System.Drawing.Image.FromFile(path);
        var data = new DataObject();
        data.SetImage((System.Drawing.Bitmap)img);
        var files = new System.Collections.Specialized.StringCollection { path };
        data.SetFileDropList(files);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true, 10, 100);
                return;
            }
            catch { Thread.Sleep(120); }
        }
        throw new InvalidOperationException("剪贴板写入失败");
    }
}




