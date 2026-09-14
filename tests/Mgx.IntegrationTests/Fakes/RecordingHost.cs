using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security;

namespace Mgx.IntegrationTests;

/// <summary>
/// A PSHost that keeps every line written to it. A ShouldProcess gate writes its "What if:" line
/// to the host rather than to one of the streams a PowerShell object exposes, so a test that
/// reads what a preview actually said needs a host of its own to read it off.
/// </summary>
internal sealed class RecordingHost : PSHost
{
    private readonly Guid _id = Guid.NewGuid();

    public RecordingHostUI Recorder { get; } = new();

    public override string Name => "MgxRecordingHost";
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

    /// <summary>
    /// Imports <paramref name="module"/> into a runspace over one of these hosts, runs the
    /// command <paramref name="build"/> adds, and returns the lines the host was given.
    /// </summary>
    internal static List<string> Preview(Assembly module, Action<PowerShell> build)
    {
        var host = new RecordingHost();
        using var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Import-Module").AddParameter("Assembly", module);
        ps.Invoke();
        ps.Commands.Clear();

        build(ps);
        ps.Invoke();

        Assert.Empty(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));
        return host.Recorder.Lines;
    }
}

/// <summary>Every write, in the order it arrived, and nothing else.</summary>
internal sealed class RecordingHostUI : PSHostUserInterface
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
        string caption, string message,
        System.Collections.ObjectModel.Collection<FieldDescription> descriptions) => [];

    public override int PromptForChoice(
        string caption, string message,
        System.Collections.ObjectModel.Collection<ChoiceDescription> choices,
        int defaultChoice) => defaultChoice;

    public override PSCredential PromptForCredential(
        string caption, string message, string userName, string targetName) => PSCredential.Empty;

    public override PSCredential PromptForCredential(
        string caption, string message, string userName, string targetName,
        PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => PSCredential.Empty;
}
