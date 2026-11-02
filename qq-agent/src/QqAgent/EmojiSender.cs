using System.Windows.Automation;

namespace QqAgent;

/// <summary>QQ 表情面板的一键发送（2026-10-26 首版，2026-10-28 修 9.9.36 适配）：
/// 点「表情」→ 面板收藏列表里的 sticker 项 → 点「发送」→ 回读消息。
/// 面板控件：sticker-panel / sticker-container（name=收藏表情列表）/ collection-sticker（自定义表情）/ sticker-list-item（骰子等）。
/// 点击 sticker 是"插入输入框"而不是直接发送，所以必须再点一次「发送」（cls=send-msg）。
/// 2026-10-28 实测修掉两处：①面板项按「主屏」过滤，多屏 11520 宽的机器上整排被丢光；
/// ②按分类名「我的收藏」切 tab，9.9.36 里没有这个 tab 名（容器才叫收藏表情列表）。</summary>
internal static class EmojiSender
{
    public sealed record Item(string Name, string ClassName, double X, double Y, double W, double H);

    /// <summary>打开表情面板并列出可点的表情项（1 基）；打开后由调用方决定发送或关闭。</summary>
    public static (bool Ok, List<Item> Items, string Detail) OpenPanel(QqUi ui, string tab = "我的收藏")
    {
        try
        {
            if (!ui.PinForOperation()) return (false, new List<Item>(), "QQ 不在当前桌面或无法置前（先解锁桌面/还原窗口）");
            var panel = FindPanel();
            if (panel is null)
            {
                if (!ui.ClickButton("表情")) return (false, new List<Item>(), "找不到「表情」按钮");
                for (var i = 0; i < 15 && panel is null; i++)
                {
                    Thread.Sleep(200);
                    panel = FindPanel();
                }
            }
            if (panel is null) return (false, new List<Item>(), "表情面板未出现（点击后 3s 内没找到 sticker-panel）");
            if (!SelectTab(ui, panel, tab))
            {
                ClosePanel(ui);
                return (false, new List<Item>(), $"切不到表情分类「{tab}」");
            }
            var items = CollectItems(panel);
            return (items.Count > 0, items, items.Count == 0 ? "面板里没有可点表情（我的收藏为空？）" : $"面板表情 {items.Count} 个");
        }
        catch (Exception ex)
        {
            return (false, new List<Item>(), ex.Message);
        }
    }

    /// <summary>列表面板表情（不发送）；会打开再关闭面板。</summary>
    public static int List(string tab = "我的收藏")
    {
        var ui = QqUi.Attach();
        var (ok, items, detail) = OpenPanel(ui, tab);
        try
        {
            if (!ok) { Console.WriteLine("[sendemoji] " + detail); return 1; }
            for (var i = 0; i < items.Count; i++)
                Console.WriteLine($"{i + 1}\t{items[i].Name}\t{items[i].ClassName}\t({(int)items[i].X},{(int)items[i].Y})");
            return 0;
        }
        finally
        {
            ClosePanel(ui);
            ui.UnpinAfterOperation();
        }
    }

    /// <summary>发送第 index 个（1 基，默认 1）表情到当前会话；不切会话（调用方先核 header）。
    /// name 非空时按备注匹配（先全等再包含），命中后覆盖 index——备注会随收藏增删变化，按名字比按序号稳。</summary>
    public static (bool Ok, string Detail) Send(int index = 1, bool dryRun = false, string tab = "我的收藏", string? name = null)
    {
        var ui = QqUi.Attach();
        var before = ui.Messages();
        var beforeCount = before.Count;
        var (ok, items, detail) = OpenPanel(ui, tab);
        if (!ok)
        {
            ClosePanel(ui);
            ui.UnpinAfterOperation();
            return (false, detail);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var hit = items.FindIndex(i => string.Equals(i.Name, name, StringComparison.Ordinal));
            if (hit < 0) hit = items.FindIndex(i => i.Name.Contains(name!, StringComparison.Ordinal));
            if (hit < 0)
            {
                var have = string.Join(" / ", items.Select(i => i.Name));
                ClosePanel(ui);
                ui.UnpinAfterOperation();
                return (false, $"面板里没有叫「{name}」的表情（现有：{have}）");
            }
            index = hit + 1;
        }
        if (index < 1 || index > items.Count)
        {
            ClosePanel(ui);
            ui.UnpinAfterOperation();
            return (false, $"index={index} 越界（1..{items.Count}）");
        }
        var item = items[index - 1];
        if (dryRun)
        {
            ClosePanel(ui);
            ui.UnpinAfterOperation();
            return (true, $"dry-run：会点第 {index} 个「{item.Name}」（{item.ClassName}）");
        }

        try
        {
            ui.ClickPoint((int)(item.X + item.W / 2), (int)(item.Y + item.H / 2));
            Thread.Sleep(600);
            // sticker 是插入输入框：必须点发送
            if (!ui.ClickButton("发送")) return (false, "点表情后找不到「发送」按钮");
            Thread.Sleep(1400);

            // 2026-10-29 修误报：原来只看「消息条数是否变多」，可消息区是虚拟列表，滚动/裁剪会让条数变少
            // （实测 00:16 已发出却被判 新增 -2 → 报 FAIL）。改成看尾部是否变了。
            var after = ui.Messages();
            var newer = after.Count - beforeCount;
            var landed = StickerLanded(before, after);
            if (!landed)
            {
                Thread.Sleep(1200);          // 渲染慢的再给一次机会
                after = ui.Messages();
                newer = after.Count - beforeCount;
                landed = StickerLanded(before, after);
            }
            var lastSelf = after.LastOrDefault(m => m.IsSelf);
            return landed
                ? (true, $"已发送「{item.Name}」（条数 {newer:+#;-#;0}，尾条 ← '{after[^1].Text}'）")
                : (false, $"已点发送但没看到新表情（条数 {newer:+#;-#;0}，最后自发='{lastSelf?.Text}'）");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            ClosePanel(ui);
            ui.UnpinAfterOperation();
        }
    }

