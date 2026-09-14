using System.Reflection;

namespace Mgx.IntegrationTests.Engine.Pagination;

/// <summary>
/// Promotion of the temp a checkpoint names, which is how every checkpoint this release writes
/// recovers: the file is taken by name and by the length recorded for it, so nothing has to be
/// guessed about which run's items are in it. What the name does not say is whether that run is
/// finished. Promotion replaces the output with those bytes and then unlinks the temp, so the
/// same claim adoption takes in OrphanAdoptionTests has to be taken here - a file nobody can be
/// found holding is an interrupted run's leftovers, and one that cannot be claimed belongs to a
/// run that is still writing it.
/// </summary>
[Collection("ClaimSeam")]
public class NamedTempPromotionTests
{
    // An instance method, because what follows a promotion is an append onto the file it landed
    // in: the run keeps that file from the move that puts the rows there until its writing ends,
    // and the handle lives on the cmdlet.
    private static readonly MethodInfo Promote =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TryPromoteNamedTemp", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo CanPromote =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "CanPromoteNamedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// The route a resumed run takes the output by, which is the shortest way to a claim whose
    /// caller lets the handle go on its own path rather than in a finally: it asks for the file,
    /// reads the claim's answer, and keeps nothing where the answer is not Taken.
    /// </summary>
    private static readonly MethodInfo Resume =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TakeOutputForCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// A directory a run may write in, which is how the tests below find every directory and how
    /// they leave it.
    /// </summary>
    private const UnixFileMode Searchable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// And one that answers for every name in it and takes no new file: the mode a create is
    /// refused by, with the stat of the name it was refused at still answering.
    /// </summary>
    private const UnixFileMode ReadAndSearch = UnixFileMode.UserRead | UnixFileMode.UserExecute;

    /// <summary>
    /// The mode on a directory. Set through here rather than at each site, because the claim
    /// seam below is a lambda and a lambda is its own body to the platform analyzer: it cannot
    /// see the Windows guard the test opened with, and the guard has to be in front of the call.
    /// </summary>
    private static void SetMode(string dir, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(dir, mode);
    }

    /// <summary>
    /// The promotion's answer, by name: the enum it returns is protected on the cmdlet base, so
    /// a test outside that hierarchy cannot name the type. "Taken" is a promotion that landed
    /// and kept the output it landed in; every other answer left both files where they were.
    /// </summary>
    private static string Invoke(Mgx.Cmdlets.Base.MgxCmdletBase run,
        string outputPath, string tempFileName, long dataLength) =>
        Promote.Invoke(run, Slots(Promote, outputPath, tempFileName, dataLength))!.ToString()!;

    /// <summary>
    /// The three arguments, followed by an empty slot for each out parameter the method carries.
    /// Reflection wants a slot for every one of them and this suite asks about them by position
    /// rather than by count - the reason a refusal carries at 3, the staging failure at 4 -
    /// so the array is sized off the method itself.
    /// </summary>
    private static object?[] Slots(MethodInfo method, string outputPath, string tempFileName,
        long dataLength)
    {
        var slots = new object?[method.GetParameters().Length];
        slots[0] = outputPath;
        slots[1] = tempFileName;
        slots[2] = dataLength;
        return slots;
    }

    /// <summary>
    /// The preview's answer, by the same name. It reports what the applying pass would answer -
    /// which of "Taken" and "NotPromoted" a temp claim gives it, and the two refusals the output
    /// standing at the path gives it - and promotes nothing to find out.
    /// </summary>
    private static string InvokeCan(string outputPath, string tempFileName, long dataLength) =>
        CanPromote.Invoke(null, Slots(CanPromote, outputPath, tempFileName, dataLength))!
            .ToString()!;

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"mgx-promote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Reads back a file the run standing in for a second export still holds open to write it.
    /// Windows checks the sharing both ways round - what the reader asks of the file, and what
    /// the reader will let a handle already on it go on doing - so File.ReadAllLines, which asks
    /// for FileShare.Read and nothing more, is refused by the writer this test put there itself.
    /// The sharing the holder asked for is offered back, which is what a reader of a file a live
    /// run is appending to has to do on either platform.
    /// </summary>
    private static string[] ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    /// <summary>
    /// The state a crash leaves: the previous run's output, this run's items in the temp beside
    /// it, and a checkpoint naming both. The temp replaces the output, trimmed to the length
    /// recorded, and goes.
    /// </summary>
    [Fact]
    public void Promotes_a_named_temp_nothing_holds()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            // The writer's position at the last flush, which is what a checkpoint records.
            var counted = new FileInfo(temp).Length;
            File.AppendAllText(temp, "{\"id\":\"after-the-flush\"}\n");

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            Assert.Equal("Taken", InvokeCan(output, Path.GetFileName(temp), counted));
            Assert.Equal("Taken", Invoke(run, output, Path.GetFileName(temp), counted));

            // The rows are read once the run that took the file lets it go, which is what
            // disposing it does: while the hold stands, a reader of that file is refused.
            Assert.ThrowsAny<IOException>(() => File.ReadAllLines(output));
            run.Dispose();

            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The same three files, with the run that wrote the temp still writing it. Promotion
    /// copied its rows into this run's output and unlinked the file underneath it, so that run
    /// wrote into an unlinked inode and its own closing move failed against a name that was no
    /// longer there. Being named by a checkpoint says which file holds the items; it says
    /// nothing about whether anyone still has it open.
    /// </summary>
    [Fact]
    public void Refuses_a_named_temp_a_live_run_still_holds()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var live = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");

            // Opened the way an export opens its own temp: new StreamWriter(path, append).
            using (var writer = new StreamWriter(live, append: false))
            {
                writer.WriteLine("{\"id\":\"a\"}");
                writer.WriteLine("{\"id\":\"b\"}");
                writer.Flush();
                var counted = new FileInfo(live).Length;
                Assert.True(counted > 0);

                using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
                Assert.Equal("NotPromoted", InvokeCan(output, Path.GetFileName(live), counted));
                Assert.Equal("NotPromoted",
                    Invoke(run, output, Path.GetFileName(live), counted));

                // Nothing was promoted, so nothing was taken either: the output is the previous
                // run's, unheld, and readable.
                Assert.Equal(["{\"id\":\"old\"}"], File.ReadAllLines(output));
                Assert.False(File.Exists($"{output}.adopt"));
                Assert.True(File.Exists(live), "a running export's temp was promoted out from under it");

                // The run that owns it goes on writing into the file it still has.
                writer.WriteLine("{\"id\":\"c\"}");
            }

            Assert.Equal(
                ["{\"id\":\"a\"}", "{\"id\":\"b\"}", "{\"id\":\"c\"}"],
                File.ReadAllLines(live));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The third file. A promotion replaces the output and then appends onto what it replaced,
    /// so the output another run is holding is one it must not replace at all - and a rename
    /// asks nothing of the lock: the move went through, the holder's next write landed in an
    /// inode with no name on it, and the claim taken afterwards answered Taken about a file one
    /// statement old. The output is asked for before the move now, and a held one is the whole
    /// answer.
    /// </summary>
    [Fact]
    public void Refuses_an_output_another_run_holds_and_replaces_nothing()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"theirs\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBefore = File.ReadAllBytes(temp);

