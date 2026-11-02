using System.Windows.Automation;

namespace QqAgent;

/// <summary>转发阅读的两进程接力（2026-10-25 定案）。
/// 物化规律：查看器离屏正文要「无 UIA 客户端连接」静置数十秒后，Chromium 才发布全量文本；
/// 活着的客户端连接（哪怕只是持有引用不查询）会一直抑制发布。
/// 因此：进程 A 只负责 Invoke 开窗后立刻退出；外部静置；进程 B 全新连接首查即得全文。</summary>
internal static class Fwd
{
    /// <summary>阶段 1：Invoke 开查看器，返回句柄与元信息，不做任何读取。
    /// 识别：查看器标题=卡片标题（精确匹配）；新窗口初始标题是 'QQ'，改名要几秒到几十秒
    /// （重启后的 QQ 尤其慢），所以按标题轮询而不是追新窗口。</summary>
    public static (IntPtr Hwnd, string Title, int Expected) Open(QqUi ui, int indexFromEnd)
    {
        var cards = ui.ForwardCards();
        if (cards.Count == 0)
            throw new InvalidOperationException("当前会话没有可见的转发消息卡片");
        var card = cards[^indexFromEnd];
        string title = card.Title;
        int expected = card.Count;
        if (title.Length == 0)
            throw new InvalidOperationException("转发卡片没有标题，无法识别查看器窗口");

        if (card.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var ip) &&
            ip is InvokePattern inv)
        {
            inv.Invoke();
            Console.WriteLine("[fwd1] card invoked");
        }
        else
            throw new InvalidOperationException("转发卡片不支持 InvokePattern（重开会话或重启 QQ 试试）");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var (hwnd, rect, wtitle) in QqUi.Windows())
            {
                if (wtitle != title) continue;
                if (rect.Right - rect.Left < 400 || rect.Bottom - rect.Top < 300) continue;
                Console.WriteLine($"[fwd1] viewer hwnd={hwnd} title='{title}' expected={expected}");
                return (hwnd, title, expected);
            }
            Thread.Sleep(300);
        }
        throw new InvalidOperationException($"查看器 30s 内未出现标题'{title}'（先 closefwd 清掉遗留查看器再试）");
    }

    /// <summary>阶段 2：全新进程里调用。首查 → 不全则再静置一轮 → 关窗。</summary>
    public static ForwardContent Read(IntPtr viewerHwnd, string title, int expected, bool keepOpen)
    {
        // 查看器沉底：阅读的几秒里它藏在用户窗口后面（弹出→短暂操作→隐藏）
        Native.SetWindowPos(viewerHwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            0x0002 /*SWP_NOMOVE*/ | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        var viewer = AutomationElement.FromHandle(viewerHwnd);
        var msgs = new List<ForwardMessage>();
        // 新开查看器的 a11y 树要几秒才开始发布（先有容器结构，正文 Name 随手可得——
        // ControlType 过滤 bug 修复后无需任何静置），等树出现即读
        for (int i = 0; i < 30; i++)
        {
            var scroll = QqUi.FindByClassContains(viewer, "q-scroll-view") ?? viewer;
            msgs = QqUi.ParseViewerMessages(scroll);
            if (msgs.Count > 0) break;
            Thread.Sleep(500);
        }
        Console.WriteLine($"[fwd2] parsed: {msgs.Count} msgs, needMore={QqUi.NeedsMoreScroll(msgs, expected)}");

        if (!keepOpen)
        {
            if (QqUi.NeedsMoreScroll(msgs, expected))
                Console.WriteLine("[fwd2] 首查未全（静置不足或仍被抑制），按现状返回");
            Native.PostMessage(viewerHwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(300);
        }
        else Console.WriteLine($"[fwd2] keep-open hwnd={viewerHwnd}");

        return new ForwardContent(title, msgs, expected);
    }
}
