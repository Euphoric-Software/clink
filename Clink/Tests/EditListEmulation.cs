using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Clink.Engine;
using Clink.Native;

namespace Clink.Tests;

/// <summary>
/// Makes this machine's Media Foundation behave like Windows Server 2025's (GitHub's windows-latest), which applies the audio edit
/// lists of an MP4 where Windows 11 ignores them. That was inferred from the CI log of run 39829947787 (twelve audio tests
/// failed there and none here) and checked by reproducing every one of its numbers: a source reader on a clip whose audio edit lists
/// Media Foundation can see hands out each audio sample at <c>max(0, time + the track's start offset)</c> (the start offset is an empty
/// edit minus the media time of the first real one, <see cref="AudioTrackInfo.StartOffset"/>), and a seek takes a time on that moved
/// timeline. It is a decorator of the reader <see cref="ClipSource"/> hands out, installed through <see cref="ClipSource.Hook"/>:
/// <list type="bullet">
/// <item>A reader opened with the edit lists hidden (<see cref="ClipSource.Open(string, IMFAttributes*, out IDisposable?)"/> does that for
/// every Clink clip) is left alone: Media Foundation has no audio edit list to apply. The whole suite has to pass this way, which shows
/// that no audio is read any other way.</item>
/// <item><c>evenWhenHidden</c> moves the tracks of those readers too: a Windows whose media reader moves the tracks in a way that hiding
/// the edit lists does not stop, for the timeline self-check to fail on.</item>
/// <item><c>hideEditLists: false</c> is Clink without <see cref="ClipSource"/>, whose readers see the edit lists: with it the twelve audio
/// tests of that CI run fail again.</item>
/// </list>
/// The environment variable CLINK_EMULATE_ELST turns it on for a whole run, of the unit tests or of the headless --trim and --probe:
/// <c>1</c> is the first case, <c>nohide</c> the third (and takes the timeline self-check out, as Clink had none then). It assumes the
/// real Media Foundation ignores the edit lists (this PC's does): on one that applies them the readers would be moved twice.
/// </summary>
internal sealed unsafe class EditListEmulation : IClipSourceHook
{
    internal const string Variable = "CLINK_EMULATE_ELST";

    private readonly bool _hide, _evenWhenHidden;

    private EditListEmulation(bool hide, bool evenWhenHidden)
    {
        _hide = hide;
        _evenWhenHidden = evenWhenHidden;
    }

