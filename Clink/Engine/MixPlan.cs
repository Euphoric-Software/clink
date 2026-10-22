namespace Clink.Engine;

/// <summary>What a track of a clip is, going by its name in the file (Clink writes Mix, Game, Mic, Voice and Desktop).</summary>
internal enum TrackRole { Mix, Game, Mic, Voice, Desktop, Other }

/// <summary>
/// How the volume sliders of the trim editor shape "All audio", the first track of a Clink clip. That track (the <b>Mix</b>) is
/// baked at record time as Game + Mic + Voice, so a slider on one of its sources used to change only that source's own track and
/// never what the Mix contained. A plan decides, from the roles of the file's tracks, what the preview plays and what the export
/// renders so that Game / Mic / Voice shape the Mix as well. Pure: no file, no player, no window.
/// <list type="bullet">
/// <item><b>Rebuild</b> (Mix, Game, Mic and Voice all in the file): All audio = master × Σ (gain of source × source). The Mix is
/// rendered again from the three source tracks.</item>
/// <item><b>Delta</b> (the Mix and at least one, but not all three, sources; for example Voice is not saved as its own track):
/// All audio = master × (mix + Σ (gain of source − 1) × source). A source turned down is taken out of the baked mix by adding it
/// again with a negative gain. The mix and the source were AAC-coded separately, so a source pulled to 0 % can leave a faint
/// trace of itself.</item>
/// <item>With every source at 100 % the baked Mix is used as it is (preview and export alike), which is what a plain save does.</item>
/// <item>Desktop and any other track stand alone: they never feed the Mix. A track's own volume always applies to its own track.</item>
/// </list>
/// </summary>
internal static class MixPlan
{
    /// <summary>Volumes this close to 1 count as untouched, this close to 0 as silent (the sliders step by 0.01).</summary>
    private const float Eps = 0.001f;

    /// <summary>
    /// The role of each of <paramref name="count"/> tracks, from the names the file itself has (<see cref="Trimmer.ReadAudioTrackNames"/>,
    /// which only trusts files Clink wrote). The Mix is the track named "Mix" <b>first in the file</b>; a name is taken once, so a
    /// file with two "Game" tracks has one source and one stranger. No names, or names that do not match the count, mean no roles:
    /// every track stands alone. A v0.1.0 clip calls its loopback track "All audio": that is not a Mix and stays on its own.
    /// </summary>
    public static TrackRole[] Roles(string[]? names, int count)
    {
        var roles = new TrackRole[Math.Max(0, count)];
        Array.Fill(roles, TrackRole.Other);
        if (names == null || names.Length != roles.Length) return roles;
        bool game = false, mic = false, voice = false, desktop = false;
        for (int i = 0; i < roles.Length; i++)
        {
            string name = names[i] ?? "";
            if (i == 0 && name == "Mix") roles[i] = TrackRole.Mix;
            else if (name == "Game" && !game) { roles[i] = TrackRole.Game; game = true; }
            else if (name == "Mic" && !mic) { roles[i] = TrackRole.Mic; mic = true; }
            else if (name == "Voice" && !voice) { roles[i] = TrackRole.Voice; voice = true; }
            else if (name == "Desktop" && !desktop) { roles[i] = TrackRole.Desktop; desktop = true; }
        }
        return roles;
    }

    /// <summary>All audio is first in the file and at least one of Game, Mic and Voice is in it too: their sliders can shape it.</summary>
    public static bool CanRemix(TrackRole[] r) => r.Length > 1 && r[0] == TrackRole.Mix && Sources(r).Count > 0;

    /// <summary>The Mix and all three sources are in the file: All audio can be rendered from them alone.</summary>
    public static bool CanRebuild(TrackRole[] r) => r.Length > 3 && r[0] == TrackRole.Mix && Sources(r).Count == 3;

    /// <summary>The sources a remixable Mix has no track for (they are inside the baked mix only, so their volume cannot be changed); empty when it is not remixable.</summary>
    public static TrackRole[] MissingSources(TrackRole[] r)
    {
        if (!CanRemix(r)) return Array.Empty<TrackRole>();
        return new[] { TrackRole.Game, TrackRole.Mic, TrackRole.Voice }.Where(role => Array.IndexOf(r, role) < 0).ToArray();
    }

    /// <summary>True for the tracks whose volume also shapes All audio: Game, Mic and Voice of a file whose Mix can be remixed.</summary>
    public static bool FeedsMix(TrackRole[] r, int index) => index > 0 && index < r.Length && r[index] is TrackRole.Game or TrackRole.Mic or TrackRole.Voice && CanRemix(r);

