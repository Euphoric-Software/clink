using System.IO;
using System.Reflection;

namespace Clink.Services;

/// <summary>
/// How this exe carries the .NET runtime (separate from <see cref="AppMode"/>, which says where the copy lives): the portable
/// <c>Clink.exe</c> is <see cref="SelfContained"/> (runtime inside the single file), the installer's exe and the dev builds in
/// <c>bin</c> are <see cref="Framework"/> (they use an installed .NET 10 Desktop Runtime). Decides which update asset a copy
/// takes and which updates folder it uses.
/// </summary>
internal enum Packaging { SelfContained, Framework }

/// <summary>What the exe's own runtime looks like, how the build labelled it, and what <see cref="PackagingInfo"/> made of the two.</summary>
internal sealed record PackagingDecision(Packaging Packaging, Packaging RuntimeSays, string? BuildLabel, string? Disagreement);

/// <summary>
/// Packaging detection. The runtime is the authority: a single-file self-contained exe has no <c>FX_DEPS_FILE</c> (the host sets it only
/// when it resolves a shared framework) and its core library is inside the bundle (<c>Assembly.Location</c> is empty); a
/// framework-dependent exe has the shared framework's deps file and a core library on disk. The <c>Packaging</c> AssemblyMetadata that
/// Clink.csproj computes from <c>$(SelfContained)</c> at build time is only a cross-check: when it disagrees with the runtime the
/// copy is treated as <see cref="Packaging.SelfContained"/> (the flavour that works with no installed runtime) and the mismatch is
/// logged once by <see cref="Updater.LogSource"/>.
/// </summary>
internal static class PackagingInfo
{
    internal const string MetadataKey = "Packaging";

    /// <summary>The pure decision (the unit tests feed it every combination).</summary>
    internal static PackagingDecision Decide(string? coreLibLocation, string? fxDepsFile, string? buildLabel)
    {
        // Either signal missing means there is no shared framework behind this exe. A framework-dependent exe has both.
        var runtime = string.IsNullOrEmpty(fxDepsFile) || string.IsNullOrEmpty(coreLibLocation) ? Packaging.SelfContained : Packaging.Framework;
        Packaging? label = buildLabel?.Trim() is { } text && Enum.GetNames<Packaging>().Contains(text) ? Enum.Parse<Packaging>(text) : null;   // exact names only: "1" or "framework" is no label
        if (label is { } l && l != runtime)
            return new(Packaging.SelfContained, runtime, buildLabel, $"the build says {l} but the runtime looks {runtime}; treating this copy as SelfContained");
        return new(runtime, runtime, buildLabel, null);
    }

    /// <summary>The two runtime signals of this process: where the core library is loaded from (empty in a single-file bundle) and the host's FX_DEPS_FILE.</summary>
    internal static (string? CoreLibLocation, string? FxDepsFile) RuntimeSignals()
    {
        string? location = null;
#pragma warning disable IL3000   // intentional: empty in a single-file bundle is exactly the signal
        try { location = typeof(object).Assembly.Location; } catch { }
#pragma warning restore IL3000
        return (location, AppContext.GetData("FX_DEPS_FILE") as string);
    }

    private static readonly Lazy<PackagingDecision> s_current = new(() =>
    {
        var (location, deps) = RuntimeSignals();
        string? label = null;
        try
        {
            label = (Assembly.GetEntryAssembly() ?? typeof(PackagingInfo).Assembly).GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == MetadataKey)?.Value;
        }
        catch { }
        return Decide(location, deps, label);
    });

    /// <summary>The decision for the running process (computed once).</summary>
    internal static PackagingDecision Current => s_current.Value;
}

/// <summary>
/// <c>Clink.exe --packaging-check [SelfContained|Framework]</c>: for CI, which has to look at the published exes (the unit tests cannot
/// run from them). Prints one line (the packaging this exe decided on, the build's label and the runtime signals) and exits 0 when
/// the build label and the runtime agree and, if an expected packaging was given, it is the one decided on.
/// </summary>
internal static class PackagingCheck
{
    internal const string Verb = "--packaging-check";
    internal const int ExitDisagreement = 1, ExitUnexpected = 2, ExitFailed = 5, ExitBadArgument = 64;

