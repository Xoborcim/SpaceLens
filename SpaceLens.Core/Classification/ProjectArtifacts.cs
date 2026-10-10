using SpaceLens.Core.Models;

namespace SpaceLens.Core.Classification;

/// <summary>A folder of a software project that its build or package tools recreate (node_modules, Rust target...).</summary>
public readonly record struct ProjectArtifact(int DirectoryIndex, string Group, string? Title);

/// <summary>
/// Finds project build artifacts and dependency folders in a scan, on any platform.
/// <para>
/// Generic folder names like "bin", "build" or "target" are only reported when a project marker file is
/// next to them (a *.csproj, Cargo.toml, package.json, ...), so ordinary folders that happen to share these
/// names are not flagged. Nested matches (node_modules inside node_modules) are folded into the outermost
/// one. Which areas hold installed software rather than projects is platform knowledge, supplied by the
/// caller as <c>skipFolder</c>.
/// </para>
/// </summary>
public static class ProjectArtifacts
{
    public static IReadOnlyDictionary<string, string> GroupExplanations { get; } = new Dictionary<string, string>
    {
        ["node_modules"] = "Packages installed by npm, yarn or pnpm for a JavaScript project. Recreated by running the project's install command (for example \"npm install\").",
        ["Rust target folders"] = "Build output of Cargo (Rust). Recreated by \"cargo build\"; \"cargo clean\" removes it.",
        [".NET build output"] = "bin and obj folders of .NET / Visual Studio projects. Recreated on the next build.",
        ["Java build output"] = "Build output of Maven or Gradle projects. Recreated on the next build.",
        ["JavaScript build output"] = "Framework build output and caches (.next, .nuxt, dist, build, ...). Recreated by the project's build command.",
        ["Python environments"] = "Python virtual environments with installed packages. They can be recreated from the project's requirements, but packages must be downloaded again.",
        ["Python bytecode caches"] = "Compiled Python bytecode (__pycache__). Recreated automatically.",
    };

    private static readonly HashSet<string> JsBuildNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache", ".angular", ".expo", ".vite",
    };

    private static readonly HashSet<string> MarkerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "target", "bin", "obj", "dist", "build", "out", ".gradle", ".venv", "venv", "env", "__pycache__",
    };

    /// <param name="fileExists">Probe for small marker files that the scan did not index (package.json, Cargo.toml...).</param>
    /// <param name="skipFolder">True for folders not to search (installed software, system areas, already claimed locations).</param>
    public static List<ProjectArtifact> Find(ScanTree tree, Func<string, bool> fileExists, Func<int, bool> skipFolder, CancellationToken cancellationToken = default)
    {
        var found = new List<ProjectArtifact>();
        TreeWalker.Walk(tree, ScanTree.RootIndex, d =>
        {
            if (skipFolder(d))
            {
                return false;
            }

            string name = tree.Dir(d).Name;
            if (!MarkerNames.Contains(name) && !JsBuildNames.Contains(name))
            {
                return true;
            }

            var (group, title) = Classify(tree, fileExists, d, name);
            if (group is null)
            {
                // Packages inside a node_modules that is not a project's each carry a package.json of
                // their own; never look for artifacts in there.
                return !name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
            }

            found.Add(new ProjectArtifact(d, group, title));

            // Do not report nested matches inside an artifact folder.
            return false;
        }, cancellationToken);
        return found;
    }

    private static (string? Group, string? Title) Classify(ScanTree tree, Func<string, bool> fileExists, int dir, string name)
    {
        int parent = tree.Dir(dir).Parent;
        string parentPath = tree.GetPath(parent);
        string parentName = tree.Dir(parent).Name;
        bool Has(string file) => fileExists(PathUtil.Combine(parentPath, file));
        bool HasProjectFile() => HasFileWithExtension(parentPath, ".csproj", ".fsproj", ".vbproj", ".vcxproj", ".sln", ".slnx");

        switch (name.ToLowerInvariant())
        {
            case "node_modules":
                // Only a project's packages can be reinstalled: they sit next to its package.json. Electron
                // applications ship a package.json too, inside resources/app (or app.asar.unpacked).
                return Has("package.json") && !IsElectronBundle(tree, parent) ? ("node_modules", null) : (null, null);
            case "__pycache__":
                return ("Python bytecode caches", null);
            case ".venv":
            case "venv":
            case "env":
                return fileExists(PathUtil.Combine(tree.GetPath(dir), "pyvenv.cfg")) ? ("Python environments", $"{name} ({parentName})") : (null, null);
            case "target":
                if (Has("Cargo.toml") || fileExists(PathUtil.Combine(tree.GetPath(dir), "CACHEDIR.TAG")))
                {
                    return ("Rust target folders", $"target ({parentName})");
                }

                return Has("pom.xml") ? ("Java build output", $"target ({parentName})") : (null, null);
            case "bin":
            case "obj":
                return HasProjectFile() ? (".NET build output", $"{name} ({parentName})") : (null, null);
            case ".gradle":
                return Has("build.gradle") || Has("build.gradle.kts") || Has("settings.gradle") || Has("settings.gradle.kts")
                    ? ("Java build output", $".gradle ({parentName})") : (null, null);
            case "dist":
            case "build":
            case "out":
                if (Has("package.json"))
                {
                    return ("JavaScript build output", $"{name} ({parentName})");
                }

                if (Has("build.gradle") || Has("build.gradle.kts"))
                {
                    return ("Java build output", $"{name} ({parentName})");
                }

                return (null, null);
            default:
                return JsBuildNames.Contains(name) ? ("JavaScript build output", $"{name} ({parentName})") : (null, null);
        }
    }

    private static bool IsElectronBundle(ScanTree tree, int dir)
    {
        string name = tree.Dir(dir).Name;
        int parent = tree.Dir(dir).Parent;
        return parent >= 0 &&
               (name.Equals("app", StringComparison.OrdinalIgnoreCase) || name.Equals("app.asar.unpacked", StringComparison.OrdinalIgnoreCase)) &&
               tree.Dir(parent).Name.Equals("resources", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFileWithExtension(string directory, params string[] extensions)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(PathUtil.ToLongPath(directory)))
            {
                foreach (var ext in extensions)
                {
                    if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }
}
