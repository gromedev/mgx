using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Serializes the test classes that set HeldEntry's claim seam, which is one static for the
/// whole process: run in parallel, one class's assignment is the other's, and tests written
/// against the instant between a claim and its check would fire each other's.
/// </summary>
[CollectionDefinition("ClaimSeam")]
public class ClaimSeamCollection;

/// <summary>
/// The question every claim on Unix is finished with: is the file this run holds still the file
/// standing at the name it asked for? A handle says nothing about that on its own - the lock
/// .NET takes for FileShare.None is granted several syscalls after the open that resolved the
/// name, and rename and unlink honor no lock - so the three ways a name moves under a handle are
/// asked about here, one test each.
/// <para>
/// Those three are Unix's alone, and not because the answer would differ: Windows refuses to
/// unlink or rename a file another handle has open at all, so a name cannot move under a claim
/// there and the three states cannot be reached to be asked about. That refusal is the whole
/// reason the primitive answers Windows at the open and asks the platform nothing.
/// </para>
/// </summary>
[Collection("ClaimSeam")]
public class HeldEntryTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-held-{Guid.NewGuid():N}")).FullName;

    private static FileStream Claim(string path) =>
        new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

    /// <summary>A file nothing has touched since the claim: the name is still this handle's.</summary>
    [Fact]
    public void A_handle_on_the_file_at_the_name_still_stands_there()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "out.jsonl");
            using var held = Claim(path);
            held.Write("{\"id\":\"a\"}\n"u8);
            held.Flush();

            Assert.True(HeldEntry.StillStandsAt(held, path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Unlinked under the handle, which is what a second run's sweep of the name does: the inode
    /// is this run's for as long as it holds it and has no name left on it at all. The file still
    /// reads and still takes writes, and every one of them is into a file no name reaches.
    /// </summary>
    [Fact]
    public void A_file_unlinked_under_the_handle_no_longer_stands_at_the_name()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "out.jsonl");
            using var held = Claim(path);
            File.Delete(path);

            Assert.False(HeldEntry.StillStandsAt(held, path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Renamed away, which leaves the handle's own inode with a name - just not this one. Nothing
    /// about the file has changed and the name reaches nothing.
    /// </summary>
    [Fact]
    public void A_file_renamed_away_no_longer_stands_at_the_name()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "out.jsonl");
            using var held = Claim(path);
            File.Move(path, Path.Combine(dir, "elsewhere.jsonl"));

            Assert.False(HeldEntry.StillStandsAt(held, path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A second run's file standing at the name instead, with this run's own inode still named
    /// somewhere else: the one case a link count cannot answer. The name exists, the handle is
    /// open over a file with a name on it, and they are two different files - which is exactly
    /// the state a promotion whose claim was granted late came away in.
    /// </summary>
    [Fact]
    public void Another_file_at_the_name_is_not_the_one_the_handle_holds()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "out.jsonl");
            using var held = Claim(path);
            File.Move(path, Path.Combine(dir, "elsewhere.jsonl"));
            File.WriteAllText(path, "{\"id\":\"theirs\"}\n");

            Assert.False(HeldEntry.StillStandsAt(held, path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// And the seam the deterministic tests of the rule are written against fires with the path
    /// that was claimed, in the instant before the question is asked. One static for the whole
    /// process, so it hears every claim any run in it takes: what a test written against it may
    /// say anything about is its own name, which is what this counts.
    /// </summary>
    [Fact]
    public void The_seam_fires_with_the_path_the_claim_was_taken_on()
    {
        var dir = NewDir();
        var seen = new List<string>();
        try
        {
            var path = Path.Combine(dir, "out.jsonl");
            using var held = Claim(path);
            HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (string.Equals(claimed, path, StringComparison.Ordinal)) seen.Add(claimed);
            };

            Assert.True(HeldEntry.StillStandsAt(held, path));
            Assert.Equal([path], seen);
        }
        finally
        {
            HeldEntry.BeforeTheClaimIsVerified = null;
            Directory.Delete(dir, true);
        }
    }
}
