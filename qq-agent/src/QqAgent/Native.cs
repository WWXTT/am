using System.Text;
using System.Runtime.InteropServices;

namespace QqAgent;

internal static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    public const uint PW_RENDERFULLCONTENT = 0x00000002;   // Electron/Chromium 窗口必须带此 flag，否则黑屏

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
        WinEventDelegate proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    /// <summary>绕过前台锁把窗口真正置前（AttachThreadInput + Alt 抖动兜底）。</summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        ShowWindow(hwnd, 5);

        var fg = GetForegroundWindow();
        if (fg == hwnd) return true;

        GetWindowThreadProcessId(fg, out var fgPid);
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        var cur = GetCurrentThreadId();

        if (fgThread != 0) AttachThreadInput(cur, fgThread, true);
        BringWindowToTop(hwnd);
        var ok = SetForegroundWindow(hwnd);
        if (fgThread != 0) AttachThreadInput(cur, fgThread, false);

        if (!ok || GetForegroundWindow() != hwnd)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        Thread.Sleep(200);
        return GetForegroundWindow() == hwnd;
    }

    public delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint ms);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    public const uint EVENT_MIN = 0x8000;
    public const uint EVENT_MAX = 0x8010;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_PASTE = 0x0302;

    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);

    // SendInput 虚拟输入（真输入事件，Chromium 只认这个；投递式 WM_* 消息会被忽略）
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;   // 0=MOUSE 1=KEYBOARD
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const byte VK_RETURN = 0x0D;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>扫描码版按键（KEYEVENTF_SCANCODE）：面板级键盘监听（QQNT 录音面板的空格/Esc）
    /// 不认 vk 注入只认扫描码——vk 版 SendKey 只对编辑器文本输入有效（2026-10-25 实测空格全部落空）。
    /// 常用扫描码：ESC=0x01 SPACE=0x39。</summary>
    public static void SendKeyScan(byte scanCode, bool down)
    {
        var inp = new INPUT { type = 1 };
        inp.ki.wScan = scanCode;
        inp.ki.dwFlags = down ? KEYEVENTF_SCANCODE : KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP;
        _ = SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>以 KEYEVENTF_UNICODE 真键序注入单个字符（绕过 IME；Chromium 编辑器认作键盘输入，
    /// @成员面板等按键过滤的弹层只认这条路径，粘贴会关面板——2026-10-25 实测）。</summary>
    public static void SendCharUnicode(char c)
    {
        var down = new INPUT { type = 1 };
        down.ki.wScan = c;
        down.ki.dwFlags = KEYEVENTF_UNICODE;
        var up = new INPUT { type = 1 };
        up.ki.wScan = c;
        up.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        _ = SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint mode);

    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr updateRect, IntPtr updateRgn, uint flags);
    public const uint RDW_INVALIDATE = 0x0001, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    // 2026-10-27 小屏专属：窗口所在显示器（档位改成就地铺满，不再写死屏幕坐标）
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>关掉控制台 QuickEdit（2026-10-27）。控制台被鼠标点一下就进"选择"模式，
    /// 此时任何 stdout 写入都会阻塞整个进程——daemon 实测卡在启动、心跳停更，窗口标题变成
    /// 「选择 …」。启动时调一次即可，代价是窗口内不能再用鼠标选中复制。</summary>
    public static void DisableConsoleQuickEdit()
    {
        try
        {
            var h = GetStdHandle(-10);   // STD_INPUT_HANDLE
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return;
            if (!GetConsoleMode(h, out var mode)) return;
            SetConsoleMode(h, mode & ~0x0040u);   // ENABLE_QUICK_EDIT_MODE
        }
        catch { }
    }

    public static void DrainMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public static List<(IntPtr Hwnd, RECT Rect, string Title)> TopLevelWindows(HashSet<uint> pids)
    {
        var result = new List<(IntPtr, RECT, string)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            if (!cls.ToString().StartsWith("Chrome_WidgetWin_1")) return true;
            GetWindowRect(h, out var r);
            var title = new StringBuilder(128);
            GetWindowText(h, title, 128);
            result.Add((h, r, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