    /// <summary>
    /// The same roles with the given tracks standing alone: a source that cannot be lined up with the Mix (a clip trimmed by an older
    /// Clink, see <see cref="ClipAlignment"/>) must not be added to or taken out of it. The Mix stays the Mix; with no source left
    /// nothing is rebuilt.
    /// </summary>
    public static TrackRole[] WithoutTracks(TrackRole[] r, IEnumerable<int> indexes)
    {
        var copy = (TrackRole[])r.Clone();
        foreach (int i in indexes)
            if (i > 0 && i < copy.Length && copy[i] is TrackRole.Game or TrackRole.Mic or TrackRole.Voice) copy[i] = TrackRole.Other;
        return copy;
    }

    /// <summary>
    /// True when <see cref="PreviewGains"/> for this state is a Mix put together from the sources (a rebuilt or corrected All audio)
    /// and not one track at its own volume: that sum is limited with the recording's soft knee, a single track with the plain clip.
    /// </summary>
    public static bool PreviewIsRemix(TrackRole[] r, bool[] include, float[] gain, int listen)
        => listen >= 0 && listen < r.Length && Included(include, listen) && r[listen] == TrackRole.Mix && CanRemix(r) && !Untouched(Sources(r), gain);

    /// <summary>
    /// The player's gain for each track while <paramref name="listen"/> is the one being auditioned (0 = silent; delta mode uses
    /// negative gains). A track that is not in the file (<paramref name="include"/> false) cannot be listened to. Listening to any
    /// track but All audio plays that track at its own volume; listening to All audio of a remixable file plays the sources
    /// (or the baked mix) so that what is heard is what <see cref="Export"/> renders.
    /// </summary>
    public static float[] PreviewGains(TrackRole[] r, bool[] include, float[] gain, int listen)
    {
        var g = new float[r.Length];
        if (listen < 0 || listen >= r.Length || !Included(include, listen)) return g;
        if (r[listen] != TrackRole.Mix || !CanRemix(r))
        {
            g[listen] = Gain(gain, listen);
            return g;
        }
        float master = Gain(gain, 0);
        var sources = Sources(r);
        if (Untouched(sources, gain))
        {
            g[0] = master;   // nothing to rebuild: the baked mix is the file's All audio, exactly
        }
        else if (CanRebuild(r))
        {
            foreach (int s in sources) g[s] = master * Gain(gain, s);
        }
        else
        {
            g[0] = master;
            foreach (int s in sources) g[s] = master * (Gain(gain, s) - 1f);
        }
        return g;
    }

    /// <summary>
    /// What <see cref="Trimmer.Trim(string, string, double, double, string[]?, TrackMix[]?)"/> should do with each track, in file order:
    /// every track as ticked at its own volume, except a remixable Mix whose sources were moved, which is rendered from them
    /// (<see cref="TrackMix.Sum"/>, with the Mix's own volume as the master). A source whose own track is not ticked still feeds the Mix.
    /// </summary>
    public static TrackMix[] Export(TrackRole[] r, bool[] include, float[] gain)
    {
        var mix = new TrackMix[r.Length];
        for (int i = 0; i < r.Length; i++) mix[i] = new TrackMix(Included(include, i), Gain(gain, i));
        if (!CanRemix(r) || !mix[0].Include) return mix;
        var sources = Sources(r);
        if (Untouched(sources, gain)) return mix;
        var inputs = new List<MixInput>();
        if (CanRebuild(r))
        {
            foreach (int s in sources)
                if (Math.Abs(Gain(gain, s)) >= Eps) inputs.Add(new MixInput(s, Gain(gain, s)));
        }
        else
        {
            inputs.Add(new MixInput(0, 1f));
            foreach (int s in sources)
            {
                float delta = Gain(gain, s) - 1f;
                if (Math.Abs(delta) >= Eps) inputs.Add(new MixInput(s, delta));
            }
        }
        mix[0] = new TrackMix(true, Gain(gain, 0), inputs.ToArray());
        return mix;
    }

    private static List<int> Sources(TrackRole[] r)
    {
        var list = new List<int>(3);
        if (r.Length == 0 || r[0] != TrackRole.Mix) return list;
        for (int i = 1; i < r.Length; i++)
            if (r[i] is TrackRole.Game or TrackRole.Mic or TrackRole.Voice) list.Add(i);
        return list;
    }

    private static bool Untouched(List<int> sources, float[]? gain)
    {
        foreach (int s in sources)
            if (Math.Abs(Gain(gain, s) - 1f) >= Eps) return false;
        return true;
    }

    private static bool Included(bool[]? include, int i) => include == null || i >= include.Length || include[i];
    private static float Gain(float[]? gain, int i) => gain != null && i < gain.Length ? gain[i] : 1f;
}
