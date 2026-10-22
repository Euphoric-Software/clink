using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Clink.Engine;
using Clink.Native;
using Clink.Playback;
using static Clink.Tests.MixRenderTests;

namespace Clink.Tests;

/// <summary>
/// <see cref="ClipSource"/>: Clink reads its clips with the audio edit lists hidden from Media Foundation (which applies them on Windows
/// Server 2025 and ignores them on Windows 11), the stream that does it, the timeline self-check that makes sure every track
/// still starts at 0 on the Windows at hand, and what Clink does where it does not. The audio tests of <see cref="AlignmentTests"/> and
/// <see cref="MixRenderTests"/> are the proof that the readers put the tracks on the timeline right; <see cref="EditListEmulation"/> runs
/// them (CLINK_EMULATE_ELST=1) as on a Media Foundation that applies the edit lists.
/// </summary>
internal static unsafe class ClipSourceTests
{
    private static readonly string[] FiveTracks = { "Mix", "Game", "Mic", "Voice", "Desktop" };
    private static readonly string[] FourTracks = { "Mix", "Game", "Mic", "Desktop" };

    /// <summary>Five tracks that start at five places, two before the clip origin and two after it: the edit lists have every form the muxer writes.</summary>
    private static readonly (string, int, float)[] Apart = { ("Mix", -480, 1), ("Game", 0, 1), ("Mic", 19, 1), ("Voice", -37, 1), ("Desktop", 1200, 1) };

    /// <summary>The stream as a COM object, as Media Foundation gets it (the caller releases it).</summary>
    private static IStream* AsComStream(EditListFreeStream stream)
    {
        nint identity = ClinkComWrappers.Instance.GetOrCreateComInterfaceForObject(stream, CreateComInterfaceFlags.None);
        Guid iid = IStream.IID_Guid;
        int hr = Marshal.QueryInterface(identity, in iid, out nint com);
        Marshal.Release(identity);
        Marshal.ThrowExceptionForHR(hr);
        return (IStream*)com;
    }

    /// <summary>What a read of <paramref name="count"/> bytes at <paramref name="position"/> returns.</summary>
    private static byte[] ReadAt(IStream* stream, long position, int count)
    {
        ulong now;
        Check.True(stream->Seek(position, SeekOrigin.Begin, &now).Succeeded, $"seek to {position}");
        Check.Equal((ulong)position, now, "where the seek landed");
        var buffer = new byte[count];
        uint got;
        fixed (byte* p = buffer) Check.True(stream->Read(p, (uint)count, &got).Succeeded, $"read of {count} bytes at {position}");
        return buffer[..(int)got];
    }

