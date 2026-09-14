using System.Diagnostics;
using System.Net;
using System.Text;
using Mgx.Engine.Errors;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;
using Polly.Timeout;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class RetryTests
{
    [Fact]
    public async Task Retry_On429_RetriesAndSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 2,
            failStatus: (HttpStatusCode)429,
            successBody: TestData.SingleUser,
            failHeaders: new() { ["Retry-After"] = "1" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.RequestCount); // 2 failures + 1 success
    }

    [Fact]
    public async Task Retry_On503_RetriesAndSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 3,
            failStatus: HttpStatusCode.ServiceUnavailable,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, handler.RequestCount); // 3 failures + 1 success
    }

    [Fact]
    public async Task Retry_On504_RetriesAndSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.GatewayTimeout,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_RespectsRetryAfterHeader()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "2" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
        // Should have waited at least 2 seconds for Retry-After
        Assert.True(sw.ElapsedMilliseconds >= 1800, $"Expected >= 1800ms delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Retry_UsesExponentialBackoff()
    {
        var handler = new MockHttpHandler();
        // 3 failures, no Retry-After → should use exponential backoff
        handler.QueueFailuresThenSuccess(
            failCount: 3,
            failStatus: HttpStatusCode.ServiceUnavailable,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, handler.RequestCount);
        // With base delay of 1s and 3 retries, exponential backoff should take at least:
        // ~1s + ~2s + ~4s = ~7s (with jitter it could be less, but should be > 2s)
        Assert.True(sw.ElapsedMilliseconds >= 2000, $"Expected >= 2000ms total delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Retry_DoesNotRetryOn400()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.BadRequest, """{"error":{"message":"Bad request"}}""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/bad");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry on 400
    }

    [Fact]
    public async Task Retry_DoesNotRetryOn404()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.NotFound);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/notfound");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry on 404
    }

    [Fact]
    public async Task Retry_DoesNotRetryOn403()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.Forbidden);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/forbidden");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry on 403
    }

    [Fact]
    public async Task Retry_MaxRetryExhausted_ReturnsLastFailure()
    {
        var handler = new MockHttpHandler();
        // Queue more failures than max retries (7)
        for (int i = 0; i < 10; i++)
            handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/throttled");

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        // Should have made 1 initial + 7 retries = 8 requests
        Assert.Equal(8, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn503()
    {
        // POST is non-idempotent: a 503 may mean the request was partially processed.
        // The retry guard should prevent retrying POST on 503 (only 429 is retried for POST).
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // POST should NOT retry on 503: only the initial request, no retry
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostRetriesOn429()
    {
        // POST on 429 IS safe to retry (matches Kiota SDK behavior).
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 failure + 1 success
    }

    [Fact]
    public async Task Retry_GetRetriesOn500()
    {
        // GET on 500 IS safe to retry (idempotent method).
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.InternalServerError,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_GetRetriesOn502()
    {
        // GET on 502 IS safe to retry (idempotent method).
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.BadGateway,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn500()
    {
        // POST should NOT retry on 500 (non-idempotent)
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.InternalServerError);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn502()
    {
        // POST should NOT retry on 502 (non-idempotent)
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.BadGateway);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PreservesAllContentHeadersOnRetry()
    {
        // Verify that ALL content headers (not just ContentType) are preserved on retry
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("Content-Encoding", "identity");
        content.Headers.TryAddWithoutValidation("Content-Language", "en-US");

        var response = await client.SendAsync(
            HttpMethod.Put, "https://graph.microsoft.com/v1.0/users/user1",
            content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);

        // The retried request (second request) should have all content headers
        var retriedRequest = handler.Requests[1];
        Assert.NotNull(retriedRequest.Content);
        Assert.Contains("identity", retriedRequest.Content!.Headers.ContentEncoding);
        Assert.Contains("en-US", retriedRequest.Content.Headers.ContentLanguage);
        Assert.Equal("application/json", retriedRequest.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Retry_ReplaysTheRequestBodyOnRetry()
    {
        // Content is buffered before the pipeline; without that the second attempt
        // sends an already-consumed stream and Graph sees an empty body. Bodies are
        // captured at send time (the stub) because HttpClient disposes the request
        // once the call completes.
        const string payload = """{"displayName":"Test User"}""";
        var bodies = new List<string>();
        var handler = new StubHttpMessageHandler()
            .EnqueueRepeated(2, request =>
            {
                bodies.Add(request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() ?? "");
                var throttled = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                throttled.Headers.Add("Retry-After", "0");
                return throttled;
            })
            .Enqueue(request =>
            {
                bodies.Add(request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() ?? "");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(
            HttpMethod.Patch, "https://graph.microsoft.com/v1.0/users/abc", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, bodies.Count);
        Assert.All(bodies, body => Assert.Equal(payload, body));
    }

    [Fact]
    public async Task Headers_ArePassedThrough()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var headers = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" };
        await client.GetAsync("https://graph.microsoft.com/v1.0/users", default, headers);

        var sentRequest = handler.Requests[0];
        Assert.True(sentRequest.Headers.Contains("ConsistencyLevel"));
        Assert.Equal("eventual", sentRequest.Headers.GetValues("ConsistencyLevel").First());
    }

    [Fact]
    public async Task Retry_ClampsRetryAfterToMaxRetryAfterSeconds()
    {
        // Server sends Retry-After: 120 (way above our test MaxRetryAfterSeconds=30).
        // The pipeline should clamp the delay to MaxRetryAfterSeconds.
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "120" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAfterSeconds = 30
        });

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
        // Clamped to 30s: actual delay should be around 30s, not 120s.
        // Allow generous lower bound (25s) for timer precision, but must be well under 120s.
        Assert.True(sw.ElapsedMilliseconds < 60_000,
            $"Expected delay clamped to ~30s, but took {sw.ElapsedMilliseconds}ms (server requested 120s)");
        Assert.True(sw.ElapsedMilliseconds >= 25_000,
            $"Expected delay of ~30s (clamped from 120s), but only waited {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Retry_VerboseWriter_FiresOnRetry()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages(); // Messages are buffered; drain to VerboseWriter

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(messages);
        Assert.Contains("Retry attempt", messages[0]);
        Assert.Contains("429", messages[0]);
    }

    [Fact]
    public async Task Retry_VerboseWriter_LogsClampedRetryAfter()
    {
        // Verify the verbose message mentions clamping when server requests > MaxRetryAfterSeconds
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "120" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAfterSeconds = 30
        });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages(); // Messages are buffered; drain to VerboseWriter

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(messages);
        Assert.Contains("clamped to", messages[0]);
        Assert.Contains("120", messages[0]); // server requested
        Assert.Contains("30", messages[0]);  // clamped to
    }

    [Fact]
    public async Task Retry_DoesNotRetryOnUserCancellation()
    {
        // Simulate Ctrl+C: user cancels mid-request. The handler cancels the CTS
        // during SendAsync (as HttpClient would when the user token fires), then
        // throws TaskCanceledException. The retry predicate should detect the
        // cancelled context token and NOT retry.
        var cts = new CancellationTokenSource();
        var handler = new CancellingMockHandler(cts);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        // TaskCanceledException extends OperationCanceledException; Polly may wrap either way
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/users/user1", cts.Token));

        Assert.Equal(1, handler.RequestCount); // No retry: only the initial request
    }

    /// <summary>
    /// Handler that simulates user cancellation mid-request.
    /// Cancels the CTS during SendAsync and throws TaskCanceledException with the token.
    /// </summary>
    private sealed class CancellingMockHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        private int _callCount;
        public int RequestCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("User cancelled", null, cts.Token));
        }
    }

    [Fact]
    public async Task Retry_RetriesOnNonUserTaskCanceledException()
    {
        // TaskCanceledException from HttpClient timeout (not user cancellation)
        // should be retried since the user didn't cancel.
        var handler = new MockHttpHandler();
        handler.QueueException(new TaskCanceledException("Request timed out"));
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 timeout + 1 success
    }

    [Fact]
    public async Task Retry_GetRetriesOn408()
    {
        // HTTP 408 Request Timeout is transient and should be retried for idempotent methods.
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.RequestTimeout,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 failure + 1 success
    }

    [Fact]
    public async Task Retry_DeleteRetriesOn408()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.RequestTimeout);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Delete, "https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PatchRetriesOn408()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.RequestTimeout);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Patch, "https://graph.microsoft.com/v1.0/users/user1",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn408()
    {
        // POST is non-idempotent: 408 may mean the request was partially processed.
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.RequestTimeout);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry for POST on 408
    }

    [Fact]
    public async Task Retry_RetriesOnAttemptTimeout_ForIdempotentMethods()
    {
        // Simulate: inner handler takes longer than AttemptTimeoutSeconds (e.g., SDK's
        // RetryHandler honoring a long Retry-After). Polly's attempt timeout fires and
        // throws TimeoutRejectedException. This should be retried for idempotent methods.
        var handler = new SlowThenFastMockHandler(
            slowDelayMs: 5000, // First attempt: takes 5s (exceeds 2s attempt timeout)
            fastResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestData.SingleUser, System.Text.Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            AttemptTimeoutSeconds = 2,   // Short timeout to trigger TimeoutRejectedException
            TotalTimeoutSeconds = 30     // Plenty of time for retry
        });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 timeout + 1 success
    }

    [Fact]
    public async Task Retry_DoesNotRetryAttemptTimeout_ForPost()
    {
        // POST is non-idempotent: TimeoutRejectedException should NOT be retried.
        var handler = new SlowThenFastMockHandler(
            slowDelayMs: 5000,
            fastResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestData.SingleUser, System.Text.Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            AttemptTimeoutSeconds = 2,
            TotalTimeoutSeconds = 30
        });

        // POST should fail on timeout without retry
        await Assert.ThrowsAsync<Polly.Timeout.TimeoutRejectedException>(
            () => client.SendAsync(
                HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));

        Assert.Equal(1, handler.RequestCount); // No retry for POST
    }

    /// <summary>
    /// Handler that delays on the first request (simulating SDK RetryHandler honoring
    /// a long Retry-After) and responds fast on subsequent requests.
    /// </summary>
    private sealed class SlowThenFastMockHandler(int slowDelayMs, HttpResponseMessage fastResponse) : HttpMessageHandler
    {
        private int _callCount;
        public int RequestCount => Volatile.Read(ref _callCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _callCount);
            if (attempt == 1)
            {
                // Simulate slow inner handler (SDK RetryHandler waiting on Retry-After)
                await Task.Delay(slowDelayMs, ct);
                // If we reach here, timeout didn't fire (shouldn't happen in test)
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return fastResponse;
        }
    }

    [Fact]
    public async Task Retry_VerboseWriter_MessagesBufferedUntilDrain()
    {
        // Verify that messages are NOT in the writer before DrainVerboseMessages,
        // and ARE in the writer after DrainVerboseMessages.
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        // Before drain: messages should be empty (buffered in ConcurrentQueue)
        Assert.Empty(messages);

        client.DrainVerboseMessages();

        // After drain: messages should have the retry info
        Assert.Single(messages);
        Assert.Contains("Retry attempt", messages[0]);
    }

    [Fact]
    public async Task ResilientDelegatingHandler_AlwaysClonesRequest()
    {
        // Verify the handler sends a CLONE, not the original request.
        // This is critical for the Enable-MgxResilience SDK bridge path where
        // HttpClient marks the original request's _sendStatus = AlreadySent
        // before the handler runs. Sending the original would throw.
        var capturedRequests = new List<HttpRequestMessage>();
        var capturingHandler = new CapturingMockHandler(capturedRequests);

        var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(
            new ResilientGraphClientOptions { NoRateLimit = true });

        var handler = new ResilientDelegatingHandler(pipeline, null)
        {
            InnerHandler = capturingHandler
        };

        using var client = new HttpClient(handler);
        var original = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users");
        original.Headers.TryAddWithoutValidation("X-Test", "original");

        await client.SendAsync(original);

        Assert.Single(capturedRequests);
        var sent = capturedRequests[0];

        // The sent request must NOT be the same instance as the original
        Assert.False(ReferenceEquals(original, sent),
            "Handler must clone the request, not send the original. " +
            "The SDK bridge path marks the original as AlreadySent.");

        // But it must carry the same headers
        Assert.True(sent.Headers.Contains("X-Test"));

        ResiliencePipelineFactory.Reset();
    }

    /// <summary>
    /// A keyed entry answers the request its predicate picks out and no other, so a fault sits
    /// at a point in the operation instead of at a position in the send order. Everything the
    /// predicate misses falls through to the default.
    /// </summary>
    [Fact]
    public async Task Mock_KeyedResponse_AnswersOnlyTheMatchingRequest()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        handler.When(r => r.Uri.EndsWith("/users/user2", StringComparison.Ordinal))
               .Respond(HttpStatusCode.NotFound);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        foreach (var id in new[] { "user1", "user2", "user3" })
            (await client.GetAsync($"https://graph.microsoft.com/v1.0/users/{id}")).Dispose();

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.OK },
            handler.ServedStatusCodes);
        Assert.Equal(3, handler.RequestCount);
    }

    /// <summary>
    /// The attempt index counts the requests matching one entry's predicate, so a fault scoped
    /// to the second leaves the first alone, and the retry the fault provokes falls through to
    /// whatever answers next.
    /// </summary>
    [Fact]
    public async Task Mock_KeyedResponse_ScopedToAttempt2_LeavesAttempt1Alone()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        handler.When(r => r.Uri.EndsWith("/users/user1", StringComparison.Ordinal))
               .OnAttempt(2)
               .Respond((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        using var first = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, handler.RequestCount);

        using var second = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(
            new[] { HttpStatusCode.OK, (HttpStatusCode)429, HttpStatusCode.OK },
            handler.ServedStatusCodes);
    }

    /// <summary>
    /// A body that delivers a few bytes and then stops. The class is named rather than compared
    /// against another exception of the same type, which no classification of a stalled body
    /// could differ from: the contract is TransientTransport, the class a connection failure
    /// carries, so the pipeline may retry an idempotent request and nothing reads the stall as
    /// the server's final answer.
    /// </summary>
    [Fact]
    public async Task Mock_StalledBody_SurfacesAsTheTransportClassTheClassifierAssigns()
    {
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.EndsWith("/users", StringComparison.Ordinal))
               .RespondStalled(HttpStatusCode.OK, prefix: """{"value":[""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true })
        {
            BodyReadTimeout = TimeSpan.FromSeconds(2)
        };

        using var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ReadBodyAsStringAsync(response));
        Assert.Equal(ResilientGraphClient.BodyReadTimedOutMessage, thrown.Message);

        var stalled = MgxErrorClassifier.Classify(thrown, cancellationRequested: false);
        Assert.Equal(MgxErrorClass.TransientTransport, stalled.Class);
    }

    /// <summary>
    /// An answer held past the attempt timeout never reaches the caller, so nothing is recorded
    /// as served for it. Both attempts time out and the served list stays empty, which is what
    /// makes it usable as evidence that a fault landed.
    /// </summary>
    [Fact]
    public async Task Mock_HeldAnswer_TimedOutByTheAttempt_RecordsNothingAsServed()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        handler.When(r => r.Uri.EndsWith("/users/slow", StringComparison.Ordinal))
               .AfterDelay(TimeSpan.FromSeconds(5))
               .Respond(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            AttemptTimeoutSeconds = 1,
            TotalTimeoutSeconds = 30,
            MaxRetryAttempts = 1
        });

        var thrown = await Record.ExceptionAsync(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/users/slow"));

        Assert.IsType<TimeoutRejectedException>(thrown);
        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(handler.ServedStatusCodes);
        Assert.Empty(handler.UnansweredRules);
    }

    /// <summary>
    /// Two answers, one held and one immediate, are recorded in the order the handler handed
    /// them back rather than the order it chose them: the held one resolved first and arrives
    /// last.
    /// </summary>
    [Fact]
    public async Task Mock_HeldAnswer_IsRecordedWhenItIsHandedBack()
    {
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.EndsWith("/a", StringComparison.Ordinal))
               .AfterDelay(TimeSpan.FromSeconds(2))
               .Respond(HttpStatusCode.OK, """{"which":"a"}""");
        handler.When(r => r.Uri.EndsWith("/b", StringComparison.Ordinal))
               .Respond(HttpStatusCode.Accepted, """{"which":"b"}""");

        using var httpClient = new HttpClient(handler);
        var held = httpClient.GetAsync("https://graph.microsoft.com/v1.0/a");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        using var immediate = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/b");
        using var released = await held;

        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, immediate.StatusCode);
        Assert.Equal(
            new[] { HttpStatusCode.Accepted, HttpStatusCode.OK },
            handler.ServedStatusCodes);
    }

    /// <summary>
    /// A fault programmed after a generator that also matches its request never answers: the
    /// first matching entry wins, the enumeration completes, every item arrives, and the run
    /// looks exactly like one with no fault in it. The unanswered view is the only thing that
    /// says so - the shadowed entry's own match counter still advanced. Programmed the other
    /// way round the same fault lands.
    /// </summary>
    [Fact]
    public async Task Mock_ShadowedFault_IsNamedInUnansweredRules()
    {
        const string FaultName = "page 3 fails once";
        var collection = new PagedCollection(total: 6, pageSize: 2);

        var shadowed = new MockHttpHandler();
        collection.ServeOn(shadowed);
        ArmPageThreeFault(shadowed, FaultName);

        var injected = new MockHttpHandler();
        ArmPageThreeFault(injected, FaultName);
        collection.ServeOn(injected);

        var expected = new[] { "user1", "user2", "user3", "user4", "user5", "user6" };
        Assert.Equal(expected, await EnumerateAsync(shadowed, collection));
        Assert.Equal(expected, await EnumerateAsync(injected, collection));

        Assert.DoesNotContain(HttpStatusCode.ServiceUnavailable, shadowed.ServedStatusCodes);
        Assert.Contains(HttpStatusCode.ServiceUnavailable, injected.ServedStatusCodes);

        Assert.Equal(new[] { FaultName }, shadowed.UnansweredRules);
        Assert.Empty(injected.UnansweredRules);

        var shadowedFault = shadowed.ProgrammedRules.Single(rule => rule.Name == FaultName);
        Assert.Equal(1, shadowedFault.Matches);
        Assert.Equal(0, shadowedFault.Answers);
    }

    private static void ArmPageThreeFault(MockHttpHandler handler, string name) =>
        handler.When(r => r.Uri.Contains("$skiptoken=page3", StringComparison.Ordinal), name)
               .OnAttempt(1)
               .Respond(HttpStatusCode.ServiceUnavailable, null, new() { ["Retry-After"] = "0" });

    private static async Task<List<string>> EnumerateAsync(
        MockHttpHandler handler, PagedCollection collection)
    {
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            AttemptTimeoutSeconds = 5,
            TotalTimeoutSeconds = 60,
            MaxRetryAttempts = 2
        });

        var ids = new List<string>();
        await foreach (var item in new PageIterator(client)
            .StreamAllWithCountAsync(collection.InitialUrl, 0, null))
        {
            ids.Add(item.GetProperty("id").GetString()!);
        }
        return ids;
    }

    /// <summary>
    /// The attempt index counts the requests that matched one entry, not the ones it answered.
    /// The first request is answered by the first entry; the second entry counted that request
    /// too, which is what puts its own fault on the second.
    /// </summary>
    [Fact]
    public async Task Mock_AttemptIndex_CountsMatchesPerEntry_NotAnswers()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        handler.When(r => r.Uri.EndsWith("/users/user1", StringComparison.Ordinal), "first request")
               .OnAttempt(1)
               .Respond(HttpStatusCode.NotFound);
        handler.When(r => r.Uri.EndsWith("/users/user1", StringComparison.Ordinal), "second request")
               .OnAttempt(2)
               .Respond(HttpStatusCode.Conflict);

        // The bare client: a 404 and a 409 are answers, and nothing here should retry them.
        using var httpClient = new HttpClient(handler);
        for (var i = 0; i < 2; i++)
            (await httpClient.GetAsync("https://graph.microsoft.com/v1.0/users/user1")).Dispose();

        Assert.Equal(
            new[] { HttpStatusCode.NotFound, HttpStatusCode.Conflict },
            handler.ServedStatusCodes);
        Assert.Empty(handler.UnansweredRules);
        Assert.Equal(new[] { 2, 2 }, handler.ProgrammedRules.Select(rule => rule.Matches));
        Assert.Equal(new[] { 1, 1 }, handler.ProgrammedRules.Select(rule => rule.Answers));
    }

    /// <summary>
    /// The attempt index over the retries of one request: the queue answers the first attempt,
    /// the keyed entry the retry it provoked, and the retry after that falls through to the
    /// default. One request, three attempts, three different answers.
    /// </summary>
    [Fact]
    public async Task Mock_KeyedResponse_ScopedToAttempt2_AnswersTheRetryOfOneRequest()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable);
        handler.When(r => r.Uri.EndsWith("/users/user1", StringComparison.Ordinal))
               .OnAttempt(2)
               .Respond((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            AttemptTimeoutSeconds = 5,
            TotalTimeoutSeconds = 60,
            MaxRetryAttempts = 4
        });

        using var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.RequestCount);
        Assert.All(handler.CapturedRequests, r => Assert.EndsWith("/users/user1", r.Uri, StringComparison.Ordinal));
        Assert.Equal(
            new[] { HttpStatusCode.ServiceUnavailable, (HttpStatusCode)429, HttpStatusCode.OK },
            handler.ServedStatusCodes);
        Assert.Empty(handler.UnansweredRules);
    }

    /// <summary>A byte body comes back as programmed, content type and all.</summary>
    [Fact]
    public async Task Mock_RespondBytes_HandsBackTheBytesAndTheContentType()
    {
        var payload = new byte[] { 0x00, 0x01, 0xFE, 0xFF };
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.EndsWith("/content", StringComparison.Ordinal))
               .RespondBytes(HttpStatusCode.OK, payload, "application/octet-stream");

        using var httpClient = new HttpClient(handler);
        using var response = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/content");

        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(new[] { HttpStatusCode.OK }, handler.ServedStatusCodes);
    }

    /// <summary>A status with no body at all, and the headers still arrive.</summary>
    [Fact]
    public async Task Mock_RespondEmpty_HandsBackAStatusAndNoBody()
    {
        var handler = new MockHttpHandler();
        handler.When(r => r.Method == HttpMethod.Delete)
               .RespondEmpty(HttpStatusCode.NoContent, new() { ["request-id"] = "empty-1" });

        using var httpClient = new HttpClient(handler);
        using var response = await httpClient.DeleteAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("empty-1", Assert.Single(response.Headers.GetValues("request-id")));
        Assert.Equal(new[] { HttpStatusCode.NoContent }, handler.ServedStatusCodes);
    }

    /// <summary>
    /// A thrown answer reaches the caller as itself and records no status: there was none.
    /// </summary>
    [Fact]
    public async Task Mock_Throw_SurfacesTheExceptionAndRecordsNothingAsServed()
    {
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.EndsWith("/users", StringComparison.Ordinal))
               .Throw(new HttpRequestException("connection reset"));

        using var httpClient = new HttpClient(handler);
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => httpClient.GetAsync("https://graph.microsoft.com/v1.0/users"));

        Assert.Equal("connection reset", thrown.Message);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(handler.ServedStatusCodes);
        Assert.Empty(handler.UnansweredRules);
    }

    /// <summary>
    /// A computed answer: one entry answers every request in the collection, each from the
    /// request itself, and the status it computed is what gets recorded.
    /// </summary>
    [Fact]
    public async Task Mock_FactoryAnswer_ComputesTheResponseAndRecordsItsStatus()
    {
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.Contains("/users/", StringComparison.Ordinal))
               .Respond(r =>
               {
                   var id = r.Uri[(r.Uri.LastIndexOf('/') + 1)..];
                   return new HttpResponseMessage(
                       id == "user2" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                   {
                       Content = new StringContent(
                           $$"""{"id":"{{id}}"}""", Encoding.UTF8, "application/json")
                   };
               });

        using var httpClient = new HttpClient(handler);
        var bodies = new List<string>();
        foreach (var id in new[] { "user1", "user2" })
        {
            using var response = await httpClient.GetAsync($"https://graph.microsoft.com/v1.0/users/{id}");
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(new[] { """{"id":"user1"}""", """{"id":"user2"}""" }, bodies);
        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.NotFound },
            handler.ServedStatusCodes);
    }

    /// <summary>
    /// The scripted handler's keyed path, keyed on the $batch envelope: the predicate reads the
    /// request body, so a fault can name the subrequest it is aimed at. The entry answers that
    /// envelope and consumes nothing from the script.
    /// </summary>
    [Fact]
    public async Task Stub_KeyedResponse_MatchesOnTheBatchBody()
    {
        var handler = new StubHttpMessageHandler();
        handler.When(r => r.BodyText?.Contains("""{"id":"7"}""", StringComparison.Ordinal) == true,
                     "subrequest 7 fails")
               .Respond(HttpStatusCode.InternalServerError, """{"error":{"code":"aimed"}}""");
        handler.EnqueueJson(HttpStatusCode.OK, """{"responses":[]}""");

        using var httpClient = new HttpClient(handler);
        using var first = await PostBatchAsync(httpClient, """{"requests":[{"id":"1"}]}""");
        using var aimed = await PostBatchAsync(httpClient, """{"requests":[{"id":"7"}]}""");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, aimed.StatusCode);
        Assert.Equal("""{"error":{"code":"aimed"}}""", await aimed.Content.ReadAsStringAsync());
        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(handler.UnansweredRules);
    }

    private static Task<HttpResponseMessage> PostBatchAsync(HttpClient client, string body) =>
        client.PostAsync(
            "https://graph.microsoft.com/v1.0/$batch",
            new StringContent(body, Encoding.UTF8, "application/json"));

    private sealed class CapturingMockHandler(List<HttpRequestMessage> captured) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captured.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
