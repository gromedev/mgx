using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Mgx.IntegrationTests;

/// <summary>
/// Everything the shipped assemblies reference has to be reachable from the module folder plus
/// the runtime the host already has. Nothing else is on offer: the ALC resolver serves
/// Dependencies/ only for assemblies found nowhere, so a reference that is neither ours, nor
/// staged, nor part of the framework src/ targets resolves to nothing and throws
/// FileNotFoundException at the first call that needs it - on a build agent, not here.
///
/// The closure is computed by reading metadata, not by loading: start at the staged
/// Mgx.Cmdlets.dll and Mgx.Engine.dll and follow every assembly reference through whatever the
/// running runtime would resolve it to. That is deliberately the newest runtime rather than
/// net8.0's, because a host is free to be newer and the module has to survive it: PowerShell 7.6
/// binds the engine's System.Text.Json reference to its own 10.0 copy, and that copy's own
/// references are as much a part of the module's closure as the engine's are.
///
/// System.Management.Automation is where the walk stops. It is the host itself, it is never
/// shipped, and whatever it drags in is the host's to supply.
///
/// Every path here resolves under the repository root - the directory holding Mgx.slnx - and
/// nothing above that root is ever consulted, so a module folder above the repository cannot be
/// measured in place of the staged one.
/// </summary>
public class ModuleAssemblyClosureTests
{
    /// <summary>The host. Present by definition, never staged, and not expanded.</summary>
    private const string HostAssembly = "System.Management.Automation";

    /// <summary>
    /// A walk that stops early reports no unresolved references and reads exactly like a clean
    /// one, so the size of the closure is asserted too. The floor carries slack because
    /// references come and go; what it catches is a walk that has stopped walking.
    /// </summary>
    private const int ClosureFloor = 40;