    /// <summary>判断刚发的表情是否落到会话尾部。列表是虚拟化的，条数不作为依据：
    /// 只要「尾条变成了自己的一条图片/表情」就算发出（旧实现用条数增量，滚动裁剪时会误报失败）。</summary>
    private static bool StickerLanded(IReadOnlyList<MessageItem> before, IReadOnlyList<MessageItem> after)
    {
        if (after.Count == 0) return false;
        var last = after[^1];
        if (!last.IsSelf) return false;
        var isPic = last.Text.Contains("[图片]") || last.Text.Contains("[动画表情]") || last.Text.Length == 0;
        if (!isPic) return false;
        if (before.Count == 0) return true;
        var prev = before[^1];
        return !(prev.IsSelf == last.IsSelf && prev.Text == last.Text && prev.Time == last.Time);
    }

    private static AutomationElement? FindPanel()
    {
        try
        {
            var global = QqUi.FindByClassContains(AutomationElement.RootElement, "sticker-panel");
            if (global is not null) return global;
            var ui = QqUi.Attach();
            return QqUi.FindByClassContains(ui.Root, "sticker-panel");
        }
        catch { return null; }
    }

    /// <summary>切到收藏分类。9.9.36 的 tab 不一定叫「我的收藏」，所以先看容器名（收藏表情列表）判断是否已在收藏；
    /// 不在才按名字找 tab（含「收藏」即可）。找不到也不硬失败——只要面板里有 collection-sticker 就照用。</summary>
    private static bool SelectTab(QqUi ui, AutomationElement panel, string tab)
    {
        try
        {
            if (OnCollectionTab(panel)) return true;
            foreach (AutomationElement el in panel.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string cls, name;
                try { cls = el.Current.ClassName ?? ""; name = el.Current.Name ?? ""; } catch { continue; }
                if (!cls.Contains("tabs-container-item", StringComparison.Ordinal)) continue;
                var match = string.Equals(name, tab, StringComparison.Ordinal)
                            || (name.Contains("收藏", StringComparison.Ordinal) && tab.Contains("收藏", StringComparison.Ordinal));
                if (!match) continue;
                if (cls.Contains("tabs-container-item-active", StringComparison.Ordinal)) return true;
                var r = el.Current.BoundingRectangle;
                ui.ClickPoint((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                Thread.Sleep(700);
                return true;
            }
            // 兜底：面板已经在收藏容器里（常见：打开就是收藏页），或者压根没有分类栏
            return OnCollectionTab(panel) || FindContainer(panel) is not null;
        }
        catch { return false; }
    }

    /// <summary>面板当前是否停在收藏列表（容器 name 含「收藏」）。</summary>
    private static bool OnCollectionTab(AutomationElement panel)
    {
        var c = FindContainer(panel);
        if (c is null) return false;
        try { return (c.Current.Name ?? "").Contains("收藏", StringComparison.Ordinal); } catch { return false; }
    }

    private static AutomationElement? FindContainer(AutomationElement panel)
        => QqUi.FindByClassContains(panel, "sticker-container");

    private static List<Item> CollectItems(AutomationElement panel)
    {
        var list = new List<Item>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (AutomationElement el in panel.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string cls, name;
                Rect r;
                try
                {
                    cls = el.Current.ClassName ?? "";
                    name = el.Current.Name ?? "";
                    var br = el.Current.BoundingRectangle;
                    r = new Rect(br.X, br.Y, br.Width, br.Height);
                }
                catch { continue; }
                var isItem = cls.Contains("collection-sticker", StringComparison.Ordinal) && !cls.Contains("img", StringComparison.Ordinal)
                             || cls.Contains("sticker-list-item", StringComparison.Ordinal) && !cls.Contains("img", StringComparison.Ordinal);
                if (!isItem) continue;
                if (r.W < 20 || r.H < 20) continue;
                // 只收屏内可见项（面板在屏外/虚拟化时坐标会是负的）。
                // 2026-10-28 修：原来按 PrimaryScreen.WorkingArea 过滤，多屏 11520x2160 的机器上面板在 x>10000，
                // 整排收藏表情被当成「屏外」丢光 → 现在用虚拟桌面范围。
                var wa = System.Windows.Forms.SystemInformation.VirtualScreen;
                if (r.X < wa.Left || r.Y < wa.Top || r.X + r.W > wa.Right || r.Y + r.H > wa.Bottom) continue;
                var key = $"{(int)r.X},{(int)r.Y}";
                if (!seen.Add(key)) continue;
                list.Add(new Item(string.IsNullOrWhiteSpace(name) ? "(未命名)" : name, cls, r.X, r.Y, r.W, r.H));
            }
        }
        catch { }
        // 按面板视觉顺序（先 y 后 x）
        return list.OrderBy(i => i.Y).ThenBy(i => i.X).ToList();
    }

    private static void ClosePanel(QqUi ui)
    {
        try
        {
            var panel = FindPanel();
            if (panel is null) return;
            ui.ClickButton("表情");   // 再点一次收起
            Thread.Sleep(250);
        }
        catch { }
    }

    private readonly record struct Rect(double X, double Y, double W, double H);
}





