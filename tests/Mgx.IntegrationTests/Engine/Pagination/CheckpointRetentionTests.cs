using System.Reflection;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests.Engine.Pagination;

/// <summary>
/// TryAdoptOrphanedTemp's contract when there is nothing to adopt: report failure and leave the
/// existing output exactly as it was.
///
/// Scope note: these are unit tests of the helper. They cannot guard the caller's decision about
/// what a failed adoption MEANS - the helper never opens the checkpoint, so no assertion here can
/// distinguish "checkpoint kept" from "checkpoint deleted". That decision lives in SyncMgxDelta
/// and is covered end to end by DeltaRecoveryTests, which drives the cmdlet against a mock
/// transport.
/// </summary>
public class CheckpointRetentionTests
{
    private static readonly MethodInfo Adopt =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TryAdoptOrphanedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// The two arguments, followed by an empty slot for each out parameter the method carries.
    /// Sized off the method, since reflection wants a slot for every one of them.
    /// </summary>
    private static bool TryAdopt(string outputPath, long itemCount)
    {
        var slots = new object?[Adopt.GetParameters().Length];
        slots[0] = outputPath;
        slots[1] = itemCount;
        return (bool)Adopt.Invoke(null, slots)!;
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"mgx-ckpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// The exact second-interruption state: checkpoint + output + NO temp. Adoption must fail,
    /// and the caller must NOT read that as a missing output.
    /// </summary>
    [Fact]
    public void Adoption_fails_when_a_resumed_run_left_no_temp()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"baseline-1\"}", "{\"id\":\"baseline-2\"}"]);

            Assert.False(TryAdopt(output, 2));

            // The caller distinguishes these two facts. Conflating them is what destroyed data:
            // adoption failing is routine; the output being absent is not.
            Assert.True(File.Exists(output));
            Assert.Equal(2, File.ReadAllLines(output).Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_failed_adoption_leaves_the_existing_output_untouched()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"baseline\"}"]);
            // Temp holds fewer lines than the checkpoint promises: adoption must refuse.
            File.WriteAllLines($"{output}.aaa.tmp", ["{\"id\":\"partial\"}"]);

            Assert.False(TryAdopt(output, 99));

            Assert.Equal(["{\"id\":\"baseline\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists($"{output}.adopt"));
        }
        finally { Directory.Delete(dir, true); }
    }

}