    /// <summary>The file that marks the repository root.</summary>
    private const string RepositoryMarker = "Mgx.slnx";

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> to the first ancestor containing Mgx.slnx
    /// and return it as the repository root. Nothing above that directory is ever consulted, so
    /// a decoy further up - a module folder beside a worktree checked out under it - cannot bind.
    /// </summary>
    private static string FindRepositoryRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, RepositoryMarker))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {RepositoryMarker} above {startDirectory}");
    }

    /// <summary>Resolve a repository-relative path under the root reached from <paramref name="startDirectory"/>.</summary>
    private static string FindRepositoryPath(string relativePath, string startDirectory)
    {
        var root = FindRepositoryRoot(startDirectory);
        var candidate = Path.Combine(root, relativePath);
        if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' under repository root {root}");
    }

    /// <summary>FindRepositoryPath, starting from the test binaries.</summary>
    private static string FindRepositoryPath(string relativePath) =>
        FindRepositoryPath(relativePath, AppContext.BaseDirectory);

    /// <summary>The staged module folder - the directory holding mgx.psd1.</summary>
    private static string ModuleRoot() =>
        Path.GetDirectoryName(FindRepositoryPath(Path.Combine("module", "mgx.psd1")))!;

    /// <summary>
    /// The staged Dependencies folder. Absent means the module has not been built, which is the
    /// same demand the Pester harness makes: this test measures build output, so it fails rather
    /// than skipping - a skipped test does not count toward the suite floor CI asserts on.
    /// </summary>
    private static string DependenciesDirectory()
    {
        var deps = Path.Combine(ModuleRoot(), "Dependencies");
        if (!Directory.Exists(deps))
            throw new DirectoryNotFoundException($"Dependencies folder not found at '{deps}'. Run ./build.ps1 first.");

        return deps;
    }

    /// <summary>Simple assembly names of the DLLs in a folder, or an empty set if it is absent.</summary>
    private static HashSet<string> DllNamesIn(string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return names;

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            names.Add(Path.GetFileNameWithoutExtension(file));

        return names;
    }

    /// <summary>The framework src/ compiles against, e.g. "net8.0". Read, not hardcoded, so a TFM bump moves this with it.</summary>
    private static string ShippedTargetFramework()
    {
        var csproj = FindRepositoryPath(Path.Combine("src", "Mgx.Cmdlets", "Mgx.Cmdlets.csproj"));
        var tfm = XDocument.Load(csproj).Descendants("TargetFramework").FirstOrDefault()?.Value;

        Assert.False(string.IsNullOrWhiteSpace(tfm), $"No <TargetFramework> in {csproj}");
        return tfm!;
    }

    /// <summary>Directories that may hold a Microsoft.NETCore.App reference pack.</summary>
    private static IEnumerable<string> ReferencePackRoots()
    {
        // Restored as a package when the SDK building net8.0 is newer than 8.0 ...
        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(nugetRoot))
            nugetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        yield return Path.Combine(nugetRoot, "microsoft.netcore.app.ref");

        // ... and bundled with the SDK when that SDK is installed alongside, which is what CI has.
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot))
        {
            // <dotnet root>/shared/Microsoft.NETCore.App/<version>/ is where the core library lives.
            var runtimeDirectory = RuntimeDirectory();
            dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory)));
        }

        if (!string.IsNullOrEmpty(dotnetRoot))
            yield return Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
    }

    /// <summary>
    /// Simple names of every assembly the shared framework src/ targets provides, taken from that
    /// framework's reference pack. This is the list a host is guaranteed to satisfy on its own;
    /// anything outside it has to come from the module folder.
    /// </summary>
    private static HashSet<string> SharedFrameworkAssemblyNames(string targetFramework)
    {
        var major = targetFramework["net".Length..].Split('.')[0] + ".";
        var candidates =
            from root in ReferencePackRoots()
            where Directory.Exists(root)
            from versionDirectory in Directory.EnumerateDirectories(root)
            let name = Path.GetFileName(versionDirectory)
            where name.StartsWith(major, StringComparison.Ordinal)
                && Version.TryParse(name, out _)
            let refDirectory = Path.Combine(versionDirectory, "ref", targetFramework)
            where Directory.Exists(refDirectory)
            orderby Version.Parse(name)
            select refDirectory;

        var newest = candidates.LastOrDefault();
        Assert.True(newest is not null,
            $"No Microsoft.NETCore.App reference pack for {targetFramework} under: "
            + string.Join(", ", ReferencePackRoots())
            + ". The closure cannot be checked without the list of assemblies the framework supplies.");

        return DllNamesIn(newest!);
    }

    /// <summary>Where the running shared framework's assemblies sit on disk.</summary>
    private static string RuntimeDirectory() => Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    /// <summary>
    /// Assembly references recorded in a file's metadata. Reading rather than loading keeps the
    /// walk honest about the staged copies: Mgx.Cmdlets, Polly.Core and the rest are already
    /// loaded in this process from the test's own output folder, and loading them again from
    /// module/ would just hand back the copy that is already there.
    /// </summary>
    private static IReadOnlyList<string> ReferencesOf(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        return [.. metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))];
    }

    /// <summary>
    /// The file a host would bind a reference to: the module folder first, then this process's own
    /// folder, then the shared framework - app-local before framework, the order the runtime uses.
    /// </summary>
    private static string? ResolveReference(string simpleName, params string[] directories)
    {
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, $"{simpleName}.dll");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// The runtime's own implementation assemblies. They are in no reference pack because they
    /// have no compile-time surface, and no package ships them - they exist wherever the runtime
    /// does, so a host cannot be missing one.
    /// </summary>
    private static bool IsRuntimePrivate(string simpleName) =>
        simpleName.StartsWith("System.Private.", StringComparison.Ordinal);

    private sealed record Reached(string Name, string ReferencedBy, string? Path);

    /// <summary>
    /// Breadth-first over assembly references from the two staged assemblies, recording where each
    /// name was first reached from and which file it binds to.
    /// </summary>
    private static IReadOnlyList<Reached> Closure()
    {
        var moduleRoot = ModuleRoot();
        var dependencies = DependenciesDirectory();
        var probeOrder = new[] { moduleRoot, dependencies, AppContext.BaseDirectory, RuntimeDirectory() };

        var roots = new[] { "Mgx.Cmdlets", "Mgx.Engine" };
        var reached = new List<Reached>();
        var seen = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Name, string Path)>();

        foreach (var root in roots)
        {
            var path = Path.Combine(moduleRoot, $"{root}.dll");
            Assert.True(File.Exists(path), $"'{path}' not found. Run ./build.ps1 first.");
            queue.Enqueue((root, path));
        }

        while (queue.Count > 0)
        {
            var (from, path) = queue.Dequeue();
            foreach (var name in ReferencesOf(path))
            {
                if (!seen.Add(name)) continue;

                var resolved = ResolveReference(name, probeOrder);
                reached.Add(new Reached(name, from, resolved));

                // The host and everything below it is the host's to supply.
                if (name == HostAssembly) continue;
                if (resolved is not null) queue.Enqueue((name, resolved));
            }
        }

        return reached;
    }

    [Fact]
    public void Every_referenced_assembly_resolves_from_the_module_layout_or_the_framework()
    {
        var mgx = DllNamesIn(ModuleRoot());
        var staged = DllNamesIn(DependenciesDirectory());
        var framework = SharedFrameworkAssemblyNames(ShippedTargetFramework());
        var closure = Closure();

        var unsatisfied = closure
            .Where(r => !mgx.Contains(r.Name)
                     && !staged.Contains(r.Name)
                     && !framework.Contains(r.Name)
                     && !IsRuntimePrivate(r.Name)
                     && r.Name != HostAssembly)
            .Select(r => $"{r.Name} (referenced by {r.ReferencedBy})")
            .ToList();

        Assert.True(unsatisfied.Count == 0,
            "assemblies in the module's closure that a host is not guaranteed to have and the "
            + "module does not ship - stage each into module/Dependencies/ (build.ps1 and "
            + "Mgx.Cmdlets.csproj's StageModuleOutput target both list what is staged): "
            + string.Join("; ", unsatisfied));

        // Two ways the check above goes quiet: a walk that stops after the roots, and a reference
        // that resolves to nothing so its own references are never read. Neither shows up as a
        // failure on its own, so both are asserted.
        var dead = closure
            .Where(r => r.Path is null && r.Name != HostAssembly)
            .Select(r => $"{r.Name} (referenced by {r.ReferencedBy})")
            .ToList();

        Assert.True(dead.Count == 0,
            "references the walk could not bind to a file, so nothing below them was read: "
            + string.Join("; ", dead));

        Assert.True(closure.Count >= ClosureFloor,
            $"the walk reached {closure.Count} assemblies - it is not following references");
    }

    [Fact]
    public void The_walk_up_does_not_cross_the_repository_root()
    {
        // A decoy module/mgx.psd1 sits above the repository root here - the shape of a worktree
        // checked out under a directory that holds a module folder of its own, where a walk-up
        // taking the first ancestor with a module/mgx.psd1 under it measures the decoy's staging
        // and reports on a module nobody built. Anchoring on Mgx.slnx means the decoy is never
        // reached: the staged manifest resolves under the marked root, and a root without one
        // resolves to nothing rather than to the decoy above it.
        var sandbox = Path.Combine(Path.GetTempPath(), $"mgx-closure-root-test-{Guid.NewGuid():N}");
        var manifest = Path.Combine("module", "mgx.psd1");
        var repoRoot = Path.Combine(sandbox, "repo");
        var bareRoot = Path.Combine(sandbox, "bare");
        try
        {
            Directory.CreateDirectory(Path.Combine(sandbox, "module"));
            File.WriteAllText(Path.Combine(sandbox, manifest), string.Empty);

            Directory.CreateDirectory(Path.Combine(repoRoot, "module"));
            File.WriteAllText(Path.Combine(repoRoot, RepositoryMarker), string.Empty);
            File.WriteAllText(Path.Combine(repoRoot, manifest), string.Empty);
            var startDirectory = Path.Combine(repoRoot, "tests", "x", "bin", "Debug");
            Directory.CreateDirectory(startDirectory);

            Assert.Equal(Path.Combine(repoRoot, manifest),
                FindRepositoryPath(manifest, startDirectory));

            // The same tree with nothing staged under its root: the decoy above is still not an
            // answer, and a walk that climbs past the marker to find one binds it here.
            Directory.CreateDirectory(bareRoot);
            File.WriteAllText(Path.Combine(bareRoot, RepositoryMarker), string.Empty);
            var bareStart = Path.Combine(bareRoot, "tests", "x", "bin", "Debug");
            Directory.CreateDirectory(bareStart);

            try
            {
                var climbed = FindRepositoryPath(manifest, bareStart);
                Assert.Fail($"a root with no '{manifest}' of its own resolved one at '{climbed}'");
            }
            catch (FileNotFoundException)
            {
                // Nothing above the root was consulted, which is the whole of it.
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void The_staged_dependencies_are_carried_not_compiled_against()
    {
        // Dependencies/ holds two different kinds of file. Polly.Core and
        // System.Threading.RateLimiting are ours: the engine compiles against them, and they are
        // isolated there so a neighboring module's copy cannot win the bind. System.IO.Pipelines
        // is nobody's: it is carried because the System.Text.Json a newer host supplies needs it
        // and net8.0 does not include it. If it ever turned into a compile-time reference, the
        // engine would be built against a surface the framework it targets does not have.
        var moduleRoot = ModuleRoot();
        var referenced = ReferencesOf(Path.Combine(moduleRoot, "Mgx.Engine.dll"))
            .Concat(ReferencesOf(Path.Combine(moduleRoot, "Mgx.Cmdlets.dll")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("System.IO.Pipelines", referenced);
        Assert.Contains("Polly.Core", referenced);
        Assert.Contains("System.Threading.RateLimiting", referenced);
    }
}
