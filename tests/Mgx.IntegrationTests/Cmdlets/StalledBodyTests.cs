using System.Management.Automation;
using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// A server that sends response headers and then stops sending the body, without closing the
/// connection. Requests go out with HttpCompletionOption.ResponseHeadersRead, so neither
/// HttpClient.Timeout nor the pipeline's attempt timeout bounds the content read - the engine's
/// own BodyReadTimeout is the only thing that does. The collection path applied it; the single
/// request, the write and the entity fan-out read the body on the bare cmdlet token and waited
/// for a byte that never came, until Ctrl-C.
/// </summary>
[Collection("Pipeline")]
public class StalledBodyTests
{
    private sealed class StallingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StallingContent();
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }

    private static MgxTransportScope InjectStallingTransport(
        HttpStatusCode status = HttpStatusCode.OK) =>
        MgxTransportScope.Inject(new StallingHandler(status), options: new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            AttemptTimeoutSeconds = 2
        });

    /// <summary>
    /// Runs the cmdlet on a worker so a hang fails the test instead of wedging the suite.
    /// </summary>
    private static IReadOnlyList<ErrorRecord> RunAndCollectErrors(Action<PowerShell> build)
    {
        var work = Task.Run(() =>
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();

            build(ps);
            // A terminating error carries the record too, and a stall that ends the pipeline
            // rather than reporting an error record is exactly what these tests are about.
            try { ps.Invoke(); }
            catch (RuntimeException ex) { return (IReadOnlyList<ErrorRecord>)[ex.ErrorRecord]; }
            return (IReadOnlyList<ErrorRecord>)ps.Streams.Error.ToList();
        });

        Assert.True(work.Wait(TimeSpan.FromSeconds(30)),
            "the cmdlet was still running 30s after the server stalled the response body");
        return work.Result;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    public void A_stalled_body_ends_the_request_instead_of_hanging(string method)
    {
        using var transport = InjectStallingTransport();
        var errors = RunAndCollectErrors(ps =>
        {
            var cmd = ps.AddCommand("Invoke-MgxRequest")
                        .AddParameter("Uri", "/users/u1")
                        .AddParameter("Method", method);
            if (method != "GET") cmd.AddParameter("Body", "{}").AddParameter("Confirm", false);
        });

        Assert.Contains(errors, e =>
            e.ToString().Contains(ResilientGraphClient.BodyReadTimedOutMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void A_stalled_body_ends_an_entity_fan_out_instead_of_hanging()
    {
        using var transport = InjectStallingTransport();
        var errors = RunAndCollectErrors(ps =>
            ps.AddScript("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}'"));

        Assert.Contains(errors, e =>
            e.ToString().Contains(ResilientGraphClient.BodyReadTimedOutMessage, StringComparison.Ordinal));
    }

    /// <summary>
    /// The error body on the collection path. It was the one body read in the client without
    /// the timeout wrapper, so a server that stalled while sending a 500 ended the pipeline
    /// with a raw TaskCanceledException: outside the error contract, and past the filter the
    /// cmdlet catches its transport failures with.
    /// </summary>
    [Fact]
    public void A_stalled_error_body_on_the_all_path_is_reported_as_a_transport_failure()
    {
        using var transport = InjectStallingTransport(HttpStatusCode.InternalServerError);
        var errors = RunAndCollectErrors(ps =>
            ps.AddCommand("Invoke-MgxRequest")
              .AddParameter("Uri", "/users")
              .AddParameter("All", true));

        var error = Assert.Single(errors);
        Assert.Equal(ResilientGraphClient.BodyReadTimedOutMessage, error.Exception.Message);
        Assert.StartsWith("HttpError", error.FullyQualifiedErrorId, StringComparison.Ordinal);
    }
}
