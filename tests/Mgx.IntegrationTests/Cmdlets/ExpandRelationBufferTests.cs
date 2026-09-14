using System.Collections;
using System.Management.Automation;
using System.Net;

namespace Mgx.IntegrationTests;

/// <summary>
/// Expand-MgxRelation holds every piped object until the pipeline ends, because the fan-out
/// cannot start before it knows all the ids. That is a whole collection resident in memory,
/// and the 50,000th arrival is where the cmdlet says so - while the caller can still stop it
/// and put a -Filter or a -Top upstream.
///
/// The count is the buffer's, not the fan-out's, so the objects below all carry one id: the
/// dedup leaves a single request to answer and what the test measures is the buffering.
/// </summary>
[Collection("Pipeline")]
public class ExpandRelationBufferTests
{
    private const int WarnAt = 50_000;

    private static PowerShell Shell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    /// <summary>
    /// <paramref name="count"/> objects down the pipe, and everything the run had to say.
    /// Built here rather than generated in script: the shape under test is what the cmdlet
    /// does with the objects, not what it costs PowerShell to make them.
    /// </summary>
    private static (int Output, List<string> Warnings, List<ErrorRecord> Errors, int Requests)
        Expand(int count)
    {
        var input = new Hashtable[count];
        for (int i = 0; i < count; i++)
            input[i] = new Hashtable(StringComparer.OrdinalIgnoreCase) { ["id"] = "u1" };

        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = Shell();
            ps.Runspace.SessionStateProxy.SetVariable("objects", input);
            ps.AddScript("$objects | Expand-MgxRelation -Uri '/users/{id}/manager' -As manager");
            var output = ps.Invoke();

            return (output.Count, ps.Streams.Warning.Select(w => w.Message).ToList(),
                ps.Streams.Error.ToList(), wire.RequestCount);
        }
    }

    [Fact]
    public void The_fifty_thousandth_buffered_object_warns_what_is_being_held()
    {
        var (output, warnings, errors, requests) = Expand(WarnAt);

        Assert.Empty(errors);

        // One id, so one request: nothing here waited on a fan-out.
        Assert.Equal(1, requests);
        Assert.Equal(WarnAt, output);

        var warned = Assert.Single(warnings);
        Assert.Contains("Buffered 50,000+ objects in memory", warned, StringComparison.Ordinal);
        Assert.Contains("-Filter", warned, StringComparison.Ordinal);
        Assert.Contains("-Top", warned, StringComparison.Ordinal);
    }

    [Fact]
    public void One_object_short_of_the_count_is_not_warned_about()
    {
        var (output, warnings, errors, _) = Expand(WarnAt - 1);

        Assert.Empty(errors);
        Assert.Equal(WarnAt - 1, output);
        Assert.Empty(warnings);
    }
}
