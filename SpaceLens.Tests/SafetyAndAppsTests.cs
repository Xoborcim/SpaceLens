using SpaceLens.Core.Classification;
using SpaceLens.Core.InstalledApps;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;
using SpaceLens.Detectors.Developer;
using SpaceLens.Detectors.Steam;
using SpaceLens.Windows.FileSystem;

namespace SpaceLens.Tests;

public class SafetyPolicyTests
{
    internal static readonly KnownLocations Known = new()
    {
        SystemDrive = @"C:\",
        WindowsDirectory = @"C:\Windows",
        ProgramFiles = @"C:\Program Files",
        ProgramFilesX86 = @"C:\Program Files (x86)",
        ProgramData = @"C:\ProgramData",
        UsersDirectory = @"C:\Users",
        UserProfile = @"C:\Users\me",
        LocalAppData = @"C:\Users\me\AppData\Local",
        RoamingAppData = @"C:\Users\me\AppData\Roaming",
        TempDirectory = @"C:\Users\me\AppData\Local\Temp\",
        UserKnownFolders = [@"C:\Users\me\Downloads", @"C:\Users\me\Documents"],
    };

    private readonly SafetyPolicy _policy = new(Known);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\WinSxS")]
    [InlineData(@"c:\windows\winsxs\amd64_something")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\Program Files\Microsoft Visual Studio")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"C:\Users\me\AppData")]
    [InlineData(@"C:\Users\me\Downloads")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"C:\System Volume Information")]
    [InlineData(@"C:\Recovery")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"C:\EFI")]
    [InlineData(@"C:\Windows.old")]
    [InlineData(@"D:\System Volume Information\something")]
    public void Protected_directories_cannot_be_deleted(string path) =>
        Assert.False(_policy.AssessDirectory(path).CanDelete, path);

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\old-stuff")]
    [InlineData(@"C:\Users\me\source\repo\node_modules")]
    [InlineData(@"D:\Games\Backup")]
    public void Ordinary_folders_can_be_deleted(string path)
    {
        var assessment = _policy.AssessDirectory(path);
        Assert.True(assessment.CanDelete);
        Assert.Equal(ProtectionLevel.None, assessment.Level);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\SomeApp\Cache")]
    [InlineData(@"C:\ProgramData\Vendor\Data")]
    public void Application_data_requires_caution(string path)
    {
        var assessment = _policy.AssessDirectory(path);
        Assert.True(assessment.CanDelete);
        Assert.Equal(ProtectionLevel.Caution, assessment.Level);
    }

    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"C:\bootmgr")]
    public void Windows_root_files_are_protected_with_advice(string path)
    {
        var assessment = _policy.AssessFile(path);
        Assert.False(assessment.CanDelete);
        Assert.True(assessment.IsSystemManaged);
    }

    [Fact]
    public void Files_inside_windows_get_a_prominent_warning_but_are_not_silently_allowed()
    {
        var assessment = _policy.AssessFile(@"C:\Windows\WinSxS\Backup\file.dll");
        Assert.Equal(ProtectionLevel.Caution, assessment.Level);
        Assert.True(assessment.IsSystemManaged);
        Assert.Contains("Windows", assessment.Explanation);

        Assert.Equal(ProtectionLevel.Caution, _policy.AssessFile(@"C:\Program Files\App\app.dll").Level);
        Assert.Equal(ProtectionLevel.None, _policy.AssessFile(@"C:\Users\me\Downloads\ubuntu.iso").Level);
        Assert.Equal(ProtectionLevel.None, _policy.AssessFile(@"D:\movie.mkv").Level);
    }

    [Theory]
    [InlineData(@"C:\Users\Public")]
    [InlineData(@"C:\Users\Public\Documents")]
    [InlineData(@"C:\Users\Default")]
    [InlineData(@"C:\Users\Default\AppData\Local")]
    [InlineData(@"C:\Users\bob")]
    [InlineData(@"C:\Users\bob\AppData")]
    [InlineData(@"C:\Users\bob\Documents")]
    public void Other_profiles_are_protected(string path) =>
        Assert.False(_policy.AssessDirectory(path).CanDelete, path);

    [Fact]
    public void Content_of_other_profiles_is_reviewed_individually()
    {
        Assert.Equal(ProtectionLevel.Caution, _policy.AssessDirectory(@"C:\Users\bob\AppData\Local\SomeApp").Level);
        Assert.Equal(ProtectionLevel.None, _policy.AssessDirectory(@"C:\Users\bob\Downloads\old-stuff").Level);
        Assert.Equal(ProtectionLevel.None, _policy.AssessDirectory(@"C:\Users\Public\Documents\Shared project").Level);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\IconCache.db")]
    [InlineData(@"C:\Users\me\AppData\Roaming\settings.dat")]
    [InlineData(@"C:\Users\me\.gitconfig")]
    [InlineData(@"C:\ProgramData\vendor.dat")]
    [InlineData(@"C:\Users\bob\NTUSER.DAT")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\setup.log")]
    public void Loose_files_in_settings_folders_need_caution(string path) =>
        Assert.Equal(ProtectionLevel.Caution, _policy.AssessFile(path).Level);

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\ubuntu.iso")]
    [InlineData(@"C:\Users\me\Documents\report.docx")]
    [InlineData(@"C:\Users\Public\shared.zip")]
    [InlineData(@"C:\big.bin")]
    public void Loose_files_in_document_folders_stay_ordinary(string path) =>
        Assert.Equal(ProtectionLevel.None, _policy.AssessFile(path).Level);

    [Fact]
    public void Hiberfil_advice_mentions_powercfg()
    {
        Assert.Contains("powercfg", _policy.AssessFile(@"C:\hiberfil.sys").Advice);
        Assert.Contains("Dism", _policy.AssessDirectory(@"C:\Windows\WinSxS").Advice);
    }
}

public class UninstallParsingTests
{
    private static Dictionary<string, object?> Values(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Parses_a_typical_entry_and_converts_estimated_size()
    {
        var app = UninstallEntryParser.Parse("Blender", Values(
            ("DisplayName", "Blender 4.2"),
            ("Publisher", "Blender Foundation"),
            ("DisplayVersion", "4.2.0"),
            ("InstallLocation", @"C:\Program Files\Blender Foundation\Blender 4.2\"),
            ("EstimatedSize", 2_000_000),
            ("InstallDate", "20240716"),
            ("UninstallString", @"""C:\Program Files\Blender Foundation\Blender 4.2\uninstall.exe"""),
            ("QuietUninstallString", @"""C:\Program Files\Blender Foundation\Blender 4.2\uninstall.exe"" /S")), AppSource.MachineRegistry64);

        Assert.NotNull(app);
        Assert.Equal("Blender 4.2", app.Name);
        Assert.Equal(2_000_000L * 1024, app.ReportedSize);
        Assert.Equal(@"C:\Program Files\Blender Foundation\Blender 4.2", app.InstallLocation);
        Assert.False(app.InstallLocationInferred);
        Assert.Equal(new DateTime(2024, 7, 16), app.InstallDate);
        Assert.True(app.CanUninstall);
        Assert.True(app.CanQuietUninstall);
        Assert.False(app.IsSizeMeasured);
    }

    [Theory]
    [InlineData("SystemComponent", 1)]
    [InlineData("ParentKeyName", "OfficeParent")]
    [InlineData("ReleaseType", "Security Update")]
    public void Skips_hidden_components_and_updates(string name, object value)
    {
        var app = UninstallEntryParser.Parse("x", Values(("DisplayName", "Something"), ("UninstallString", "x.exe"), (name, value)), AppSource.MachineRegistry64);
        Assert.Null(app);
    }

    [Fact]
    public void Skips_entries_without_name_or_uninstaller()
    {
        Assert.Null(UninstallEntryParser.Parse("a", Values(("UninstallString", "x.exe")), AppSource.UserRegistry));
        Assert.Null(UninstallEntryParser.Parse("b", Values(("DisplayName", "No way to remove")), AppSource.UserRegistry));
    }

    [Fact]
    public void Msi_products_uninstall_with_msiexec_x_and_product_code()
    {
        const string code = "{12345678-ABCD-ABCD-ABCD-1234567890AB}";
        var app = UninstallEntryParser.Parse(code, Values(
            ("DisplayName", "Some MSI Product"),
            ("WindowsInstaller", 1),
            ("UninstallString", $"MsiExec.exe /I{code}")), AppSource.MachineRegistry32)!;

        Assert.True(app.IsMsi);
        Assert.Equal(code, app.MsiProductCode);
        var command = UninstallCommand.ForApp(app, quiet: false)!;
        Assert.Equal("msiexec.exe", command.FileName);
        Assert.Equal($"/x {code}", command.Arguments);
    }

    [Fact]
    public void Infers_install_folder_from_display_icon_and_rejects_broad_folders()
    {
        var app = UninstallEntryParser.Parse("Tool", Values(
            ("DisplayName", "Tool"),
            ("InstallLocation", @"C:\Program Files"),
            ("DisplayIcon", @"C:\Program Files\Tool\tool.exe,0"),
            ("UninstallString", @"C:\Program Files\Tool\uninst\unins000.exe")), AppSource.MachineRegistry64)!;

        Assert.Equal(@"C:\Program Files\Tool", app.InstallLocation);
        Assert.True(app.InstallLocationInferred);

        Assert.False(UninstallEntryParser.IsPlausibleInstallFolder(@"C:\"));
        Assert.False(UninstallEntryParser.IsPlausibleInstallFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(UninstallEntryParser.IsPlausibleInstallFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(UninstallEntryParser.IsPlausibleInstallFolder(@"C:\ProgramData\Package Cache\{guid}"));
        Assert.True(UninstallEntryParser.IsPlausibleInstallFolder(@"D:\Games\Baldurs Gate 3"));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\uninstall.exe"" /S", @"C:\Program Files\App\uninstall.exe", "/S")]
    [InlineData(@"C:\Program Files\App\uninst.exe /quiet /norestart", @"C:\Program Files\App\uninst.exe", "/quiet /norestart")]
    [InlineData(@"C:\Program Files (x86)\My App\Uninstall My App.exe", @"C:\Program Files (x86)\My App\Uninstall My App.exe", "")]
    [InlineData(@"MsiExec.exe /X{AAAA}", "MsiExec.exe", "/X{AAAA}")]
    [InlineData(@"rundll32.exe dfshim.dll,ShArpMaintain app.application", "rundll32.exe", "dfshim.dll,ShArpMaintain app.application")]
    [InlineData(@"""C:\Users\me\AppData\Local\Discord\Update.exe"" --uninstall", @"C:\Users\me\AppData\Local\Discord\Update.exe", "--uninstall")]
    [InlineData(@"C:\Tools\executor.exe.bak\real.exe /x", @"C:\Tools\executor.exe.bak\real.exe", "/x")]
    public void Splits_command_lines(string commandLine, string file, string args)
    {
        var command = UninstallCommand.TryParse(commandLine)!;
        Assert.Equal(file, command.FileName);
        Assert.Equal(args, command.Arguments);
    }

    [Fact]
    public void Unquoted_paths_without_extension_are_resolved_against_the_file_system()
    {
        var command = UninstallCommand.TryParse(@"C:\Program Files\Odd Tool\remover --all", p => p == @"C:\Program Files\Odd Tool\remover")!;
        Assert.Equal(@"C:\Program Files\Odd Tool\remover", command.FileName);
        Assert.Equal("--all", command.Arguments);
    }
}

public class DeduplicationTests
{
    private static InstalledApp App(string id, string name, AppSource source, string? location = null, string? publisher = "Vendor", string? version = "1.0", string? uninstall = "u.exe", long? size = null) =>
        new() { Id = id, Name = name, Source = source, InstallLocation = location, Publisher = publisher, Version = version, UninstallString = uninstall, RegistrationKey = id, ReportedSize = size };

    [Fact]
    public void Merges_32_and_64_bit_registrations_of_the_same_product()
    {
        var merged = AppDeduplicator.Merge(
        [
            App("a", "Visual Studio Code", AppSource.MachineRegistry64, @"C:\Program Files\VS Code", size: 100),
            App("b", "Visual Studio Code", AppSource.MachineRegistry32, @"C:\Program Files\VS Code\", size: 300),
        ]);

        var app = Assert.Single(merged);
        Assert.Equal(300, app.ReportedSize);
    }

    [Fact]
    public void Keeps_different_installations_with_the_same_name()
    {
        var merged = AppDeduplicator.Merge(
        [
            App("a", "Python 3.12", AppSource.MachineRegistry64, @"C:\Python312"),
            App("b", "Python 3.12", AppSource.UserRegistry, @"C:\Users\me\AppData\Local\Programs\Python\Python312"),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Same_user_key_seen_through_both_views_is_merged_and_fills_missing_fields()
    {
        var first = App("k", "Tool", AppSource.UserRegistry, location: null, uninstall: null);
        var second = App("k", "Tool", AppSource.UserRegistry, location: @"C:\Tools\Tool");
        var merged = AppDeduplicator.Merge([first, second]);

        var app = Assert.Single(merged);
        Assert.Equal(@"C:\Tools\Tool", app.InstallLocation);
        Assert.Equal("u.exe", app.UninstallString);
    }

    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7 zip 23 01")]
    [InlineData("Git  version 2.45", "git version 2 45")]
    [InlineData("Node.js x64", "node js")]
    public void Normalizes_names(string input, string expected) => Assert.Equal(expected, AppDeduplicator.NormalizeName(input));
}

public class DetectorTests
{
    [Fact]
    public async Task Developer_detector_requires_project_markers_and_folds_nested_matches()
    {
        using var dir = new TestDirectory();
        dir.File(@"web\package.json", 10);
        dir.File(@"web\node_modules\react\index.js", 2 << 20);
        dir.File(@"web\node_modules\react\node_modules\inner\x.js", 1 << 20);
        dir.File(@"rust\Cargo.toml", 10);
        dir.File(@"rust\target\debug\app.exe", 3 << 20);
        dir.File(@"dotnet\App.csproj", 10);
        dir.File(@"dotnet\bin\Debug\App.dll", 2 << 20);
        dir.File(@"photos\bin\holiday.jpg", 2 << 20); // "bin" without a project file: not a build folder
        dir.File(@"py\.venv\pyvenv.cfg", 10);
        dir.File(@"py\.venv\Lib\site.py", 2 << 20);

        var tree = new ScanTree(dir.Root);
        await new SpaceLens.Core.Scanning.ParallelDirectoryScanner(new FileFullDirInfoEnumeratorFactory())
            .ScanAsync(tree, new SpaceLens.Core.Scanning.ScanOptions { MaxParallelism = 2 }, null, CancellationToken.None);

        var context = new DetectionContext { Tree = tree, Known = KnownLocations.FromEnvironment() };
        var findings = new DeveloperFilesDetector().Detect(context, CancellationToken.None).ToList();

        Assert.Single(findings, f => f.Group == "node_modules");
        Assert.Single(findings, f => f.Group == "Rust target folders");
        Assert.Single(findings, f => f.Group == ".NET build output");
        Assert.Single(findings, f => f.Group == "Python environments");
        Assert.DoesNotContain(findings, f => f.Path.Contains("photos", StringComparison.OrdinalIgnoreCase));
        Assert.All(findings, f => Assert.Equal(LocationCategory.Developer, f.Category));
        Assert.True(findings.Single(f => f.Group == "node_modules").Size >= 3 << 20);
    }

    [Fact]
    public void Developer_detector_skips_node_modules_that_are_not_projects()
    {
        // D:\
        //   web\package.json + node_modules             a project: reported
        //   loose\node_modules                          no package.json: not reinstallable
        //   App\resources\app\package.json + node_modules  Electron application bundle
        //   Program Files\Tool\package.json + node_modules  installed program
        //   SteamLibrary\steamapps\common\Game\...       installed game
        var tree = new ScanTree(@"D:\");
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int AddNodeModules(int parent, bool withPackageJson)
        {
            int modules = tree.AddDirectory(parent, "node_modules");
            int inner = tree.AddDirectory(modules, "some-package");
            tree.CompleteDirectory(inner, 2 << 20, 3, 0);
            if (withPackageJson)
            {
                markers.Add(tree.GetPath(parent) + @"\package.json");
                markers.Add(tree.GetPath(inner) + @"\package.json");
            }

            return modules;
        }

        int Dir(int parent, string name) => tree.AddDirectory(parent, name);

        int project = AddNodeModules(Dir(ScanTree.RootIndex, "web"), withPackageJson: true);
        AddNodeModules(Dir(ScanTree.RootIndex, "loose"), withPackageJson: false);
        AddNodeModules(Dir(Dir(Dir(ScanTree.RootIndex, "App"), "resources"), "app"), withPackageJson: true);
        AddNodeModules(Dir(Dir(ScanTree.RootIndex, "Program Files"), "Tool"), withPackageJson: true);
        AddNodeModules(Dir(Dir(Dir(Dir(ScanTree.RootIndex, "SteamLibrary"), "steamapps"), "common"), "Game"), withPackageJson: true);

        var context = new DetectionContext
        {
            Tree = tree,
            Known = SafetyPolicyTests.Known,
            FileExists = p => markers.Contains(p.Replace('/', '\\')),
            DirectoryExists = _ => false,
        };
        var findings = new DeveloperFilesDetector().Detect(context, CancellationToken.None).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal("node_modules", finding.Group);
        Assert.Equal(project, finding.DirectoryIndex);
    }

    [Fact]
    public void Parses_steam_library_folders_and_manifests()
    {
        using var dir = new TestDirectory();
        string vdf = Path.Combine(dir.Root, "libraryfolders.vdf");
        File.WriteAllText(vdf, """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """);

        var libraries = SteamDetector.ReadLibraryFolders(vdf).ToList();
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], libraries);

        var manifest = SteamDetector.ParseManifest("""
            "AppState"
            {
                "appid"		"1086940"
                "name"		"Baldur's Gate 3"
                "installdir"		"Baldurs Gate 3"
            }
            """);
        Assert.Equal("1086940", manifest.AppId);
        Assert.Equal("Baldur's Gate 3", manifest.Name);
        Assert.Equal("Baldurs Gate 3", manifest.InstallDir);
    }
}

public class DriveTests
{
    [Fact]
    public void Lists_the_system_drive_with_sane_numbers()
    {
        var drives = DriveService.GetDrives();
        var system = Assert.Single(drives, d => d.IsSystemDrive);
        Assert.True(system.TotalBytes > 0);
        Assert.InRange(system.FreeBytes, 0, system.TotalBytes);
        Assert.InRange(system.UsedFraction, 0, 1);
        Assert.StartsWith(system.Letter, system.DisplayName);
        Assert.True(system.RecommendedParallelism >= 1);
        Assert.NotEqual(0u, system.SerialNumber);
    }

    [Fact]
    public void Resolves_the_drive_of_a_path()
    {
        var drive = DriveService.GetDrive(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Assert.NotNull(drive);
        Assert.True(drive.IsSystemDrive);
        Assert.Null(DriveService.GetDrive(@"\\server\share\x"));
    }
}
