using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Polly;

namespace Mgx.Engine.Http;

/// <summary>
/// DelegatingHandler that applies the shared Polly resilience pipeline to HTTP requests.
/// Used by Enable-MgxResilience to inject retry/circuit breaker/rate limiting
/// into the Microsoft.Graph SDK's HttpClient handler chain.
/// </summary>
public sealed class ResilientDelegatingHandler : DelegatingHandler
{
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly TokenBucketRateLimiter? _rateLimiter;
    private readonly ConcurrentQueue<string> _pendingVerbose = new();

    /// <summary>
    /// Optional callback for verbose messages from the resilience pipeline.
    /// Messages are buffered during pipeline execution and drained after
    /// ExecuteAsync returns on the calling thread.
    /// </summary>
    public Action<string>? VerboseWriter { get; set; }

    public ResilientDelegatingHandler(
        ResiliencePipeline<HttpResponseMessage> pipeline,
        TokenBucketRateLimiter? rateLimiter)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _rateLimiter = rateLimiter;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer content upfront so retries can reconstruct a fresh request body.
        // Also snapshot all content headers (not just ContentType) to preserve
        // Content-Encoding, Content-Disposition, etc. on retry.
        byte[]? contentBytes = null;
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null;
        if (request.Content != null)
        {
            contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            contentHeaders = request.Content.Headers.ToList();
        }

        RateLimitLease? lease = null;
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(ResiliencePipelineFactory.IsIdempotentKey, request.Method != HttpMethod.Post);
        context.Properties.Set(ResiliencePipelineFactory.VerboseWriterKey,
            (Action<string>)(msg => _pendingVerbose.Enqueue(msg)));
        try
        {
            if (_rateLimiter != null)
            {
                lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
                if (!lease.IsAcquired)
                    throw new InvalidOperationException("Rate limit exceeded. Too many concurrent requests. Reduce -Concurrency on fan-out cmdlets, increase the queue with Set-MgxOption -RateLimitQueueLimit, or disable with Set-MgxOption -NoRateLimit.");
            }

            return await _pipeline.ExecuteAsync(
                async ctx =>
                {
                    // Clone on every attempt, including the first. On the SDK bridge path
                    // (Enable-MgxResilience), the outer HttpClient sets _sendStatus = AlreadySent
                    // before this handler runs. Passing the original to SdkClientBridgeHandler
                    // throws "already sent". Cloning resets the flag.
                    var clone = new HttpRequestMessage(request.Method, request.RequestUri)
                    {
                        Version = request.Version,
                        VersionPolicy = request.VersionPolicy
                    };

                    foreach (var header in request.Headers)
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    clone.Headers.TryAddWithoutValidation("SdkVersion", MgxSdkVersion.Value);

                    // Copy request options (used by SDK handlers for per-request metadata)
#pragma warning disable CS8714 // nullability mismatch in IDictionary generic
                    foreach (var option in request.Options)
                        ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
#pragma warning restore CS8714

                    if (contentBytes != null)
                    {
                        var freshContent = new ByteArrayContent(contentBytes);
                        foreach (var header in contentHeaders!)
                            freshContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        clone.Content = freshContent;
                    }

                    return await base.SendAsync(clone, ctx.CancellationToken);
                },
                context);
        }
        finally
        {
            // Drain buffered verbose messages on the calling thread
            if (VerboseWriter != null)
            {
                while (_pendingVerbose.TryDequeue(out var msg))
                    VerboseWriter(msg);
            }
            else
            {
                while (_pendingVerbose.TryDequeue(out _)) { }
            }

            ResilienceContextPool.Shared.Return(context);
            lease?.Dispose();
        }
    }
}
