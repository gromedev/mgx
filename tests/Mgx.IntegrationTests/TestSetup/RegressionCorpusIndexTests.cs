using System.Text.RegularExpressions;

namespace Mgx.IntegrationTests;

/// <summary>
/// Provenance is only useful if it can be trusted, and a doc comment naming an issue number is
/// unverifiable on its own. Every issue id written anywhere under tests/ has to have a row in
/// the index saying what it guarantees and where. The index itself is private (tmp/CORPUS.md,
/// untracked), so a host without it - public CI - runs these as counted no-ops: the guard
/// binds wherever the index lives. Every path here resolves under the repository root - the
/// directory holding Mgx.slnx - and nothing above that root is ever consulted.
///
/// The guard runs one way only. The reverse - every indexed id has a test - cannot hold while
/// the index carries rows that name the release bringing their test, which is the honest way to
/// record a gap.
/// </summary>
public class RegressionCorpusIndexTests
{
    private const string IndexPath = "tmp/CORPUS.md";
    private const string RepositoryMarker = "Mgx.slnx";

    /// <summary>
    /// Matches an issue id and any ids abbreviated onto it: "M365DSC-5306/7175" is two ids, and
    /// a scan that read it as one would let the second go unindexed.
    /// </summary>
    private static readonly Regex IssueId = new(
        @"\b(M365DSC|GraphSDK)-(\d+)((?:/\d+)*)", RegexOptions.Compiled);

    private static IEnumerable<string> IdsIn(string text)
    {
        foreach (Match match in IssueId.Matches(text))
        {
            var prefix = match.Groups[1].Value;
            yield return $"{prefix}-{match.Groups[2].Value}";
            foreach (var abbreviated in match.Groups[3].Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
                yield return $"{prefix}-{abbreviated}";
        }
    }

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> to the first ancestor containing
    /// Mgx.slnx and return it as the repository root. Nothing above that directory is ever
    /// consulted, so a decoy further up - a worktree's own tmp/, a stranger's tests/ - cannot
    /// bind.
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

        throw new DirectoryNotFoundException(
            $"Could not locate '{relativePath}' under repository root {root}");
    }

    /// <summary>FindRepositoryPath, starting from the test binaries.</summary>
    private static string FindRepositoryPath(string relativePath) =>
        FindRepositoryPath(relativePath, AppContext.BaseDirectory);

    /// <summary>FindRepositoryPath for a file allowed to be absent: null instead of a throw.</summary>
    private static string? TryFindRepositoryPath(string relativePath, string startDirectory)
    {
        try { return FindRepositoryPath(relativePath, startDirectory); }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>TryFindRepositoryPath, starting from the test binaries.</summary>
    private static string? TryFindRepositoryPath(string relativePath) =>
        TryFindRepositoryPath(relativePath, AppContext.BaseDirectory);

    /// <summary>
    /// Every source file under tests/, minus build output, the index itself, and this file - which
    /// carries fabricated ids on purpose and would otherwise fail its own guard.
    /// </summary>
    private static IEnumerable<string> CorpusSourceFiles()
    {
        var testsRoot = FindRepositoryPath("tests");
        // The index lives outside tests/ and needs no exclusion; this file carries fabricated
        // ids on purpose and would otherwise fail its own guard.
        var excluded = new[]
        {
            Path.GetFullPath(FindRepositoryPath(
                Path.Combine("tests", "Mgx.IntegrationTests", "TestSetup", "RegressionCorpusIndexTests.cs")))
        };

        return Directory.EnumerateFiles(testsRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal))
            .Where(path => !excluded.Contains(Path.GetFullPath(path), StringComparer.Ordinal));
    }

    /// <summary>The ids a body of text claims, that the index does not carry.</summary>
    private static IReadOnlyList<string> Unindexed(string indexText, string claimingText)
    {
        var indexed = IdsIn(indexText).ToHashSet(StringComparer.Ordinal);
        return [.. IdsIn(claimingText).Distinct(StringComparer.Ordinal).Where(id => !indexed.Contains(id))];
    }

