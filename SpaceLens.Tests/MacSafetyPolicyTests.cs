using SpaceLens.Core.Safety;

namespace SpaceLens.Tests;

public class MacSafetyPolicyTests
{
    private readonly MacSafetyPolicy _policy = new(new MacKnownLocations { Home = "/Users/me" });

    [Theory]
    [InlineData("/")]
    [InlineData("/Volumes/Backup")]
    [InlineData("/System")]
    [InlineData("/System/Library/Caches")]
    [InlineData("/System/Volumes/Data")]
    [InlineData("/usr")]
    [InlineData("/usr/local")]
    [InlineData("/bin")]
    [InlineData("/private/var/vm")]
    [InlineData("/private/var/folders")]
    [InlineData("/Library")]
    [InlineData("/Applications")]
    [InlineData("/Applications/Utilities")]
    [InlineData("/Applications/Safari.app")]
    [InlineData("/Applications/Xcode.app/Contents/Developer")]
    [InlineData("/Users/me/Applications/Tool.app")]
    [InlineData("/Users")]
    [InlineData("/Users/Shared")]
    [InlineData("/Users/me")]
    [InlineData("/Users/me/Documents")]
    [InlineData("/Users/me/Library")]
    [InlineData("/Users/me/.Trash")]
    [InlineData("/Users/bob")]
    [InlineData("/Users/bob/Desktop")]
    [InlineData("/.Spotlight-V100")]
    [InlineData("/Volumes/Backup/Backups.backupdb")]
    [InlineData("/Volumes/USB/.fseventsd")]
    [InlineData("/opt/homebrew")]
    public void Protected_folders_cannot_be_removed(string path) =>
        Assert.False(_policy.AssessDirectory(path).CanDelete, path);

    [Theory]
    [InlineData("/Users/me/Library/Application Support/SomeApp", "Application data")]
    [InlineData("/Users/me/Library/Caches/com.example.app", "Cache")]
    [InlineData("/Users/me/Library/Mobile Documents/com~apple~CloudDocs/Taxes", "iCloud Drive")]
    [InlineData("/Library/Application Support/Vendor", "Shared application data")]
    [InlineData("/opt/homebrew/Cellar/node", "Package manager")]
    [InlineData("/usr/local/Cellar/python", "Package manager")]
    [InlineData("/Users/me/.ssh", "Settings")]
    [InlineData("/Users/bob/Library/Caches", "Application data")]
    public void Application_data_needs_caution(string path, string label)
    {
        var assessment = _policy.AssessDirectory(path);
        Assert.Equal(ProtectionLevel.Caution, assessment.Level);
        Assert.Equal(label, assessment.Label);
    }

    [Theory]
    [InlineData("/Users/me/Downloads/old-installers")]
    [InlineData("/Users/me/Projects/web/node_modules")]
    [InlineData("/Users/me/Projects/Build/MyApp.app")]
    [InlineData("/Users/bob/Downloads/stuff")]
    [InlineData("/Users/Shared/Recordings")]
    [InlineData("/Volumes/Backup/Old Projects")]
    public void Ordinary_folders_can_be_removed(string path)
    {
        var assessment = _policy.AssessDirectory(path);
        Assert.True(assessment.CanDelete, path);
        Assert.Equal(ProtectionLevel.None, assessment.Level);
    }

    [Fact]
    public void Files_follow_their_folder()
    {
        Assert.False(_policy.AssessFile("/System/Library/Kernels/kernel").CanDelete);
        Assert.True(_policy.AssessFile("/System/Library/Kernels/kernel").IsSystemManaged);
        Assert.False(_policy.AssessFile("/private/var/vm/sleepimage").CanDelete);
        Assert.False(_policy.AssessFile("/Applications/Safari.app/Contents/Info.plist").CanDelete);
        Assert.Equal(ProtectionLevel.Caution, _policy.AssessFile("/Users/me/Library/Preferences/com.example.plist").Level);
        Assert.Equal(ProtectionLevel.Caution, _policy.AssessFile("/Users/me/.zshrc").Level);
        Assert.Equal(ProtectionLevel.None, _policy.AssessFile("/Users/me/Downloads/ubuntu.iso").Level);
        Assert.Equal(ProtectionLevel.None, _policy.AssessFile("/Volumes/USB/movie.mov").Level);
        Assert.Equal(ProtectionLevel.Caution, _policy.AssessFile("/Users/me/Library/Caches/com.example/data.db").Level);
    }
}
