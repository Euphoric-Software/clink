using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Clink.Native;

/// <summary>Media Foundation helpers over the raw IMFAttributes vtable.</summary>
internal static unsafe class Mf
{
    private static int s_started;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref s_started, 1) == 0)
            PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).Check("MFStartup");
    }

    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;
    public const uint METransformDrainComplete = 603;
    public const uint METransformMarker = 604;
    public const uint MEError = 1;

    public static readonly HRESULT MF_E_ATTRIBUTENOTFOUND = (HRESULT)unchecked((int)0xC00D36E6);
    public static readonly HRESULT MF_E_NO_EVENTS_AVAILABLE = (HRESULT)unchecked((int)0xC00D3E80);

    public static void SetGuid(IMFAttributes* a, Guid key, Guid value) => a->SetGUID(&key, &value).Check("SetGUID");
    public static void SetU32(IMFAttributes* a, Guid key, uint value) => a->SetUINT32(&key, value).Check("SetUINT32");
    public static void SetU64(IMFAttributes* a, Guid key, ulong value) => a->SetUINT64(&key, value).Check("SetUINT64");
    public static void SetSize(IMFAttributes* a, Guid key, uint w, uint h) => SetU64(a, key, ((ulong)w << 32) | h);
    public static void SetRatio(IMFAttributes* a, Guid key, uint num, uint den) => SetU64(a, key, ((ulong)num << 32) | den);
    public static void SetBlob(IMFAttributes* a, Guid key, ReadOnlySpan<byte> blob)
    {
        fixed (byte* p = blob) a->SetBlob(&key, p, (uint)blob.Length).Check("SetBlob");
    }
    public static void SetUnknown(IMFAttributes* a, Guid key, void* unk) => a->SetUnknown(&key, (IUnknown*)unk).Check("SetUnknown");

    public static uint GetU32(IMFAttributes* a, Guid key, uint fallback = 0)
    {
        uint v;
        return a->GetUINT32(&key, &v).Succeeded ? v : fallback;
    }
    public static ulong GetU64(IMFAttributes* a, Guid key, ulong fallback = 0)
    {
        ulong v;
        return a->GetUINT64(&key, &v).Succeeded ? v : fallback;
    }
    public static Guid GetGuid(IMFAttributes* a, Guid key)
    {
        Guid v;
        return a->GetGUID(&key, &v).Succeeded ? v : Guid.Empty;
    }
    public static byte[]? GetBlob(IMFAttributes* a, Guid key)
    {
        byte* p; uint size;
        if (a->GetAllocatedBlob(&key, &p, &size).Failed) return null;
        var result = new byte[size];
        Marshal.Copy((nint)p, result, 0, (int)size);
        PInvoke.CoTaskMemFree(p);
        return result;
    }

    public static IMFMediaType* CreateMediaType()
    {
        IMFMediaType* t;
        PInvoke.MFCreateMediaType(&t).Check("MFCreateMediaType");
        return t;
    }

    public static IMFAttributes* CreateAttributes(uint size)
    {
        IMFAttributes* a;
        PInvoke.MFCreateAttributes(&a, size).Check("MFCreateAttributes");
        return a;
    }

    /// <summary>Creates a sample wrapping a fresh memory buffer with a copy of <paramref name="data"/>.</summary>
    public static IMFSample* CreateMemorySample(ReadOnlySpan<byte> data, long time100ns, long duration100ns)
    {
        IMFMediaBuffer* buf;
        PInvoke.MFCreateMemoryBuffer((uint)Math.Max(1, data.Length), &buf).Check("MFCreateMemoryBuffer");
        try
        {
            byte* p; uint max, cur;
            buf->Lock(&p, &max, &cur).Check("IMFMediaBuffer.Lock");
            data.CopyTo(new Span<byte>(p, data.Length));
            buf->Unlock();
            buf->SetCurrentLength((uint)data.Length);
            IMFSample* s;
            PInvoke.MFCreateSample(&s).Check("MFCreateSample");
            s->AddBuffer(buf).Check("AddBuffer");
            s->SetSampleTime(time100ns);
            s->SetSampleDuration(duration100ns);
            return s;
        }
        finally { buf->Release(); }
    }

    /// <summary>Copies all bytes of a sample into a managed array.</summary>
    public static byte[] ReadSampleBytes(IMFSample* sample)
    {
        IMFMediaBuffer* buf;
        sample->ConvertToContiguousBuffer(&buf).Check("ConvertToContiguousBuffer");
        try
        {
            byte* p; uint max, cur;
            buf->Lock(&p, &max, &cur).Check("IMFMediaBuffer.Lock");
            var r = new byte[cur];
            Marshal.Copy((nint)p, r, 0, (int)cur);
            buf->Unlock();
            return r;
        }
        finally { buf->Release(); }
    }
}
