namespace Clink.Engine;

/// <summary>What a track of a clip is, going by its name in the file (Clink writes Mix or All first, then Game, Mic, Voice and Desktop).</summary>
internal enum TrackRole { Mix, Game, Mic, Voice, Desktop, Other, All }

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
/// <item><b>All</b> (first track of newer clips): everything except Clink's own sound, plus the Mic. Only the Mic is that same PCM, so
/// only the Mic can be taken out of it exactly (Delta mode, never Rebuild); Game, Voice and Desktop are independent alternates whose
/// sliders change only their own track.</item>
/// <item>Desktop and any other track stand alone: they never feed the Mix. A track's own volume always applies to its own track.</item>
/// </list>
/// </summary>
internal static class MixPlan
{
    /// <summary>Volumes this close to 1 count as untouched, this close to 0 as silent (the sliders step by 0.01).</summary>
    private const float Eps = 0.001f;

    /// <summary>The track names Clink writes.</summary>
    public const string MixName = "Mix", AllName = "All", GameName = "Game", MicName = "Mic", VoiceName = "Voice", DesktopName = "Desktop";

    private static readonly TrackRole[] AllSources = { TrackRole.Game, TrackRole.Mic, TrackRole.Voice };

    /// <summary>
    /// The role of each of <paramref name="count"/> tracks, from the names the file itself has (<see cref="Trimmer.ReadAudioTrackNames"/>,
    /// which only trusts files Clink wrote). The Mix is the track named "Mix" <b>first in the file</b>; a name is taken once, so a
    /// file with two "Game" tracks has one source and one stranger. No names, or names that do not match the count, mean no roles:
    /// every track stands alone. A v0.1.0 clip calls its loopback track "All audio": that is not a Mix and stays on its own, and "All" anywhere but first is a stranger.
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
            if (i == 0 && name == MixName) roles[i] = TrackRole.Mix;
            else if (i == 0 && name == AllName) roles[i] = TrackRole.All;
            else if (name == GameName && !game) { roles[i] = TrackRole.Game; game = true; }
            else if (name == MicName && !mic) { roles[i] = TrackRole.Mic; mic = true; }
            else if (name == VoiceName && !voice) { roles[i] = TrackRole.Voice; voice = true; }
            else if (name == DesktopName && !desktop) { roles[i] = TrackRole.Desktop; desktop = true; }
        }
        return roles;
    }

    /// <summary>All audio is first in the file and at least one of Game, Mic and Voice is in it too: their sliders can shape it.</summary>
    public static bool CanRemix(TrackRole[] r) => r.Length > 1 && IsMix(r[0]) && Sources(r).Count > 0;

    /// <summary>The first-track roles that hold a baked mix: Mix (Game + Mic + Voice) and All (everything but Clink, plus the Mic).</summary>
    public static bool IsMix(TrackRole r) => r is TrackRole.Mix or TrackRole.All;

    /// <summary>True when <paramref name="src"/> is baked into the first track <paramref name="mix"/> as the very same PCM: Game, Mic and Voice for a Mix, the Mic alone for an All.</summary>
    public static bool Feeds(TrackRole mix, TrackRole src) => mix switch
    {
        TrackRole.Mix => src is TrackRole.Game or TrackRole.Mic or TrackRole.Voice,
        TrackRole.All => src == TrackRole.Mic,
        _ => false,
    };

    /// <summary>The indexes of the tracks that feed the first-track mix (<see cref="Feeds"/>); empty when the first track is not a mix.</summary>
    public static int[] SourceIndexes(TrackRole[] r) => Sources(r).ToArray();

    /// <summary>
    /// The index of the first track a player will play when the first track (a Mix or All, the baked mix) is left out of the file
    /// but another track is kept; null when the first track is kept, is not a mix, or nothing at all is kept.
    /// </summary>
    public static int? FirstTrackDropped(TrackRole[] r, bool[] include)
    {
        if (r.Length < 2 || !IsMix(r[0]) || Included(include, 0)) return null;
        for (int i = 1; i < r.Length; i++)
            if (Included(include, i)) return i;
        return null;
    }

    /// <summary>The Mix and all three sources are in the file: All audio can be rendered from them alone.</summary>
    public static bool CanRebuild(TrackRole[] r) => r.Length > 3 && r[0] == TrackRole.Mix && Sources(r).Count == 3;   // an All is never rebuilt from sources

    /// <summary>The sources a remixable Mix has no track for (they are inside the baked mix only, so their volume cannot be changed); empty when it is not remixable.</summary>
    public static TrackRole[] MissingSources(TrackRole[] r)
    {
        if (!CanRemix(r)) return Array.Empty<TrackRole>();
        return AllSources.Where(role => Feeds(r[0], role) && Array.IndexOf(r, role) < 0).ToArray();
    }

    /// <summary>True for the tracks whose volume also shapes All audio: Game, Mic and Voice of a file whose Mix can be remixed.</summary>
    public static bool FeedsMix(TrackRole[] r, int index) => index > 0 && index < r.Length && Feeds(r[0], r[index]) && CanRemix(r);

    /// <summary>
    /// The same roles with the given tracks standing alone: a source that cannot be lined up with the Mix (a clip trimmed by an older
    /// Clink, see <see cref="ClipAlignment"/>) must not be added to or taken out of it. The Mix stays the Mix; with no source left
    /// nothing is rebuilt.
    /// </summary>
    public static TrackRole[] WithoutTracks(TrackRole[] r, IEnumerable<int> indexes)
    {
        var copy = (TrackRole[])r.Clone();
        foreach (int i in indexes)
            if (i > 0 && i < copy.Length && Feeds(IsMix(copy[0]) ? copy[0] : TrackRole.Mix, copy[i])) copy[i] = TrackRole.Other;
        return copy;
    }

    /// <summary>
    /// True when <see cref="PreviewGains"/> for this state is a Mix put together from the sources (a rebuilt or corrected All audio)
    /// and not one track at its own volume: that sum is limited with the recording's soft knee, a single track with the plain clip.
    /// </summary>
    public static bool PreviewIsRemix(TrackRole[] r, bool[] include, float[] gain, int listen)
        => listen >= 0 && listen < r.Length && Included(include, listen) && listen == 0 && IsMix(r[0]) && CanRemix(r) && !Untouched(Sources(r), gain);

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
        if (listen != 0 || !CanRemix(r))
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
        if (r.Length == 0 || !IsMix(r[0])) return list;
        for (int i = 1; i < r.Length; i++)
            if (Feeds(r[0], r[i])) list.Add(i);
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
