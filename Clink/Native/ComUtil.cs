using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;

namespace Clink.Native;

/// <summary>Small helpers for the raw-pointer COM style that CsWin32 generates with allowMarshaling=false.</summary>
internal static unsafe class ComUtil
{
    public static void Check(this HRESULT hr, string what)
    {
        if (hr.Failed)
            throw new COMException($"{what} failed: 0x{(uint)hr.Value:X8} ({Marshal.GetPInvokeErrorMessage(hr.Value)})", hr.Value);
    }

    public static void Release<T>(ref T* p) where T : unmanaged
    {
        if (p != null)
        {
            ((IUnknown*)p)->Release();
            p = null;
        }
    }

    public static void AddRef<T>(T* p) where T : unmanaged
    {
        if (p != null) ((IUnknown*)p)->AddRef();
    }

    public static T* Query<T>(void* unknown, string what = "QueryInterface") where T : unmanaged, IComIID
    {
        Guid iid = T.Guid;
        void* result;
        ((IUnknown*)unknown)->QueryInterface(&iid, &result).Check(what);
        return (T*)result;
    }

    public static T* TryQuery<T>(void* unknown) where T : unmanaged, IComIID
    {
        Guid iid = T.Guid;
        void* result;
        return ((IUnknown*)unknown)->QueryInterface(&iid, &result).Succeeded ? (T*)result : null;
    }

    public static Guid Iid<T>() where T : IComIID => T.Guid;

    /// <summary>Converts a managed string to a WinRT HSTRING. Caller must delete.</summary>
    public static HSTRING CreateHString(string s)
    {
        PInvoke.WindowsCreateString(s, (uint)s.Length, out HSTRING h).Check("WindowsCreateString");
        return h;
    }

    public static long QpcNow100ns()
    {
        // Stopwatch uses QueryPerformanceCounter on Windows; convert to 100ns units (same base WGC/WASAPI use).
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        long freq = System.Diagnostics.Stopwatch.Frequency;
        // (ticks / freq) * 1e7 without overflow: split into whole seconds + remainder.
        long sec = ticks / freq;
        long rem = ticks % freq;
        return sec * 10_000_000L + rem * 10_000_000L / freq;
    }
}

/// <summary>
/// ComWrappers implementation so managed objects can be handed to native code as COM callbacks
/// (IActivateAudioInterfaceCompletionHandler for process-loopback WASAPI activation; IStream for the clip file
/// Media Foundation reads through <c>Engine.ClipSource</c>).
/// </summary>
internal sealed unsafe class ClinkComWrappers : ComWrappers
{
    public static readonly ClinkComWrappers Instance = new();

    private static readonly ComInterfaceEntry* s_activateEntries = BuildActivateEntries();
    private static readonly ComInterfaceEntry* s_streamEntries = BuildStreamEntries();

    /// <summary>
    /// IStream, ISequentialStream (the first two methods of IStream's vtable are its: one vtable serves both) and IAgileObject (Media
    /// Foundation calls the stream from its own work-queue threads, in no apartment of ours).
    /// </summary>
    private static ComInterfaceEntry* BuildStreamEntries()
    {
        var vtbl = (IStream.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ClinkComWrappers), sizeof(IStream.Vtbl));
        ComHelpers.PopulateIUnknown<IStream>((IUnknown.Vtbl*)vtbl);
        IStream.PopulateVTable(vtbl);

        var agile = (IUnknown.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ClinkComWrappers), sizeof(IUnknown.Vtbl));
        ComHelpers.PopulateIUnknown<IAgileObject>(agile);

        var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ClinkComWrappers), sizeof(ComInterfaceEntry) * 3);
        entries[0] = new ComInterfaceEntry { IID = IStream.IID_Guid, Vtable = (nint)vtbl };
        entries[1] = new ComInterfaceEntry { IID = ISequentialStream.IID_Guid, Vtable = (nint)vtbl };
        entries[2] = new ComInterfaceEntry { IID = IAgileObject.IID_Guid, Vtable = (nint)agile };
        return entries;
    }

    private static ComInterfaceEntry* BuildActivateEntries()
    {
        var vtbl = (IActivateAudioInterfaceCompletionHandler.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(ClinkComWrappers), sizeof(IActivateAudioInterfaceCompletionHandler.Vtbl));
        ComHelpers.PopulateIUnknown<IActivateAudioInterfaceCompletionHandler>((IUnknown.Vtbl*)vtbl);
        IActivateAudioInterfaceCompletionHandler.PopulateVTable(vtbl);

        var agile = (IUnknown.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ClinkComWrappers), sizeof(IUnknown.Vtbl));
        ComHelpers.PopulateIUnknown<IAgileObject>(agile);

        var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ClinkComWrappers), sizeof(ComInterfaceEntry) * 2);
        entries[0] = new ComInterfaceEntry { IID = IActivateAudioInterfaceCompletionHandler.IID_Guid, Vtable = (nint)vtbl };
        entries[1] = new ComInterfaceEntry { IID = IAgileObject.IID_Guid, Vtable = (nint)agile };
        return entries;
    }

    protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
    {
        if (obj is IActivateAudioInterfaceCompletionHandler.Interface)
        {
            count = 2;
            return s_activateEntries;
        }
        if (obj is IStream.Interface)
        {
            count = 3;
            return s_streamEntries;
        }
        throw new NotSupportedException($"No COM vtable for {obj.GetType()}");
    }

    protected override object? CreateObject(nint externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();

    protected override void ReleaseObjects(System.Collections.IEnumerable objects) => throw new NotSupportedException();
}

