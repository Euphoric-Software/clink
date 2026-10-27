using Clink.Services;

namespace Clink.Tests;

/// <summary>The classifier of where Clink.exe sits (first-run setup). Every folder is injected; nothing reads the real machine.</summary>
internal static class InstallLocationTests
{
    private sealed class FakeRoots : ICloudRootReader
    {
        private readonly string[] _roots;
        public FakeRoots(params string[] roots) => _roots = roots;
        public IReadOnlyList<string> ReadRoots() => _roots;
    }

    private static InstallEnvironment Env(string[]? cloudEnv = null, string[]? registry = null, Func<string, uint>? drive = null, Func<string, int>? attrs = null,
        string? documents = null) => new()
    {
        Downloads = @"C:\Users\u\Downloads",
        Desktop = @"C:\Users\u\Desktop",
        Documents = documents ?? @"C:\Users\u\Documents",
        ProgramsFolder = @"C:\Users\u\AppData\Local\Programs",
        ProgramFiles = new[] { @"C:\Program Files", @"C:\Program Files (x86)" },
        Temp = new[] { @"C:\Users\u\AppData\Local\Temp", @"C:\Windows\Temp" },
        CloudEnvRoots = cloudEnv ?? Array.Empty<string>(),
        CloudReader = new FakeRoots(registry ?? Array.Empty<string>()),
        DriveTypeOf = drive ?? (_ => 3),
        AttributesOf = attrs ?? (_ => 0),
    };

    private static LocationClass Of(string path, InstallEnvironment? env = null) => InstallLocation.Classify(path, env ?? Env());

    [UnitTest]
    public static void Downloads_desktop_documents_and_other_are_told_apart()
    {
        Check.Equal(LocationClass.Downloads, Of(@"C:\Users\u\Downloads\Clink.exe"));
        Check.Equal(LocationClass.Downloads, Of(@"C:\Users\u\Downloads\sub\Clink.exe"));
        Check.Equal(LocationClass.Desktop, Of(@"C:\Users\u\Desktop\Clink.exe"));
        Check.Equal(LocationClass.Documents, Of(@"C:\Users\u\Documents\Clink\Clink.exe"));
        Check.Equal(LocationClass.Other, Of(@"D:\Tools\Clink.exe"));
        Check.Equal(LocationClass.Other, Of(@"C:\Users\u\DownloadsX\Clink.exe"), "a sibling that starts with the same letters");
    }

    [UnitTest]
    public static void A_browser_duplicate_name_is_still_a_downloads_exe()
    {
        Check.Equal(LocationClass.Downloads, Of(@"C:\Users\u\Downloads\Clink (1).exe"));
        Check.True(InstallLocation.IsBrowserDuplicateName(@"C:\Users\u\Downloads\Clink (1).exe"));
        Check.True(InstallLocation.IsBrowserDuplicateName(@"C:\x\Clink (12).exe"));
        Check.True(InstallLocation.IsBrowserDuplicateName(@"C:\x\Clink(1).exe"));
        Check.False(InstallLocation.IsBrowserDuplicateName(@"C:\x\Clink.exe"));
        Check.False(InstallLocation.IsBrowserDuplicateName(@"C:\x\Clink(1)x.exe"));
        Check.False(InstallLocation.IsBrowserDuplicateName(null));
    }

