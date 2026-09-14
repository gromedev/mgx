using System.Reflection;

namespace Mgx.IntegrationTests.Engine.Pagination;

/// <summary>
/// Orphan-temp adoption after a JSONL run is interrupted, for checkpoints that predate the
/// recorded temp name and length. With only a line count and the newest file a run could have
/// named - "{output}.{32-hex}.tmp" - to go on, adoption is safe solely when no output exists;
/// against an existing output it must refuse and leave the caller to re-enumerate, since
/// nothing ties that temp to the run the checkpoint is actually about.
/// </summary>
public class OrphanAdoptionTests
{
    private static readonly MethodInfo Adopt =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TryAdoptOrphanedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo CanAdopt =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "CanAdoptOrphanedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// The two arguments, followed by an empty slot for each out parameter the method carries -
    /// the temp it picked at 2, the staging failure at 3, what the sweep removed at 4. Sized off
    /// the method, since reflection wants a slot for every one of them.
    /// </summary>
    private static object?[] Slots(MethodInfo method, string outputPath, long itemCount)
    {
        var slots = new object?[method.GetParameters().Length];
        slots[0] = outputPath;
        slots[1] = itemCount;
        return slots;
    }

    private static bool Invoke(string outputPath, long itemCount) =>
        (bool)Adopt.Invoke(null, Slots(Adopt, outputPath, itemCount))!;

    private static bool InvokeCan(string outputPath, long itemCount) =>
        (bool)CanAdopt.Invoke(null, Slots(CanAdopt, outputPath, itemCount))!;

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"mgx-adopt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Adopts_the_temp_when_no_output_exists_yet()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines($"{output}.{Guid.NewGuid():N}.tmp", ["{\"id\":\"a\"}", "{\"id\":\"b\"}", "{\"id\":\"c\"}"]);

            Assert.True(Invoke(output, 2));

            // Trimmed to the checkpointed count: the third line was written after the last flush.
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Refuses_when_an_output_already_exists()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            // Run 1 completed.
            File.WriteAllLines(output, ["{\"id\":\"run1-1\"}", "{\"id\":\"run1-2\"}"]);
            // Run 2 crashed: a temp is on disk, but nothing says it is run 2's rather than a
            // survivor of some unrelated enumeration against the same output name.
            File.WriteAllLines($"{output}.{Guid.NewGuid():N}.tmp", ["{\"id\":\"run2-1\"}", "{\"id\":\"run2-2\"}", "{\"id\":\"unflushed\"}"]);

            Assert.False(Invoke(output, 2));

            // The output is untouched; recovery falls to re-enumeration, not a guessed merge.
            Assert.Equal(
                ["{\"id\":\"run1-1\"}", "{\"id\":\"run1-2\"}"],
                File.ReadAllLines(output));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Refuses_when_the_temp_holds_less_than_the_checkpoint_promises()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"kept\"}"]);
            File.WriteAllLines($"{output}.{Guid.NewGuid():N}.tmp", ["{\"id\":\"only-one\"}"]);

            Assert.False(Invoke(output, 5));

            // A refused adoption must not touch the existing output, nor leave staging behind.
            Assert.Equal(["{\"id\":\"kept\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists($"{output}.adopt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A count is a count of line STARTS unless something says otherwise, and ReadLine returns
    /// an unterminated tail as a line like any other - so a temp cut inside the row the
    /// checkpoint counts handed the fragment over and it was written into the output as an
    /// item, with the rest of the enumeration appended behind it. mgx's own runs flush before
    /// they count, so this is a temp truncated under one rather than one it left.
    /// </summary>
    [Fact]
    public void Refuses_when_the_counted_row_was_cut_short()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText($"{output}.{Guid.NewGuid():N}.tmp",
                "{\"id\":\"a\"}\n{\"id\":\"b\"}\n{\"id\":\"to");

            Assert.False(Invoke(output, 3));

            Assert.False(File.Exists(output));
            Assert.False(File.Exists($"{output}.adopt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The state an interrupted run does leave: rows written after the last flush are torn or
    /// missing, and the checkpoint counts none of them. Refusing here would cost a
    /// re-enumeration for nothing.
    /// </summary>
    [Fact]
    public void Adopts_when_only_the_uncounted_row_was_cut_short()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText($"{output}.{Guid.NewGuid():N}.tmp",
                "{\"id\":\"a\"}\n{\"id\":\"b\"}\n{\"id\":\"to");

            Assert.True(Invoke(output, 2));

            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// "Orphan" is an assumption about a file this run did not write, and a second export
    /// against the same output holds one matching the same glob right now. Adoption copies what
    /// it picks into the output and then unlinks it, so it took the live run's rows as its own
    /// and left that run writing into an unlinked inode - the failure the stale-temp sweep asks
    /// CanTakeExclusively about before it deletes anything. Adoption has to ask the same
    /// question, and a file it cannot claim is not an orphan.
    /// </summary>
    [Fact]
    public void Refuses_a_temp_a_live_run_still_holds()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var live = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");

            // Opened the way an export opens its own temp: new StreamWriter(path, append).
            using (var writer = new StreamWriter(live, append: false))
            {
                writer.WriteLine("{\"id\":\"other-1\"}");
                writer.WriteLine("{\"id\":\"other-2\"}");
                writer.Flush();

                Assert.False(InvokeCan(output, 2));
                Assert.False(Invoke(output, 2));

                Assert.False(File.Exists(output));
                Assert.False(File.Exists($"{output}.adopt"));
                Assert.True(File.Exists(live), "a running export's temp was consumed as an orphan");

                // The run that owns it goes on writing into the file it still has.
                writer.WriteLine("{\"id\":\"other-3\"}");
            }

            Assert.Equal(
                ["{\"id\":\"other-1\"}", "{\"id\":\"other-2\"}", "{\"id\":\"other-3\"}"],
                File.ReadAllLines(live));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The newest temp is the one a live run is writing into, which is exactly the file
    /// adoption must not take - so passing over it cannot mean giving up. The dead run's temp
    /// underneath is the orphan recovery exists for, and it is still the one to adopt.
    /// </summary>
    [Fact]
    public void Adopts_the_orphan_underneath_a_temp_a_live_run_holds()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");

            // A dead run's temp, aged so the live one below is unambiguously newer.
            var orphan = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllLines(orphan, ["{\"id\":\"dead-1\"}", "{\"id\":\"dead-2\"}"]);
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddMinutes(-5));

            var live = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            using (var writer = new StreamWriter(live, append: false))
            {
                writer.WriteLine("{\"id\":\"live-1\"}");
                writer.WriteLine("{\"id\":\"live-2\"}");
                writer.Flush();

                Assert.True(Invoke(output, 2));

                Assert.Equal(["{\"id\":\"dead-1\"}", "{\"id\":\"dead-2\"}"], File.ReadAllLines(output));
                Assert.False(File.Exists(orphan));
                Assert.True(File.Exists(live), "a running export's temp was consumed as an orphan");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Does_nothing_when_there_is_no_temp()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"kept\"}"]);
            Assert.False(Invoke(output, 3));
            Assert.Equal(["{\"id\":\"kept\"}"], File.ReadAllLines(output));
        }
        finally { Directory.Delete(dir, true); }
    }
}
