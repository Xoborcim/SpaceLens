using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.Mac.Tests;

public class PropertyListTests
{
    /// <summary>Written by Python's plistlib in Apple's binary format (bplist00).</summary>
    private const string BinaryInfoPlist = "YnBsaXN0MDDdAQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRpUQmxvYl8QE0NGQnVuZGxlRGlzcGxheU5hbWVfEBJDRkJ1bmRsZUlkZW50aWZpZXJcQ0ZCdW5kbGVOYW1lXxAaQ0ZCdW5kbGVTaG9ydFZlcnNpb25TdHJpbmdVQ291bnRXQ3JlYXRlZF8QFkxTTWluaW11bVN5c3RlbVZlcnNpb25UTG9uZ18QF05TSGlnaFJlc29sdXRpb25DYXBhYmxlWE5lZ2F0aXZlVVJhdGlvVVR5cGVzQwABAm8QEQBFAHgAYQBtAHAAbABlACAARQBkAGkAdAD2AHIAICcN/g9fEBJjb20uZXhhbXBsZS5FZGl0b3JWRWRpdG9yVTIuNC4xEgABEXAzQcX0XKyAAABUMTMuMF8RASx4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHgJE//////////7Iz/4AAAAAAAAoxscHVtwdWJsaWMudGV4dFtwdWJsaWMuZGF0YdEeH1ZOZXN0ZWRTeWVzAAgAIwAoAD4AUwBgAH0AgwCLAKQAqQDDAMwA0gDYANwBAQEWAR0BIwEoATEBNgJmAmcCcAJ5An0CiQKVApgCnwAAAAAAAAIBAAAAAAAAACAAAAAAAAAAAAAAAAAAAAKj";

