using Clink.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Clink.Capture;

/// <summary>A video capture device (a capture card, a webcam): the stable Media Foundation symbolic link and the name it shows.</summary>
internal sealed record VideoDevice(string Link, string Name);

/// <summary>Lists the video capture devices through Media Foundation and reads the modes one offers.</summary>
internal static unsafe class VideoDevices
{
    /// <summary>The "first video stream" index of a source reader.</summary>
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint TransferFunctionPq = 15;   // MFVideoTransFunc_2084
    private static readonly HRESULT MF_E_NO_MORE_TYPES = (HRESULT)unchecked((int)0xC00D36B9);

    /// <summary>The video capture devices connected now, in the order Windows lists them. Empty when there are none or the listing fails (logged).</summary>
    public static IReadOnlyList<VideoDevice> Enumerate()
    {
        var result = new List<VideoDevice>();
        IMFAttributes* attrs = null;
        IMFActivate** list = null;
        uint count = 0;
        try
        {
            Mf.EnsureStarted();
            attrs = Mf.CreateAttributes(1);
            Mf.SetGuid(attrs, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            PInvoke.MFEnumDeviceSources(attrs, &list, &count).Check("MFEnumDeviceSources");
            for (uint i = 0; i < count; i++)
            {
                var a = (IMFAttributes*)list[i];
                string? link = GetString(a, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                if (string.IsNullOrEmpty(link)) continue;
                result.Add(new VideoDevice(link, GetString(a, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? "Video device"));
            }
        }
        catch (Exception ex) { Log.Warn("video devices: could not list them: " + ex.Message); }
        finally
        {
            for (uint i = 0; list != null && i < count; i++) ((IUnknown*)list[i])->Release();
            if (list != null) PInvoke.CoTaskMemFree(list);
            if (attrs != null) ((IUnknown*)attrs)->Release();
        }
        return result;
    }

    /// <summary>
    /// The modes the device offers, read by opening it briefly. Throws when it cannot be opened (in use by another app, unplugged).
    /// </summary>
    public static IReadOnlyList<VideoMode> ReadModes(string link)
    {
        var modes = new List<VideoMode>();
        Mf.EnsureStarted();
        IMFAttributes* attrs = null;
        IMFMediaSource* source = null;
        IMFSourceReader* reader = null;
        try
        {
            attrs = Mf.CreateAttributes(2);
            Mf.SetGuid(attrs, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            Guid key = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
            fixed (char* p = link) attrs->SetString(&key, new PCWSTR(p)).Check("SetString");
            PInvoke.MFCreateDeviceSource(attrs, &source).Check("MFCreateDeviceSource");
            PInvoke.MFCreateSourceReaderFromMediaSource(source, null, &reader).Check("MFCreateSourceReaderFromMediaSource");
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                HRESULT hr = reader->GetNativeMediaType(FirstVideoStream, i, &t);
                if (hr == MF_E_NO_MORE_TYPES) break;
                hr.Check("GetNativeMediaType");
                try
                {
                    var a = (IMFAttributes*)t;
                    ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE), rate = Mf.GetU64(a, PInvoke.MF_MT_FRAME_RATE);
                    uint num = (uint)(rate >> 32), den = (uint)rate;
                    if (den == 0 || num == 0) continue;
                    modes.Add(new VideoMode((uint)(size >> 32), (uint)size, (double)num / den,
                        VideoModePick.FormatOf(Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE)),
                        Mf.GetU32(a, PInvoke.MF_MT_TRANSFER_FUNCTION) == TransferFunctionPq));
                }
                finally { ((IUnknown*)t)->Release(); }
            }
        }
        finally
        {
            if (reader != null) ((IUnknown*)reader)->Release();
            if (source != null) { source->Shutdown(); ((IUnknown*)source)->Release(); }
            if (attrs != null) ((IUnknown*)attrs)->Release();
        }
        return modes;
    }

    private static string? GetString(IMFAttributes* a, Guid key)
    {
        PWSTR s; uint len;
        if (a->GetAllocatedString(&key, &s, &len).Failed) return null;
        try { return s.ToString(); }
        finally { PInvoke.CoTaskMemFree(s.Value); }
    }
}