    /// <summary>Whether CLINK_EMULATE_ELST asked for the emulation of this run ("1" or "nohide").</summary>
    internal static string? FromEnvironment
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(Variable);
            return value is "1" or "nohide" ? value : null;
        }
    }

    /// <summary>The emulation is on (in this run or inside an <see cref="Apply"/>).</summary>
    internal static bool Active => ClipSource.Hook is EditListEmulation;

    /// <summary>What the emulation is doing, for a test's report.</summary>
    internal static string Describe => ClipSource.Hook is EditListEmulation e
        ? $"emulating a Media Foundation that applies the audio edit lists{(e._hide ? "" : ", with Clink not hiding them")}{(e._evenWhenHidden ? ", hidden or not" : "")}"
        : "no emulation";

    [ModuleInitializer]
    internal static void StartFromEnvironment()
    {
        // Only a test run or a headless mode (checking a clip without a window) ever emulates: the app itself does not, whatever its environment says.
        var args = Environment.GetCommandLineArgs();
        if (args.Length < 2 || !(args[1].Equals("--unittest", StringComparison.OrdinalIgnoreCase) || args[1].Equals("--trim", StringComparison.OrdinalIgnoreCase)
            || args[1].Equals("--probe", StringComparison.OrdinalIgnoreCase))) return;
        string? mode = FromEnvironment;
        if (mode == null) return;
        ClipSource.Hook = new EditListEmulation(hide: mode != "nohide", evenWhenHidden: false);
        // "nohide" is Clink as it was before ClipSource: nothing hides the edit lists, and nothing checks the timeline either.
        if (mode == "nohide") ClipSource.TrustOverride = true;
    }

    /// <summary>
    /// Turns the emulation on until the returned scope is disposed (then what was there before is back). The timeline self-check is
    /// forgotten on the way in and out, so that it runs under it and nothing of it is left behind.
    /// </summary>
    internal static IDisposable Apply(bool hideEditLists = true, bool evenWhenHidden = false)
    {
        var before = ClipSource.Hook;
        ClipSource.Hook = new EditListEmulation(hideEditLists, evenWhenHidden);
        ClipSource.ResetTimelineCheck();
        return new Scope(() =>
        {
            ClipSource.Hook = before;
            ClipSource.ResetTimelineCheck();
        });
    }

    /// <summary>Takes the emulation away until the returned scope is disposed (a test of the emulation itself needs the real Media Foundation to compare with, also in a run that has it on).</summary>
    internal static IDisposable Without()
    {
        var before = ClipSource.Hook;
        ClipSource.Hook = null;
        ClipSource.ResetTimelineCheck();
        return new Scope(() =>
        {
            ClipSource.Hook = before;
            ClipSource.ResetTimelineCheck();
        });
    }

    private sealed class Scope : IDisposable
    {
        private Action? _undo;
        public Scope(Action undo) => _undo = undo;
        public void Dispose()
        {
            var undo = Interlocked.Exchange(ref _undo, null);
            undo?.Invoke();
        }
    }

    bool IClipSourceHook.HideEditLists(string path) => _hide;

    IMFSourceReader* IClipSourceHook.Wrap(string path, IMFSourceReader* reader, bool editListsHidden, ref IDisposable? owner)
    {
        if (editListsHidden && !_evenWhenHidden) return reader;   // Media Foundation has no audio edit list to apply
        EmulatedReader emulated;
        try { emulated = new EmulatedReader(path, reader); }
        catch
        {
            reader->Release();
            owner?.Dispose();
            throw;
        }
        nint identity = EmulationWrappers.Instance.GetOrCreateComInterfaceForObject(emulated, CreateComInterfaceFlags.None);
        Guid iid = IMFSourceReader.IID_Guid;
        int hr = Marshal.QueryInterface(identity, in iid, out nint comReader);
        Marshal.Release(identity);
        Marshal.ThrowExceptionForHR(hr);
        owner = new EmulatedOwner(emulated, owner);
        return (IMFSourceReader*)comReader;
    }

    /// <summary>What <see cref="ClipSource"/>'s callers dispose after releasing the reader: the real reader behind the decorator, then the stream it read through.</summary>
    private sealed class EmulatedOwner : IDisposable
    {
        private readonly EmulatedReader _reader;
        private IDisposable? _stream;

        public EmulatedOwner(EmulatedReader reader, IDisposable? stream)
        {
            _reader = reader;
            _stream = stream;
        }

        public void Dispose()
        {
            _reader.ReleaseInner();
            _stream?.Dispose();
            _stream = null;
        }
    }

    /// <summary>The decorator: every call goes to the real reader, but the timestamps of audio samples and the positions of seeks are on the moved timeline.</summary>
    private sealed class EmulatedReader : IMFSourceReader.Interface
    {
        private static readonly HRESULT Disposed = (HRESULT)unchecked((int)0x80131622);   // COR_E_OBJECTDISPOSED
        private IMFSourceReader* _inner;
        private readonly Dictionary<uint, long> _offset = new();   // the reader's stream number of each audio track -> where it starts on the clip's timeline, 100 ns

        public EmulatedReader(string path, IMFSourceReader* inner)
        {
            _inner = inner;
            var tracks = Trimmer.ReadClipTracks(path);
            var streams = Trimmer.AudioStreamsOf(inner);
            if (tracks != null && tracks.Audio.Length == streams.Count)
                for (int i = 0; i < streams.Count; i++) _offset[streams[i]] = tracks.Audio[i].StartOffset;
        }

        public void ReleaseInner()
        {
            var inner = _inner;
            _inner = null;
            if (inner != null) inner->Release();
        }

        /// <summary>The start offset of the one audio stream that is selected (a seek applies to the whole reader; none of Clink's readers has two audio streams selected), or 0.</summary>
        private long SelectedOffset()
        {
            long found = 0;
            int selected = 0;
            foreach (var (stream, offset) in _offset)
            {
                BOOL on;
                if (_inner->GetStreamSelection(stream, &on).Succeeded && on)
                {
                    found = offset;
                    selected++;
                }
            }
            return selected == 1 ? found : 0;
        }

        public HRESULT GetStreamSelection(uint dwStreamIndex, BOOL* pfSelected) => _inner == null ? Disposed : _inner->GetStreamSelection(dwStreamIndex, pfSelected);
        public HRESULT SetStreamSelection(uint dwStreamIndex, BOOL fSelected) => _inner == null ? Disposed : _inner->SetStreamSelection(dwStreamIndex, fSelected);
        public HRESULT GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, IMFMediaType** ppMediaType) => _inner == null ? Disposed : _inner->GetNativeMediaType(dwStreamIndex, dwMediaTypeIndex, ppMediaType);
        public HRESULT GetCurrentMediaType(uint dwStreamIndex, IMFMediaType** ppMediaType) => _inner == null ? Disposed : _inner->GetCurrentMediaType(dwStreamIndex, ppMediaType);
        public HRESULT SetCurrentMediaType(uint dwStreamIndex, uint* pdwReserved, IMFMediaType* pMediaType) => _inner == null ? Disposed : _inner->SetCurrentMediaType(dwStreamIndex, pdwReserved, pMediaType);
        public HRESULT Flush(uint dwStreamIndex) => _inner == null ? Disposed : _inner->Flush(dwStreamIndex);
        public HRESULT GetServiceForStream(uint dwStreamIndex, Guid* guidService, Guid* riid, void** ppvObject) => _inner == null ? Disposed : _inner->GetServiceForStream(dwStreamIndex, guidService, riid, ppvObject);
        public HRESULT GetPresentationAttribute(uint dwStreamIndex, Guid* guidAttribute, PROPVARIANT* pvarAttribute) => _inner == null ? Disposed : _inner->GetPresentationAttribute(dwStreamIndex, guidAttribute, pvarAttribute);

        public HRESULT SetCurrentPosition(Guid* guidTimeFormat, PROPVARIANT* varPosition)
        {
            if (_inner == null) return Disposed;
            PROPVARIANT position = *varPosition;
            // A time on the moved timeline is where the stream's own time is that much earlier.
            if (position.vt == VARENUM.VT_I8) position.hVal = Math.Max(0, position.hVal - SelectedOffset());
            return _inner->SetCurrentPosition(guidTimeFormat, &position);
        }

        public HRESULT ReadSample(uint dwStreamIndex, uint dwControlFlags, uint* pdwActualStreamIndex, uint* pdwStreamFlags, long* pllTimestamp, IMFSample** ppSample)
        {
            if (_inner == null) return Disposed;
            uint actual, flags;
            long time;
            IMFSample* sample = null;
            HRESULT hr = _inner->ReadSample(dwStreamIndex, dwControlFlags, &actual, &flags, &time, &sample);
            if (hr.Succeeded && sample != null && _offset.TryGetValue(actual, out long offset)) time = Math.Max(0, time + offset);
            if (pdwActualStreamIndex != null) *pdwActualStreamIndex = actual;
            if (pdwStreamFlags != null) *pdwStreamFlags = flags;
            if (pllTimestamp != null) *pllTimestamp = time;
            if (ppSample != null) *ppSample = sample;
            else if (sample != null) sample->Release();
            return hr;
        }
    }

    /// <summary>The COM side of <see cref="EmulatedReader"/>: IMFSourceReader (and IAgileObject: it is called from the player's thread and any other).</summary>
    private sealed class EmulationWrappers : ComWrappers
    {
        public static readonly EmulationWrappers Instance = new();
        private static readonly ComInterfaceEntry* s_entries = Build();

        private static ComInterfaceEntry* Build()
        {
            var vtbl = (IMFSourceReader.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(EmulationWrappers), sizeof(IMFSourceReader.Vtbl));
            ComHelpers.PopulateIUnknown<IMFSourceReader>((IUnknown.Vtbl*)vtbl);
            IMFSourceReader.PopulateVTable(vtbl);
            var agile = (IUnknown.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(EmulationWrappers), sizeof(IUnknown.Vtbl));
            ComHelpers.PopulateIUnknown<IAgileObject>(agile);
            var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(EmulationWrappers), sizeof(ComInterfaceEntry) * 2);
            entries[0] = new ComInterfaceEntry { IID = IMFSourceReader.IID_Guid, Vtable = (nint)vtbl };
            entries[1] = new ComInterfaceEntry { IID = IAgileObject.IID_Guid, Vtable = (nint)agile };
            return entries;
        }

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            count = 2;
            return s_entries;
        }

        protected override object? CreateObject(nint externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();
        protected override void ReleaseObjects(IEnumerable objects) => throw new NotSupportedException();
    }
}