    [UnitTest]
    public static void Paths_are_compared_without_case_trailing_separators_or_the_long_path_prefix()
    {
        Check.Equal(LocationClass.Downloads, Of(@"c:\USERS\U\downloads\clink.EXE"));
        Check.Equal(LocationClass.Downloads, Of(@"\\?\C:\Users\u\Downloads\Clink.exe"));
        Check.Equal(LocationClass.Downloads, Of("C:/Users/u/Downloads/Clink.exe"));
        Check.Equal(LocationClass.Downloads, Of(@"C:\Users\u\Other\..\Downloads\Clink.exe"));
        Check.Equal(@"C:\Users\u", InstallLocation.Normalize(@"C:\Users\u\"));
        Check.Equal(@"C:\", InstallLocation.Normalize(@"C:\"));
        Check.Equal(@"C:\", InstallLocation.Normalize("C:"));
        Check.Equal(@"\\srv\share\a", InstallLocation.Normalize(@"\\?\UNC\srv\share\a\"));
        Check.Equal("", InstallLocation.Normalize("  "));
        // a folder given with a trailing separator still matches
        var env = Env() with { Downloads = @"C:\Users\u\Downloads\" };
        Check.Equal(LocationClass.Downloads, Of(@"C:\Users\u\Downloads\Clink.exe", env));
    }

    [UnitTest]
    public static void Temp_and_zip_extraction_folders_are_temp()
    {
        Check.Equal(LocationClass.Temp, Of(@"C:\Users\u\AppData\Local\Temp\Clink.exe"));
        Check.Equal(LocationClass.Temp, Of(@"C:\Users\u\AppData\Local\Temp\Temp1_Clink.zip\Clink.exe"));
        Check.Equal(LocationClass.Temp, Of(@"C:\Windows\Temp\x\Clink.exe"));
        // a zip tool that extracts next to the zip or elsewhere: the folder names give it away
        Check.Equal(LocationClass.Temp, Of(@"D:\Stuff\Temp3_Clink.zip\Clink.exe"));
        Check.Equal(LocationClass.Temp, Of(@"D:\Stuff\Clink.zip\Clink.exe"));
        Check.Equal(LocationClass.Temp, Of(@"D:\Stuff\7zO12AB34CD\Clink.exe"));
        Check.Equal(LocationClass.Temp, Of(@"D:\Stuff\Rar$EXa1234.567\Clink.exe"));
        Check.Equal(LocationClass.Other, Of(@"D:\Stuff\Temperature\Clink.exe"));
    }

    [UnitTest]
    public static void Program_files_and_the_programs_folder_are_told_apart()
    {
        Check.Equal(LocationClass.ProgramFiles, Of(@"C:\Program Files\Clink\Clink.exe"));
        Check.Equal(LocationClass.ProgramFiles, Of(@"C:\Program Files (x86)\Clink\Clink.exe"));
        Check.Equal(LocationClass.ProgramsFolder, Of(@"C:\Users\u\AppData\Local\Programs\Clink\Clink.exe"));
        Check.Equal(LocationClass.Other, Of(@"C:\Users\u\AppData\Local\Clink\Clink.exe"));
    }

    [UnitTest]
    public static void A_unc_path_or_a_mapped_network_drive_is_network()
    {
        Check.Equal(LocationClass.Network, Of(@"\\nas\share\Clink.exe"));
        Check.Equal(LocationClass.Network, Of(@"\\?\UNC\nas\share\Clink.exe"));
        var mapped = Env(drive: root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? 4u : 3u);
        Check.Equal(LocationClass.Network, Of(@"Z:\tools\Clink.exe", mapped));
        Check.Equal(LocationClass.Other, Of(@"D:\tools\Clink.exe", mapped));
        // network beats everything: a mapped drive under a "Downloads" folder name is still network
        Check.Equal(LocationClass.Network, Of(@"Z:\Users\u\Downloads\Clink.exe", mapped));
    }

    [UnitTest]
    public static void A_removable_or_optical_drive_is_removable_and_beats_downloads()
    {
        var usb = Env(drive: root => root.StartsWith("E", StringComparison.OrdinalIgnoreCase) ? 2u : root.StartsWith("F", StringComparison.OrdinalIgnoreCase) ? 5u : 3u);
        Check.Equal(LocationClass.Removable, Of(@"E:\Clink.exe", usb));
        Check.Equal(LocationClass.Removable, Of(@"F:\Clink.exe", usb));
        var all = Env(drive: _ => 2u) with { Downloads = @"E:\Downloads" };
        Check.Equal(LocationClass.Removable, Of(@"E:\Downloads\Clink.exe", all));
    }

    [UnitTest]
    public static void Precedence_is_network_removable_temp_program_files_programs_cloud_downloads()
    {
        // temp beats program files / cloud: a sync root that contains Temp
        var env = Env(cloudEnv: new[] { @"C:\Users\u" });
        Check.Equal(LocationClass.Temp, Of(@"C:\Users\u\AppData\Local\Temp\Clink.exe", env));
        // programs folder (installed) beats cloud
        Check.Equal(LocationClass.ProgramsFolder, Of(@"C:\Users\u\AppData\Local\Programs\Clink\Clink.exe", env));
        // cloud beats downloads / desktop / documents
        Check.Equal(LocationClass.Cloud, Of(@"C:\Users\u\Downloads\Clink.exe", env));
        Check.Equal(LocationClass.Cloud, Of(@"C:\Users\u\Desktop\Clink.exe", env));
        Check.Equal(LocationClass.Cloud, Of(@"C:\Users\u\Documents\Clink.exe", env));
        // a program-files path in a (silly) sync root stays program files
        var pf = Env(cloudEnv: new[] { @"C:\" });   // a drive root is ignored as a sync root
        Check.Equal(LocationClass.ProgramFiles, Of(@"C:\Program Files\Clink\Clink.exe", pf));
    }

    [UnitTest]
    public static void A_onedrive_documents_folder_is_cloud_and_documents_is_reported_synced()
    {
        string od = @"C:\Users\u\OneDrive";
        var env = Env(cloudEnv: new[] { od }, documents: od + @"\Documents");
        Check.Equal(LocationClass.Cloud, Of(od + @"\Documents\Clink\Clink.exe", env));
        Check.True(InstallLocation.IsDocumentsCloudSynced(env));
        Check.False(InstallLocation.IsDocumentsCloudSynced(Env()), "plain local Documents");
        Check.False(InstallLocation.IsDocumentsCloudSynced(Env() with { Documents = null }));
    }

    [UnitTest]
    public static void Every_onedrive_variable_and_registry_source_is_a_cloud_root()
    {
        var env = Env(cloudEnv: new[] { @"C:\Users\u\OneDrive", @"C:\Users\u\OneDrive - Contoso" },
            registry: new[] { @"D:\Dropbox", @"C:\Users\u\OneDrive Personal\" });
        Check.Equal(LocationClass.Cloud, Of(@"C:\Users\u\OneDrive\x\Clink.exe", env));
        Check.Equal(LocationClass.Cloud, Of(@"C:\Users\u\OneDrive - Contoso\Clink.exe", env));
        Check.Equal(LocationClass.Cloud, Of(@"D:\Dropbox\Clink.exe", env), "a Dropbox root (sync root manager)");
        Check.Equal(LocationClass.Cloud, Of(@"C:\Users\u\OneDrive Personal\Clink.exe", env), "a registered root with a trailing separator");
        Check.Equal(LocationClass.Other, Of(@"D:\DropboxOther\Clink.exe", env));
        Check.Equal(4, InstallLocation.CloudRoots(env).Count, "four roots, normalised");
    }

    [UnitTest]
    public static void Cloud_file_attributes_make_a_file_or_its_folder_cloud()
    {
        foreach (int bit in new[] { 0x400000, 0x80000, 0x100000 })
        {
            var env = Env(attrs: p => p.EndsWith("Clink.exe", StringComparison.OrdinalIgnoreCase) ? bit | 0x20 : 0x20);
            Check.Equal(LocationClass.Cloud, Of(@"D:\Stuff\Clink.exe", env), "file bit 0x" + bit.ToString("X"));
        }
        var dir = Env(attrs: p => p.EndsWith("Stuff", StringComparison.OrdinalIgnoreCase) ? 0x80000 : 0);
        Check.Equal(LocationClass.Cloud, Of(@"D:\Stuff\Clink.exe", dir), "the folder is pinned");
        Check.Equal(LocationClass.Other, Of(@"D:\Stuff\Clink.exe", Env(attrs: _ => 0x20 | 0x1)));
        Check.Equal(LocationClass.Other, Of(@"D:\Stuff\Clink.exe", Env(attrs: _ => 0x40000)), "RECALL_ON_OPEN alone is not a cloud file");
    }

    [UnitTest]
    public static void A_broken_reader_or_probe_does_not_throw()
    {
        var env = Env() with { CloudReader = new ThrowingReader(), AttributesOf = _ => throw new InvalidOperationException("x"), DriveTypeOf = _ => throw new InvalidOperationException("y") };
        Check.Equal(LocationClass.Downloads, Of(@"C:\Users\u\Downloads\Clink.exe", env));
        Check.Equal(LocationClass.Other, Of(null!, env));
        Check.Equal(LocationClass.Other, Of("", env));
    }

    private sealed class ThrowingReader : ICloudRootReader
    {
        public IReadOnlyList<string> ReadRoots() => throw new InvalidOperationException("registry");
    }

    [UnitTest]
    public static void The_system_environment_and_registry_reader_run_without_throwing()
    {
        // read-only: the real registry and folders are only read, never written
        var env = InstallEnvironment.FromSystem();
        Check.NotNull(env.Documents);
        _ = InstallLocation.CloudRoots(env);
        _ = InstallLocation.Classify(Environment.ProcessPath, env);
        Check.NotNull(new RegistryCloudRootReader().ReadRoots());
    }

    private sealed class CountingReader : ICloudRootReader
    {
        public int Reads;
        public IReadOnlyList<string> ReadRoots() { Reads++; return new[] { @"D:\Cloud" }; }
    }

    [UnitTest]
    public static void The_cloud_roots_are_read_once_per_environment()
    {
        var reader = new CountingReader();
        var env = Env() with { CloudReader = reader };
        for (int i = 0; i < 5; i++) Of(@"D:\Cloud\x\Clink.exe", env);
        _ = InstallLocation.CloudRoots(env);
        Check.Equal(1, reader.Reads);
        Check.Equal(LocationClass.Cloud, Of(@"D:\Cloud\x\Clink.exe", env));
    }

    [UnitTest]
    public static void Only_the_current_users_sync_roots_count()
    {
        const string me = "S-1-5-21-1-2-3-1001", other = "S-1-5-21-1-2-3-1002";
        var values = new[] { (me, @"C:\Users\me\Sync"), (other, @"C:\Users\other\Sync"), (me.ToLowerInvariant(), @"D:\Mine"), ("NotASid", @"E:\x"), (me, "") };
        Check.Equal(new[] { @"C:\Users\me\Sync", @"D:\Mine" }, RegistryCloudRootReader.SelectUserSyncRoots(values, me).ToArray());
        Check.Equal(0, RegistryCloudRootReader.SelectUserSyncRoots(values, null).Count());
        Check.Equal(0, RegistryCloudRootReader.SelectUserSyncRoots(values, "").Count());
    }

    [UnitTest]
    public static void Dropbox_info_and_the_google_drive_mount_point_are_understood()
    {
        string json = "{\"personal\":{\"path\":\"C:\\\\Users\\\\u\\\\Dropbox\",\"host\":1},\"business\":{\"path\":\"D:\\\\Dropbox (Work)\"},\"x\":5,\"y\":{\"nopath\":1}}";
        Check.Equal(new[] { @"C:\Users\u\Dropbox", @"D:\Dropbox (Work)" }, RegistryCloudRootReader.ParseDropboxInfo(json).ToArray());
        Check.Equal(0, RegistryCloudRootReader.ParseDropboxInfo("not json").Count);
        Check.Equal(0, RegistryCloudRootReader.ParseDropboxInfo("[1]").Count);
        Check.Equal(0, RegistryCloudRootReader.ParseDropboxInfo(null).Count);

        Check.Equal(@"G:\", RegistryCloudRootReader.GoogleDriveMountRoot("g"));
        Check.Equal(@"G:\", RegistryCloudRootReader.GoogleDriveMountRoot("G:"));
        Check.Equal(@"G:\", RegistryCloudRootReader.GoogleDriveMountRoot(@" G:\ "));
        Check.Null(RegistryCloudRootReader.GoogleDriveMountRoot(""));
        Check.Null(RegistryCloudRootReader.GoogleDriveMountRoot(@"\\srv\share"));
        Check.Null(RegistryCloudRootReader.GoogleDriveMountRoot("GG"));

        // a Google Drive virtual drive root is ignored as a sync root (a drive root is too broad), but its folders count
        Check.Equal(new[] { @"G:\My Drive", @"G:\Shared drives", @"G:\Computers" }, RegistryCloudRootReader.GoogleDriveFolders(@"G:\").ToArray());
        var env = Env(registry: RegistryCloudRootReader.GoogleDriveFolders(@"G:\").Append(@"C:\Users\u\My Drive").ToArray());
        Check.Equal(LocationClass.Cloud, Of(@"G:\My Drive\Clink.exe", env));
        Check.Equal(LocationClass.Cloud, Of(@"C:\Users\u\My Drive\Clink.exe", env));
    }
}