    [Fact]
    public void Reads_binary_property_lists()
    {
        var info = Assert.IsType<Dictionary<string, object?>>(PropertyList.Read(Convert.FromBase64String(BinaryInfoPlist)));

        Assert.Equal("com.example.Editor", info.GetString("CFBundleIdentifier"));
        Assert.Equal("Example Editör ✍️", info.GetString("CFBundleDisplayName")); // UTF-16 string
        Assert.Equal("2.4.1", info.GetString("CFBundleShortVersionString"));
        Assert.Equal(true, info["NSHighResolutionCapable"]);
        Assert.Equal(70000L, info["Count"]);
        Assert.Equal(-5L, info["Negative"]);
        Assert.Equal(1.5, info["Ratio"]);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), info["Created"]);
        Assert.Equal(new byte[] { 0, 1, 2 }, info["Blob"]);
        var types = Assert.IsType<List<object?>>(info["Types"]);
        Assert.Equal("public.data", types[1]);
        Assert.Equal("yes", Assert.IsType<Dictionary<string, object?>>(types[2])["Nested"]);
        Assert.Equal(300, Assert.IsType<string>(info["Long"]).Length); // length stored as a following integer
    }

    [Fact]
    public void Reads_xml_property_lists()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleName</key><string>Tool</string>
                <key>CFBundleVersion</key><string>17</string>
                <key>LSUIElement</key><true/>
                <key>Size</key><integer>42</integer>
                <key>List</key><array><string>a</string><real>2.5</real></array>
                <key>Data</key><data>AAEC</data>
            </dict>
            </plist>
            """;
        var info = Assert.IsType<Dictionary<string, object?>>(PropertyList.Read(System.Text.Encoding.UTF8.GetBytes(xml)));
        Assert.Equal("Tool", info.GetString("CFBundleName"));
        Assert.Equal(true, info["LSUIElement"]);
        Assert.Equal(42L, info["Size"]);
        Assert.Equal(2.5, Assert.IsType<List<object?>>(info["List"])[1]);
        Assert.Equal(new byte[] { 0, 1, 2 }, info["Data"]);
    }

    [Fact]
    public void Corrupt_binary_lists_fail_cleanly()
    {
        var data = Convert.FromBase64String(BinaryInfoPlist);
        var truncated = data[..(data.Length - 10)];
        Assert.ThrowsAny<Exception>(() => PropertyList.Read(truncated));

        var badOffsets = (byte[])data.Clone();
        badOffsets[^8] = 0x7F; // offset table pointing past the end
        Assert.Throws<InvalidDataException>(() => PropertyList.Read(badOffsets));

        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".plist");
        File.WriteAllBytes(file, truncated);
        try
        {
            Assert.Null(PropertyList.ReadDictionary(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    internal static byte[] BinaryPlist => Convert.FromBase64String(BinaryInfoPlist);
}

public class MacAppsTests
{
    [Fact]
    public void Reads_app_bundles_and_finds_their_leftovers()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpaceLensMac", Guid.NewGuid().ToString("N"));
        try
        {
            string bundle = Path.Combine(root, "Applications", "Editor.app");
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "_MASReceipt"));
            File.WriteAllBytes(Path.Combine(bundle, "Contents", "Info.plist"), PropertyListTests.BinaryPlist);
            File.WriteAllText(Path.Combine(bundle, "Contents", "_MASReceipt", "receipt"), "r");

            string home = Path.Combine(root, "home");
            Directory.CreateDirectory(Path.Combine(home, "Library", "Application Support", "com.example.Editor"));
            Directory.CreateDirectory(Path.Combine(home, "Library", "Caches", "com.example.Editor"));
            Directory.CreateDirectory(Path.Combine(home, "Library", "Preferences"));
            File.WriteAllText(Path.Combine(home, "Library", "Preferences", "com.example.Editor.plist"), "p");

            var app = Assert.IsType<MacApp>(MacApps.ReadBundle(bundle));
            Assert.Equal("Example Editör ✍️", app.Name);
            Assert.Equal("2.4.1", app.Version);
            Assert.Equal("com.example.Editor", app.BundleId);
            Assert.True(app.IsAppStore);
            Assert.False(app.IsSystem);

            var leftovers = MacApps.FindLeftovers(app, home);
            Assert.Equal(3, leftovers.Count);
            Assert.Contains(leftovers, l => l.IsFile && l.Kind == "Preferences");
            Assert.Contains(leftovers, l => l.Kind == "Caches");

            Assert.Null(MacApps.ReadBundle(Path.Combine(root, "Applications", "NotAnApp.app")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/Applications/Safari.app", "com.apple.Safari", true)]
    [InlineData("/System/Applications/Mail.app", "com.apple.mail", true)]
    [InlineData("/Applications/Xcode.app", "com.apple.dt.Xcode", true)]
    [InlineData("/Applications/Firefox.app", "org.mozilla.firefox", false)]
    [InlineData("/Users/me/Applications/Tool.app", "com.apple.tool", false)]
    public void Apple_apps_in_system_places_are_part_of_macOS(string path, string bundleId, bool expected) =>
        Assert.Equal(expected, MacApps.IsSystemApp(path, bundleId));

    [Fact]
    public void Leftover_candidates_use_the_bundle_id_and_a_safe_name()
    {
        var candidates = MacApps.LeftoverCandidates("org.mozilla.firefox", "Firefox", "/Users/me");
        Assert.Contains(candidates, c => c.Path == "/Users/me/Library/Containers/org.mozilla.firefox");
        Assert.Contains(candidates, c => c.Path == "/Users/me/Library/Preferences/org.mozilla.firefox.plist" && c.IsFile);
        Assert.Contains(candidates, c => c.Path == "/Users/me/Library/Application Support/Firefox");
        Assert.DoesNotContain(MacApps.LeftoverCandidates(null, "../../etc", "/Users/me"), c => c.Path.Contains("..", StringComparison.Ordinal) && c.Path.Contains("etc", StringComparison.Ordinal));
    }
}

public class MacVolumesTests
{
    [Fact]
    public void Lists_the_startup_disk_and_external_volumes_only()
    {
        var volumes = MacVolumes.Select(
        [
            ("/", "apfs", 500L << 30, 120L << 30, DriveType.Fixed),
            ("/System/Volumes/Data", "apfs", 500L << 30, 120L << 30, DriveType.Fixed),
            ("/System/Volumes/VM", "apfs", 500L << 30, 120L << 30, DriveType.Fixed),
            ("/dev", "devfs", 1, 0, DriveType.Ram),
            ("/Volumes/Backup", "apfs", 2L << 40, 1L << 40, DriveType.Fixed),
            ("/Volumes/USB STICK", "msdos", 32L << 30, 30L << 30, DriveType.Removable),
            ("/private/var/folders/xy/T/AppTranslocation/X", "nullfs", 1, 0, DriveType.Fixed),
        ]);

        Assert.Equal(["/", "/Volumes/Backup", "/Volumes/USB STICK"], volumes.Select(v => v.RootPath));
        Assert.True(volumes[0].IsStartup);
        Assert.Equal("Macintosh HD", volumes[0].Name);
        Assert.Equal(380L << 30, volumes[0].UsedBytes);
        Assert.True(volumes[2].IsRemovable);
    }

    [Fact]
    public void A_startup_disk_scan_skips_firmlinked_and_other_volumes()
    {
        Assert.Contains("/System/Volumes", MacVolumes.ExcludedPathsFor("/"));
        Assert.Contains("/Volumes", MacVolumes.ExcludedPathsFor("/"));
        Assert.Empty(MacVolumes.ExcludedPathsFor("/Volumes/Backup"));
        Assert.Empty(MacVolumes.ExcludedPathsFor("/Users/me"));
    }
}

public class MacDetectionTests
{
    private static readonly MacKnownLocations Known = new() { Home = "/Users/me" };

    /// <summary>Adds a folder with the given own size, creating parents.</summary>
    private static int Folder(ScanTree tree, string path, long ownSize = 0)
    {
        int dir = ScanTree.RootIndex;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            int child = tree.FindChild(dir, segment);
            dir = child >= 0 ? child : tree.AddDirectory(dir, segment);
        }

        if (ownSize > 0)
        {
            tree.CompleteDirectory(dir, ownSize, 1, 0);
        }

        return dir;
    }

    [Fact]
    public void Finds_xcode_backups_libraries_projects_and_virtual_disks()
    {
        var tree = new ScanTree("/");
        int derived = Folder(tree, "/Users/me/Library/Developer/Xcode/DerivedData", 30L << 30);
        Folder(tree, "/Users/me/Library/Application Support/MobileSync/Backup", 60L << 30);
        Folder(tree, "/Users/me/Library/Caches/Homebrew", 2L << 30);
        Folder(tree, "/Users/me/Library/Caches/pip", 1L << 20); // below the 10 MB minimum
        int photos = Folder(tree, "/Users/me/Pictures/Photos Library.photoslibrary", 80L << 30);
        Folder(tree, "/Users/me/Pictures/Photos Library.photoslibrary/resources/node_modules", 5L << 20); // inside a library
        int web = Folder(tree, "/Users/me/Projects/web");
        int modules = Folder(tree, "/Users/me/Projects/web/node_modules", 300L << 20);
        Folder(tree, "/Applications/Electron.app/Contents/Resources/app/node_modules", 200L << 20);
        Folder(tree, "/Users/me/Applications/Tool.app/Contents/node_modules", 200L << 20);
        int docker = Folder(tree, "/Users/me/Library/Containers/com.docker.docker/Data/vms/0/data");
        tree.AddFile(docker, "Docker.raw", 64L << 30, FileCategory.VirtualMachine, 0);

        var markers = new HashSet<string> { "/Users/me/Projects/web/package.json", "/Users/me/Applications/Tool.app/Contents/package.json" };
        var findings = MacStorageDetector.Detect(tree, Known, markers.Contains);

        Assert.Contains(findings, f => f.DirectoryIndex == derived && f.Group == "Xcode" && f.AllowDirectRemoval);
        Assert.Contains(findings, f => f.Title == "iPhone and iPad backups" && !f.AllowDirectRemoval);
        Assert.Contains(findings, f => f.Title == "Homebrew downloads");
        Assert.DoesNotContain(findings, f => f.Title == "pip cache");
        Assert.Contains(findings, f => f.DirectoryIndex == photos && f.Category == LocationCategory.Pictures && !f.AllowDirectRemoval);
        var artifact = Assert.Single(findings, f => f.Group == "node_modules");
        Assert.Equal(modules, artifact.DirectoryIndex);
        Assert.Contains(findings, f => f.Title == "Docker Desktop disk" && f.FileIndex >= 0 && !f.AllowDirectRemoval);
        _ = web;
    }

    [Fact]
    public void Location_map_and_labels_use_mac_folders()
    {
        var tree = new ScanTree("/");
        int downloads = Folder(tree, "/Users/me/Downloads", 1);
        int caches = Folder(tree, "/Users/me/Library/Caches", 1);
        int system = Folder(tree, "/System/Library", 1);
        int trash = Folder(tree, "/Users/me/.Trash", 1);

        var map = MacLocations.BuildBaseMap(tree, Known);
        Assert.Equal(LocationCategory.Downloads, LocationClassifier.NearestCategory(tree, map, downloads));
        Assert.Equal(LocationCategory.TemporaryAndCache, LocationClassifier.NearestCategory(tree, map, caches));
        Assert.Equal(LocationCategory.Windows, LocationClassifier.NearestCategory(tree, map, system));
        Assert.Equal(LocationCategory.RecycleBin, LocationClassifier.NearestCategory(tree, map, trash));
        Assert.Equal("macOS & system", MacLocations.Label(LocationCategory.Windows));
        Assert.Equal("Trash", MacLocations.Label(LocationCategory.RecycleBin));
        Assert.Equal("Downloads", MacLocations.Label(LocationCategory.Downloads));
    }

    [Fact]
    public void Trash_rechecks_the_safety_policy_before_touching_anything()
    {
        var shell = new MacShell(new MacSafetyPolicy(Known));
        var results = shell.MoveToTrash([("/System/Library", true), ("/Users/me/Documents", true)]);
        Assert.All(results, r => Assert.False(r.Result.Success));
        Assert.All(results, r => Assert.Contains("protected", r.Result.Error));

        var system = new MacApp { Name = "Safari", BundlePath = "/Applications/Safari.app", IsSystem = true };
        Assert.Contains("part of macOS", shell.RemoveApplication(system).Error);
    }
}