    private static int Count(byte[] bytes, int from, int to, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        int n = 0;
        for (int i = from; i + needle.Length <= to; i++)
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle)) n++;
        return n;
    }

    /// <summary>The payload range of the file's 'moov' box (a Clink clip is written fast-start, moov first).</summary>
    private static (int from, int to) Moov(byte[] file)
    {
        for (int at = 0; at + 8 <= file.Length;)
        {
            long size = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];
            if (size < 8) break;
            if (Encoding.ASCII.GetString(file, at + 4, 4) == "moov") return (at + 8, (int)(at + size));
            at += (int)size;
        }
        throw new TestFailure("the clip has no moov box");
    }

    /// <summary>
    /// The file can be opened exclusively: nobody has it open. Media Foundation may close a file a moment after its reader was released
    /// (on its own thread), so for a few seconds it is tried again; a reader that was never let go of stays open for good.
    /// </summary>
    private static void CheckNobodyHasItOpen(string path, string when)
    {
        for (int tries = 0; ; tries++)
        {
            try
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException ex)
            {
                if (tries >= 150) throw new TestFailure($"{when}: the clip is still open ({ex.Message})");
                Thread.Sleep(20);
            }
        }
    }

    [UnitTest]
    public static void The_edit_list_free_stream_hides_only_the_audio_edts()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            SyntheticClip.WriteSame(clip, 2, Apart, out _);
            var file = File.ReadAllBytes(clip);
            var edits = Trimmer.ReadClipTracks(clip)!.AudioEditBoxes;
            Check.Equal(5, edits.Length, "an edit box for each of the five audio traks");
            foreach (long at in edits) Check.Equal("edts", Encoding.ASCII.GetString(file, (int)at, 4), "every offset is that of an 'edts' type");
            var expected = (byte[])file.Clone();
            foreach (long at in edits) Encoding.ASCII.GetBytes("free").CopyTo(expected, at);
            var (moovFrom, moovTo) = Moov(file);
            Check.Equal(6, Count(file, moovFrom, moovTo, "edts"), "the clip has an edit list for each of its six traks");
            Check.Equal(1, Count(expected, moovFrom, moovTo, "edts"), "the video's is the only one that is left");

            var stream = new EditListFreeStream(clip, edits);
            IStream* com = AsComStream(stream);
            try
            {
                // The whole file in one read, then in pieces that fall anywhere against the four bytes.
                Check.True(ReadAt(com, 0, file.Length + 100).SequenceEqual(expected), "the bytes read are the file's but for the four type bytes of each audio edit box");
                var piece = new byte[997];
                var assembled = new List<byte>();
                uint got;
                ulong pos;
                Check.True(com->Seek(0, SeekOrigin.Begin, &pos).Succeeded);
                do
                {
                    fixed (byte* p = piece) Check.True(com->Read(p, (uint)piece.Length, &got).Succeeded);
                    assembled.AddRange(piece.Take((int)got));
                } while (got > 0);
                Check.True(assembled.SequenceEqual(expected), "read in pieces of 997 bytes it is the same");
                int differing = 0;
                for (int i = 0; i < file.Length; i++) if (file[i] != expected[i]) differing++;
                Check.Equal(20, differing, "five boxes of four bytes differ, nothing else");

                // A read that only reaches into the four bytes, from either side, or lies inside them, is patched as far as it reaches.
                foreach (long at in edits)
                    for (long start = at - 6; start <= at + 6; start++)
                        for (int length = 1; length <= 10; length++)
                            Check.True(ReadAt(com, start, length).SequenceEqual(expected.AsSpan((int)start, length).ToArray()), $"{length} bytes from {start}, around the type at {at}");

                // The rest of the stream: the size, the seek pointer, the end, and a stream that cannot be written to.
                STATSTG stat;
                Check.True(com->Stat(&stat, 0).Succeeded, "Stat");
                Check.Equal((ulong)file.Length, stat.cbSize, "the size");
                Check.Equal((uint)STGTY.STGTY_STREAM, stat.type, "a stream");
                Check.True(stat.pwcsName.Value == null, "no name");
                Check.True(com->Seek(-4, SeekOrigin.End, &pos).Succeeded);
                Check.Equal((ulong)file.Length - 4, pos, "four bytes before the end");
                Check.True(com->Seek(2, SeekOrigin.Current, &pos).Succeeded);
                Check.Equal((ulong)file.Length - 2, pos, "two on");
                Check.True(com->Seek(-1, SeekOrigin.Begin, &pos).Failed, "before the start");
                Check.True(com->Seek(0, SeekOrigin.End, &pos).Succeeded);
                fixed (byte* p = piece) Check.True(com->Read(p, 10, &got).Succeeded && got == 0, "a read at the end hands out nothing");
                uint written;
                fixed (byte* p = piece) Check.True(com->Write(p, 4, &written).Failed, "it is read only");
                Check.True(com->SetSize(10).Failed, "and cannot be resized");

                // Open until it is disposed, closed after: a reader's file is closed by the owner the reader came with.
                Check.Throws<IOException>(() => { using var exclusive = new FileStream(clip, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }, "open while the stream is");
                stream.Dispose();
                CheckNobodyHasItOpen(clip, "after Dispose");
                fixed (byte* p = piece) Check.True(com->Read(p, 10, &got).Failed, "a read after Dispose fails, it does not crash");
            }
            finally { com->Release(); }
            File.Delete(clip);
            Check.False(File.Exists(clip), "and the clip can be deleted");
        });
    }

    [UnitTest]
    public static void Through_the_clip_source_every_audio_track_starts_at_0_with_the_same_streams_and_sound()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4"), other = dir.File("other.mp4");
        OnMta(() =>
        {
            SyntheticClip.WriteSame(clip, 3, Apart, out _);
            var offsets = TestAudio.Offsets(clip);
            Check.Equal(new long[] { -480, 0, 19, -37, 1200 }, offsets, "the edit lists put the tracks apart");

            // Opened through the stream that hides the edit lists (not through Media Foundation's own reader, which the same numbers would
            // come out of on a Windows that ignores them), as every reader of Clink is.
            IMFSourceReader* reader = ClipSource.Open(clip, null, out var owner);
            Check.NotNull(owner, "a clip Clink wrote is read through its own stream");
            var viaClipSource = Trimmer.AudioStreamsOf(reader);
            reader->Release();
            owner!.Dispose();
            IMFSourceReader* plain = ClipSource.Open(clip, null, out var plainOwner, hideEditLists: false);
            var viaPlain = Trimmer.AudioStreamsOf(plain);
            plain->Release();
            plainOwner?.Dispose();
            Check.Equal(viaPlain, viaClipSource, "the same stream numbers as Media Foundation's own reader gives");
            Check.Equal(5, viaClipSource.Count);

            // Every track from its first sample, whatever its edit list says.
            for (int track = 0; track < 5; track++)
            {
                var ts = TestAudio.Timestamps(clip, track);
                Check.Equal(0L, ts[0], $"{FiveTracks[track]} at {offsets[track]}: first frame");
                Check.Near(213_333, ts[1], 1, $"{FiveTracks[track]}: second frame");
                Check.Near(426_667, ts[2], 1, $"{FiveTracks[track]}: third frame");
            }

            // On a Windows that ignores the edit lists (this one if it does) the very same timestamps, frames and sound as the plain reader.
            var handling = ClipSource.Timeline.MediaFoundation;
            Console.WriteLine($"  (Media Foundation {handling.ToString().ToLowerInvariant()} the audio edit lists of a clip here)");
            if (handling == EditListHandling.Ignores)
                for (int track = 0; track < 5; track++)
                {
                    Check.Equal(TestAudio.Timestamps(clip, track, hideEditLists: false), TestAudio.Timestamps(clip, track), $"{FiveTracks[track]}: timestamps");
                    Check.True(TestAudio.Frames(clip, track).SequenceEqual(TestAudio.Frames(clip, track, hideEditLists: false), new BytesEqual()), $"{FiveTracks[track]}: compressed frames");
                    var through = TestAudio.Decode(clip, track);
                    var own = TestAudio.Decode(clip, track, hideEditLists: false);
                    Check.True(through.AsSpan().SequenceEqual(own), $"{FiveTracks[track]}: the decoded sound is the same to the sample");
                }

            // Everything is released again: the file is nobody's.
            CheckNobodyHasItOpen(clip, "after the readers");

            // A file Clink did not write (its video trak has another name) is opened as Media Foundation does it.
            var bytes = File.ReadAllBytes(clip);
            var name = Encoding.ASCII.GetBytes("vide\0\0\0\0\0\0\0\0\0\0\0\0Video");
            int at = bytes.AsSpan().IndexOf(name);
            Check.True(at > 0, "the video handler is in the file");
            Encoding.ASCII.GetBytes("Vidyo").CopyTo(bytes, at + name.Length - 5);
            File.WriteAllBytes(other, bytes);
            Check.Null(Trimmer.ReadClipTracks(other), "no longer a Clink file");
            IMFSourceReader* foreign = ClipSource.Open(other, null, out var foreignOwner);
            try
            {
                Check.Equal(5, Trimmer.AudioStreamsOf(foreign).Count, "it still opens, and with its five audio streams");
                if (!EditListEmulation.Active) Check.Null(foreignOwner, "through Media Foundation's own reader: nothing is hidden in a file that is not Clink's");
            }
            finally
            {
                foreign->Release();
                foreignOwner?.Dispose();
            }
        });
    }

    [UnitTest]
    public static void The_readers_of_the_player_and_of_a_cut_let_go_of_the_clip_when_they_are_closed()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4"), cut = dir.File("cut.mp4"), moved = dir.File("moved.mp4");
        OnMta(() =>
        {
            SyntheticClip.WriteSame(clip, 4, Apart, out _);
            // The player holds the clip through a reader of its own for the video and one for each audio track; a trim that replaces
            // the clip, and the archive's delete, need every one of them gone once the player is closed.
            using (var player = new ClipPlayer(clip, 0, headless: true))
            {
                Check.Equal(5, player.TrackCount);
                var heard = player.RenderAudio(1.0, 4800);
                Check.True(heard.Length == 9600, "it plays");
                Check.True(player.Close(), "it closes");
                CheckNobodyHasItOpen(clip, "after the player was closed");
            }
            // A cut reads the clip with a reader of its own for the copy and one for every track it renders again.
            var plan = PlanFor(FiveTracks, new float[] { 1, .5f, 1, 1, 1 });
            Check.NotNull(plan[0].Sum, "All audio is rebuilt from Game, Mic and Voice, each with a reader of its own");
            Trimmer.Trim(clip, cut, 1, 3, FiveTracks, plan);
            CheckNobodyHasItOpen(clip, "after a trim that rendered from three tracks");
            File.Move(clip, moved);   // what a trim that replaces its clip does
            Check.True(File.Exists(moved) && !File.Exists(clip), "and the clip can be moved aside");
        });
    }

    [UnitTest]
    public static void A_clip_somebody_is_writing_to_is_opened_as_Media_Foundation_would_and_not_half_hidden()
    {
        using var dir = new TempDir();
        string clip = dir.File("busy.mp4");
        OnMta(() =>
        {
            SyntheticClip.WriteSame(clip, 2, Apart, out _);
            var edits = Trimmer.ReadClipTracks(clip)!.AudioEditBoxes;
            using var writer = new FileStream(clip, FileMode.Open, FileAccess.Write, FileShare.Read);
            // The stream opens the file as Media Foundation's own reader does, which refuses a file that somebody may write to.
            Check.Throws<IOException>(() => new EditListFreeStream(clip, edits).Dispose(), "no stream on a clip that is being written");
            // ClipSource then falls back on Media Foundation's own reader: it opens the clip, or says why it cannot, as ever.
            try
            {
                IMFSourceReader* reader = ClipSource.Open(clip, null, out var owner);
                try { if (!EditListEmulation.Active) Check.Null(owner, "Media Foundation's own reader, with nothing hidden"); }
                finally
                {
                    reader->Release();
                    owner?.Dispose();
                }
            }
            catch (COMException) { }
        });
    }

    private sealed class BytesEqual : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? a, byte[]? b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] x) => x.Length;
    }

    [UnitTest]
    public static void The_timeline_self_check_trusts_this_machine()
    {
        // (Needs the AAC encoder, like every test that writes a clip: a machine without it skips this, and the check there says it could not run.)
        OnMta(() =>
        {
            var before = ClipSource.TrustOverride;
            ClipSource.TrustOverride = null;
            try
            {
                ClipSource.ResetTimelineCheck();
                var check = ClipSource.Timeline;
                Console.WriteLine($"  (timeline self-check: {check.Detail}; Media Foundation {check.MediaFoundation.ToString().ToLowerInvariant()} the edit lists)");
                Check.True(check.Passed == true, $"every track of the probe clip starts at 0 and is read in whole frames ({check.Detail})");
                Check.True(check.Trusted && ClipSource.TimelineTrusted, "so clips are put on one timeline here");
                Check.True(ReferenceEquals(check, ClipSource.Timeline), "and the check is made once");
                Check.False(File.Exists(Path.Combine(Path.GetTempPath(), $"clink-timeline-probe-{Environment.ProcessId}.mp4")), "the probe clip is deleted again");
                Check.NotEqual(EditListHandling.Unknown, check.MediaFoundation, "Media Foundation's own reading of the probe was classified");
            }
            finally { ClipSource.TrustOverride = before; }
        });
    }

    /// <summary>
    /// Not a test, a report that never fails: the Windows build, the Media Foundation files that matter, and whether Media Foundation
    /// here applies the audio edit lists of an MP4 or ignores them (what the CI log needs to say about its runner). Run with
    /// CLINK_EMULATE_ELST it also says what is emulated.
    /// </summary>
    [UnitTest]
    public static void Media_Foundations_handling_of_edit_lists_is_reported()
    {
        try
        {
            string system = Environment.SystemDirectory;
            string Version(string dll)
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(Path.Combine(system, dll));
                    return $"{dll} {v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}";
                }
                catch { return $"{dll} not found"; }
            }
            Console.WriteLine($"  ({ClipSource.WindowsBuild()}; {Version("mfplat.dll")}, {Version("mfmp4srcsnk.dll")}, {Version("msauddecmft.dll")})");
            var check = ClipSource.Timeline;
            string how = check.MediaFoundation switch
            {
                EditListHandling.Applies => "APPLIES the audio edit lists of an MP4 (as Windows Server 2025 does)",
                EditListHandling.Ignores => "IGNORES the audio edit lists of an MP4 (as Windows 11 does)",
                EditListHandling.Other => $"does something else with the audio edit lists of an MP4: {check.OwnReading}",
                _ => $"was not asked ({check.Detail})",
            };
            Console.WriteLine($"  (Media Foundation here {how}; through Clink's reader the probe clip reads: {check.Detail}; {EditListEmulation.Describe})");
        }
        catch (Exception ex) { Console.WriteLine($"  (the report could not be made: {ex.Message})"); }
    }

    [UnitTest]
    public static void An_untrusted_timeline_keeps_every_source_out_of_All_audio_and_writes_no_mark()
    {
        using var dir = new TempDir();
        string five = dir.File("five.mp4"), four = dir.File("four.mp4"), refused = dir.File("refused.mp4"), cut = dir.File("cut.mp4"),
            kept = dir.File("kept.mp4"), louder = dir.File("louder.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(five, 6);
            SyntheticClip.Write(four, 6, voiceTrack: false);
            Check.True(Trimmer.ReadClipTracks(five)!.Marked, "a recording says its places are true");
            var before = ClipSource.TrustOverride;
            ClipSource.TrustOverride = false;   // what a failed self-check does
            try
            {
                // Nothing is lined up and nothing is vouched for, the sources stand alone.
                var align = ClipAlignment.Read(five);
                Check.True(align.Untrusted, "untrusted");
                Check.False(align.Marked, "the file's mark is not believed here");
                Check.False(align.Aligned, "so nothing is vouched for");
                Check.Equal(new[] { 1, 2, 3 }, align.Unlined, "Game, Mic and Voice are left out of All audio");
                Check.Equal(FiveTracks, align.Names, "the names are the file's");
                Check.Equal("This Windows reads clips differently: sources cannot be changed inside All audio", ClipAlignment.UntrustedNote);
                var four4 = ClipAlignment.Read(four);
                Check.Equal(new[] { 1, 2 }, four4.Unlined, "a clip without a Voice track: Game and Mic");
                Check.False(ClipAlignment.Read(dir.File("missing.mp4")).Untrusted, "a file that is not there has nothing to distrust");

                // A plan that rebuilds the Mix from its sources, and one that takes a source out of the baked Mix: refused, nothing written.
                var rebuild = PlanFor(FiveTracks, new float[] { 1, 0, 1, 1, 1 });
                Check.NotNull(rebuild[0].Sum, "the editor's plan without this knowledge rebuilds All audio");
                var ex = Check.Throws<InvalidOperationException>(() => Trimmer.Trim(five, refused, 1, 5, FiveTracks, rebuild));
                Check.Contains(ex.Message, ClipAlignment.UntrustedNote);
                var delta = PlanFor(FourTracks, new float[] { 1, 0, 1, 1 });
                Check.Equal(new[] { new MixInput(0, 1f), new MixInput(1, -1f) }, delta[0].Sum!, "the delta plan");
                ex = Check.Throws<InvalidOperationException>(() => Trimmer.Trim(four, refused, 1, 5, FourTracks, delta));
                Check.Contains(ex.Message, ClipAlignment.UntrustedNote);
                Check.False(File.Exists(refused), "nothing was written");

                // The plan the editor makes from the alignment: the sources stand alone, so All audio is copied and the Game's slider is its own track's.
                var roles = MixPlan.WithoutTracks(MixPlan.Roles(FiveTracks, 5), align.Unlined);
                var editor = MixPlan.Export(roles, new[] { true, true, true, true, true }, new float[] { 1, 0, 1, 1, 1 });
                Check.Null(editor[0].Sum, "All audio is not remixed");
                Trimmer.Trim(five, kept, 1, 5, FiveTracks, editor);
                Check.False(Trimmer.ReadClipTracks(kept)!.Marked, "and the cut does not say its places are true");
                Check.True(TestAudio.IsSlice(TestAudio.Frames(five, 0), TestAudio.Frames(kept, 0)), "the Mix is the very same frames");
                Check.True(TestAudio.Rms(TestAudio.Decode(kept, 1)) < .002, "Game's own track is silent, as its slider said");

                // A plain cut writes no mark either.
                Trimmer.Trim(five, cut, 1, 5, null, null);
                Check.False(Trimmer.ReadClipTracks(cut)!.Marked, "a plain cut is not marked");

                // One track at another volume is still rendered, and not marked.
                var single = PlanFor(FiveTracks, new float[] { 1, 1, 1, 1, 1 });
                single[1] = new TrackMix(true, .5f);
                Trimmer.Trim(five, louder, 1, 5, FiveTracks, single);
                Check.False(Trimmer.ReadClipTracks(louder)!.Marked, "a render of one track is not marked");
                Check.Near(.5 * SyntheticClip.Level, TestAudio.Amplitude(TestAudio.Decode(louder, 1), 48000, SyntheticClip.GameHz), .07 * SyntheticClip.Level, "Game at half volume");

                // The preview says the same as the export.
                using var player = new ClipPlayer(five, 0, headless: true);
                Check.True(player.Alignment.Untrusted, "the player's alignment is untrusted too");
                Check.Equal(new[] { 1, 2, 3 }, player.Alignment.Unlined);
            }
            finally { ClipSource.TrustOverride = before; }
            Check.False(ClipAlignment.Read(five).Untrusted, "and with the check passed again, the clip is as ever");
            Check.True(ClipAlignment.Read(five).Marked);
        });
    }

    [UnitTest]
    public static void The_timeline_self_check_fails_when_the_tracks_are_moved_and_the_untrusted_path_holds()
    {
        using var dir = new TempDir();
        string clip = dir.File("five.mp4"), refused = dir.File("refused.mp4"), cut = dir.File("cut.mp4");
        OnMta(() =>
        {
            SyntheticClip.Write(clip, 6);
            var before = ClipSource.TrustOverride;
            ClipSource.TrustOverride = null;
            try
            {
                // A Media Foundation that moves every audio track by its start offset, though the edit lists are hidden from it: the
                // third behaviour that nobody has seen, which the check is for.
                using (EditListEmulation.Apply(evenWhenHidden: true))
                {
                    var check = ClipSource.Timeline;
                    Console.WriteLine($"  (the self-check under {EditListEmulation.Describe}: {check.Detail})");
                    Check.True(check.Passed == false, "the probe clip's tracks do not start at 0");
                    Check.False(check.Trusted, "so this Windows is not trusted");
                    Check.False(ClipSource.TimelineTrusted);
                    Check.Contains(check.Detail, "expected 0 213333 426667", "and the check says what it expected");

                    var align = ClipAlignment.Read(clip);
                    Check.True(align.Untrusted, "the clip is read as untrusted");
                    Check.Equal(new[] { 1, 2, 3 }, align.Unlined, "every source is left out of All audio");
                    Check.False(align.Aligned);
                    var ex = Check.Throws<InvalidOperationException>(() => Trimmer.Trim(clip, refused, 1, 5, FiveTracks, PlanFor(FiveTracks, new float[] { 1, 0, 1, 1, 1 })));
                    Check.Contains(ex.Message, ClipAlignment.UntrustedNote);
                    Check.False(File.Exists(refused), "nothing was written");
                    Trimmer.Trim(clip, cut, 1, 5, null, null);
                    Check.False(Trimmer.ReadClipTracks(cut)!.Marked, "a plain cut is not marked");
                }
                // The emulation is gone, and so is its verdict.
                Check.True(ClipSource.Timeline.Passed == true, "the check passes again");
                Check.False(ClipAlignment.Read(clip).Untrusted);
            }
            finally
            {
                ClipSource.TrustOverride = before;
                ClipSource.ResetTimelineCheck();
            }
        });
    }

    [UnitTest]
    public static void The_emulated_Media_Foundation_moves_what_a_plain_reader_reads_and_not_what_the_clip_source_reads()
    {
        using var dir = new TempDir();
        string clip = dir.File("apart.mp4");
        OnMta(() =>
        {
            // The Voice starts half a second late, which a seek can tell from the frame (21 ms) it lands within.
            SyntheticClip.WriteSame(clip, 12, new (string, int, float)[] { ("Mix", -480, 1), ("Game", 0, 1), ("Voice", 24000, 1) }, out _);
            var offsets = Trimmer.ReadAudioTracks(clip)!.Select(t => t.StartOffset).ToArray();
            Check.Equal(new long[] { -100_000, 0, 5_000_000 }, offsets, "the edit lists say -10 ms, 0 and +500 ms");
            // What Media Foundation itself does (also when the run has the emulation on: it is taken away for this).
            long[][] own, hidden;
            bool ignores;
            using (EditListEmulation.Without())
            {
                own = Enumerable.Range(0, 3).Select(t => TestAudio.Timestamps(clip, t, hideEditLists: false).ToArray()).ToArray();
                hidden = Enumerable.Range(0, 3).Select(t => TestAudio.Timestamps(clip, t).ToArray()).ToArray();
                ignores = ClipSource.Timeline.MediaFoundation == EditListHandling.Ignores;
            }
            using (EditListEmulation.Apply())
            {
                for (int t = 0; t < 3; t++)
                {
                    var emulated = TestAudio.Timestamps(clip, t, hideEditLists: false);
                    Check.Equal(own[t].Select(x => Math.Max(0, x + offsets[t])).ToArray(), emulated.ToArray(), $"track {t + 1}: a reader that sees the edit list is moved by the track's offset, and not before 0");
                    Check.Equal(hidden[t], TestAudio.Timestamps(clip, t).ToArray(), $"track {t + 1}: a reader that does not see it is not");
                }
                // A seek takes a time on the moved timeline (where the real Media Foundation has none: it would be moved twice on one that applies the edit lists itself).
                if (ignores)
                {
                    var late = TestAudio.StreamOf(clip, 2);
                    IMFSourceReader* reader = ClipSource.Open(clip, null, out var owner, hideEditLists: false);
                    try
                    {
                        reader->SetStreamSelection(0xFFFFFFFE, false);
                        reader->SetStreamSelection(late, true);
                        Windows.Win32.System.Com.StructuredStorage.PROPVARIANT position = default;
                        position.vt = Windows.Win32.System.Variant.VARENUM.VT_I8;
                        position.hVal = 80_000_000;
                        Guid format = Guid.Empty;
                        Check.True(reader->SetCurrentPosition(&format, &position).Succeeded, "seek");
                        uint idx, flags; long ts; IMFSample* sample;
                        reader->ReadSample(late, 0, &idx, &flags, &ts, &sample).Check("ReadSample");
                        if (sample != null) sample->Release();
                        // Not at 8.5 s, where a seek to 8 s of the stream's own time would be on the moved timeline.
                        Check.True(Math.Abs(ts - 80_000_000) < 2 * 213_333, $"after a seek to 8 s the late track's frame is at {ts / 10_000_000.0:0.000} s, on its moved timeline");
                    }
                    finally
                    {
                        reader->Release();
                        owner?.Dispose();
                    }
                }
            }
            CheckNobodyHasItOpen(clip, "after the emulated readers");
        });
    }
}