            // The run that has it: appending, sharing reads, the way a resumed run holds it.
            using var live = new StreamWriter(
                new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            { AutoFlush = true };
            live.BaseStream.Seek(0, SeekOrigin.End);

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            Assert.Equal("OutputHeld", InvokeCan(output, Path.GetFileName(temp), counted));
            Assert.Equal("OutputHeld", Invoke(run, output, Path.GetFileName(temp), counted));

            // Nothing was replaced, nothing was staged, and the temp the checkpoint names is
            // still there with the checkpoint's own bytes in it.
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.False(File.Exists($"{output}.adopt"));

            // The same inode, not a new file of the same name: the holder's next write is read
            // back through the path.
            live.WriteLine("{\"id\":\"theirs-2\"}");
            Assert.Equal(
                ["{\"id\":\"theirs\"}", "{\"id\":\"theirs-2\"}"],
                ReadSharedLines(output));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The other refusal, which nothing could reach before: an output path that is not a file
    /// this run can write at an offset. A FIFO opens read-write and answers no length, and the
    /// move would have replaced it with a regular file - so the caller's pipe, and whatever is
    /// reading it, would be gone.
    /// </summary>
    [Fact]
    public void Refuses_an_output_it_cannot_open_and_replaces_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        try
        {
            using (var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [output]))
            {
                Assert.NotNull(mkfifo);
                mkfifo.WaitForExit();
                Assert.Equal(0, mkfifo.ExitCode);
            }

            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBefore = File.ReadAllBytes(temp);

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            Assert.Equal("OutputUnopenable", InvokeCan(output, Path.GetFileName(temp), counted));
            Assert.Equal("OutputUnopenable", Invoke(run, output, Path.GetFileName(temp), counted));

            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.False(File.Exists($"{output}.adopt"));

            // Still a pipe, and still nobody's: a stream with no offset in it is what says so,
            // and a claim of this test's own would be refused if the run had kept one.
            using var stillAFifo = new FileStream(output, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
            Assert.False(stillAFifo.CanSeek, "the move replaced the caller's FIFO with a file");
        }
        finally
        {
            try { File.Delete(output); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A temp big enough that the copy beside the output takes long enough to be watched, so a
    /// question can be asked of the output path while the promotion is in the middle of staging.
    /// </summary>
    private static long WriteTempOfAtLeast(string temp, long bytes)
    {
        const string row = "{\"id\":\"0000000000000000000000000000000000000000000000000000\"}\n";
        var block = string.Concat(Enumerable.Repeat(row, 1024));
        using (var writer = new StreamWriter(temp, append: false))
            while (writer.BaseStream.Length < bytes)
                writer.Write(block);
        return new FileInfo(temp).Length;
    }

    /// <summary>
    /// The interval a claim released in front of the rename left open. The promotion asked for
    /// the standing output and let it go, and only then renamed over it - and rename(2) honors no
    /// lock on either side of it, so a run that took the output in between had it replaced under
    /// it: its rows in an inode with no name left on them, and no warning on any stream. Widened
    /// to 600 ms by a gate, a second process took the output there.
    ///
    /// The handle is kept now, from before the copy is staged through the move, so the interval
    /// is one nothing can claim the output in. Measured over the whole of it: the staging file
    /// appearing is what says the promotion has begun, and from that instant until it answers,
    /// every claim on the output is refused. flock belongs to the open file description, so a
    /// second open from this very process is refused exactly as another process's is - which is
    /// also why the promotion never asks for the output again on the far side of the move.
    /// </summary>
    [Fact]
    public void Nothing_can_claim_the_output_between_the_standing_claim_and_the_move()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        using var promoting = new ManualResetEventSlim(false);
        Thread? racer = null;
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"theirs\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            var counted = WriteTempOfAtLeast(temp, 64L * 1024 * 1024);
            var adopt = $"{output}.adopt";

            // Spins on the output for as long as the promotion is staging or moving, and comes
            // back with whether the path was ever anybody's to take. It starts asking when the
            // staged file appears, which is the first instant a promotion has reached.
            var claimed = false;
            racer = new Thread(() =>
            {
                while (!File.Exists(adopt) && !promoting.IsSet) Thread.SpinWait(64);
                while (!promoting.IsSet)
                {
                    try
                    {
                        using var mine = new FileStream(output, FileMode.Open,
                            FileAccess.ReadWrite, FileShare.None);
                        // A claim granted has to be a claim on the output, and one taken here
                        // is not always: the open and the lock behind it are two syscalls, and
                        // a thread descheduled between them comes back holding the inode the
                        // rename has since unnamed - a file at no path, whose lock the
                        // promotion let go on its way out. The staged file still standing and
                        // the claimed length still the path's are what say otherwise.
                        if (!File.Exists(adopt) || mine.Length != new FileInfo(output).Length)
                            continue;
                        claimed = true;
                        return;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            });
            racer.Start();

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var answer = Invoke(run, output, Path.GetFileName(temp), counted);
            promoting.Set();
            Assert.True(racer.Join(TimeSpan.FromSeconds(30)), "the racing claim never finished");

            Assert.Equal("Taken", answer);
            Assert.False(claimed,
                "the output was claimable between the standing claim and the rename");

            // And the run that promoted has it: the rows are readable once it lets go.
            run.Dispose();
            Assert.Equal(counted, new FileInfo(output).Length);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.False(File.Exists(adopt));
        }
        finally
        {
            promoting.Set();
            racer?.Join(TimeSpan.FromSeconds(30));
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A promotion that stops on the output staged the whole temp beside it first and then took
    /// the copy back - 172 ms and a moved directory mtime for a 256 MiB temp, for a run that
    /// replaces nothing. The output is asked for before any of it now, so a stop costs the
    /// directory beside it nothing at all: no entry appears under the staging name, and the
    /// directory's own mtime is where the run found it.
    /// </summary>
    [Fact]
    public void A_held_output_stops_the_promotion_before_a_byte_is_staged()
    {
        var dir = NewDir();
        using var stopped = new ManualResetEventSlim(false);
        Thread? watcher = null;
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"theirs\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            var counted = WriteTempOfAtLeast(temp, 64L * 1024 * 1024);
            var adopt = $"{output}.adopt";
            var tempBefore = new FileInfo(temp).Length;

            // The run that has the output: appending, sharing reads, the way a resumed run holds
            // it. Its claim is what the promotion stops on.
            using var live = new StreamWriter(
                new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            { AutoFlush = true };
            live.BaseStream.Seek(0, SeekOrigin.End);

            // Whether the staging name is ever on disk. A create and a delete inside one tick
            // would leave the directory's mtime moved and nothing to see in it, so the entry is
            // watched for as well as measured after.
            var sawStaging = false;
            watcher = new Thread(() =>
            {
                while (!stopped.IsSet)
                {
                    if (File.Exists(adopt)) { sawStaging = true; return; }
                    Thread.SpinWait(64);
                }
            });
            watcher.Start();

            var directoryBefore = Directory.GetLastWriteTimeUtc(dir);
            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var answer = Invoke(run, output, Path.GetFileName(temp), counted);
            stopped.Set();
            Assert.True(watcher.Join(TimeSpan.FromSeconds(30)), "the staging watch never finished");

            Assert.Equal("OutputHeld", answer);
            Assert.False(sawStaging, "the stop staged a copy of the temp beside the output");
            Assert.Equal(directoryBefore, Directory.GetLastWriteTimeUtc(dir));
            Assert.False(File.Exists(adopt));
            Assert.Equal(tempBefore, new FileInfo(temp).Length);

            // The same inode, not a new file wearing the name.
            live.WriteLine("{\"id\":\"theirs-2\"}");
            Assert.Equal(["{\"id\":\"theirs\"}", "{\"id\":\"theirs-2\"}"], ReadSharedLines(output));
        }
        finally
        {
            stopped.Set();
            watcher?.Join(TimeSpan.FromSeconds(30));
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Which errno the claim reads as another run holding the file. .NET backs FileShare.None on
    /// Unix with a non-blocking flock, and a refused LOCK_EX comes back EWOULDBLOCK - 35 on
    /// macOS, 11 on Linux, where the name EAGAIN shares that value. Read as a union of the two,
    /// each platform also read its own EDEADLK as a sharing violation: 11 is EDEADLK on macOS
    /// and 35 is EDEADLK on Linux, so a deadlock the kernel had just refused to enter was
    /// reported as an export to wait for. Each number is read on the platform that uses it.
    /// </summary>
    [Fact]
    public void The_sharing_errno_is_the_one_this_platform_uses()
    {
        var isSharingViolation = typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "IsSharingViolation", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Reads(int hResult) =>
            (bool)isSharingViolation.Invoke(null, [new IOException("errno", hResult)])!;

        Assert.Equal(OperatingSystem.IsMacOS(), Reads(35));
        Assert.Equal(OperatingSystem.IsLinux(), Reads(11));

        // Windows carries the win32 code in the low half of an HRESULT, and those two are read
        // wherever they turn up.
        Assert.True(Reads(unchecked((int)0x80070020)));
        Assert.True(Reads(unchecked((int)0x80070021)));

        // And the failures that are not a second run at all: ELOOP, ENOTSUP for a socket, and
        // the win32-shaped HRESULT .NET maps an over-long path to by hand.
        Assert.False(Reads(62));
        Assert.False(Reads(45));
        Assert.False(Reads(unchecked((int)0x800700CE)));
    }

    /// <summary>
    /// The staging failure a promotion answers with, beside its answer: the reason a caller
    /// quotes in the stop it writes.
    /// </summary>
    private static (string Answer, string Reason) InvokeForStaging(
        Mgx.Cmdlets.Base.MgxCmdletBase run, string outputPath, string tempFileName, long dataLength)
    {
        var args = Slots(Promote, outputPath, tempFileName, dataLength);
        var answer = Promote.Invoke(run, args)!.ToString()!;
        return (answer, args[4]?.ToString() ?? "<none>");
    }

    /// <inheritdoc cref="InvokeForStaging"/>
    private static (string Answer, string Reason) InvokeCanForStaging(
        string outputPath, string tempFileName, long dataLength)
    {
        var args = Slots(CanPromote, outputPath, tempFileName, dataLength);
        var answer = CanPromote.Invoke(null, args)!.ToString()!;
        return (answer, args[4]?.ToString() ?? "<none>");
    }

    private static void Mkfifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [path]);
        Assert.NotNull(mkfifo);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    private static void Symlink(string target, string link)
    {
        using var ln = System.Diagnostics.Process.Start("/bin/ln", ["-s", target, link]);
        Assert.NotNull(ln);
        ln.WaitForExit();
        Assert.Equal(0, ln.ExitCode);
    }

    /// <summary>
    /// A second directory entry for a file's own inode. Not a symlink: nothing on the entry says
    /// it is an alias, and unlinking it takes the name and leaves the file.
    /// </summary>
    private static void HardLink(string target, string link)
    {
        using var ln = System.Diagnostics.Process.Start("/bin/ln", [target, link]);
        Assert.NotNull(ln);
        ln.WaitForExit();
        Assert.Equal(0, ln.ExitCode);
    }

    /// <summary>
    /// A FIFO at the staging name. The copy was staged with FileMode.Create and
    /// FileAccess.Write, which is O_WRONLY, and open(2) on a FIFO with no reader blocks until
    /// one arrives - so the promotion never returned, holding the temp's claim and the output's
    /// lock for as long as the process lived, with StopProcessing unable to reach it and a
    /// second run refused on the output. The name is swept before it is created now, and a FIFO
    /// standing there is one of the kinds that goes.
    /// </summary>
    [Fact]
    public void Sweeps_a_fifo_off_the_staging_name_and_promotes_without_blocking()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var adopt = $"{output}.adopt";
            Mkfifo(adopt);

            // On its own thread with a watchdog: the failure this pins is a promotion that does
            // not come back at all, and a test that called it inline would hang the suite.
            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            string? answer = null;
            var promoting = new Thread(() =>
            {
                try { answer = Invoke(run, output, Path.GetFileName(temp), counted); }
                catch (Exception ex) { answer = $"threw {ex.GetType().Name}"; }
            }) { IsBackground = true };
            promoting.Start();
            Assert.True(promoting.Join(TimeSpan.FromSeconds(15)),
                "the promotion never returned: the staging open is waiting for a reader on the FIFO");

            Assert.Equal("Taken", answer);
            run.Dispose();

            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists(adopt));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            // A regular file at the output path, not the pipe that stood beside it: a seekable
            // handle is what says so.
            using var landed = new FileStream(output, FileMode.Open, FileAccess.Read);
            Assert.True(landed.CanSeek);
        }
        finally
        {
            try { File.Delete($"{Path.Combine(dir, "out.jsonl")}.adopt"); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A symlink at the staging name, pointing at a file outside the output's directory. The
    /// staging followed it: the copy went into the link's target, truncating a file no export
    /// had ever written, and the rename then moved the LINK onto the output path - so the
    /// output was a symlink to somebody's file and both passes answered as though the promotion
    /// had landed. The sweep takes the link off the name, and never what it pointed at.
    /// </summary>
    [Fact]
    public void Sweeps_a_symlink_off_the_staging_name_and_leaves_its_target_alone()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var elsewhere = NewDir();
        try
        {
            var victim = Path.Combine(elsewhere, "precious.conf");
            var victimBytes = "KEEP-THIS-EXACTLY\n"u8.ToArray();
            File.WriteAllBytes(victim, victimBytes);

            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var adopt = $"{output}.adopt";
            Symlink(victim, adopt);

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            Assert.Equal("Taken", Invoke(run, output, Path.GetFileName(temp), counted));
            run.Dispose();

            Assert.Equal(victimBytes, File.ReadAllBytes(victim));
            Assert.True(File.Exists(victim), "the sweep deleted what the link pointed at");
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Null(new FileInfo(output).LinkTarget);
            Assert.False(File.Exists(adopt));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(elsewhere, true); } catch { }
        }
    }

    /// <summary>
    /// A dangling link at the staging name - the kind the sweep answered Absent about and left
    /// standing, and the create then followed to a name in a directory that may not even be
    /// there. Read off the entry rather than through an open, so a link with nothing behind it
    /// is still a link.
    /// </summary>
    [Fact]
    public void Sweeps_a_dangling_link_off_the_staging_name()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var adopt = $"{output}.adopt";
            Symlink(Path.Combine(dir, "no-such-file"), adopt);

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            Assert.Equal("Taken", Invoke(run, output, Path.GetFileName(temp), counted));
            run.Dispose();

            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists(Path.Combine(dir, "no-such-file")),
                "the create made the file the dangling link pointed at");
            Assert.False(File.Exists(adopt));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A directory at the staging name, which is the one kind the sweep does not remove: it is
    /// not this run's to delete and could hold anything. Both passes stop there, and both say a
    /// directory - the applying pass built its reason from the raw exception, so the same disk
    /// state read "Access to the path ... is denied" in the run and "a directory stands at the
    /// path" in the preview.
    /// </summary>
    [Fact]
    public void A_directory_at_the_staging_name_stops_both_passes_saying_a_directory()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var outputBytes = "{\"id\":\"old\"}\n"u8.ToArray();
            File.WriteAllBytes(output, outputBytes);
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);
            var adopt = $"{output}.adopt";
            Directory.CreateDirectory(adopt);
            File.WriteAllText(Path.Combine(adopt, "somebody-elses"), "keep\n");

            var preview = InvokeCanForStaging(output, Path.GetFileName(temp), counted);
            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            Assert.Equal("StagingFailed", preview.Answer);
            Assert.Equal("StagingFailed", applied.Answer);
            Assert.Contains("a directory stands at the path", preview.Reason);
            Assert.Equal(preview.Reason, applied.Reason);

            // The one wording carries no sentence-ending period of its own: the caller quotes
            // it inside a sentence it punctuates itself.
            Assert.DoesNotContain("denied", applied.Reason);
            Assert.False(applied.Reason.TrimEnd().EndsWith('.'));

            // Nothing moved: not the output, not the temp, and not what the directory held.
            Assert.Equal(outputBytes, File.ReadAllBytes(output));
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
            Assert.True(Directory.Exists(adopt));
            Assert.Equal("keep\n", File.ReadAllText(Path.Combine(adopt, "somebody-elses")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A second run's handle on the staged copy. The discard was guarded on this run having
    /// opened the name for writing, and FileMode.Create opened whatever stood there - so a
    /// promotion that stopped unlinked the copy another run was holding across its own rename,
    /// leaving that run writing into a file at no path. The create refuses a name that already
    /// exists now, so the promotion stops before it has anything to discard.
    /// </summary>
    [Fact]
    public void A_held_staging_name_stops_the_promotion_and_keeps_the_holder_s_file()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var adopt = $"{output}.adopt";

            // The other run, holding the copy it has just written the way a promotion holds it
            // across the rename.
            using var holder = new FileStream(adopt, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None);
            holder.Write("{\"id\":\"theirs\"}\n"u8);
            holder.Flush();
            var holderBytes = holder.Length;

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            Assert.Equal("StagingFailed", applied.Answer);
            Assert.Contains("another run has the staged copy open", applied.Reason);

            // The holder still has a file, at the name it wrote it under, with its own bytes.
            Assert.True(File.Exists(adopt), "the stop unlinked the copy another run was holding");
            Assert.Equal(holderBytes, holder.Length);

            // Read back through the handle, since it is the only thing that can read the file:
            // the hold is FileShare.None, which is what a promotion holds its copy under.
            var theirs = new byte[holderBytes];
            holder.Position = 0;
            Assert.Equal((int)holderBytes, holder.Read(theirs));
            Assert.Equal("{\"id\":\"theirs\"}\n"u8.ToArray(), theirs);
            Assert.Equal(["{\"id\":\"old\"}"], File.ReadAllLines(output));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The other route that stages under the same name: the adoption of a temp left by a run
    /// whose checkpoint predates the recorded name and length. It wrote its copy with
    /// StreamWriter(path, append: false), which truncates whatever stands at the name and
    /// follows a link there like every other open of a path.
    /// </summary>
    [Fact]
    public void Adoption_sweeps_a_symlinked_staging_name_and_leaves_its_target_alone()
    {
        if (OperatingSystem.IsWindows()) return;

        var adopt = typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TryAdoptOrphanedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

        var dir = NewDir();
        var elsewhere = NewDir();
        try
        {
            var victim = Path.Combine(elsewhere, "precious.conf");
            var victimBytes = "KEEP-THIS-EXACTLY\n"u8.ToArray();
            File.WriteAllBytes(victim, victimBytes);

            // No output at all, which is the only state adoption runs in.
            var output = Path.Combine(dir, "out.jsonl");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            Symlink(victim, $"{output}.adopt");

            var slots = new object?[adopt.GetParameters().Length];
            slots[0] = output;
            slots[1] = 2L;
            Assert.True((bool)adopt.Invoke(null, slots)!);

            Assert.Equal(victimBytes, File.ReadAllBytes(victim));
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Null(new FileInfo(output).LinkTarget);
            Assert.False(File.Exists($"{output}.adopt"));
            Assert.False(File.Exists(temp));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(elsewhere, true); } catch { }
        }
    }

    private static void Chmod(string mode, string path)
    {
        using var chmod = System.Diagnostics.Process.Start("/bin/chmod", [mode, path]);
        Assert.NotNull(chmod);
        chmod.WaitForExit();
        Assert.Equal(0, chmod.ExitCode);
    }

    /// <summary>
    /// An output directory this account may read and not write. The staging is a create in that
    /// directory and a real run stops on it, and the preview only asked whether something
    /// already stood at the staging name: it found nothing, answered that the run would recover
    /// the items, and the run then stopped on the write the gate had just promised. The preview
    /// makes the create too, at no length, and both passes fail on the same open with the same
    /// reason.
    /// </summary>
    [Fact]
    public void A_read_only_output_directory_stops_both_passes_on_the_create()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var outputBytes = "{\"id\":\"old\"}\n"u8.ToArray();
            File.WriteAllBytes(output, outputBytes);
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);
            var adopt = $"{output}.adopt";
            var entriesBefore = Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Chmod("555", dir);

            var preview = InvokeCanForStaging(output, Path.GetFileName(temp), counted);
            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            Assert.Equal("StagingFailed", preview.Answer);
            Assert.Equal("StagingFailed", applied.Answer);
            Assert.Equal(preview.Reason, applied.Reason);
            Assert.Contains(adopt, applied.Reason);

            Chmod("755", dir);
            Assert.Equal(outputBytes, File.ReadAllBytes(output));
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
            Assert.False(File.Exists(adopt));
            Assert.Equal(entriesBefore, Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try { Chmod("755", dir); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The reason both passes carry has no sentence-ending period of its own, whichever site
    /// built it: the caller quotes it inside a sentence and punctuates it there, and a
    /// permission failure's runtime text ends in one - so the stop read "...is denied.. The
    /// resume checkpoint..." wherever it was not taken off. One refinement reads the runtime's
    /// words now, for the claim on a file and for the create a promotion stages with alike.
    /// </summary>
    [Fact]
    public void The_staging_reason_carries_no_period_of_its_own()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            Chmod("555", dir);

            var preview = InvokeCanForStaging(output, Path.GetFileName(temp), counted);
            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();
            Chmod("755", dir);

            // The record prints as "StagingFailure { Path = ..., Reason = ... }", so the reason
            // is read out of it rather than off the end of the whole line.
            const string opening = "Reason = ";
            foreach (var (pass, printed) in new[]
                     { ("preview", preview.Reason), ("run", applied.Reason) })
            {
                var at = printed.LastIndexOf(opening, StringComparison.Ordinal);
                Assert.True(at >= 0, $"the {pass} carried no staging reason: {printed}");
                var reason = printed[(at + opening.Length)..].TrimEnd('}', ' ');
                Assert.NotEmpty(reason);
                Assert.False(reason.EndsWith('.'),
                    $"the {pass}'s staging reason ends in a period of its own: '{reason}'");
            }
        }
        finally
        {
            try { Chmod("755", dir); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What the preview leaves at the staging name where the promotion would have gone through:
    /// nothing. It makes the create a run makes - which is what tells it a directory it cannot
    /// write in from one it can - and takes the entry straight back, so the directory holds
    /// what it held and the two files the checkpoint stands for are untouched.
    /// </summary>
    [Fact]
    public void The_preview_leaves_no_staged_copy_behind()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var outputBytes = "{\"id\":\"old\"}\n"u8.ToArray();
            File.WriteAllBytes(output, outputBytes);
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);
            var adopt = $"{output}.adopt";
            var entriesBefore = Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.Equal("Taken", InvokeCan(output, Path.GetFileName(temp), counted));

            Assert.False(File.Exists(adopt));
            Assert.Equal(entriesBefore, Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(outputBytes, File.ReadAllBytes(output));
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));

            // And the promotion the preview described still lands afterwards.
            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            Assert.Equal("Taken", Invoke(run, output, Path.GetFileName(temp), counted));
            run.Dispose();
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A hard link at the staging name, standing for the output the promotion is about to
    /// replace. Nothing on the entry says it is an alias - a hard link is a name, not a link -
    /// so what the sweep asks about is the output's own inode, and the run had already claimed
    /// that inode by the time it asked: a second open file description over a locked file is
    /// refused even from this process, so the alias answered "another run has the staged copy
    /// open" and the promotion stopped on a name nothing but itself was holding. The sweep runs
    /// before either claim now, so the alias answers its claim and comes off the name - in the
    /// run, which is the pass that clears the name; the preview names it and leaves it there.
    /// </summary>
    [Fact]
    public void Sweeps_a_hard_link_to_the_output_off_the_staging_name_and_promotes()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var outputBytes = "{\"id\":\"old\"}\n"u8.ToArray();
            File.WriteAllBytes(output, outputBytes);
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);
            var adopt = $"{output}.adopt";

            HardLink(output, adopt);
            Assert.Equal("Taken", InvokeCan(output, Path.GetFileName(temp), counted));

            // The preview holds neither file, so the alias answers a claim rather than reading
            // as a staged copy another run has open - and it is left standing, since a preview
            // removes nothing. What it names is where it was either way.
            Assert.True(File.Exists(adopt), "the preview removed what a run would remove");
            Assert.Equal(outputBytes, File.ReadAllBytes(output));
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            Assert.Equal("Taken", applied.Answer);
            Assert.Equal("<none>", applied.Reason);
            Assert.False(File.Exists(adopt));
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same alias, one file over: a hard link at the staging name for the temp the
    /// checkpoint names. The claim on the temp is the first one a promotion takes and the one it
    /// holds longest, so this is the shape the old order refused hardest - and the temp itself
    /// is never what the sweep removes, only the extra name.
    /// </summary>
    [Fact]
    public void Sweeps_a_hard_link_to_the_temp_off_the_staging_name_and_promotes()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var outputBytes = "{\"id\":\"old\"}\n"u8.ToArray();
            File.WriteAllBytes(output, outputBytes);
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);
            var adopt = $"{output}.adopt";

            HardLink(temp, adopt);
            Assert.Equal("Taken", InvokeCan(output, Path.GetFileName(temp), counted));

            Assert.True(File.Exists(adopt), "the preview removed what a run would remove");
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
            Assert.Equal(outputBytes, File.ReadAllBytes(output));

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            Assert.Equal("Taken", applied.Answer);
            Assert.Equal("<none>", applied.Reason);
            Assert.False(File.Exists(adopt));
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Two promotions over one output, released together, with a copy left at the staging name
    /// by a run killed between the write and the rename. The sweep asked for that copy, let the
    /// claim go, and unlinked the name afterwards - and what the other promotion does in between
    /// is sweep the same copy, create its own at the name and hold it, so the stale unlink took
    /// the second run's staged file out from under its handle and that run stopped on a name
    /// that was no longer there. The claim is held across the unlink now, which refuses the
    /// second sweep outright: whichever run gets there second either lands its own promotion or
    /// stops with its temp where it was, and neither loses a file to the other.
    /// </summary>
    [Fact]
    public void Two_promotions_over_one_leftover_never_unlink_each_others_files()
    {
        if (OperatingSystem.IsWindows()) return;

        for (var iteration = 0; iteration < 40; iteration++)
        {
            var dir = NewDir();
            try
            {
                var output = Path.Combine(dir, "out.jsonl");
                File.WriteAllText(output, "{\"id\":\"old\"}\n");
                var tempA = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
                var tempB = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempA, "{\"id\":\"a1\"}\n{\"id\":\"a2\"}\n");
                File.WriteAllText(tempB, "{\"id\":\"b1\"}\n{\"id\":\"b2\"}\n");
                var countedA = new FileInfo(tempA).Length;
                var countedB = new FileInfo(tempB).Length;
                var adopt = $"{output}.adopt";
                File.WriteAllText(adopt, "LEFTOVER-FROM-AN-INTERRUPTED-PROMOTION\n");

                var runA = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
                var runB = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
                (string Answer, string Reason) a = default;
                (string Answer, string Reason) b = default;
                using var released = new Barrier(2);
                var threads = new[]
                {
                    new Thread(() =>
                    {
                        released.SignalAndWait();
                        a = InvokeForStaging(runA, output, Path.GetFileName(tempA), countedA);
                    }) { IsBackground = true },
                    new Thread(() =>
                    {
                        released.SignalAndWait();
                        b = InvokeForStaging(runB, output, Path.GetFileName(tempB), countedB);
                    }) { IsBackground = true },
                };
                try
                {
                    foreach (var t in threads) t.Start();
                    foreach (var t in threads)
                        Assert.True(t.Join(TimeSpan.FromSeconds(30)),
                            "a promotion never returned");
                }
                finally
                {
                    runA.Dispose();
                    runB.Dispose();
                }

                // One landed. Neither answer is a file that vanished: the run that stopped says
                // so about a name another run holds or an output another run has, never about a
                // copy of its own that is no longer there.
                Assert.Single(new[] { a.Answer, b.Answer }.Where(x => x == "Taken"));
                Assert.DoesNotContain("Could not find file", a.Reason);
                Assert.DoesNotContain("Could not find file", b.Reason);

                // The output holds one run's rows and not a weave of both, the run that stopped
                // still has the temp it would come back to, and the leftover is gone.
                var landed = a.Answer == "Taken";
                Assert.Equal(
                    landed
                        ? ["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"]
                        : ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"],
                    File.ReadAllLines(output));
                Assert.True(File.Exists(landed ? tempB : tempA),
                    "the promotion that stopped lost the temp it was to come back to");
                Assert.False(File.Exists(adopt));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>
    /// A directory standing at the staging name where the create is what meets it, which is the
    /// instant between the sweep and the create. The sweep calls that entry a directory; a
    /// create refuses a name that already exists with the runtime's "already exists" whatever
    /// kind of entry wears it, so one disk state came back in two wordings depending on which of
    /// the two instructions reached it - and "already exists" reads as a file in the way, which
    /// a caller answers by removing it. The refinement asks the filesystem on the create's route
    /// too.
    /// </summary>
    [Fact]
    public void A_directory_the_create_meets_reads_as_a_directory_and_not_as_a_file_in_the_way()
    {
        var reasonFor = typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "ReasonForUnopenable", BindingFlags.Static | BindingFlags.NonPublic)!;

        var dir = NewDir();
        try
        {
            var adopt = Path.Combine(dir, "out.jsonl.adopt");
            Directory.CreateDirectory(adopt);

            // The create a promotion stages its copy with, against the entry that appeared under
            // it. The runtime's own words for it name no directory on either platform, and they
            // are not the same words or even the same exception: a create meets a directory as a
            // name that already exists on Unix and as an access failure on Windows. Which of the
            // two the platform raises is not the question - both reach the refinement through
            // the promotion's own catch, and what it makes of them is.
            var raised = Assert.ThrowsAny<Exception>(() => new FileStream(
                adopt, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
            Assert.True(raised is IOException or UnauthorizedAccessException,
                $"the create raised {raised.GetType()}, which the promotion does not catch");
            Assert.DoesNotContain("directory", raised.Message, StringComparison.OrdinalIgnoreCase);

            var slots = new object?[reasonFor.GetParameters().Length];
            slots[0] = adopt;
            slots[1] = raised;
            // Raised by a create, where the refinement is told which open it came from.
            for (var i = 2; i < slots.Length; i++) slots[i] = true;
            var refined = reasonFor.Invoke(null, slots)!.ToString()!;

            Assert.Contains("a directory stands at the path", refined);
            Assert.DoesNotContain("already exists", refined);

            // And a failure from any other open still carries the runtime's words: the question
            // the create's refusal cannot answer for itself is not asked of every exception.
            slots[1] = new IOException("Too many levels of symbolic links.");
            for (var i = 2; i < slots.Length; i++) slots[i] = false;
            Assert.Contains("Too many levels of symbolic links",
                reasonFor.Invoke(null, slots)!.ToString()!);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The claim a promotion takes on the output the move will replace, over an output that is
    /// not there yet - which is the ordinary end of a fresh run interrupted into a temp. It
    /// opened FileMode.Open and answered Absent with no handle at all, so nothing was held
    /// through the staging or the rename: two runs with different temps over one output path both
    /// got past this line, both renamed a copy onto it, and the first one's rows ended in an
    /// unlinked inode with its own handle appending into a file at no path. The claim makes the
    /// file in order to hold it now, and a second promotion is refused on it the way it is
    /// refused on an output that was already there.
    /// </summary>
    [Fact]
    public void The_claim_on_an_absent_output_makes_the_file_and_refuses_a_second_promotion()
    {
        if (OperatingSystem.IsWindows()) return;

        var claimStanding = typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "ClaimStandingOutput", BindingFlags.Static | BindingFlags.NonPublic)!;

        var dir = NewDir();
        FileStream? held = null;
        try
        {
            // No output at all, and a temp for each of the two runs.
            var output = Path.Combine(dir, "out.jsonl");
            var theirs = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(theirs, "{\"id\":\"t1\"}\n{\"id\":\"t2\"}\n");
            var theirsCounted = new FileInfo(theirs).Length;
            var theirsBytes = File.ReadAllBytes(theirs);
            var mine = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(mine, "{\"id\":\"m1\"}\n{\"id\":\"m2\"}\n");
            var mineCounted = new FileInfo(mine).Length;
            Assert.False(File.Exists(output));

            // The first run, stopped where its claim is taken: what it holds from before the
            // staging through the rename, and nothing else of the promotion.
            var slots = new object?[claimStanding.GetParameters().Length];
            slots[0] = output;
            var answer = claimStanding.Invoke(null, slots)!.ToString()!;
            held = (FileStream?)slots[1];
            var created = slots.Length > 3 && slots[3] is true;

            Assert.Equal("Taken", answer);
            Assert.True(created, "the claim on an absent output reported making nothing");
            Assert.NotNull(held);
            Assert.True(File.Exists(output), "the claim on an absent output made no file to hold");
            Assert.Equal(0, new FileInfo(output).Length);

            // The second run, over the same output path with its own temp, while the first is
            // between its claim and its move. Refused on the output, with both of its files
            // where it found them.
            using (var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection())
            {
                Assert.Equal("OutputHeld", Invoke(run, output, Path.GetFileName(theirs),
                    theirsCounted));
            }

            Assert.Equal(theirsBytes, File.ReadAllBytes(theirs));
            Assert.Equal(0, new FileInfo(output).Length);
            Assert.False(File.Exists($"{output}.adopt"));

            // And once the first run lets go, a promotion lands in the file the claim made: the
            // rename replaces it, so nothing of the empty one is left to remove.
            held.Dispose();
            held = null;
            using (var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection())
            {
                Assert.Equal("Taken", Invoke(run, output, Path.GetFileName(mine), mineCounted));
            }

            Assert.Equal(["{\"id\":\"m1\"}", "{\"id\":\"m2\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists(mine));
        }
        finally
        {
            held?.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// An output that is not there in a directory this account may read and not write. The claim
    /// cannot make the file, and what refused it is the directory and not the file it would name
    /// - so the answer is left to the staging create, in that same directory, which is the
    /// question both passes ask and answer in one wording. Nothing of either pass is left at the
    /// output path.
    /// </summary>
    [Fact]
    public void A_read_only_directory_with_no_output_stops_both_passes_on_the_create()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);
            var entriesBefore = Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Chmod("555", dir);

            var preview = InvokeCanForStaging(output, Path.GetFileName(temp), counted);
            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();
            Chmod("755", dir);

            Assert.Equal("StagingFailed", preview.Answer);
            Assert.Equal("StagingFailed", applied.Answer);
            Assert.Equal(preview.Reason, applied.Reason);
            Assert.Contains($"{output}.adopt", applied.Reason);

            // The temp is where it was, no output was left standing at the path, and the
            // directory holds exactly the entries it held.
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
            Assert.False(File.Exists(output));
            Assert.Equal(entriesBefore, Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try { Chmod("755", dir); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The interleaving two promotions over one output reach on Unix, at the instant that makes
    /// it: the claim on the output is granted several syscalls after the open that resolved the
    /// name - .NET takes the lock behind FileShare.None after open(2) - and a second run that
    /// renames its own copy onto the name in between leaves this one holding an inode with no
    /// name on it. Read as a claim, that is a promotion renaming its copy over a file another
    /// run has just promoted, and both runs answering that they took the output. The seam below
    /// is that instant, fired on every attempt, so the name never settles: the answer is the
    /// held stop, and nothing at the path is replaced.
    /// </summary>
    [Fact]
    public void An_output_replaced_under_the_claim_is_not_an_output_this_run_took()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);

            // The second run's rename, landing in the instant between this run's claim being
            // granted and the check that the name still reaches what it granted. A fresh file
            // each time, so which one stands at the path says how many attempts were made.
            var renames = 0;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (!string.Equals(claimed, output, StringComparison.Ordinal)) return;
                renames++;
                var theirs = Path.Combine(dir, $"theirs-{renames}.jsonl");
                File.WriteAllText(theirs, $"{{\"id\":\"theirs-{renames}\"}}\n");
                File.Move(theirs, output, overwrite: true);
            };

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            // What the other run left at the path is whole, this run's temp is where it was,
            // and no copy was staged beside either - the answer is the hold it is, and the
            // claim was asked three times to reach it.
            Assert.Equal("OutputHeld", applied.Answer);
            Assert.Equal(["{\"id\":\"theirs-3\"}"], File.ReadAllLines(output));
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
            Assert.False(File.Exists($"{output}.adopt"));
            Assert.Equal(3, renames);
        }
        finally
        {
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same instant in the sweep of the staging name, which is where the interleaving costs
    /// the OTHER run its file: this run's claim on the leftover is granted after a second run
    /// has taken that leftover off the name and created its own staged copy there, and the
    /// unlink that follows a claim asks the directory - so it takes the second run's copy, and
    /// that run's rename ends on a name that is no longer there. The sweep answers the hold it
    /// met now, and the copy the other run is holding stays where it put it.
    /// </summary>
    [Fact]
    public void A_staging_name_taken_under_the_sweep_s_claim_is_not_the_sweep_s_to_unlink()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        FileStream? theirs = null;
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);
            var adopt = $"{output}.adopt";
            File.WriteAllText(adopt, "LEFTOVER-FROM-AN-INTERRUPTED-PROMOTION\n");

            // The second run, in that instant: it swept the same leftover, created its staged
            // copy at the name and holds it there through the rename it is about to make.
            var swept = 0;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (!string.Equals(claimed, adopt, StringComparison.Ordinal)) return;
                if (swept++ > 0) return;
                File.Delete(adopt);
                theirs = new FileStream(adopt, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None);
                theirs.Write("{\"id\":\"theirs\"}\n"u8);
                theirs.Flush();
            };

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            // The name is the other run's, which is the answer a name a second run holds always
            // had here - and this run stopped on it with its own temp untouched.
            Assert.Equal("StagingFailed", applied.Answer);
            Assert.Contains("another run has the staged copy open", applied.Reason);
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
            Assert.Equal(1, swept);

            // And the rename that copy is being held for lands, because the copy is still there.
            File.Move(adopt, output, overwrite: true);
            theirs!.Dispose();
            theirs = null;
            Assert.Equal(["{\"id\":\"theirs\"}"], File.ReadAllLines(output));
        }
        finally
        {
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            theirs?.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The unlink at the end of a promotion that landed, which is the same question one file
    /// later: the temp goes because this run read it, and what goes is the file this run holds -
    /// not whatever wears its name by then. A second run that took the name in the interval
    /// keeps its file.
    /// </summary>
    [Fact]
    public void A_temp_replaced_at_its_name_is_not_the_temp_a_promotion_unlinks()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var moved = Path.Combine(dir, "moved-away.tmp");

            // Two claims are taken on the temp's name in one promotion: the one it is read
            // through, and the check in front of the unlink that spends it. The second run
            // arrives between them.
            var claims = 0;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (!string.Equals(claimed, temp, StringComparison.Ordinal)) return;
                if (++claims != 2) return;
                File.Move(temp, moved);
                File.WriteAllText(temp, "{\"id\":\"theirs\"}\n");
            };

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var answer = Invoke(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            // The rows are in the output, and the file standing at the temp's name is the other
            // run's, whole. The temp this promotion actually spent is where that run moved it,
            // for the sweep that reaches it next.
            Assert.Equal("Taken", answer);
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Equal(["{\"id\":\"theirs\"}"], File.ReadAllLines(temp));
            Assert.True(File.Exists(moved));
            Assert.Equal(2, claims);
        }
        finally
        {
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the empty output a promotion makes in order to hold it, taken back where the
    /// promotion stops. That unlink is the same shape as the two above: what goes is the file
    /// this claim made, and a second run standing at the name by then keeps what it put there.
    /// </summary>
    [Fact]
    public void An_output_the_claim_made_is_not_taken_back_from_a_second_run()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            // Nothing at the output path, so the claim makes the file in order to hold it.
            var output = Path.Combine(dir, "out.jsonl");
            var adopt = $"{output}.adopt";
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);

            var claims = 0;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (!string.Equals(claimed, output, StringComparison.Ordinal)) return;
                if (++claims == 1)
                {
                    // A directory at the staging name, in the instant after the sweep passed
                    // it: the create the copy is staged with meets it, and the promotion stops
                    // with the empty file it made still standing at the output path.
                    Directory.CreateDirectory(adopt);
                    return;
                }
                if (claims != 2) return;

                // And the second run, in the instant before that file is taken back: it is
                // gone from the name and theirs stands there instead.
                File.Delete(output);
                File.WriteAllText(output, "{\"id\":\"theirs\"}\n");
            };

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();

            Assert.Equal(["{\"id\":\"theirs\"}"], File.ReadAllLines(output));
            Assert.Equal("StagingFailed", applied.Answer);
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
            Assert.Equal(2, claims);
        }
        finally
        {
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A claim granted, and then a question about it the platform cannot answer: the output's
    /// parent directory stops being searchable in the instant between the two, so the stat of
    /// the name comes back EACCES - which is not the name resolving to nothing, and is not read
    /// as "still there". A claim that cannot be verified is not a claim, so the caller is handed
    /// a refusal in the shape it already reads every refusal in: the file could not be opened,
    /// the reason says why, and the handle comes back with it for the caller to let go of the
    /// way it lets go of one it kept. A resumed run asks that way, and keeps nothing.
    /// </summary>
    [Fact]
    public void A_claim_the_platform_cannot_verify_is_refused_with_the_handle_handed_back()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var contents = File.ReadAllBytes(output);

            // The premise: a directory with no search bit answers for no name under it. Where
            // the stat still answers, the account is root, a mode says nothing, and there is
            // nothing here to measure.
            SetMode(dir, 0);
            var unsearchable = !File.Exists(output);
            SetMode(dir, Searchable);
            if (!unsearchable) return;

            var looks = 0;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (!string.Equals(claimed, output, StringComparison.Ordinal)) return;
                looks++;
                SetMode(dir, 0);
            };

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var args = new object?[] { output, (long)contents.Length, null };
            var answer = Resume.Invoke(run, args)!.ToString()!;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            SetMode(dir, Searchable);

            // The answer a run words its stop out of, and the reason it quotes in it: the call,
            // the errno, and what could not be finished with them.
            Assert.Equal("CannotBeOpened", answer);
            Assert.Equal(1, looks);
            Assert.Contains("cannot be verified", args[2]!.ToString()!);

            // The handle went with the answer, on the caller's own path. Asked for again the way
            // every later claim asks for it, the file is granted at once - where a failure
            // carried out of the claim with the stream still open left an exclusive lock on the
            // caller's own output until the finalizer got around to it, with nothing
            // deterministic able to say when.
            using var next = new FileStream(output, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
            Assert.Equal(contents.Length, next.Length);
        }
        finally
        {
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            try { SetMode(dir, Searchable); Directory.Delete(dir, true); }
            catch { }
        }
    }

    /// <summary>
    /// The same failed question at the one caller that lets its claim go in a finally. A
    /// promotion over an output that is not there yet makes the file in order to hold it, and
    /// takes that file away again on every answer but Taken - under the claim that made it, so
    /// the name goes only while this run is still what stands at it. That take-back is armed by
    /// the handle and by the claim's own answer about what it made, and a refusal that clears
    /// either of them disarms it: the finally then holds nothing, unlinks nothing, and a file
    /// with no rows in it that no run holds is left standing at the caller's -OutputFile.
    /// <para>
    /// The parent answers again for the second look, which is the one the take-back is made
    /// under: the failure being transient is what makes the empty file outlive it.
    /// </para>
    /// </summary>
    [Fact]
    public void An_output_made_to_hold_a_claim_that_cannot_be_verified_is_taken_back()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);

            // The premise, as above: a directory with no search bit answers for no name under
            // it, and an account it does not refuse is one there is nothing here to measure on.
            SetMode(dir, 0);
            var unsearchable = !File.Exists(temp);
            SetMode(dir, Searchable);
            if (!unsearchable) return;

            var looks = 0;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (!string.Equals(claimed, output, StringComparison.Ordinal)) return;

                // The claim on the output, which cannot be verified; then the finally's look at
                // the file it made, which can.
                SetMode(dir, ++looks == 1 ? 0 : Searchable);
            };

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var args = Slots(Promote, output, Path.GetFileName(temp), counted);
            var answer = Promote.Invoke(run, args)!.ToString()!;
            run.Dispose();
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            SetMode(dir, Searchable);

