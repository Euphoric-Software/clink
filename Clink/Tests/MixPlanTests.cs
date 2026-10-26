using Clink.Engine;

namespace Clink.Tests;

/// <summary>
/// The mix plan behind "All audio" in the player and the trim editor (Engine/MixPlan.cs): which track plays what in the preview and
/// what the export renders, from the roles of the file's tracks. Pure; the rendering itself is in MixRenderTests.
/// </summary>
internal static class MixPlanTests
{
    private static readonly string[] FiveTracks = { "Mix", "Game", "Mic", "Voice", "Desktop" };

    private static TrackRole[] Roles(params string[] names) => MixPlan.Roles(names, names.Length);
    private static bool[] All(int n, bool value = true) => Enumerable.Repeat(value, n).ToArray();
    private static float[] Gains(params float[] g) => g;

    [UnitTest]
    public static void Roles_come_from_the_names_of_a_Clink_file()
    {
        Check.Equal(new[] { TrackRole.Mix, TrackRole.Game, TrackRole.Mic, TrackRole.Voice, TrackRole.Desktop }, Roles(FiveTracks));
        Check.Equal(new[] { TrackRole.Mix, TrackRole.Game, TrackRole.Desktop }, Roles("Mix", "Game", "Desktop"));
        Check.Equal(new[] { TrackRole.Mix }, Roles("Mix"));
        Check.Equal(0, MixPlan.Roles(null, 0).Length);
    }

    [UnitTest]
    public static void A_file_without_usable_names_has_no_roles_and_no_remix()
    {
        Check.Equal(new[] { TrackRole.Other, TrackRole.Other, TrackRole.Other }, MixPlan.Roles(null, 3));
        Check.Equal(new[] { TrackRole.Other, TrackRole.Other }, MixPlan.Roles(FiveTracks, 2), "names that do not match the track count are not trusted");
        // v0.1.0 clips call their loopback track "All audio": not a Mix, so nothing is rebuilt from the tracks next to it.
        var old = Roles("All audio", "Game", "Mic");
        Check.Equal(TrackRole.Other, old[0]);
        Check.False(MixPlan.CanRemix(old));
        Check.False(MixPlan.CanRebuild(old));
        Check.False(MixPlan.CanRemix(MixPlan.Roles(null, 5)));
    }

    [UnitTest]
    public static void The_Mix_is_only_the_first_track_and_a_name_counts_once()
    {
        var r = Roles("Game", "Mix", "Mic");
        Check.Equal(new[] { TrackRole.Game, TrackRole.Other, TrackRole.Mic }, r, "a Mix that is not first is a stranger");
        Check.False(MixPlan.CanRemix(r));
        Check.Equal(new[] { TrackRole.Mix, TrackRole.Game, TrackRole.Other }, Roles("Mix", "Game", "Game"), "the second Game stands alone");
        Check.Equal(new[] { TrackRole.Mix, TrackRole.Other }, Roles("Mix", "mic"), "names are exact");
    }

    [UnitTest]
    public static void A_Mix_with_a_source_can_be_remixed_and_with_all_three_rebuilt()
    {
        Check.True(MixPlan.CanRemix(Roles(FiveTracks)));
        Check.True(MixPlan.CanRebuild(Roles(FiveTracks)));
        Check.True(MixPlan.CanRemix(Roles("Mix", "Game", "Mic")));
        Check.False(MixPlan.CanRebuild(Roles("Mix", "Game", "Mic")));
        Check.True(MixPlan.CanRemix(Roles("Mix", "Desktop", "Voice")), "one source is enough");
        Check.False(MixPlan.CanRemix(Roles("Mix", "Desktop")), "Desktop never feeds the Mix");
        Check.False(MixPlan.CanRemix(Roles("Mix")));
        Check.False(MixPlan.CanRebuild(Roles("Mix", "Game", "Mic", "Desktop")));
        Check.Equal(new[] { TrackRole.Voice }, MixPlan.MissingSources(Roles("Mix", "Game", "Mic", "Desktop")));
        Check.Equal(new[] { TrackRole.Game, TrackRole.Mic }, MixPlan.MissingSources(Roles("Mix", "Voice")));
        Check.Equal(0, MixPlan.MissingSources(Roles(FiveTracks)).Length);
        Check.Equal(0, MixPlan.MissingSources(Roles("Mix", "Desktop")).Length, "nothing to say when no source can be changed");
        var r = Roles(FiveTracks);
        Check.Equal(new[] { false, true, true, true, false }, Enumerable.Range(0, 5).Select(i => MixPlan.FeedsMix(r, i)).ToArray());
    }

