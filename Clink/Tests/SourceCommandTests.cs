using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

/// <summary>Dual capture, model and grammar: the source word of --clip / --record / --share-last, and the new settings keys.</summary>
internal static class SourceCommandTests
{
    [UnitTest]
    public static void The_command_line_carries_a_source_word_after_clip_record_and_share()
    {
        Check.Equal("clip", AppController.CommandFromArgs(new[] { "--clip" }));
        Check.Equal("clip card", AppController.CommandFromArgs(new[] { "--clip", "card" }));
        Check.Equal("clip crad", AppController.CommandFromArgs(new[] { "--dev", "x", "--CLIP", "crad" }), "an unknown word is forwarded");
        Check.Equal("clip", AppController.CommandFromArgs(new[] { "--clip", "--minimized" }), "a switch is not a source word");
        Check.Equal("record screen", AppController.CommandFromArgs(new[] { "--record", "screen" }));
        Check.Equal("record both", AppController.CommandFromArgs(new[] { "--record", "both" }));
        Check.Equal("share card", AppController.CommandFromArgs(new[] { "--share-last", "card" }));
        Check.Equal("share", AppController.CommandFromArgs(new[] { "--share-last", "--minimized" }));
        Check.Equal("trim", AppController.CommandFromArgs(new[] { "--trim-last", "card" }), "trim takes no source");
        Check.Equal("screenshot", AppController.CommandFromArgs(new[] { "--screenshot", "card" }));
    }

    [UnitTest]
    public static void A_pipe_line_is_split_into_verb_and_source()
    {
        Check.Equal(new SourceCommand("clip", null), SourceCommand.Parse("clip"));
        Check.Equal(new SourceCommand("clip", "card"), SourceCommand.Parse("  Clip   CARD "));
        Check.Equal(new SourceCommand("share", "screen"), SourceCommand.Parse("share screen"));
        Check.Equal(new SourceCommand("record", "xyz"), SourceCommand.Parse("record xyz"));
        Check.Equal(new SourceCommand("show", null), SourceCommand.Parse("show"));
        Check.Equal(new SourceCommand("show card", null), SourceCommand.Parse("show card"), "only clip, record and share take a word");
        Check.Equal(new SourceCommand("clip a b", null), SourceCommand.Parse("clip a b"), "two words are no command");
        Check.Equal(new SourceCommand("", null), SourceCommand.Parse(null));
    }

    [UnitTest]
    public static void IsClipsCommand_looks_at_the_verb()
    {
        Check.True(AppController.IsClipsCommand("clip"));
        Check.True(AppController.IsClipsCommand("clip card"));
        Check.True(AppController.IsClipsCommand("share screen"));
        Check.True(AppController.IsClipsCommand("record both"));
        Check.False(AppController.IsClipsCommand("show card"));
        Check.False(AppController.IsClipsCommand("screenshot-edit C:\\a clip.png"));
    }

    [UnitTest]
    public static void A_restart_drops_the_source_word_with_its_switch()
    {
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(new[] { "--clip", "card" }).ToArray());
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(new[] { "--record", "both", "--minimized" }).ToArray());
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(new[] { "--share-last", "screen" }).ToArray());
        Check.Equal(new[] { "--show" }, AppController.RestartArguments(new[] { "--clip", "--minimized" }).ToArray());
        Check.Equal(new[] { "card", "--show" }, AppController.RestartArguments(new[] { "card", "--clip" }).ToArray(), "a word before the switch is not its source");
    }

    [UnitTest]
    public static void CaptureBoth_and_a_hotkey_source_are_written_only_when_set()
    {
        string plain = Settings.FromJson("{}").ToJson();
        Check.False(plain.Contains("CaptureBoth"), "false is not written");
        Check.False(plain.Contains("\"Source\""), "no hotkey source is not written");
        Check.Equal(plain, Settings.FromJson(plain).ToJson(), "a single-source file is unchanged by load + save");

        var s = Settings.FromJson("""{ "CaptureBoth": true, "Hotkeys": [ { "Keys": { "Alt": true, "Vk": 67 }, "Action": "clip" }, { "Keys": { "Vk": 120 }, "Action": "clip", "Seconds": 15, "Source": "card" } ] }""");
        Check.True(s.CaptureBoth);
        Check.Equal("card", s.Hotkeys.First(h => h.Seconds == 15).Source);
        Check.Null(s.Hotkeys[0].Source);
        var again = Settings.FromJson(s.ToJson());
        Check.True(again.CaptureBoth);
        Check.Equal("card", again.Hotkeys.First(h => h.Seconds == 15).Source);
        Check.Null(again.Hotkeys[0].Source);
        Check.Contains(s.ToJson(), "\"CaptureBoth\": true");

        Check.False(Settings.FromJson("""{ "CaptureBoth": "yes" }""").CaptureBoth, "a value of the wrong kind costs only itself");
    }

    [UnitTest]
    public static void A_clip_entry_source_is_written_only_when_set()
    {
        string old = JsonSerializer.Serialize(new ClipEntry { Path = "a.mp4" });
        Check.False(old.Contains("Source\""), "an old clip has none");
        Check.Null(JsonSerializer.Deserialize<ClipEntry>(old)!.Source);
        string card = JsonSerializer.Serialize(new ClipEntry { Path = "b.mp4", Source = "card" });
        Check.Equal("card", JsonSerializer.Deserialize<ClipEntry>(card)!.Source);
    }
}
