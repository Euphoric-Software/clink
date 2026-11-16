using System.Diagnostics;
using System.IO;
using System.Reflection;
using Clink.Audio;
using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// Small performance fixes on hot paths: the UI's once-a-second engine stats must not wait for the audio thread, the voice-app scan lists
/// the processes once, and the clip library matches folder files against its index by a set lookup. Each keeps the old result.
/// </summary>
internal static class CheapPerfTests
{
    private static FieldInfo EngineField(string name) => typeof(Clink.Engine.Engine).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Runs <paramref name="read"/> while another thread holds the engine's audio gate (what a 3 s process-loopback activation looks like to GetStats).</summary>
    private static T WhileAudioGateHeld<T>(Clink.Engine.Engine engine, Func<T> read)
    {
        var gate = EngineField("_audioGate").GetValue(engine)!;
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(5000); } }) { IsBackground = true, Name = "audio-gate-holder" };
        holder.Start();
        try { held.Wait(); return read(); }
        finally { release.Set(); holder.Join(); }
    }

    [UnitTest]
    public static void Engine_stats_do_not_wait_for_a_busy_audio_gate_and_give_the_last_audio_status()
    {
        using var engine = new Clink.Engine.Engine();
        var fresh = engine.GetStats().Audio;
        Check.NotNull(fresh, "stats with the gate free");
        var last = new List<Clink.Engine.AudioTrackStatus>();   // a sentinel: only the cached value can be this instance
        EngineField("_lastAudio").SetValue(engine, last);
        var sw = Stopwatch.StartNew();
        var stats = WhileAudioGateHeld(engine, engine.GetStats);
        sw.Stop();
        Check.True(sw.ElapsedMilliseconds < 200, $"GetStats returned in {sw.ElapsedMilliseconds} ms while the gate was held");
        Check.True(ReferenceEquals(last, stats.Audio), "the audio part is the last one read");
        var again = engine.GetStats().Audio;
        Check.True(again != null && !ReferenceEquals(last, again), "and is read again once the gate is free");
    }

    [UnitTest]
    public static void Engine_stats_say_the_audio_is_unknown_after_a_teardown_until_it_is_read_again()
    {
        using var engine = new Clink.Engine.Engine();
        EngineField("_lastAudio").SetValue(engine, new List<Clink.Engine.AudioTrackStatus>());   // the previous run's tracks
        typeof(Clink.Engine.Engine).GetMethod("TearDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, null);
        var stats = WhileAudioGateHeld(engine, engine.GetStats);
        Check.Null(stats.Audio, "not the previous run's tracks, and not \"no audio tracks\"");
    }

    private static GameProcessTracker.NamedProcess P(string name, uint pid, int? minutesAgo)
        => new(name, pid, minutesAgo is { } m ? new DateTime(2026, 1, 1, 12, 0, 0).AddMinutes(-m) : null);

    [UnitTest]
    public static void Voice_app_pick_from_one_process_list_keeps_name_order_and_the_oldest_process()
    {
        var names = GameProcessTracker.VoiceAppNames;
        var procs = new List<GameProcessTracker.NamedProcess>
        {
            P("notepad", 1, 500),
            P("slack", 2, 900),            // older, but Slack comes after Discord in the list
            P("Discord", 10, 5),
            P("DISCORD", 11, 60),          // the root of Discord's tree: the oldest, whatever the case of the name
            P("Discord", 12, null),        // start time unreadable
            P("Discord", 13, 30),
        };
        Check.Equal(((uint)11, "Discord"), GameProcessTracker.PickByName(procs, names), "first name in the list, oldest process");
        Check.Equal(((uint)2, "Slack"), GameProcessTracker.PickByName(procs.Where(p => p.Pid < 10).ToList(), names), "the next name when the first is not running");
        Check.Null(GameProcessTracker.PickByName(new[] { P("notepad", 1, 1) }, names), "none running");
        Check.Equal(((uint)20, "Zoom"), GameProcessTracker.PickByName(new[] { P("Zoom", 20, null), P("Zoom", 21, null) }, names), "no start time: the first one listed");
        Check.Equal(((uint)31, "Mumble"), GameProcessTracker.PickByName(new[] { P("Mumble", 30, null), P("Mumble", 31, 10) }, names), "a known start time beats an unknown one");
    }

    [UnitTest]
    public static void Library_load_matches_folder_files_to_index_entries_ignoring_case()
    {
        using var meta = new TempDir();
        using var clips = new TempDir();
        ClipLibrary.IndexPathOverride = meta.File("clips.json");
        File.WriteAllBytes(clips.File("a.mp4"), new byte[10]);
        File.WriteAllBytes(clips.File("b.mp4"), new byte[20]);
        var lib = new ClipLibrary();
        lib.Add(clips.File("A.MP4"), 7, 10, "Game", Array.Empty<string>());   // the index spells it another way
        lib.Load(clips.Path);
        var entries = lib.Entries;
        Check.Equal(2, entries.Count, "one entry per file");
        Check.Equal(7.0, lib.Find(clips.File("a.mp4"))!.Duration, "the indexed entry is kept, not a scanned copy");
        Check.Equal(20L, lib.Find(clips.File("b.mp4"))!.Size, "the file the index did not know is added");
    }
}