    [UnitTest]
    public static void Rebuild_mode_plays_the_sources_at_their_volume_and_renders_the_mix_from_them()
    {
        var r = Roles(FiveTracks);
        var include = All(5);
        var gain = Gains(1f, .5f, 1f, 1f, 1f);

        Check.Equal(new[] { 0f, .5f, 1f, 1f, 0f }, MixPlan.PreviewGains(r, include, gain, listen: 0));

        var export = MixPlan.Export(r, include, gain);
        Check.Equal(5, export.Length);
        Check.True(export[0].Include);
        Check.Equal(1f, export[0].Gain, "the master is the Mix's own volume");
        Check.Equal(new[] { new MixInput(1, .5f), new MixInput(2, 1f), new MixInput(3, 1f) }, export[0].Sum!);
        for (int i = 1; i < 5; i++)
        {
            Check.Null(export[i].Sum, $"track {i} is copied or scaled on its own");
            Check.Equal(gain[i], export[i].Gain);
            Check.True(export[i].Include);
        }
    }

    [UnitTest]
    public static void Rebuild_mode_applies_the_mix_volume_as_the_master()
    {
        var r = Roles(FiveTracks);
        var gain = Gains(.5f, .5f, 1f, 2f, 1f);
        Check.Equal(new[] { 0f, .25f, .5f, 1f, 0f }, MixPlan.PreviewGains(r, All(5), gain, 0));
        var export = MixPlan.Export(r, All(5), gain);
        Check.Equal(.5f, export[0].Gain);
        Check.Equal(new[] { new MixInput(1, .5f), new MixInput(2, 1f), new MixInput(3, 2f) }, export[0].Sum!);
    }

