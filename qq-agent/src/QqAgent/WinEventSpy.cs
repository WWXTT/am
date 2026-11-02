using System.Collections.Concurrent;

namespace QqAgent;

internal sealed class WinEventSpy : IDisposable
{
    private readonly List<IntPtr> _hooks = new();
    private readonly ConcurrentQueue<uint> _events = new();
    private int _count;

    // 必须用字段持有委托，否则被 GC 回收后回调会崩（Process terminated）
    private Native.WinEventDelegate? _callback;

    public int Count => Volatile.Read(ref _count);

    public void Start(int pid)
    {
        _callback = new Native.WinEventDelegate(OnEvent);
        GC.KeepAlive(_callback);

        var h = Native.SetWinEventHook(Native.EVENT_MIN, Native.EVENT_MAX, IntPtr.Zero,
            _callback, (uint)pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (h == IntPtr.Zero) throw new InvalidOperationException("SetWinEventHook 失败");
        _hooks.Add(h);
    }

    private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint ms)
    {
        Interlocked.Increment(ref _count);
        _events.Enqueue(type);
    }

    public int Drain(out Dictionary<uint, int> byType)
    {
        byType = new Dictionary<uint, int>();
        int n = 0;
        while (_events.TryDequeue(out var t))
        {
            byType[t] = byType.GetValueOrDefault(t) + 1;
            n++;
        }
        return n;
    }

    public void Dispose()
    {
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
        _callback = null;
    }
}