            // Nothing of this run's making left at either name, and the temp holding every item
            // the checkpoint counts exactly as it was found - so the run that comes back
            // promotes the same bytes to the same length over the same output.
            Assert.False(File.Exists(output),
                "the empty output the claim made is left standing at the path");
            Assert.False(File.Exists($"{output}.adopt"));
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));

            // Two looks: the claim's, which could not be answered, and the finally's, which is
            // the one the take-back was made under.
            Assert.Equal(2, looks);
            Assert.Equal("OutputUnopenable", answer);
            Assert.Contains("cannot be verified", args[3]!.ToString()!);
        }
        finally
        {
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            try { SetMode(dir, Searchable); Directory.Delete(dir, true); }
            catch { }
        }
    }

    /// <summary>
    /// The output leaving the name in the instant the claim on it was granted, and the directory
    /// refusing a new file in the same one: the first look found a file, so the claim opened
    /// rather than created; the file left the name, so the claim was dropped and asked again;
    /// and the attempt that answers is a create the directory refused. Which of those two the
    /// answer stands for decides who words the stop. A create the directory refused is the
    /// staging create's own answer a few lines later, in that same directory, and the preview
    /// asks it too - so the claim leaves the wording there rather than naming the output in a
    /// sentence only the applying pass can reach.
    /// </summary>
    [Fact]
    public void A_create_refused_at_an_output_that_went_is_left_to_the_staging_create_s_words()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var adopt = $"{output}.adopt";
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            var counted = new FileInfo(temp).Length;
            var tempBytes = File.ReadAllBytes(temp);

            // The premise: a directory this account may not write refuses a new file in it.
            // Where it takes one, the account is root and there is nothing here to measure.
            var probe = Path.Combine(dir, "probe");
            SetMode(dir, ReadAndSearch);
            var refused = false;
            try { File.WriteAllText(probe, "x"); }
            catch (UnauthorizedAccessException) { refused = true; }
            SetMode(dir, Searchable);
            if (!refused)
            {
                File.Delete(probe);
                return;
            }

            var looks = 0;
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = claimed =>
            {
                if (!string.Equals(claimed, output, StringComparison.Ordinal)) return;
                if (looks++ > 0) return;

                // The file this claim was granted over, off the name - and the directory closed
                // behind it, so the attempt that follows finds nothing at the name, asks for
                // the create, and is refused it.
                File.Delete(output);
                SetMode(dir, ReadAndSearch);
            };

            using var run = new Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection();
            var applied = InvokeForStaging(run, output, Path.GetFileName(temp), counted);
            run.Dispose();
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;

            // The stop is the staging create's, in its own words, over the name it was making.
            Assert.Equal("StagingFailed", applied.Answer);
            Assert.Equal(1, looks);
            Assert.Contains(adopt, applied.Reason);
            Assert.Contains("is denied", applied.Reason);

            // And it is the wording the preview reaches over the same disk state, which is the
            // whole reason the claim does not answer this one itself.
            var preview = InvokeCanForStaging(output, Path.GetFileName(temp), counted);
            Assert.Equal("StagingFailed", preview.Answer);
            Assert.Equal(preview.Reason, applied.Reason);

            // Nothing was left at either name: no empty output of this run's making, and the
            // temp holding the items exactly as it was found.
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(adopt));
            Assert.Equal(tempBytes, File.ReadAllBytes(temp));
        }
        finally
        {
            Mgx.Engine.Pagination.HeldEntry.BeforeTheClaimIsVerified = null;
            try { SetMode(dir, Searchable); Directory.Delete(dir, true); }
            catch { }
        }
    }
}
