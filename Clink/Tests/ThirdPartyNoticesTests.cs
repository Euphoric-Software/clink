using System.IO;
using System.Text;
using System.Windows.Automation;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>The embedded third-party notices (THIRD-PARTY-NOTICES.txt), the window that shows them and the App tab link that opens it.</summary>
internal static class ThirdPartyNoticesTests
{
    [UnitTest]
    public static void The_notices_resource_is_embedded_and_valid_UTF8()
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ThirdPartyNotices.ResourceName);
        Check.NotNull(stream, "the resource is embedded");
        using var memory = new MemoryStream();
        stream!.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        Check.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        string text = new UTF8Encoding(false, true).GetString(bytes);   // throws on invalid UTF-8
        Check.True(text.Length > 20_000, $"the notices are long ({text.Length} characters)");
        Check.Equal(text, ThirdPartyNotices.Load(), "Load() is the resource text");
    }

    [UnitTest]
    public static void Every_required_section_is_there_once_in_order_with_text()
    {
        var sections = ThirdPartyNotices.ParseSections(ThirdPartyNotices.Load());
        Check.Equal(sections.Count, sections.Select(s => s.Id).Distinct().Count(), "ids are unique");
        var positions = new List<int>();
        foreach (var (id, title) in ThirdPartyNotices.RequiredSections)
        {
            var matches = sections.Where(s => s.Id == id).ToList();
            Check.Equal(1, matches.Count, $"section [{id}] appears exactly once");
            Check.Equal(title, matches[0].Title, $"title of [{id}]");
            Check.True(matches[0].Body.Trim().Length > 0, $"section [{id}] has text");
            positions.Add(sections.IndexOf(matches[0]));
        }
        Check.Equal(positions.OrderBy(p => p).ToList(), positions, "required sections are in order");
    }

    [UnitTest]
    public static void The_notices_carry_the_licence_texts_that_matter()
    {
        string text = ThirdPartyNotices.Load();
        Check.Contains(text, "Permission is hereby granted");
        Check.Contains(text, "Ookii");
        Check.Contains(text, "Json.NET");
        Check.Contains(text, "zlib");
        var first = ThirdPartyNotices.ParseSections(text).FirstOrDefault();
        Check.Equal("0", first?.Id, "Clink's own licence comes first");
        Check.Contains(first?.Body, "MIT License");
    }

    [UnitTest]
    public static void ParseSections_reads_headers_and_bodies_in_CRLF_or_LF()
    {
        string rule = ThirdPartyNotices.RuleLine;
        Check.Equal(80, rule.Length, "the rule is 80 characters");
        Check.Equal(0, ThirdPartyNotices.ParseSections($"{rule}\n[a] T\nIncluded in: x\nSource: y\n{rule}=\n\nbody\n").Count, "an 81-character rule is not a header");
        Check.Equal(0, ThirdPartyNotices.ParseSections($"{rule[..79]}\n[a] T\nIncluded in: x\nSource: y\n{rule}\n\nbody\n").Count, "a 79-character rule is not a header");
        string lf = $"preface\n\n{rule}\n[a] First one\nIncluded in: x\nSource: y\n{rule}\n\nbody a\nline 2\n\n{rule}\n[b] Second\nIncluded in: x\nSource: z\n{rule}\n\nbody b\n\n";
        foreach (string text in new[] { lf, lf.Replace("\n", "\r\n") })
        {
            var sections = ThirdPartyNotices.ParseSections(text);
            Check.Equal(2, sections.Count);
            Check.Equal("a", sections[0].Id);
            Check.Equal("First one", sections[0].Title);
            Check.Equal("x", sections[0].IncludedIn);
            Check.Equal("y", sections[0].Source);
            Check.Equal("body a\nline 2", sections[0].Body);
            Check.Equal("body b", sections[1].Body);
        }
        Check.Equal(0, ThirdPartyNotices.ParseSections("").Count);
        Check.Equal(0, ThirdPartyNotices.ParseSections("no header here\n====\n[x] y").Count);
    }

    [UnitTest]
    public static void The_runtime_version_is_read_from_the_preface()
    {
        Check.Equal(new Version(10, 0, 13), ThirdPartyNotices.RuntimeVersionInNotices("a\n" + ThirdPartyNotices.RuntimeLinePrefix + "10.0.13\r\nb"));
        Check.Null(ThirdPartyNotices.RuntimeVersionInNotices("nothing"));
        Check.Null(ThirdPartyNotices.RuntimeVersionInNotices(ThirdPartyNotices.RuntimeLinePrefix + "abc"));
        Check.NotNull(ThirdPartyNotices.RuntimeVersionInNotices(), "the embedded notices name a runtime");
    }

    // The redistributed DLLs' versions are written into the titles of sections [7] and [8]; a package bump that leaves the notices behind fails here.
    // Only the assembly attributes are read (no Location), so this works from a single-file exe too.
    [UnitTest]
    public static void The_redistributed_assemblies_have_the_versions_the_notice_titles_name()
    {
        var sections = ThirdPartyNotices.ParseSections(ThirdPartyNotices.Load());
        var assemblies = new Dictionary<string, System.Reflection.Assembly>
        {
            ["WinRT.Runtime.dll"] = typeof(WinRT.IObjectReference).Assembly,
            ["Microsoft.Windows.SDK.NET.dll"] = typeof(Windows.Graphics.Capture.GraphicsCaptureSession).Assembly,
        };
        foreach (var (dll, assembly) in assemblies)
        {
            var title = sections.Where(s => s.Id is "7" or "8").Select(s => s.Title).Single(t => t.Contains(dll, StringComparison.Ordinal));
            var m = System.Text.RegularExpressions.Regex.Match(title, System.Text.RegularExpressions.Regex.Escape(dll) + @" (\d+(\.\d+){3})");
            Check.True(m.Success, $"the title names a four-part version for {dll}: {title}");
            var fileVersion = (System.Reflection.AssemblyFileVersionAttribute?)Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyFileVersionAttribute));
            Check.NotNull(fileVersion, $"{dll} carries an AssemblyFileVersion");
            Check.Equal(m.Groups[1].Value, fileVersion!.Version,
                $"{dll} is {fileVersion.Version} but the notice title says {m.Groups[1].Value}: update sections [7]/[8] in tools\\notices\\sources.json, then make-notices.ps1 -Generate");
        }
    }

    // One window at a time. The test must not put a window on screen, so it passes a no-op "show" through the seam of ShowFor.
    [UnitTest]
    public static void ShowFor_keeps_a_single_instance_and_clears_it_on_Close()
    {
        Check.Null(ThirdPartyNoticesWindow.Open, "none is open to start with");
        int shows = 0;
        ThirdPartyNoticesWindow? first = null;
        try
        {
            first = ThirdPartyNoticesWindow.ShowFor(null, _ => shows++);
            var second = ThirdPartyNoticesWindow.ShowFor(null, _ => shows++);
            Check.True(ReferenceEquals(first, second), "the second call returns the open window");
            Check.True(ReferenceEquals(first, ThirdPartyNoticesWindow.Open), "Open is that window");
            Check.Equal(1, shows, "only one window was shown");
            first.Close();
            Check.Null(ThirdPartyNoticesWindow.Open, "Close clears the reference");
            var third = ThirdPartyNoticesWindow.ShowFor(null, _ => shows++);
            Check.False(ReferenceEquals(first, third), "a new window after Close");
            third.Close();
            Check.Null(ThirdPartyNoticesWindow.Open);
            Check.Throws<InvalidOperationException>(() => ThirdPartyNoticesWindow.ShowFor(null, _ => throw new InvalidOperationException("show failed")));
            Check.Null(ThirdPartyNoticesWindow.Open, "a failed Show leaves nothing registered");
        }
        finally { ThirdPartyNoticesWindow.Open?.Close(); }
    }

    // A self-contained exe carries the runtime it was published with; the notices must be those of that runtime. On a framework-dependent
    // exe or a bin\ run there is no bundled runtime to compare, so that is a pass (not a skip: a skip fails under CLINK_TESTS_NO_SKIP).
    [UnitTest]
    public static void A_self_contained_exe_carries_the_notices_of_its_own_runtime()
    {
        if (PackagingInfo.Current.Packaging != Packaging.SelfContained) return;
        var pinned = ThirdPartyNotices.RuntimeVersionInNotices();
        Check.NotNull(pinned, "the notices name a runtime version");
        var running = Environment.Version;
        Check.Equal(pinned!.Major, running.Major, "runtime major");
        Check.Equal(pinned.Minor, running.Minor, "runtime minor");
        Check.Equal(pinned.Build, running.Build,
            $"the bundled runtime is {running.Major}.{running.Minor}.{running.Build} but the notices are for {pinned}: run tools\\notices\\make-notices.ps1 -Update -RuntimeVersion {running.Major}.{running.Minor}.{running.Build} -Accept, then -Generate");
    }

    [UnitTest]
    public static void The_notices_window_shows_the_text_read_only_and_closes_on_Esc()
    {
        var window = new ThirdPartyNoticesWindow();
        Check.Equal(ThirdPartyNotices.Load(), window.NoticesText.Text, "the box holds the notices");
        Check.True(window.NoticesText.IsReadOnly, "read-only");
        Check.True(window.NoticesText.IsReadOnlyCaretVisible, "a caret, so the keyboard can move through it");
        Check.Equal("Third-party notices", AutomationProperties.GetName(window.NoticesText), "automation name of the box");
        Check.Equal("ThirdPartyNoticesWindow", AutomationProperties.GetAutomationId(window), "automation id of the window");
        Check.True(window.CloseButton.IsCancel, "Esc closes");
        Check.False(window.ShowInTaskbar, "no taskbar button");
        Check.Equal(System.Windows.TextWrapping.NoWrap, window.NoticesText.TextWrapping);
    }

    // The App tab link: read from the XAML sources (when the tests run from a checkout's build folder), like AppTabTests.
    [UnitTest]
    public static void The_App_tab_has_a_third_party_notices_link_that_opens_the_window()
    {
        string? root = FindSourceFolder();
        if (root == null) return;   // not run from a checkout: nothing to read
        string xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        Check.Contains(xaml, "x:Name=\"ThirdPartyNoticesButton\"");
        Check.Contains(xaml, "Click=\"ThirdPartyNotices_Click\"");
        Check.Contains(xaml, "Content=\"Third-party notices\"");
        string code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        Check.Contains(code, "void ThirdPartyNotices_Click(");
        Check.Contains(code, "ThirdPartyNoticesWindow.ShowFor(this)");
        string csproj = File.ReadAllText(Path.Combine(root, "Clink.csproj"));
        Check.Contains(csproj, "<EmbeddedResource Include=\"..\\THIRD-PARTY-NOTICES.txt\" LogicalName=\"THIRD-PARTY-NOTICES.txt\" />");
        // The file at the repository root is the embedded one (line ends aside).
        string? rootFile = Path.Combine(Path.GetDirectoryName(root)!, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(rootFile))
            Check.True(File.ReadAllText(rootFile).Replace("\r\n", "\n") == ThirdPartyNotices.Load().Replace("\r\n", "\n"), "the embedded notices are the file at the repository root (rebuild after regenerating)");
    }

    /// <summary>The folder of Clink.csproj above the running exe (a build folder of a checkout), or null.</summary>
    private static string? FindSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Clink.csproj"))) return dir.FullName;
        return null;
    }
}
