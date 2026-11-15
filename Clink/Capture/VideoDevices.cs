using Clink.Native;
using Windows.Win32;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Clink.Capture;

/// <summary>A video capture device (a capture card, a webcam): the stable Media Foundation symbolic link, the name it shows and the Windows container of the physical device (pairs it with its audio endpoint; null when unknown).</summary>
internal sealed record VideoDevice(string Link, string Name, Guid? ContainerId = null);

/// <summary>Lists the video capture devices through Media Foundation and reads the modes one offers.</summary>
internal static unsafe class VideoDevices
{
    /// <summary>The "first video stream" index of a source reader.</summary>
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint TransferFunctionPq = 15;   // MFVideoTransFunc_2084
    private static readonly HRESULT MF_E_NO_MORE_TYPES = (HRESULT)unchecked((int)0xC00D36B9);

    /// <summary>
    /// The video capture devices connected now, in the order Windows lists them; empty when there are none. Null when the listing failed
    /// (logged): that says nothing about the cards, so a caller must not take it for "no card" and switch away from one.
    /// </summary>
    public static IReadOnlyList<VideoDevice>? Enumerate()
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
                result.Add(new VideoDevice(link, GetString(a, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? "Video device", ContainerOf(link)));
            }
        }
        catch (Exception ex) { Log.Warn("video devices: could not list them: " + ex.Message); return null; }
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
        IMFMediaSource* source = null;
        IMFSourceReader* reader = null;
        try
        {
            source = CreateSource(link);
            PInvoke.MFCreateSourceReaderFromMediaSource(source, null, &reader).Check("MFCreateSourceReaderFromMediaSource");
            for (uint i = 0; ; i++)
            {
                IMFMediaType* t;
                HRESULT hr = reader->GetNativeMediaType(FirstVideoStream, i, &t);
                if (hr == MF_E_NO_MORE_TYPES) break;
                hr.Check("GetNativeMediaType");
                try { if (ModeOf(t) is { } mode) modes.Add(mode); }
                finally { ((IUnknown*)t)->Release(); }
            }
        }
        finally
        {
            if (reader != null) ((IUnknown*)reader)->Release();
            if (source != null) { source->Shutdown(); ((IUnknown*)source)->Release(); }
        }
        return modes;
    }

    /// <summary>Opens the device with this symbolic link (the caller shuts it down and releases it). Throws when it cannot be opened.</summary>
    internal static IMFMediaSource* CreateSource(string link)
    {
        Mf.EnsureStarted();
        IMFAttributes* attrs = Mf.CreateAttributes(2);
        try
        {
            Mf.SetGuid(attrs, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            Guid key = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
            fixed (char* p = link) attrs->SetString(&key, new PCWSTR(p)).Check("SetString");
            IMFMediaSource* source;
            PInvoke.MFCreateDeviceSource(attrs, &source).Check("MFCreateDeviceSource");
            return source;
        }
        finally { ((IUnknown*)attrs)->Release(); }
    }

    /// <summary>The mode a native media type of a card describes; null when it has no size or frame rate.</summary>
    internal static VideoMode? ModeOf(IMFMediaType* t)
    {
        var a = (IMFAttributes*)t;
        ulong size = Mf.GetU64(a, PInvoke.MF_MT_FRAME_SIZE), rate = Mf.GetU64(a, PInvoke.MF_MT_FRAME_RATE);
        uint num = (uint)(rate >> 32), den = (uint)rate;
        if (den == 0 || num == 0) return null;
        return new VideoMode((uint)(size >> 32), (uint)size, (double)num / den,
            VideoModePick.FormatOf(Mf.GetGuid(a, PInvoke.MF_MT_SUBTYPE)),
            Mf.GetU32(a, PInvoke.MF_MT_TRANSFER_FUNCTION) == TransferFunctionPq);
    }

    /// <summary>Whether a device with this link is connected now; null when the listing failed (unknown).</summary>
    public static bool? IsConnected(string link) =>
        Enumerate() is { } list ? list.Any(d => string.Equals(d.Link, link, StringComparison.OrdinalIgnoreCase)) : null;

    // DEVPKEY_Device_InstanceId on a device interface; DEVPKEY_Device_ContainerId on a device (the same key as the audio endpoint's PKEY_Device_ContainerId).
    private static readonly Guid InstanceIdFmt = new("78c34fc8-104a-4aca-9ea4-524d52996e57"), ContainerIdFmt = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");

    /// <summary>
    /// The container of the physical device behind a video device's symbolic link (what its audio endpoint shares), or null when Windows
    /// does not say. Interface link to device instance to container.
    /// </summary>
    internal static Guid? ContainerOf(string link)
    {
        try
        {
            var instKey = new DEVPROPKEY { fmtid = InstanceIdFmt, pid = 256 };
            DEVPROPTYPE type;
            uint size = 0;
            fixed (char* l = link)
            {
                PInvoke.CM_Get_Device_Interface_Property(l, &instKey, &type, null, &size, 0);   // CR_BUFFER_SMALL: size says how much
                if (size < 2 || size > 4096) return null;
                var buf = new byte[size];
                fixed (byte* b = buf)
                {
                    if (PInvoke.CM_Get_Device_Interface_Property(l, &instKey, &type, b, &size, 0) != 0) return null;
                    fixed (char* inst = new string(System.Text.Encoding.Unicode.GetChars(buf, 0, (int)size)).TrimEnd('\0'))
                    {
                        uint node;
                        if (PInvoke.CM_Locate_DevNode(&node, inst, 0) != 0) return null;
                        var key = new DEVPROPKEY { fmtid = ContainerIdFmt, pid = 2 };
                        Guid id; uint idSize = (uint)sizeof(Guid);
                        if (PInvoke.CM_Get_DevNode_Property(node, &key, &type, (byte*)&id, &idSize, 0) != 0 || idSize != (uint)sizeof(Guid)) return null;
                        return id == Guid.Empty ? null : id;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn("video devices: no container for a device: " + ex.Message); return null; }
    }

    private static string? GetString(IMFAttributes* a, Guid key)
    {
        PWSTR s; uint len;
        if (a->GetAllocatedString(&key, &s, &len).Failed) return null;
        try { return s.ToString(); }
        finally { PInvoke.CoTaskMemFree(s.Value); }
    }
}
