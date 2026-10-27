using Clink.UI;

namespace Clink.Services;

/// <summary>Writes what the first-run wizard decided into <see cref="Settings"/>. Pure over the settings object; the caller saves them and creates shortcuts / the Run key.</summary>
internal static class SetupApply
{
    /// <summary>The version of the setup this build runs; stored so a later build can tell what was answered already.</summary>
    public const int CurrentSetupVersion = 1;

    /// <summary>
    /// Answered: the preset (Custom changes nothing), the replay length (a value that is none of the choices changes nothing), run at
    /// startup, the hotkeys (when they were shown). Skipped on a fresh store: the defaults, written explicitly. Skipped on a re-run:
    /// only the setup version, every other setting stays as it was.
    /// </summary>
    public static void Apply(Settings s, SetupResult r, bool freshStore)
    {
        if (r.Skipped)
        {
            if (freshStore) SetupPresets.ApplyDefaults(s);
            StampVersion(s);
            return;
        }
        SetupPresets.Apply(s, r.Quality);
        SetupPresets.ApplyReplay(s, r.ReplaySeconds);
        s.RunAtStartup = r.RunAtStartup;
        if (r.Hotkeys != null)
        {
            s.Hotkeys = r.Hotkeys.Select(SetupWizardModel.Clone).ToList();
            // The v1 field mirrors the main hotkey (Settings.Hotkey): older code and a downgrade still read it.
            if (s.Hotkeys.Count > 0 && s.Hotkeys[0].Keys.Vk > 0) s.Hotkey = s.Hotkeys[0].Keys.Clone();
        }
        StampVersion(s);
    }

    private static void StampVersion(Settings s)
    {
        // slice 3: Settings.SetupVersion does not exist yet (slice 3a adds it). Then: s.SetupVersion = CurrentSetupVersion;
    }
}