    [UnitTest]
    public static void A_source_at_zero_drops_out_and_all_at_zero_is_a_silent_mix()
    {
        var r = Roles(FiveTracks);
        var export = MixPlan.Export(r, All(5), Gains(1f, 0f, 1f, 1f, 1f));
        Check.Equal(new[] { new MixInput(2, 1f), new MixInput(3, 1f) }, export[0].Sum!);
        var silent = MixPlan.Export(r, All(5), Gains(1f, 0f, 0f, 0f, 1f));
        Check.NotNull(silent[0].Sum, "a Mix of nothing is still rendered (as silence), not copied");
        Check.Equal(0, silent[0].Sum!.Length);
        Check.Equal(new[] { 0f, 0f, 0f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), Gains(1f, 0f, 0f, 0f, 1f), 0));
    }

    [UnitTest]
    public static void Delta_mode_subtracts_a_source_that_was_turned_down()
    {
        var r = Roles("Mix", "Game", "Mic");
        var gain = Gains(1f, 0f, 1f);
        Check.Equal(new[] { 1f, -1f, 0f }, MixPlan.PreviewGains(r, All(3), gain, 0));
        var export = MixPlan.Export(r, All(3), gain);
        Check.Equal(new[] { new MixInput(0, 1f), new MixInput(1, -1f) }, export[0].Sum!, "the mix itself and Game taken out again; Mic at 100% adds nothing");
        Check.Equal(1f, export[0].Gain);
    }

    [UnitTest]
    public static void Delta_mode_can_turn_a_source_up_and_scales_by_the_master()
    {
        var r = Roles("Mix", "Game", "Mic", "Desktop");
        var gain = Gains(.5f, 1.5f, .5f, 1f);
        var preview = MixPlan.PreviewGains(r, All(4), gain, 0);
        Check.Near(.5, preview[0], 1e-6);
        Check.Near(.25, preview[1], 1e-6);
        Check.Near(-.25, preview[2], 1e-6);
        Check.Near(0, preview[3], 1e-6);
        var export = MixPlan.Export(r, All(4), gain);
        Check.Equal(.5f, export[0].Gain);
        Check.Equal(3, export[0].Sum!.Length);
        Check.Equal(new MixInput(0, 1f), export[0].Sum![0]);
        Check.Equal(1, export[0].Sum![1].Track);
        Check.Near(.5, export[0].Sum![1].Gain, 1e-6);
        Check.Equal(2, export[0].Sum![2].Track);
        Check.Near(-.5, export[0].Sum![2].Gain, 1e-6);
    }

    [UnitTest]
    public static void With_every_source_at_100_percent_the_baked_mix_is_used_as_it_is()
    {
        var r = Roles(FiveTracks);
        var unity = Gains(1f, 1f, 1f, 1f, 1f);
        foreach (var export in new[] { MixPlan.Export(r, All(5), unity), MixPlan.Export(Roles("Mix", "Game", "Mic"), All(3), Gains(1f, 1f, 1f)) })
            foreach (var track in export) Check.Null(track.Sum, "no rendering: the Mix is copied untouched");
        Check.Equal(new[] { 1f, 0f, 0f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), unity, 0), "the preview plays the file's own All audio");
        // Only the Mix's own volume changed: the same single-track volume change as before, not a rebuild.
        var louder = MixPlan.Export(r, All(5), Gains(1.5f, 1f, 1f, 1f, 1f));
        Check.Null(louder[0].Sum);
        Check.Equal(1.5f, louder[0].Gain);
        Check.Equal(new[] { 1.5f, 0f, 0f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), Gains(1.5f, 1f, 1f, 1f, 1f), 0));
        // A volume that only differs by a rounding error is still 100%.
        Check.Null(MixPlan.Export(r, All(5), Gains(1f, 0.9996f, 1f, 1.0004f, 1f))[0].Sum);
    }

    [UnitTest]
    public static void Listening_to_any_other_track_plays_that_track_at_its_own_volume()
    {
        var r = Roles(FiveTracks);
        var gain = Gains(1f, .5f, 1f, 1f, .8f);
        Check.Equal(new[] { 0f, .5f, 0f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), gain, listen: 1));
        Check.Equal(new[] { 0f, 0f, 0f, 0f, .8f }, MixPlan.PreviewGains(r, All(5), gain, listen: 4));
        Check.Equal(new[] { 0f, 0f, 1f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), gain, listen: 2));
    }

    [UnitTest]
    public static void A_track_that_is_not_in_the_file_cannot_be_listened_to()
    {
        var r = Roles(FiveTracks);
        var gain = Gains(1f, .5f, 1f, 1f, 1f);
        var noGame = new[] { true, false, true, true, true };
        Check.Equal(new float[5], MixPlan.PreviewGains(r, noGame, gain, listen: 1));
        var noMix = new[] { false, true, true, true, true };
        Check.Equal(new float[5], MixPlan.PreviewGains(r, noMix, gain, listen: 0));
        Check.Equal(new float[5], MixPlan.PreviewGains(r, All(5), gain, listen: -1));
        Check.Equal(new float[5], MixPlan.PreviewGains(r, All(5), gain, listen: 9));
    }

    [UnitTest]
    public static void A_source_that_is_not_saved_on_its_own_still_shapes_All_audio()
    {
        var r = Roles(FiveTracks);
        var gain = Gains(1f, 0f, 1f, 1f, 1f);
        var noGame = new[] { true, false, true, true, true };
        Check.Equal(new[] { 0f, 0f, 1f, 1f, 0f }, MixPlan.PreviewGains(r, noGame, gain, 0));
        var export = MixPlan.Export(r, noGame, gain);
        Check.False(export[1].Include, "Game's own track is dropped from the file");
        Check.Equal(new[] { new MixInput(2, 1f), new MixInput(3, 1f) }, export[0].Sum!, "but it was taken out of All audio");
        // With the Mix itself left out there is nothing to render.
        var noMix = MixPlan.Export(r, new[] { false, true, true, true, true }, Gains(1f, .5f, 1f, 1f, 1f));
        Check.False(noMix[0].Include);
        Check.Null(noMix[0].Sum);
    }

    [UnitTest]
    public static void All_is_a_mix_only_as_the_first_track_and_All_audio_stays_a_stranger()
    {
        var r = Roles("All", "Game", "Mic", "Voice", "Desktop");
        Check.Equal(new[] { TrackRole.All, TrackRole.Game, TrackRole.Mic, TrackRole.Voice, TrackRole.Desktop }, r);
        Check.Equal(new[] { TrackRole.Other, TrackRole.Game, TrackRole.Other }, Roles("All audio", "Game", "All"), "\"All audio\" is not All, and All is a stranger when it is not first");
        Check.Equal(new[] { TrackRole.Game, TrackRole.Other, TrackRole.Mic }, Roles("Game", "All", "Mic"), "All that is not first is a stranger");
        Check.Equal(new[] { TrackRole.Other, TrackRole.Mic }, Roles("all", "Mic"), "names are exact");
        Check.True(MixPlan.IsMix(TrackRole.Mix));
        Check.True(MixPlan.IsMix(TrackRole.All));
        Check.False(MixPlan.IsMix(TrackRole.Other));
        Check.False(MixPlan.IsMix(TrackRole.Mic));
        Check.False(MixPlan.CanRemix(Roles("All audio", "Mic")));
        Check.False(MixPlan.CanRemix(Roles("Mic", "All")));
    }

    [UnitTest]
    public static void Only_the_Mic_feeds_an_All_and_an_All_is_never_rebuilt()
    {
        Check.True(MixPlan.Feeds(TrackRole.All, TrackRole.Mic));
        foreach (var role in new[] { TrackRole.Game, TrackRole.Voice, TrackRole.Desktop, TrackRole.Other, TrackRole.Mix, TrackRole.All })
            Check.False(MixPlan.Feeds(TrackRole.All, role), $"{role} is not inside All as the same sound");
        Check.Equal(3, new[] { TrackRole.Game, TrackRole.Mic, TrackRole.Voice }.Count(role => MixPlan.Feeds(TrackRole.Mix, role)));
        Check.False(MixPlan.Feeds(TrackRole.Mix, TrackRole.Desktop));
        Check.False(MixPlan.Feeds(TrackRole.Other, TrackRole.Mic));

        var r = Roles("All", "Game", "Mic", "Voice", "Desktop");
        Check.True(MixPlan.CanRemix(r), "the Mic is enough");
        Check.False(MixPlan.CanRebuild(r), "an All cannot be put together from its sources");
        Check.Equal(new[] { 2 }, MixPlan.SourceIndexes(r));
        Check.Equal(new[] { 1, 2, 3 }, MixPlan.SourceIndexes(Roles(FiveTracks)));
        Check.Equal(0, MixPlan.SourceIndexes(Roles("All audio", "Game", "Mic")).Length);
        Check.Equal(0, MixPlan.SourceIndexes(MixPlan.Roles(null, 3)).Length);
        Check.Equal(new[] { false, false, true, false, false }, Enumerable.Range(0, 5).Select(i => MixPlan.FeedsMix(r, i)).ToArray());
        Check.False(MixPlan.CanRemix(Roles("All", "Game", "Voice", "Desktop")), "without a Mic track there is nothing to take out of All");
        Check.True(MixPlan.CanRemix(Roles("All", "Mic")));
    }

    [UnitTest]
    public static void On_an_All_clip_the_Mic_at_zero_is_taken_out_of_All_by_a_delta()
    {
        var r = Roles("All", "Game", "Mic", "Voice", "Desktop");
        var gain = Gains(1f, 1f, 0f, 1f, 1f);
        var export = MixPlan.Export(r, All(5), gain);
        Check.Equal(new[] { new MixInput(0, 1f), new MixInput(2, -1f) }, export[0].Sum!);
        Check.Equal(1f, export[0].Gain);
        for (int i = 1; i < 5; i++) Check.Null(export[i].Sum, $"track {i} stands on its own");
        Check.Equal(new[] { 1f, 0f, -1f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), gain, 0));
        Check.True(MixPlan.PreviewIsRemix(r, All(5), gain, 0));
        // Half the Mic, and the master of All scales the delta.
        var half = MixPlan.Export(r, All(5), Gains(.5f, 1f, .5f, 1f, 1f));
        Check.Equal(.5f, half[0].Gain);
        Check.Equal(2, half[0].Sum!.Length);
        Check.Equal(new MixInput(0, 1f), half[0].Sum![0]);
        Check.Equal(2, half[0].Sum![1].Track);
        Check.Near(-.5, half[0].Sum![1].Gain, 1e-6);
    }

    [UnitTest]
    public static void On_an_All_clip_the_Game_and_Voice_sliders_change_only_their_own_track()
    {
        var r = Roles("All", "Game", "Mic", "Voice", "Desktop");
        foreach (var gain in new[] { Gains(1f, 0f, 1f, 1f, 1f), Gains(1f, 1f, 1f, 0f, 1f), Gains(1f, .5f, 1f, 2f, 1f), Gains(1f, 0f, 1f, 0f, 0f) })
        {
            var export = MixPlan.Export(r, All(5), gain);
            Check.Null(export[0].Sum, "All is copied");
            for (int i = 0; i < 5; i++)
            {
                Check.Null(export[i].Sum);
                Check.Equal(gain[i], export[i].Gain, $"track {i} at its own volume");
                Check.True(export[i].Include);
            }
            Check.Equal(new[] { 1f, 0f, 0f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), gain, 0), "the preview plays the baked All");
            Check.False(MixPlan.PreviewIsRemix(r, All(5), gain, 0));
        }
        // Listening to Game plays Game at its own volume.
        Check.Equal(new[] { 0f, .5f, 0f, 0f, 0f }, MixPlan.PreviewGains(r, All(5), Gains(1f, .5f, 1f, 1f, 1f), 1));
        // A moved Mic still only adds the Mic's delta, whatever Game and Voice did.
        var both = MixPlan.Export(r, All(5), Gains(1f, 0f, 0f, 0f, 1f));
        Check.Equal(new[] { new MixInput(0, 1f), new MixInput(2, -1f) }, both[0].Sum!);
        Check.Equal(0f, both[1].Gain);
        Check.Equal(0f, both[3].Gain);
        // The master of All alone is a plain volume change.
        var louder = MixPlan.Export(r, All(5), Gains(1.5f, 1f, 1f, 1f, 1f));
        Check.Null(louder[0].Sum);
        Check.Equal(1.5f, louder[0].Gain);
    }

    [UnitTest]
    public static void An_All_clip_never_misses_a_source_other_than_the_Mic_and_remixes_only_with_it()
    {
        // The Mic is the only source of an All, and an All is remixable only with it: so nothing is ever missing from a remixable All,
        // and Game and Voice are never reported as sources a Mix lacks.
        Check.Equal(0, MixPlan.MissingSources(Roles("All", "Game", "Mic")).Length);
        Check.Equal(0, MixPlan.MissingSources(Roles("All", "Game", "Voice", "Desktop")).Length, "no Mic: nothing can be changed, so nothing is said");
        Check.Equal(new[] { TrackRole.Voice }, MixPlan.MissingSources(Roles("Mix", "Game", "Mic")), "a Mix still misses its Voice");
    }

    [UnitTest]
    public static void Leaving_out_the_first_track_names_the_track_a_player_will_play_instead()
    {
        foreach (var r in new[] { Roles("All", "Game", "Mic"), Roles("Mix", "Game", "Mic") })
        {
            Check.True(MixPlan.FirstTrackDropped(r, All(3)) == null, "track 1 kept: nothing is dropped");
            Check.True(MixPlan.FirstTrackDropped(r, new[] { false, true, true }) == 1, "the first kept track is the one players play");
            Check.True(MixPlan.FirstTrackDropped(r, new[] { false, false, true }) == 2, "skipping an unticked one");
            Check.True(MixPlan.FirstTrackDropped(r, All(3, false)) == null, "nothing kept at all: no note");
            Check.True(MixPlan.FirstTrackDropped(r, new[] { true, false, false }) == null);
        }
        var plain = Roles("Game", "Mic");
        Check.True(MixPlan.FirstTrackDropped(plain, new[] { false, true }) == null, "a first track that is not a mix has no note");
        Check.True(MixPlan.FirstTrackDropped(Roles("All"), new[] { false }) == null, "a lone track has nothing else to play");
        Check.True(MixPlan.FirstTrackDropped(Array.Empty<TrackRole>(), Array.Empty<bool>()) == null);
    }

    [UnitTest]
    public static void The_headless_trim_reads_a_volume_as_a_factor_or_a_percentage()
    {
        Check.Equal(("Game", 0f), TrimCli.ParseGain("Game=0"));
        Check.Equal(("Mic", .5f), TrimCli.ParseGain("Mic=0.5"));
        Check.Equal(("Voice", 1.5f), TrimCli.ParseGain("Voice=150%"));
        Check.Equal(("Mix", .25f), TrimCli.ParseGain(" Mix = 25 % "));
        foreach (string bad in new[] { "Game", "Game=", "=1", "Game=loud", "Game=-1", "Game=%" })
            Check.Throws<ArgumentException>(() => TrimCli.ParseGain(bad), bad);
    }

    [UnitTest]
    public static void Without_roles_every_track_is_exported_as_ticked_at_its_own_volume()
    {
        var none = MixPlan.Roles(null, 3);
        var export = MixPlan.Export(none, new[] { true, false, true }, Gains(1f, .5f, 2f));
        Check.Equal(new[] { new TrackMix(true, 1f), new TrackMix(false, .5f), new TrackMix(true, 2f) }, export);
        Check.Equal(new[] { .7f, 0f, 0f }, MixPlan.PreviewGains(none, All(3), Gains(.7f, .5f, 2f), 0));
        Check.Equal(0, MixPlan.Export(MixPlan.Roles(null, 0), Array.Empty<bool>(), Array.Empty<float>()).Length);
    }
}
