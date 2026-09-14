using System.Net;
using System.Text;
using Mgx.Engine.Errors;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Every fault in the catalog, held against the middle of a content download: hop 1 redirects to
/// the download host, hop 2 answers 200, and the body faults after its first bytes have already
/// reached the caller. Two kinds can land there at all - a stall and a reset - and the rest are
/// explicit "not applicable" rows rather than tests nobody wrote: a status arrives on the
/// response line, a nextLink has no meaning here, and hop 2 is token-free by construction.
/// </summary>
[Collection("Pipeline")]
public class FaultInjectionDownloadTests : IDisposable
{
    private const string GraphContentUrl =
        "https://graph.microsoft.com/v1.0/me/drive/items/01ABC/content";
    private const string CdnUrl =
        "https://contoso-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=abc&tempauth=SECRET";

    /// <summary>The bytes the caller has already been handed when the fault lands.</summary>
    private const string DeliveredPrefix = "the-first-bytes";

    /// <summary>What the armed fault in this file is called, so the theory can assert by name
    /// that <see cref="MockHttpHandler.UnansweredRules"/> does not carry it.</summary>
    private const string FaultName = "fault-mid-body";

    /// <summary>Short enough that a stalled body ends the case in a fraction of a second.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMilliseconds(200);

    private readonly MockHttpHandler _graphHandler = new();
    private readonly MockHttpHandler _cdnHandler = new();
    private readonly HttpClient _graphHttpClient;
    private readonly ResilientGraphClient _client;

    public FaultInjectionDownloadTests()
    {
        ResiliencePipelineFactory.Reset();
        _graphHttpClient = new HttpClient(_graphHandler);
        _client = new ResilientGraphClient(_graphHttpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, NoAdaptivePacing = true, MaxRetryAttempts = 1 });
        GraphContentClient.DownloadClientForTests = new HttpClient(_cdnHandler);
    }

    public void Dispose()
    {
        GraphContentClient.DownloadClientForTests?.Dispose();
        GraphContentClient.DownloadClientForTests = null;
        _client.Dispose();
        _graphHttpClient.Dispose();
        ResiliencePipelineFactory.Reset();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A body that delivers its prefix and then fails the read, the way a connection dropped
    /// mid-transfer reaches a reader: an IOException out of the stream, not the
    /// HttpRequestException a failed send raises. Overriding the content read stream is what
    /// makes it a mid-body failure rather than a failure to produce a body at all.
    /// </summary>
    private sealed class ResetMidBodyContent : HttpContent
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(DeliveredPrefix);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new IOException("The response ended prematurely.");

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new ResettingStream(_prefix));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class ResettingStream(byte[] prefix) : Stream
        {
            private int _delivered;

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_delivered < prefix.Length)
                {
                    var count = Math.Min(buffer.Length, prefix.Length - _delivered);
                    prefix.AsSpan(_delivered, count).CopyTo(buffer.Span);
                    _delivered += count;
                    return ValueTask.FromResult(count);
                }

                throw new IOException("The response ended prematurely.");
            }

            public override Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _delivered; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private void ArmMidBody(FaultEntry entry)
    {
        _graphHandler.QueueResponse(HttpStatusCode.Found, null, new() { ["Location"] = CdnUrl });

        switch (entry.Shape)
        {
            case FaultShape.StalledBody:
                _cdnHandler.When(_ => true, FaultName).RespondStalled(
                    HttpStatusCode.OK, prefix: DeliveredPrefix, contentType: "application/octet-stream");
                break;
            case FaultShape.Transport:
                _cdnHandler.When(_ => true, FaultName).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ResetMidBodyContent()
                });
                break;
            default:
                throw new InvalidOperationException(
                    $"{entry.Kind} does not take a wire shape mid-body; it needs its own case.");
        }
    }

    [Theory]
    [MemberData(nameof(FaultCatalog.AllKinds), MemberType = typeof(FaultCatalog))]
    public async Task Each_fault_mid_download_body(FaultKind kind)
    {
        var entry = FaultCatalog.Entry(kind);
        var cell = entry.At(InjectionPoint.DownloadBody);
        if (cell.Coverage == FaultCoverage.NotApplicable)
        {
            FaultInjectionRows.AssertNotApplicable(entry, InjectionPoint.DownloadBody);
            return;
        }

        ArmMidBody(entry);

        using var result = await _client.GetContentAsync(GraphContentUrl);
        Assert.True(result.FromDownloadHost);
        Assert.DoesNotContain(FaultName, _cdnHandler.UnansweredRules);

        using var destination = new MemoryStream();
        var thrown = await Record.ExceptionAsync(() => GraphContentClient.CopyWithIdleTimeoutAsync(
            result.Content, destination, maxBytes: null, IdleTimeout, CancellationToken.None));

        Assert.NotNull(thrown);

        // The bytes that arrived before the fault are the caller's; the copy does not unwrite them.
        Assert.Equal(DeliveredPrefix, Encoding.UTF8.GetString(destination.ToArray()));

        // The classifier sees the failure the read raised, and it is the class the catalog claims.
        Assert.Equal(entry.Class,
            MgxErrorClassifier.Classify(thrown!, cancellationRequested: false).Class);

        // Whatever the download-host retry filter would say about it, nothing acts on it: the
        // body copy runs after GetContentAsync returned, outside the pipeline that filter belongs
        // to. A kind the filter would retry is therefore a pinned row, not a covered one.
        var wouldRetry = FaultCatalog.RetriedByTheDownloadPipeline(entry, thrown);
        Assert.Equal(wouldRetry ? FaultCoverage.Pinned : FaultCoverage.Covered, cell.Coverage);
        Assert.Single(_cdnHandler.Requests);
        Assert.Single(_graphHandler.Requests);

        switch (entry.Shape)
        {
            case FaultShape.StalledBody:
                var stalled = Assert.IsType<HttpRequestException>(thrown);
                Assert.Contains("stalled", stalled.Message, StringComparison.OrdinalIgnoreCase);
                break;
            case FaultShape.Transport:
                Assert.IsAssignableFrom<IOException>(thrown);
                break;
            default:
                Assert.Fail($"{entry.Kind} surfaced as {thrown!.GetType().Name} with no case to check it.");
                break;
        }
    }
}
