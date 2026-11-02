using System.Runtime.InteropServices;

namespace QqAgent;

/// <summary>Windows 11 虚拟桌面（未公开 COM）。
/// GUID 取自 Grabacr07/VirtualDesktop（MIT）Build22000 适配层，仅声明用到的最小 vtable。</summary>
internal static class Vd
{
    // ---- 公开（文档化）IVirtualDesktopManager ----
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdm
    {
        [PreserveSig] int IsWindowOnCurrentDesktop(IntPtr hwnd, out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid id);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VdmCoClass { }

    // ---- Build22000 内部接口 ----
    [ComImport, Guid("536d3495-b208-4cc9-ae26-de8111275bf8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktop
    {
        [PreserveSig] int IsViewVisible(IntPtr view);   // 占位，不用
        Guid GetID();
    }

    // 仅作 MoveViewToDesktop 的参数类型用，不调用其方法。.NET（Core/5+）不支持
    // InterfaceIsIInspectable 封送，用 IUnknown + 3 个 IInspectable 占位槽对齐 vtable
    [ComImport, Guid("372e1d3b-38d3-42e4-a15b-8ab2b178f513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationView
    {
        int Placeholder_GetIids();
        int Placeholder_GetRuntimeClassName();
        int Placeholder_GetTrustLevel();
        int SetFocus();
        int SwitchTo();
    }

    [ComImport, Guid("1841c6d7-4f9d-42c0-af41-8747538f10e5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        void GetViews(out IObjectArray array);
        void GetViewsByZOrder(out IObjectArray array);
        void GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IObjectArray array);
        void GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    }

    [ComImport, Guid("b2f925b9-5a0f-4d2e-9f4d-2b1507593c10"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVdmInternal
    {
        int GetCount(IntPtr hWndOrMon);
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        [PreserveSig] int CanViewMoveDesktops(IntPtr pView);
        IntPtr GetCurrentDesktop(IntPtr hWndOrMon);
        void GetDesktops(IntPtr hWndOrMon, out IObjectArray desktops);
        IntPtr GetAdjacentDesktop(IntPtr desktop, int direction);
        void SwitchDesktop(IntPtr hWndOrMon, IntPtr desktop);
        IntPtr CreateDesktop(IntPtr hWndOrMon);
        void MoveDesktop(IntPtr desktop, IntPtr hWndOrMon, int index);
        void RemoveDesktop(IntPtr remove, IntPtr fallback);
    }

    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        uint GetCount();
        void GetAt(uint index, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderShell
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("c2f03a33-21f5-47fa-b4bb-156362a2f239")]
    private class ImmersiveShell { }

    private static readonly Guid ClsidVdmInternal = new("c5e0cdca-7b6e-41b2-9fc4-d93975cc467b");
    private static readonly Guid IidVdmInternal = new("b2f925b9-5a0f-4d2e-9f4d-2b1507593c10");
    private static readonly Guid IidIVirtualDesktop = new("536d3495-b208-4cc9-ae26-de8111275bf8");
    private static readonly Guid IidIApplicationViewCollection = new("1841c6d7-4f9d-42c0-af41-8747538f10e5");

    private static IServiceProviderShell Shell()
    {
        var shell = (IServiceProviderShell)new ImmersiveShell();
        return shell;
    }

    private static IVdmInternal GetInternal()
    {
        var shell = Shell();
        var svc = ClsidVdmInternal;
        var iid = IidVdmInternal;
        var hr = shell.QueryService(ref svc, ref iid, out var obj);
        Marshal.ThrowExceptionForHR(hr);
        return (IVdmInternal)obj;
    }

    /// <summary>桌面 GUID 列表（顺序即桌面顺序）。</summary>
    public static List<Guid> Desktops()
    {
        var vdm = GetInternal();
        vdm.GetDesktops(IntPtr.Zero, out var arr);
        var result = new List<Guid>();
        uint n = arr.GetCount();
        for (uint i = 0; i < n; i++)
        {
            var iid = IidIVirtualDesktop;
            arr.GetAt(i, ref iid, out var obj);
            result.Add(((IVirtualDesktop)obj).GetID());
        }
        return result;
    }

    /// <summary>新建桌面，返回其 GUID。</summary>
    public static Guid Create()
    {
        var vdm = GetInternal();
        var p = vdm.CreateDesktop(IntPtr.Zero);
        var d = (IVirtualDesktop)Marshal.GetObjectForIUnknown(p);
        var id = d.GetID();
        Marshal.Release(p);
        return id;
    }

    public static Guid WindowDesktop(IntPtr hwnd)
    {
        var vdm = (IVdm)new VdmCoClass();
        var hr = vdm.GetWindowDesktopId(hwnd, out var id);
        Marshal.ThrowExceptionForHR(hr);
        return id;
    }

    public static bool WindowOnCurrentDesktop(IntPtr hwnd)
    {
        var vdm = (IVdm)new VdmCoClass();
        var hr = vdm.IsWindowOnCurrentDesktop(hwnd, out var on);
        Marshal.ThrowExceptionForHR(hr);
        return on;
    }

    /// <summary>移动任意进程的窗口到指定桌面：GetViewForHwnd → MoveViewToDesktop
    /// （文档化的 MoveWindowToDesktop 只能移本进程窗口，跨进程 E_ACCESSDENIED）。</summary>
    public static void MoveWindow(IntPtr hwnd, Guid desktop)
    {
        var shell = Shell();
        var svc = IidIApplicationViewCollection;
        var hr = shell.QueryService(ref svc, ref svc, out var colObj);
        Marshal.ThrowExceptionForHR(hr);
        var col = (IApplicationViewCollection)colObj;
        col.GetViewForHwnd(hwnd, out var view);

        var vdm = GetInternal();
        vdm.GetDesktops(IntPtr.Zero, out var arr);
        uint n = arr.GetCount();
        for (uint i = 0; i < n; i++)
        {
            var iid = IidIVirtualDesktop;
            arr.GetAt(i, ref iid, out var dObj);
            var d = (IVirtualDesktop)dObj;
            if (d.GetID() != desktop) continue;
            vdm.MoveViewToDesktop(view, d);
            return;
        }
        throw new InvalidOperationException($"目标桌面不存在: {desktop}");
    }
}
