using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Security;
using System.Text;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// -WhatIf on an export with a resume checkpoint. Recovery promotes temp files over outputs,
/// cuts outputs back to a recorded length and deletes checkpoints, and it ran above the
/// ShouldProcess gate - so the run that reported it would change nothing had already rewritten
/// the output and removed the position it was resuming from. The gate also has to name the
/// action a real run would take, which it cannot do from "a checkpoint file exists".
/// </summary>
[Collection("Pipeline")]
public class ExportWhatIfCheckpointTests
{
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Page2, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>The lines -WhatIf writes go to the host, so a test that reads them needs one.</summary>
    private sealed class RecordingHost : PSHost
    {
        private readonly Guid _id = Guid.NewGuid();
        public RecordingHostUI Recorder { get; } = new();
        public override string Name => "MgxTestHost";
        public override Version Version => new(1, 0);
        public override Guid InstanceId => _id;
        public override PSHostUserInterface UI => Recorder;
        public override CultureInfo CurrentCulture => CultureInfo.InvariantCulture;
        public override CultureInfo CurrentUICulture => CultureInfo.InvariantCulture;
        public override void EnterNestedPrompt() { }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) { }
    }

    private sealed class RecordingHostUI : PSHostUserInterface
    {
        public List<string> Lines { get; } = [];
        public override PSHostRawUserInterface? RawUI => null;
        public override void Write(string value) => Lines.Add(value);
        public override void Write(ConsoleColor f, ConsoleColor b, string value) => Lines.Add(value);
        public override void WriteLine(string value) => Lines.Add(value);
        public override void WriteErrorLine(string value) => Lines.Add(value);
        public override void WriteDebugLine(string value) => Lines.Add(value);
        public override void WriteVerboseLine(string value) => Lines.Add(value);
        public override void WriteWarningLine(string value) => Lines.Add(value);
        public override void WriteProgress(long sourceId, ProgressRecord record) { }
        public override string ReadLine() => string.Empty;
        public override SecureString ReadLineAsSecureString() => new();
        public override Dictionary<string, PSObject> Prompt(
            string caption, string message, System.Collections.ObjectModel.Collection<FieldDescription> descriptions) => [];
        public override int PromptForChoice(
            string caption, string message,
            System.Collections.ObjectModel.Collection<ChoiceDescription> choices, int defaultChoice) => defaultChoice;
        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName) => PSCredential.Empty;
        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName,
            PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => PSCredential.Empty;
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-whatif-{Guid.NewGuid():N}")).FullName;

    /// <summary>Runs the export under -WhatIf and returns every line the gate wrote.</summary>
    private static (List<string> Lines, bool HadErrors) WhatIf(string output, string checkpoint)
    {
        var host = new RecordingHost();
        using var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", output)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("All")
          .AddParameter("WhatIf", true);
        ps.Invoke();
        return (host.Recorder.Lines, ps.HadErrors);
    }

    private static void Export(string output, string checkpoint)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", output)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("All");
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    /// <summary>
    /// The applying run, with its warnings and its terminating record. A run that stops on the
    /// staging name gets there before the client, so this needs no transport either - which is
    /// what lets one test put the two passes over one disk state side by side.
    /// </summary>
    private static (string[] Warnings, ErrorRecord[] Errors) ExportRun(string output,
        string checkpoint)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", output)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("All");
        List<ErrorRecord> errors = [];
        try { ps.Invoke(); }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return ([.. ps.Streams.Warning.Select(w => w.Message)], [.. errors]);
    }

    /// <summary>
    /// A checkpoint from a release that recorded neither the temp's name nor its length, which
    /// is the one shape adoption exists for: a line count, the newest temp beside the output,
    /// and no output at all.
    /// </summary>
    private static void SaveOrphanCheckpoint(string checkpoint, long items) =>
        File.WriteAllText(checkpoint, $$"""
        {"resource":"https://graph.microsoft.com/v1.0/users?$top=999",
         "nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
         "itemsCollected":{{items}},"pageItemsAlreadyWritten":0}
        """);

    private static void Mkfifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [path]);
        Assert.NotNull(mkfifo);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    /// <summary>
    /// What a hard kill leaves partway through a fresh checkpointed export: the previous
    /// export's output, this run's items in a temp beside it, and a checkpoint naming both.
    /// A real run promotes the temp over the output and appends the rest; -WhatIf has to say
    /// so and touch none of the three. It also runs above the client, so it needs no session.
    /// </summary>
    [Fact]
    public void WhatIf_promotes_nothing_and_names_the_append_a_real_run_would_do()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n{\"id\":\"old3\"}\n");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            // No transport and no Get-MgContext: reaching the client would end the run here.
            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf asked for a Graph connection");
            Assert.Contains(lines, l => l.Contains("Append JSONL data") && l.Contains(output));
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.False(File.Exists(output + ".adopt"));

            // And the run it described does exactly that.
            var handler = new CountingHandler();
            using var transport = MgxTransportScope.Inject(handler);
            Export(output, checkpoint);

            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same gate over a checkpoint that is another export's. A real run refuses it and
    /// exports from the beginning, so that is what -WhatIf has to report - and the checkpoint,
    /// which the refusal used to delete, is still there afterwards.
    /// </summary>
    [Fact]
    public void WhatIf_over_another_exports_checkpoint_names_an_export_and_deletes_nothing()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 12,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf asked for a Graph connection");
            Assert.Contains(lines, l => l.Contains("Export JSONL data") && l.Contains(output));
            Assert.DoesNotContain(lines, l => l.Contains("Append JSONL data"));
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same gate over a checkpoint whose temp another export is still writing. A real run
    /// takes nothing from that file and exports from the beginning, so reporting an append
    /// described a recovery that would not have happened - and the report is the only thing a
    /// caller running -WhatIf gets to act on.
    /// </summary>
    [Fact]
    public void WhatIf_over_a_temp_a_running_export_holds_names_an_export()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");

            // Opened the way an export opens its own temp, with two rows flushed and the run
            // that wrote them still going.
            using var live = new StreamWriter(temp, append: false);
            live.WriteLine("{\"id\":\"u1\"}");
            live.WriteLine("{\"id\":\"u2\"}");
            live.Flush();

            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = ReadShared(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf asked for a Graph connection");
            Assert.Contains(lines, l => l.Contains("Export JSONL data") && l.Contains(output));
            Assert.DoesNotContain(lines, l => l.Contains("Append JSONL data"));
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, ReadShared(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.False(File.Exists(output + ".adopt"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And what the preview says about it. A real run over these two files stops, which is not
    /// one of the two actions the ShouldProcess gate names against -OutputFile - the gate says
    /// which of append and export a run would do, and this run would do neither - so the
    /// sentence goes out beside it, on the stream every other checkpoint refusal is written to.
    /// In the future tense, and it does not throw: -WhatIf reports what a run would do without
    /// ending the pipeline on it, and the caller may have piped a dozen more commands behind
    /// this one.
    /// </summary>
    [Fact]
    public void WhatIf_over_a_held_temp_says_the_run_would_stop()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");

            using var live = new StreamWriter(temp, append: false);
            live.WriteLine("{\"id\":\"u1\"}");
            live.WriteLine("{\"id\":\"u2\"}");
            live.Flush();

            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = ReadShared(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            Assert.Contains(lines, l =>
                l.Contains("Another export is still writing the temp file")
                && l.Contains("This run would stop here; nothing would be written."));
            Assert.DoesNotContain(lines, l => l.Contains("nothing was written"));
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, ReadShared(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.Equal([temp], Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same preview one file over. A checkpoint that names no temp counts its items into the
    /// output itself, and a real run over one another export still holds stops there - so the
    /// gate has to say so, in the tense of a pass that does none of it. The claim it needs is
    /// taken and let go here like every other question above the gate: the output, the position
    /// and the directory are exactly what they were.
    /// </summary>
    [Fact]
    public void WhatIf_over_a_held_output_says_the_run_would_stop()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            File.WriteAllLines(output, ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"]);
            var recorded = new FileInfo(output).Length;

            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = output,
                DataLength = recorded,
            }.Save(checkpoint);

            // The run that owns it is still going: appending, sharing reads, one row past the
            // length its last save recorded.
            using var live = new StreamWriter(
                new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read));
            live.WriteLine("{\"id\":\"u3-live\"}");
            live.Flush();

            var outputBefore = ReadShared(output);
            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var entriesBefore = Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            Assert.Contains(lines, l =>
                l.Contains($"Another export is still writing '{output}'")
                && l.Contains("This run would stop here; nothing would be written."));
            Assert.DoesNotContain(lines, l => l.Contains("nothing was written"));
            Assert.Equal(outputBefore, ReadShared(output));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.Equal(entriesBefore, Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What -WhatIf leaves of the directory itself, and not only of the files in it. The
    /// recovery the gate reports on compares file names, and which spellings a directory keeps
    /// apart is a question answered by creating a file in it and looking for it under the other
    /// casing - so a run that reported it would change nothing was asking it by writing in the
    /// caller's own output directory. The answer is read off the directory's entries instead,
    /// which A_directory_holding_a_lettered_entry_decides_its_case_rule_without_writing pins at
    /// the helper, and the entry list here is what says no probe was written on the way through.
    ///
    /// The entry list and not the write time: the preview makes the create the promotion makes
    /// at its own staging name, since that create is what a directory this account cannot write
    /// in refuses and asking only whether something already stood there previewed a recovery
    /// the run would have stopped on. An entry made and taken back moves the directory's write
    /// time, and what the preview owes the caller is that nothing is left of it.
    ///
    /// The answer this process has cached for the directory is forgotten first, so the question
    /// is actually reached here rather than answered from the run that set the scene up. The
    /// directory holds three lettered entries by then, which is what a run may read the answer
    /// off instead - and reading takes nothing from either.
    /// </summary>
    [Fact]
    public void WhatIf_leaves_the_output_directorys_entries_alone()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n{\"id\":\"old3\"}\n");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            MgxCmdletBase.SetDirectoryCaseSensitivity(dir, null);

            var namesBefore = Directory.GetFileSystemEntries(dir).OrderBy(n => n).ToArray();

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf asked for a Graph connection");
            Assert.Contains(lines, l => l.Contains("Append JSONL data") && l.Contains(output));
            Assert.Equal(namesBefore, Directory.GetFileSystemEntries(dir).OrderBy(n => n).ToArray());
        }
        finally
        {
            MgxCmdletBase.SetDirectoryCaseSensitivity(dir, null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Reads a file the test's own live writer still holds open: on Windows the reader
    // must offer ReadWrite sharing or the holder's write access denies the open.
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The preview on the promotion route. A checkpoint naming a temp is recovered by replacing
    /// the output with that temp's rows and appending onto them, and a real run over an output
    /// another export holds stops there instead - so the gate has to say so. It never asked about
    /// the output at all on this route, and named the append the run would not have got to.
    /// </summary>
    [Fact]
    public void WhatIf_over_a_held_output_on_the_promotion_route_says_the_run_would_stop()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            // The export that has the output: appending, sharing reads.
            using var live = new StreamWriter(
                new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read));
            live.WriteLine("{\"id\":\"old3-live\"}");
            live.Flush();

            var outputBefore = ReadShared(output);
            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var entriesBefore = Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            Assert.Contains(lines, l =>
                l.Contains($"Another export is still writing '{output}'")
                && l.Contains("This run would stop here; nothing would be written."));
            Assert.DoesNotContain(lines, l => l.Contains("nothing was written"));
            Assert.Equal(outputBefore, ReadShared(output));
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.Equal(entriesBefore, Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The name the promotion stages its copy under, asked about too. A real run whose staging
    /// name is another run's - or a directory - stops there with both files left as found, and
    /// the gate could not see that at all: it named the recovery a run would have made if it had
    /// got past a write it never would have made.
    /// </summary>
    [Fact]
    public void WhatIf_over_a_held_staging_name_says_the_run_would_stop()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        var adopt = $"{output}.adopt";
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            // The run that has the staging name, holding it the way a promotion holds the copy
            // it has just written.
            using var holder = new FileStream(adopt, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None);
            holder.Write("{\"id\":\"theirs\"}\n"u8);
            holder.Flush();
            var holderBytes = holder.Length;

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var entriesBefore = Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            Assert.Contains(lines, l =>
                l.Contains($"staging them into '{output}' failed at '{adopt}'")
                && l.Contains("Nothing would be changed;"));
            Assert.DoesNotContain(lines, l => l.Contains("Nothing was changed;"));
            Assert.DoesNotContain(lines, l => l.Contains("Recovering"));

            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.Equal(entriesBefore, Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(holderBytes, holder.Length);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
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
    /// -WhatIf over an output directory this account may read and not write. Recovery stages a
    /// copy beside the output, so a real run stops on a create it cannot make - and the gate
    /// asked only whether something already stood at the staging name, found nothing, and named
    /// the recovery the run would then have failed to make. It makes the create too, at no
    /// length, so the preview says what the run would say.
    /// </summary>
    [Fact]
    public void WhatIf_over_a_read_only_output_directory_says_the_run_would_stop()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        var adopt = $"{output}.adopt";
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var entriesBefore = Directory.GetFiles(dir).Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Chmod("555", dir);

            var (lines, hadErrors) = WhatIf(output, checkpoint);
            Chmod("755", dir);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            var stop = Assert.Single(lines.Where(l =>
                l.Contains($"staging them into '{output}' failed at '{adopt}'")));
            Assert.Contains("Nothing would be changed;", stop);
            Assert.DoesNotContain("Nothing was changed;", stop);
            Assert.DoesNotContain(lines, l => l.Contains("Recovering"));

            // The reason is quoted inside a sentence the caller punctuates, and the runtime's
            // words for a refused create end in a period of their own.
            Assert.DoesNotContain("..", stop.Replace(dir, "<dir>"));

            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
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
    /// A FIFO at the staging name, under -WhatIf. The preview swept it, which made a deletion as
    /// real as a run's on the one pass that is supposed to touch nothing: a caller asking what
    /// would be changed had an entry beside their output removed in order to be told that
    /// nothing would be. It is left standing now and named instead, in a sentence saying what
    /// the run would take off the name and what it would put there - and the create test that
    /// follows a clear name is skipped, since nothing at the name can be tested without taking
    /// it off.
    /// </summary>
    [Fact]
    public void WhatIf_leaves_a_fifo_at_the_staging_name_and_names_what_a_run_would_remove()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        var adopt = $"{output}.adopt";
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            Mkfifo(adopt);

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            Assert.Contains(lines, l => l.Contains(
                $"The run would remove what stands at the staging name '{Path.GetFileName(adopt)}' "
                + "(an entry no copy could be staged in, such as a pipe or a socket) and stage "
                + "its copy there."));
            Assert.Contains(lines, l => l.Contains("Append JSONL data") && l.Contains(output));
            Assert.DoesNotContain(lines, l => l.Contains("failed at"));
            Assert.DoesNotContain(lines, l => l.Contains("Removed what stood at"));

            // The pipe is where the preview found it, and so is everything else.
            Assert.True(File.Exists(adopt), "the preview removed what it said a run would");
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { File.Delete(adopt); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint from before the temp's name and length were recorded, with a directory at
    /// the staging name. Adoption stages its copy under that name like a promotion does, and
    /// the preview asked neither question: it reported the append the recovery would have
    /// made, and the run then found the name unusable, said the output file was missing,
    /// deleted the position counting the temp's items and exported from the beginning over it.
    /// Both passes sweep and create there now, so the preview stops where the run stops and
    /// says so in the run's own words.
    /// </summary>
    [Fact]
    public void WhatIf_over_an_orphan_checkpoint_stopped_at_the_staging_name_says_the_run_would_stop()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        var adopt = $"{output}.adopt";
        try
        {
            // No output at all, which is the only state adoption runs in.
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            SaveOrphanCheckpoint(checkpoint, 2);
            Directory.CreateDirectory(adopt);
            File.WriteAllText(Path.Combine(adopt, "somebody-elses"), "keep\n");

            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            var previewed = Assert.Single(lines.Where(l =>
                l.Contains($"records are in '{Path.GetFileName(temp)}', whole, but staging them "
                           + $"into '{output}' failed at '{adopt}': a directory stands at the "
                           + "path.")));
            Assert.Contains("Nothing would be changed;", previewed);
            Assert.DoesNotContain(lines, l => l.Contains("output file is missing"));
            Assert.DoesNotContain(lines, l => l.Contains("Recovered"));

            // Nothing of the three moved, and the preview did not delete the position counting
            // the temp's items.
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.False(File.Exists(output));
            Assert.Equal("keep\n", File.ReadAllText(Path.Combine(adopt, "somebody-elses")));

            // And the run the preview described stops the same way, on the same sentence in the
            // applying tense, with the checkpoint still there to come back to.
            var (warnings, errors) = ExportRun(output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointStagingFailed", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.WriteError, stop.CategoryInfo.Category);
            Assert.Contains($"records are in '{Path.GetFileName(temp)}', whole, but staging them "
                + $"into '{output}' failed at '{adopt}': a directory stands at the path.",
                stop.Exception.Message);
            Assert.Contains("Nothing was changed;", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("output file is missing"));

            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.False(File.Exists(output));
            Assert.Equal("keep\n", File.ReadAllText(Path.Combine(adopt, "somebody-elses")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same checkpoint with a FIFO at the staging name, on the adoption route. The preview
    /// names what the run would take off the name and leaves the pipe where it is; the run that
    /// follows takes it off and adopts. Leaving it is not what wedges a later run - the run's
    /// own pass clears it before it opens anything - and removing it was a deletion made by a
    /// pass whose whole promise is that it makes none.
    /// </summary>
    [Fact]
    public void WhatIf_over_an_orphan_checkpoint_names_the_fifo_and_the_run_that_follows_adopts()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        var adopt = $"{output}.adopt";
        var kind = "an entry no copy could be staged in, such as a pipe or a socket";
        var removed = $"Removed what stood at the staging name '{Path.GetFileName(adopt)}': "
                      + kind + ".";
        var wouldRemove = "The run would remove what stands at the staging name "
                          + $"'{Path.GetFileName(adopt)}' ({kind}) and stage its copy there.";
        try
        {
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            SaveOrphanCheckpoint(checkpoint, 2);
            Mkfifo(adopt);

            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf ended the pipeline on a run it only described");
            Assert.Contains(lines, l => l.Contains(wouldRemove));
            Assert.Contains(lines, l => l.Contains("Append JSONL data") && l.Contains(output));
            Assert.DoesNotContain(lines, l => l.Contains("failed at"));
            Assert.DoesNotContain(lines, l => l.Contains("Removed what stood at"));

            // The pipe is where the preview found it, and so are the two files the checkpoint
            // stands for: the temp, and the output that is still not there.
            Assert.True(File.Exists(adopt), "the preview removed what it said a run would");
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.False(File.Exists(output));

            // And the applying run over the state the preview left - the pipe still at the name
            // - takes it off, warns, and adopts, on a thread with a watchdog: the failure this
            // pins is a run that does not come back at all.
            var handler = new CountingHandler();
            using var transport = MgxTransportScope.Inject(handler);
            string[] warnings = [];
            var running = new Thread(() => warnings = ExportRun(output, checkpoint).Warnings)
                { IsBackground = true };
            running.Start();
            Assert.True(running.Join(TimeSpan.FromSeconds(30)),
                "the run never returned: the staging open is waiting for a reader on the FIFO");

            Assert.Contains(warnings, w => w.Contains(removed));
            Assert.Contains(warnings, w => w.Contains("Recovered 2 items"));
            Assert.False(File.Exists(adopt));
            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { File.Delete(adopt); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
