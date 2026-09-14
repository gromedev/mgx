using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Both state files are written to "&lt;path&gt;.tmp" and renamed, so a crash cannot leave a
/// half-written file. A save that FAILS should not leave the staging file either - adoption and
/// recovery already have to reason about stray files beside the output.
/// </summary>
[Collection("Pipeline")]
public class AtomicSaveHygieneTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-atomic-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void A_failed_checkpoint_save_leaves_no_staging_file()
    {
        var dir = NewDir();
        var target = Path.Combine(dir, "run.checkpoint");
        try
        {
            // The rename cannot land on a directory, so the save fails after the staging file
            // has been written - the window this is about.
            Directory.CreateDirectory(target);

            var cp = new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=x",
                ItemsCollected = 1
            };
            Assert.ThrowsAny<Exception>(() => cp.Save(target));

            Assert.False(File.Exists(target + ".tmp"),
                "the staging file outlived the save that failed to promote it");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// <summary>
/// The three scratch names the engine writes beside a file it is keeping: the write probe
/// ValidateWriteAccess makes, and the "&lt;path&gt;.tmp" each atomic save stages through. All
/// three were written with an open that creates or truncates, so whatever stood at the name was
/// what the run wrote into: a symlink there took the write into the link's target - a file
/// outside the run's business, truncated or filled with state - and a FIFO with no reader
/// blocked the open for as long as the process lived, in one case inside BeginProcessing before
/// the run had anything a cancellation could unwind. The name is cleared first now, by the same
/// primitive the promotion's staging name goes through, and created by an open that refuses to
/// use anything already there.
/// </summary>
[Collection("Pipeline")]
public class ScratchNameHygieneTests
{
    /// <summary>Which of the three names a case is about.</summary>
    public enum Site
    {
        /// <summary>DeltaState.ValidateWriteAccess's "&lt;path&gt;.probe".</summary>
        WriteProbe,

        /// <summary>DeltaState.Save's "&lt;path&gt;.tmp".</summary>
        DeltaSave,

        /// <summary>PaginationCheckpoint.Save's "&lt;path&gt;.tmp".</summary>
        CheckpointSave,
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-scratch-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// The scratch name a site writes through, and the write itself. The paths are the ones the
    /// engine derives - Save normalizes before it appends the suffix - so a test cannot name a
    /// scratch file the run does not use.
    /// </summary>
    private static (string Scratch, string Target, Action Write) SiteUnderTest(Site site,
        string dir)
    {
        if (site == Site.CheckpointSave)
        {
            var target = Path.Combine(dir, "run.checkpoint");
            var checkpoint = new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
            };
            return (Path.GetFullPath(target) + ".tmp", target, () => checkpoint.Save(target));
        }

        var statePath = Path.Combine(dir, "state.json");
        if (site == Site.DeltaSave)
        {
            var state = new DeltaState
            {
                DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=T",
                Resource = "/users/delta",
            };
            return (Path.GetFullPath(statePath) + ".tmp", statePath, () => state.Save(statePath));
        }

        return (statePath + ".probe", statePath, () => DeltaState.ValidateWriteAccess(statePath));
    }

    private static void Mkfifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [path]);
        Assert.NotNull(mkfifo);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    /// <summary>
    /// A symlink at the scratch name. The link comes off the name and the file it pointed at is
    /// not touched - which is the whole of what a run can honestly do about a link it did not
    /// put there.
    /// </summary>
    [Theory]
    [InlineData(Site.WriteProbe)]
    [InlineData(Site.DeltaSave)]
    [InlineData(Site.CheckpointSave)]
    public void A_symlink_at_a_scratch_name_comes_off_it_and_its_target_is_untouched(Site site)
    {
        var dir = NewDir();
        try
        {
            var (scratch, target, write) = SiteUnderTest(site, dir);
            var victim = Path.Combine(dir, "SOMEBODY-ELSES.txt");
            var kept = "line one\nline two\nline three\n";
            File.WriteAllText(victim, kept);
            File.CreateSymbolicLink(scratch, victim);

            write();

            Assert.Equal(kept, File.ReadAllText(victim));
            Assert.False(File.Exists(scratch), "the scratch name outlived the write");
            if (site != Site.WriteProbe)
                Assert.True(File.Exists(target), "the file the save was keeping is not there");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A FIFO at the scratch name. Nothing is on the other end of it, so an open that waits for
    /// a peer waits for as long as the process lives - past SIGINT, measured - and the write
    /// runs on a background thread here so a regression cannot take the suite with it.
    /// </summary>
    [Theory]
    [InlineData(Site.WriteProbe)]
    [InlineData(Site.DeltaSave)]
    [InlineData(Site.CheckpointSave)]
    public void A_fifo_at_a_scratch_name_comes_off_it_without_wedging_the_write(Site site)
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        try
        {
            var (scratch, target, write) = SiteUnderTest(site, dir);
            Mkfifo(scratch);

            Exception? failed = null;
            var writer = new Thread(() =>
            {
                try { write(); }
                catch (Exception ex) { failed = ex; }
            })
            {
                // A thread parked in an open cannot be interrupted, so it must not be one the
                // process waits for on the way out.
                IsBackground = true,
            };
            writer.Start();

            Assert.True(writer.Join(TimeSpan.FromSeconds(20)),
                "the write is still waiting for a reader on the pipe at its scratch name");
            Assert.Null(failed);
            Assert.False(File.Exists(scratch), "the pipe outlived the write");
            if (site != Site.WriteProbe)
                Assert.True(File.Exists(target), "the file the save was keeping is not there");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Taking a scratch name back, under the handle that holds it. Released in front of the
    /// delete, the name is decided and unheld for as long as the discard takes, and a second run
    /// that sweeps it in that interval creates its own file at the name and has it unlinked from
    /// under the handle it was holding it by. The entry goes and the handle goes with it.
    /// </summary>
    [Fact]
    public void Discarding_a_scratch_name_takes_the_entry_and_the_handle_with_it()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "run.checkpoint.tmp");
            var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None);

            ScratchName.Discard(path, held);

            Assert.False(File.Exists(path), "the entry outlived the discard");
            Assert.Throws<ObjectDisposedException>(() => held.Length);

            // And a name nothing stands at is one a discard has nothing to say about.
            ScratchName.Discard(path, null);
            Assert.False(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Two runs over one -DeltaPath, or one -OutputFile, reach the probe together as a matter of
    /// course, and the name they both use is the same one. A file the other run holds there is
    /// its own probe - taken out a moment ago, in this very directory, which is the question
    /// this probe is asking - so it answers yes and leaves the file alone. Refused for the
    /// other run's sake, the probe ended a run in BeginProcessing over nothing that was wrong
    /// with the path.
    /// </summary>
    [Fact]
    public void A_probe_another_run_is_holding_answers_the_question_it_asks()
    {
        var dir = NewDir();
        try
        {
            var (scratch, _, write) = SiteUnderTest(Site.WriteProbe, dir);
            using var theirs = new FileStream(scratch, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None);

            write();

            Assert.True(File.Exists(scratch), "a live run's own probe was taken off the name");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Runs probing one path together. The probe name is derived from the path, so two runs over
    /// one -DeltaPath - or two over one -OutputFile - meet at it as a matter of course, and the
    /// probe is written to be passed by both: a name another run is holding answers the question
    /// this one came to ask. The refusal a create came back with was read off a second look at
    /// the name instead, which arrives after the run that made the entry has taken it away - so
    /// the second run saw a clear name, called the create's refusal the path's own, and ended in
    /// BeginProcessing over a path nothing was wrong with.
    /// <para>
    /// Windows has a second way to refuse them, and the run is not allowed to stop on that one
    /// either: a delete of the name that has been started and has not finished leaves the entry
    /// there with every open refused, so the next create - and the next sweep's unlink - meets
    /// ERROR_ACCESS_DENIED, which is also what a directory this account cannot write answers.
    /// Both are waited out and only a refusal that outlasts the wait is the path's own. At this
    /// density the state is reached constantly: over 40 runs of this shape it refused 154 creates
    /// and 64 unlinks, and every run of the 40 ended in BeginProcessing over a path nothing was
    /// wrong with.
    /// </para>
    /// </summary>
    [Fact]
    public void Runs_probing_one_path_together_do_not_refuse_each_other()
    {
        var dir = NewDir();
        try
        {
            var (_, _, write) = SiteUnderTest(Site.WriteProbe, dir);
            var failures = new Exception?[4];
            var threads = new Thread[failures.Length];
            using var go = new ManualResetEventSlim(false);
            for (var i = 0; i < threads.Length; i++)
            {
                var slot = i;
                threads[slot] = new Thread(() =>
                {
                    go.Wait(TimeSpan.FromSeconds(30));
                    for (var n = 0; n < 200 && failures[slot] == null; n++)
                    {
                        try { write(); }
                        catch (Exception ex) { failures[slot] = ex; }
                    }
                });
                threads[slot].Start();
            }
            go.Set();
            foreach (var t in threads)
                Assert.True(t.Join(TimeSpan.FromSeconds(60)), "a probing run never finished");

            Assert.All(failures, f => Assert.Null(f));
            Assert.Empty(Directory.GetFiles(dir, "*.probe"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A directory at the scratch name is not the run's to delete and could hold anything, so
    /// the write says so instead of failing on an open that cannot say what refused it. The
    /// probe's refusal is the write-access error it has always been, naming the scratch file and
    /// what stood there.
    /// </summary>
    [Fact]
    public void A_directory_at_the_probe_name_is_the_write_access_refusal()
    {
        var dir = NewDir();
        try
        {
            var (scratch, target, write) = SiteUnderTest(Site.WriteProbe, dir);
            Directory.CreateDirectory(scratch);

            var stop = Assert.Throws<InvalidOperationException>(write);
            Assert.Contains($"Cannot write to delta state path '{target}'", stop.Message);
            Assert.Contains($"a directory stands at '{Path.GetFileName(scratch)}'", stop.Message);
            Assert.True(Directory.Exists(scratch), "the refusal deleted the directory");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// <summary>
/// A delta state file from before this field existed carries no apiVersion, so the mismatch
/// check is skipped and the run proceeds against whatever version the stored deltaLink names.
/// Recording the REQUESTED version at that point stamped one the token was never issued by, and
/// every later run refused with advice pointing at the wrong version.
/// </summary>
[Collection("Pipeline")]
public class DeltaApiVersionStampTests
{
    private static string? Stamp(string? link)
    {
        var m = typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).GetMethod(
            "ApiVersionOfLink",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string?)m.Invoke(null, [link]);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc", "v1.0")]
    [InlineData("https://graph.microsoft.com/beta/users/delta?$deltatoken=abc", "beta")]
    public void The_version_comes_from_the_link(string link, string expected)
    {
        Assert.Equal(expected, Stamp(link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://graph.microsoft.com/v9.9/users/delta")]
    public void An_unreadable_link_falls_back_to_the_request(string? link)
    {
        Assert.Null(Stamp(link));
    }
}
