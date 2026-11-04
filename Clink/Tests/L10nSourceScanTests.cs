using System.IO;

namespace Clink.Tests;

/// <summary>The discovery of the source folder by <see cref="L10nSourceScan.Root(string)"/>: the repo layout is &lt;repo&gt;\Clink\Clink.csproj.</summary>
internal static class L10nSourceScanTests
{
    [UnitTest]
    public static void Root_finds_the_Clink_folder_from_dist_and_from_bin()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "clink-l10n-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            string proj = Path.Combine(tmp, "repo", "Clink");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "Clink.csproj"), "<Project />");
            string dist = Path.Combine(tmp, "repo", "dist", "sc");
            string bin = Path.Combine(proj, "bin", "Release", "x");
            Directory.CreateDirectory(dist);
            Directory.CreateDirectory(bin);
            Check.Equal(proj, L10nSourceScan.Root(dist), "from a published exe under dist");
            Check.Equal(proj, L10nSourceScan.Root(bin), "from a bin build");
            Check.Equal(proj, L10nSourceScan.Root(proj), "from the project folder itself");
            Check.Equal(proj, L10nSourceScan.Root(Path.Combine(tmp, "repo")), "from the repo folder");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}