    [Fact]
    public void Every_issue_id_under_tests_has_a_row_in_the_index()
    {
        if (TryFindRepositoryPath(IndexPath) is not string indexFile) return;
        var indexText = File.ReadAllText(indexFile);

        var missing = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var file in CorpusSourceFiles())
        {
            scanned++;
            var text = File.ReadAllText(file);
            claimed.UnionWith(IdsIn(text));

            foreach (var id in Unindexed(indexText, text))
            {
                if (!missing.TryGetValue(id, out var files)) missing[id] = files = [];
                files.Add(Path.GetFileName(file));
            }
        }

        // A scan that reaches nothing reports nothing missing, so the guard has to fail on its
        // own silence first. A repository root resolved above the wrong directory, an extension
        // filter that stops matching, an exclusion that widens - each of those disarms the check
        // below while leaving it green. The floors carry slack because files and ids are added
        // over time; what they catch is a scan that has stopped running, not one that has moved.
        Assert.True(scanned >= 50,
            $"the scan reached {scanned} file(s) under tests/ - it is not reading the suite");
        Assert.True(claimed.Count >= 12,
            $"the scan found {claimed.Count} issue id(s) under tests/ - it is not reading provenance");

        Assert.True(missing.Count == 0,
            "issue ids claimed under tests/ with no row in " + IndexPath + ": "
            + string.Join("; ", missing.Select(e => $"{e.Key} ({string.Join(", ", e.Value)})")));
    }

    [Fact]
    public void A_fabricated_id_in_a_test_file_fails_the_guard()
    {
        // What the guard above is worth: an id nobody has indexed has to be reported, including
        // one abbreviated onto an id that IS indexed.
        if (TryFindRepositoryPath(IndexPath) is not string indexFile) return;
        var indexText = File.ReadAllText(indexFile);

        Assert.Equal(["GraphSDK-9999"],
            Unindexed(indexText, "/// (Corpus: GraphSDK-9999, an issue that does not exist.)"));

        Assert.Equal(["M365DSC-9999"],
            Unindexed(indexText, "/// (Corpus: M365DSC-7273/9999, one real id and one invented.)"));
    }

    [Fact]
    public void The_index_is_where_the_suite_says_it_is()
    {
        // A stale path here would read as an absent index and quietly turn all three guards
        // into no-ops on every host; a host that carries the index pins the path with this.
        if (TryFindRepositoryPath("tmp") is not string) return;
        Assert.True(TryFindRepositoryPath(IndexPath) is not null);
    }

    [Fact]
    public void The_walk_up_does_not_cross_the_repository_root()
    {
        // A decoy tmp/ sits above the repository root here - the exact shape of a worktree
        // checked out under the system temp directory, where the walk-up used to resolve the
        // system temp directory itself as the repository's tmp/. Anchoring on Mgx.slnx means
        // the decoy is never reached: "tests" resolves under the marked root, and "tmp" - which
        // exists only above the root - resolves to nothing.
        var sandbox = Path.Combine(Path.GetTempPath(), $"mgx-corpus-root-test-{Guid.NewGuid():N}");
        var repoRoot = Path.Combine(sandbox, "repo");
        var startDirectory = Path.Combine(repoRoot, "tests", "x", "bin", "Debug");
        try
        {
            Directory.CreateDirectory(Path.Combine(sandbox, "tmp"));
            Directory.CreateDirectory(repoRoot);
            File.WriteAllText(Path.Combine(repoRoot, "Mgx.slnx"), string.Empty);
            Directory.CreateDirectory(Path.Combine(repoRoot, "tests"));
            Directory.CreateDirectory(startDirectory);

            Assert.Equal(Path.Combine(repoRoot, "tests"), FindRepositoryPath("tests", startDirectory));
            Assert.Null(TryFindRepositoryPath("tmp", startDirectory));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