    /// <summary>The pure part: the exit code and the output line for a decision, the signals it was made from and the optional expected packaging text.</summary>
    internal static int Evaluate(PackagingDecision d, string? coreLibLocation, string? fxDepsFile, string? expected, out string line)
    {
        string signals = $"packaging={d.Packaging} build={d.BuildLabel ?? "none"} runtime={d.RuntimeSays} corelib={(string.IsNullOrEmpty(coreLibLocation) ? "bundled" : "on-disk")} fx_deps={(string.IsNullOrEmpty(fxDepsFile) ? "absent" : "present")}";
        Packaging? want = null;
        if (expected != null)
        {
            // Names only: Enum.TryParse would take "1" as Framework.
            string name = expected.Trim();
            if (!Enum.GetNames<Packaging>().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) || !Enum.TryParse<Packaging>(name, ignoreCase: true, out var parsed))
            {
                line = $"{signals} result=bad-argument (expected must be SelfContained or Framework, got '{expected}')";
                return ExitBadArgument;
            }
            want = parsed;
        }
        if (d.Disagreement != null) { line = $"{signals} result=disagreement ({d.Disagreement})"; return ExitDisagreement; }
        if (want is { } w && w != d.Packaging) { line = $"{signals} result=unexpected (wanted {w})"; return ExitUnexpected; }
        line = signals + " result=ok";
        return 0;
    }

    /// <summary>Runs the verb for this process: prints the line to the console and returns the exit code. <paramref name="args"/> are the whole command line (args[0] is the verb).</summary>
    internal static int Run(string[] args)
    {
        var (location, deps) = PackagingInfo.RuntimeSignals();
        int code = Evaluate(PackagingInfo.Current, location, deps, args.Length > 1 ? args[1] : null, out string line);
        Console.WriteLine(line);
        return code;
    }
}

/// <summary>The installed Microsoft.WindowsDesktop.App runtimes (what a framework-dependent Clink needs), for the manifest's minRuntime check.</summary>
internal static class DotNetRuntimes
{
    /// <summary>Stands in for the machine's runtimes in the unit tests; null otherwise.</summary>
    internal static IReadOnlyList<Version>? Override { get; set; }

    /// <summary>
    /// Versions found in <c>shared\Microsoft.WindowsDesktop.App\</c> under <see cref="DefaultRoots"/>, plus the runtime this process runs on
    /// when it is a framework-dependent copy (<see cref="ScanDesktop"/>). Never throws.
    /// </summary>
    internal static IReadOnlyList<Version> InstalledDesktop()
    {
        if (Override != null) return Override;
        try { return ScanDesktop(DefaultRoots(), PackagingInfo.Current.Packaging == Packaging.Framework ? Environment.Version : null); }
        catch { return Array.Empty<Version>(); }
    }

    /// <summary>
    /// The registered x64 install location, read from HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64 in the 32-bit registry view
    /// (where the .NET installer writes it and the apphost looks, i.e. WOW6432Node), read only. Null if there is none. Never throws.
    /// </summary>
    internal static string? ReadInstallLocation()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var key = hklm.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64", writable: false);
            return key?.GetValue("InstallLocation") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// The dotnet roots a framework-dependent apphost searches with <c>AppHostDotNetSearch=Global</c>: the registered install location
    /// (HKLM, 32-bit view, read only; <paramref name="installLocation"/> stands in for the registry in the unit tests) and Program
    /// Files\dotnet. DOTNET_ROOT is deliberately not one of them: a variable in the user's environment must not decide what counts
    /// as an installed runtime.
    /// </summary>
    internal static IReadOnlyList<string> DefaultRoots(Func<string?>? installLocation = null)
    {
        var roots = new List<string>();
        try
        {
            if ((installLocation ?? ReadInstallLocation)() is string reg && reg.Length > 0) roots.Add(reg);
        }
        catch { }
        try { roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")); } catch { }
        return roots;
    }

    /// <summary>
    /// Versions of <c>shared\Microsoft.WindowsDesktop.App\*</c> under each of <paramref name="roots"/> (each root once, any case), with
    /// <paramref name="running"/> (the runtime this process is on, if it is a framework copy) first. A root that is missing or
    /// cannot be read is skipped without hiding the others. Never throws.
    /// </summary>
    internal static IReadOnlyList<Version> ScanDesktop(IEnumerable<string> roots, Version? running)
    {
        var found = new List<Version>();
        if (running != null) found.Add(running);
        IEnumerable<string> distinct;
        try { distinct = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return found; }
        foreach (string root in distinct)
        {
            try
            {
                string dir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(dir)) continue;
                foreach (string sub in Directory.EnumerateDirectories(dir))
                    if (ParseVersion(Path.GetFileName(sub)) is { } v) found.Add(v);
            }
            catch { }   // this root only
        }
        return found;
    }

    /// <summary>"10.0.1", "10.0.0-preview.1", "10.0" -> a Version (suffix dropped); null if it is not one.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string core = text.Trim().TrimStart('v', 'V');
        int dash = core.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) core = core[..dash];
        return Version.TryParse(core, out var v) ? v : null;
    }

    /// <summary>
    /// True when <paramref name="minRuntime"/> (the manifest's text) is empty, unreadable, or not newer than the newest of
    /// <paramref name="installed"/> of the same major version (a host does not roll forward across majors by default, so 11.0.1 does not
    /// satisfy 10.0.5). A readable minimum with nothing installed is not met.
    /// </summary>
    internal static bool MeetsMinimum(string? minRuntime, IEnumerable<Version> installed)
    {
        if (string.IsNullOrWhiteSpace(minRuntime) || ParseVersion(minRuntime) is not { } min) return true;
        return installed.Any(v => v.Major == min.Major && v >= min);
    }
}
