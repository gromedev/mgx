using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Cmdlets.Batch;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class BatchWriteTests
{
    private static readonly string BatchSuccessResponse = """
    {
        "responses": [
            { "id": "1", "status": 201, "body": { "id": "new-user-1", "displayName": "Test User 1" } },
            { "id": "2", "status": 201, "body": { "id": "new-user-2", "displayName": "Test User 2" } }
        ]
    }
    """;

    private static readonly string BatchGetResponse = """
    {
        "responses": [
            { "id": "1", "status": 200, "body": { "id": "user1", "displayName": "User One" } },
            { "id": "2", "status": 200, "body": { "id": "user2", "displayName": "User Two" } }
        ]
    }
    """;

    [Fact]
    public async Task BatchPost_SetsMethodAndBody()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation>
        {
            new("/users", "POST", body),
            new("/users", "POST", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(201, result.Results[0].Response.Status);
        Assert.Equal(201, result.Results[1].Response.Status);
        // Verify the batch request sent to the server contains POST method and body
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"method\":\"POST\"", requestBody);
        Assert.Contains("\"body\":", requestBody);
        Assert.Contains("\"Content-Type\":\"application/json\"", requestBody);
        // Telemetry
        Assert.Equal(2, result.Telemetry.TotalRequests);
        Assert.Equal(2, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchGet_ItemHeaders_AppliedToEachItem()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client)
        {
            ItemHeaders = new Dictionary<string, string>
            {
                ["ConsistencyLevel"] = "eventual"
            }
        };

        var operations = new List<BatchOperation>
        {
            new("/users?$search=\"displayName:test\""),
            new("/groups?$search=\"displayName:eng\"")
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, result.Results.Count);
        // Verify the serialized batch JSON contains ConsistencyLevel on each item
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"ConsistencyLevel\":\"eventual\"", requestBody);
        // Verify it appears for both items (should be in the headers of each)
        var doc = JsonDocument.Parse(requestBody);
        var requests = doc.RootElement.GetProperty("requests");
        foreach (var item in requests.EnumerateArray())
        {
            var headers = item.GetProperty("headers");
            Assert.True(headers.TryGetProperty("ConsistencyLevel", out var cl));
            Assert.Equal("eventual", cl.GetString());
        }
    }

    [Fact]
    public async Task BatchPost_ItemHeaders_MergedWithContentType()
    {
        var singlePostResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-user-1", "displayName": "Test User 1" } }
            ]
        }
        """;
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, singlePostResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client)
        {
            ItemHeaders = new Dictionary<string, string>
            {
                ["ConsistencyLevel"] = "eventual"
            }
        };

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation>
        {
            new("/users", "POST", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // Verify both ConsistencyLevel AND Content-Type are present on the item
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(requestBody);
        var item = doc.RootElement.GetProperty("requests")[0];
        var headers = item.GetProperty("headers");
        Assert.True(headers.TryGetProperty("ConsistencyLevel", out var cl));
        Assert.Equal("eventual", cl.GetString());
        Assert.True(headers.TryGetProperty("Content-Type", out var ct));
        Assert.Equal("application/json", ct.GetString());
    }

    [Fact]
    public async Task BatchGet_NoItemHeaders_NoHeadersInJson()
    {
        var singleGetResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1", "displayName": "User One" } }
            ]
        }
        """;
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, singleGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        // No ItemHeaders set (default null)
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users") };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // GET without body and no ItemHeaders: headers key should be absent from JSON
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(requestBody);
        var item = doc.RootElement.GetProperty("requests")[0];
        Assert.False(item.TryGetProperty("headers", out _), "headers should be omitted when null (JsonIgnore WhenWritingNull)");
    }

    [Fact]
    public async Task BatchGet_BackwardCompatible()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        // Old-style call with string URLs
        var urls = new List<string> { "/users/user1", "/users/user2" };
        var results = await batchClient.ExecuteBatchAsync(urls);

        Assert.Equal(2, results.Count);
        Assert.True(results.ContainsKey("/users/user1"));
        Assert.True(results.ContainsKey("/users/user2"));

        // Verify GET is the default and no body is sent
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        var batchDoc = JsonDocument.Parse(requestBody);
        var requests = batchDoc.RootElement.GetProperty("requests");
        var firstItem = requests[0];
        Assert.Equal("GET", firstItem.GetProperty("method").GetString());
        Assert.False(firstItem.TryGetProperty("body", out _), "GET requests must not include a body");
    }

    [Fact]
    public async Task BatchPost_NonIdempotentRetryGuard()
    {
        // POST items should only retry on 429, not 503/504
        var post503Response = """
        {
            "responses": [
                { "id": "1", "status": 503 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, post503Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // POST with 503 should NOT retry (only 1 batch request sent)
        Assert.Equal(1, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(503, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchGet_503Retries()
    {
        // GET items should retry on 503
        var get503Response = """
        {
            "responses": [
                { "id": "1", "status": 503 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // GET with 503 should retry (2 batch requests sent)
        Assert.Equal(2, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.ItemRetries);
    }

    [Fact]
    public async Task BatchGet_500Retries()
    {
        // GET items should retry on 500 (aligned with ResiliencePipelineFactory)
        var get500Response = """
        {
            "responses": [
                { "id": "1", "status": 500 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, get500Response);
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, handler.RequestCount); // Retried on 500
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchGet_502Retries()
    {
        // GET items should retry on 502 (aligned with ResiliencePipelineFactory)
        var get502Response = """
        {
            "responses": [
                { "id": "1", "status": 502 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, get502Response);
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, handler.RequestCount); // Retried on 502
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchPost_DoesNotRetryOn500()
    {
        // POST items should NOT retry on 500 (non-idempotent)
        var post500Response = """
        {
            "responses": [
                { "id": "1", "status": 500 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, post500Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(1, handler.RequestCount); // No retry for POST on 500
        Assert.Single(result.Results);
        Assert.Equal(500, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchPost_DoesNotRetryOn502()
    {
        // POST items should NOT retry on 502 (non-idempotent)
        var post502Response = """
        {
            "responses": [
                { "id": "1", "status": 502 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, post502Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(1, handler.RequestCount); // No retry for POST on 502
        Assert.Single(result.Results);
        Assert.Equal(502, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchPatch_SetsMethodAndBody()
    {
        var patchResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1", "department": "Engineering" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, patchResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"department":"Engineering"}""");
        var operations = new List<BatchOperation> { new("/users/user1", "PATCH", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"method\":\"PATCH\"", requestBody);
        Assert.Contains("\"body\":", requestBody);
    }

    [Fact]
    public async Task BatchDelete_NoBody()
    {
        var deleteResponse = """
        {
            "responses": [
                { "id": "1", "status": 204 },
                { "id": "2", "status": 204 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, deleteResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation>
        {
            new("/users/user1", "DELETE"),
            new("/users/user2", "DELETE")
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(204, result.Results[0].Response.Status);
        Assert.Equal(204, result.Results[1].Response.Status);

        // Verify no body field in the request
        var requestBody = await handler.Requests[0].Content!.ReadAsStringAsync();
        var batchDoc = JsonDocument.Parse(requestBody);
        var deleteRequests = batchDoc.RootElement.GetProperty("requests");
        var firstDelete = deleteRequests[0];
        Assert.Equal("DELETE", firstDelete.GetProperty("method").GetString());
        Assert.False(firstDelete.TryGetProperty("body", out _), "DELETE requests must not include a body");
    }

    [Fact]
    public async Task BatchPost_429RetryAfter_RetriesAfterDelay()
    {
        // First batch response: item 1 gets 429 with Retry-After
        var throttledResponse = """
        {
            "responses": [
                { "id": "1", "status": 429, "headers": { "Retry-After": "1" } }
            ]
        }
        """;
        // Second batch response: item 1 succeeds on retry
        var successResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-user-1", "displayName": "Test User 1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, throttledResponse);
        handler.QueueResponse(HttpStatusCode.OK, successResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // POST retries on 429 (2 batch requests sent)
        Assert.Equal(2, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(201, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.ThrottleEncounters);
        Assert.Equal(1, result.Telemetry.ItemRetries);
    }

    [Fact]
    public async Task BatchGet_ChunkingOver20_SendsMultipleBatches()
    {
        // Build batch responses for chunk 1 (20 items) and chunk 2 (5 items)
        var chunk1Items = string.Join(",\n",
            Enumerable.Range(1, 20).Select(i =>
                $$"""{ "id": "{{i}}", "status": 200, "body": { "id": "user{{i}}" } }"""));
        var chunk1Response = $$"""{ "responses": [{{chunk1Items}}] }""";

        var chunk2Items = string.Join(",\n",
            Enumerable.Range(1, 5).Select(i =>
                $$"""{ "id": "{{i}}", "status": 200, "body": { "id": "user{{20 + i}}" } }"""));
        var chunk2Response = $$"""{ "responses": [{{chunk2Items}}] }""";

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, chunk1Response);
        handler.QueueResponse(HttpStatusCode.OK, chunk2Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        // 25 operations: should produce 2 batch calls (20 + 5)
        var operations = Enumerable.Range(1, 25)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(25, result.Results.Count);
        // All should be 200
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(25, result.Telemetry.TotalRequests);
        Assert.Equal(25, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchPost_MixedResults_PartialSuccess()
    {
        var mixedResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-group-1", "displayName": "Group 1" } },
                { "id": "2", "status": 403, "body": { "error": { "code": "Authorization_RequestDenied", "message": "Insufficient privileges" } } },
                { "id": "3", "status": 500, "body": { "error": { "code": "Service_InternalServerError", "message": "An internal error occurred" } } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, mixedResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test Group"}""");
        var operations = new List<BatchOperation>
        {
            new("/groups", "POST", body),
            new("/groups", "POST", body),
            new("/groups", "POST", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(3, result.Results.Count);
        // First item succeeds
        Assert.Equal(201, result.Results[0].Response.Status);
        // Second item: 403 Forbidden
        Assert.Equal(403, result.Results[1].Response.Status);
        // Third item: 500 (POST doesn't retry on 500)
        Assert.Equal(500, result.Results[2].Response.Status);
        // Verify order preserved
        Assert.Equal("/groups", result.Results[0].Operation.Url);
        // Telemetry: 1 succeeded, 2 failed (403 + 500 are non-retryable for POST)
        Assert.Equal(1, result.Telemetry.Succeeded);
        Assert.Equal(2, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchGet_BetaApiVersion_SendsToBetaEndpoint()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        // Construct with beta base URL (mirrors what InvokeMgxBatchRequest.VersionedBaseUrl produces)
        var batchClient = new GraphBatchClient(client, "https://graph.microsoft.com/beta");

        var urls = new List<string> { "/users/user1", "/users/user2" };
        await batchClient.ExecuteBatchAsync(urls);

        // Verify the request was sent to the beta/$batch endpoint
        var request = handler.Requests[0];
        Assert.Equal("https://graph.microsoft.com/beta/$batch", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task BatchGet_RetryExhausted_ReturnsLastError()
    {
        // GET item fails on all 4 attempts (initial + 3 retries) with 503
        var get503Response = """
        {
            "responses": [
                { "id": "1", "status": 503, "body": { "error": { "code": "ServiceUnavailable" } } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // 4 for per-chunk retries + 4 for batch-level retry = 8 total
        for (int i = 0; i < 8; i++)
            handler.QueueResponse(HttpStatusCode.OK, get503Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // Per-chunk: 4 attempts. Batch-level retry: another 4 attempts. Total: 8.
        Assert.Equal(8, handler.RequestCount);
        Assert.Single(result.Results);
        // Final result should be the last error, not Status=0
        Assert.Equal(503, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Failed);
        Assert.Equal(1, result.Telemetry.BatchLevelRetries);
    }

    [Fact]
    public async Task BatchGet_ResponseCountMismatch_IsReportedAsAChunkFailure()
    {
        // Send 3 requests but mock returns only 2 response items
        var truncatedResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } },
                { "id": "2", "status": 200, "body": { "id": "user2" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, truncatedResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new List<BatchOperation>
        {
            new("/users/a"),
            new("/users/b"),
            new("/users/c"),
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        var ex = Assert.IsType<InvalidOperationException>(result.ChunkFailure);
        Assert.Contains("count mismatch", ex.Message);
        Assert.Contains("sent 3", ex.Message);
        Assert.Contains("received 2", ex.Message);
    }

    [Fact]
    public async Task BatchGet_CrossChunkBackpressure_DelaysNextChunk()
    {
        // Chunk 1 (20 items): all return 429 with Retry-After:1 on first attempt, succeed on retry.
        // Retry-After:1 keeps intra-chunk delay short (~1-1.5s with jitter).
        // The fix (Math.Max) preserves the throttle signal across retry iterations,
        // so cross-chunk delay adds another ~1-1.5s. Total >= 1.8s proves cross-chunk fired;
        // without the fix, total would be ~1-1.5s (intra-chunk only).
        var chunk1Throttled = BuildBatchResponse(20, status: 429, retryAfterSeconds: 1);
        var chunk1Success = BuildBatchResponse(20, status: 200);
        // Chunk 2 (5 items): all succeed immediately
        var chunk2Success = BuildBatchResponse(5, status: 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, chunk1Throttled);
        handler.QueueResponse(HttpStatusCode.OK, chunk1Success);
        handler.QueueResponse(HttpStatusCode.OK, chunk2Success);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 25)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);
        sw.Stop();

        // 3 HTTP requests: chunk1 initial (429), chunk1 retry (200), chunk2 (200)
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(25, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        // Intra-chunk delay: ~1-1.5s. Cross-chunk delay: ~1-1.5s. Total must exceed intra-chunk alone.
        Assert.True(sw.Elapsed.TotalSeconds >= 1.8,
            $"Expected intra-chunk (~1s) + cross-chunk (~1s) delay but total elapsed was {sw.Elapsed.TotalSeconds:F1}s");
        // Telemetry: 20 items retried within chunk 1
        Assert.Equal(20, result.Telemetry.ItemRetries);
        Assert.Equal(20, result.Telemetry.ThrottleEncounters);
    }

    [Fact]
    public async Task BatchGet_NoThrottling_NoCrossChunkDelay()
    {
        var chunk1Success = BuildBatchResponse(20, status: 200);
        var chunk2Success = BuildBatchResponse(5, status: 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, chunk1Success);
        handler.QueueResponse(HttpStatusCode.OK, chunk2Success);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 25)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);
        sw.Stop();

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(25, result.Results.Count);
        // No throttling: should complete quickly with no artificial delay (wide margin for CI)
        Assert.True(sw.Elapsed.TotalSeconds < 5.0,
            $"Expected no delay but total elapsed was {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(0, result.Telemetry.ItemRetries);
        Assert.Equal(0, result.Telemetry.ThrottleEncounters);
    }

    [Fact]
    public async Task BatchGet_BatchLevelRetry_RecoverAfterChunkExhaustion()
    {
        // Simulate: 1 item exhausts all 4 per-chunk attempts (503), then succeeds
        // on the batch-level retry pass.
        var get503Response = """
        {
            "responses": [
                { "id": "1", "status": 503 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts all fail with 503
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        // Batch-level retry: succeeds on first attempt
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // 4 per-chunk + 1 batch-level retry = 5 total HTTP requests
        Assert.Equal(5, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
        Assert.Equal(1, result.Telemetry.BatchLevelRetries);
    }

    [Fact]
    public async Task BatchPost_BatchLevelRetry_429Recovery()
    {
        // POST item exhausts per-chunk retries with 429, succeeds on batch-level retry.
        // This tests that POST (non-idempotent) still gets batch-level retry on 429.
        var throttledResponse = """
        {
            "responses": [
                { "id": "1", "status": 429, "headers": { "Retry-After": "1" } }
            ]
        }
        """;
        var successResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-user-1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts all throttled
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, throttledResponse);
        // Batch-level retry: succeeds
        handler.QueueResponse(HttpStatusCode.OK, successResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // 4 per-chunk + 1 batch-level = 5
        Assert.Equal(5, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(201, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Succeeded);
        Assert.Equal(1, result.Telemetry.BatchLevelRetries);
        Assert.True(result.Telemetry.ThrottleEncounters >= 4,
            $"Expected at least 4 throttle encounters but got {result.Telemetry.ThrottleEncounters}");
    }

    [Fact]
    public async Task BatchGet_VerboseWriter_LogsClampEvent()
    {
        // Server requests 300s Retry-After, client clamps to 120s (default).
        // VerboseWriter should receive a clamping message.
        var throttledResponse = """
        {
            "responses": [
                { "id": "1", "status": 429, "headers": { "Retry-After": "300" } }
            ]
        }
        """;
        var successResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, throttledResponse);
        handler.QueueResponse(HttpStatusCode.OK, successResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var verboseMessages = new List<string>();
        batchClient.VerboseWriter = msg => verboseMessages.Add(msg);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        await batchClient.ExecuteBatchIndexedAsync(operations);
        batchClient.DrainVerboseMessages();

        // Verify a clamping message was logged
        Assert.Contains(verboseMessages, m => m.Contains("300s") && m.Contains("clamped"));
    }

    private static string BuildBatchResponse(int count, int status, int? retryAfterSeconds = null)
    {
        var headers = retryAfterSeconds.HasValue
            ? $", \"headers\": {{ \"Retry-After\": \"{retryAfterSeconds.Value}\" }}"
            : "";
        var body = status is >= 200 and < 300
            ? ", \"body\": { \"id\": \"item\" }"
            : "";
        var items = string.Join(",\n",
            Enumerable.Range(1, count).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": {status}{headers}{body} }}"));
        return $"{{ \"responses\": [{items}] }}";
    }

    [Fact]
    public async Task BatchWrite_Pacing_AppliedBetweenChunks()
    {
        // 40 write items = 2 chunks of 20. With pacing at 20 items/sec,
        // there should be ~1s delay between chunks (minus elapsed).
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 20);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        sw.Stop();

        // With pacing, 2 chunks should take at least ~500ms (pacing delay after first chunk).
        // Without pacing, it completes in <100ms (mock handler is instant).
        Assert.True(sw.ElapsedMilliseconds >= 400,
            $"Expected >=400ms with pacing, got {sw.ElapsedMilliseconds}ms. Pacing may not have fired.");
        Assert.Equal(40, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_DisabledWhenZero()
    {
        // batchItemsPerSecond=0 should disable pacing entirely.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 0);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        sw.Stop();

        // Without pacing, mock handler returns instantly — should be <500ms.
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected <500ms without pacing, got {sw.ElapsedMilliseconds}ms. Pacing may still be active.");
        Assert.Equal(40, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_SkippedWhenBackpressureActive()
    {
        // When a chunk triggers 429 with Retry-After, cross-chunk backpressure
        // takes over. Pacing should NOT add extra delay on top.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        // First chunk: 429 with Retry-After (triggers backpressure)
        handler.QueueResponse((HttpStatusCode)429, null,
            new Dictionary<string, string> { ["Retry-After"] = "0" });
        // Retry of first chunk: success
        handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));
        // Second chunk: success
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 20);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        // Should complete without error — backpressure handles the 429,
        // and pacing doesn't stack on top.
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        Assert.Equal(40, result.Results.Count);
    }

    [Fact]
    public async Task BatchWrite_Pacing_CrossCallDelayApplied()
    {
        // Two separate GraphBatchClient instances each with 20 write items (1 chunk each).
        // Without cross-call pacing, both complete instantly (<100ms total).
        // With cross-call pacing at 20 items/sec, the second call should wait ~1000ms
        // minus the elapsed time of the first call.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        // Call 1: establishes pacing baseline
        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client1.ExecuteBatchIndexedAsync(ops);

        // Call 2: should be delayed by cross-call pacing
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        var result = await client2.ExecuteBatchIndexedAsync(ops);
        sw.Stop();

        // 20 items at 20/sec = 1000ms target. Mock handler is instant, so
        // nearly all of the target should be pacing delay.
        Assert.True(sw.ElapsedMilliseconds >= 600,
            $"Expected >=600ms cross-call pacing delay, got {sw.ElapsedMilliseconds}ms");
        Assert.Equal(20, result.Results.Count);
    }

    [Fact]
    public async Task BatchWrite_Pacing_CrossCallDisabledWhenZero()
    {
        // With batchItemsPerSecond=0, cross-call pacing should not fire even for writes.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 0);
        await client1.ExecuteBatchIndexedAsync(ops);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 0);
        var result = await client2.ExecuteBatchIndexedAsync(ops);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected <500ms without pacing, got {sw.ElapsedMilliseconds}ms");
        Assert.Equal(20, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_SkippedForGetOnlyBatch()
    {
        // A write batch (POST) followed by a GET-only batch. Cross-call pacing
        // should NOT delay the GET batch — GETs don't hit write throttle limits.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");

        // Call 1: write batch (POSTs) — establishes pacing state
        var writeOps = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();
        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client1.ExecuteBatchIndexedAsync(writeOps);

        // Call 2: GET-only batch — should NOT be paced
        var getOps = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        var result = await client2.ExecuteBatchIndexedAsync(getOps);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected <500ms for GET-only batch after writes, got {sw.ElapsedMilliseconds}ms. Pacing should skip GETs.");
        Assert.Equal(20, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_CrossCallCappedToOneChunk()
    {
        // A large write batch (40 items = 2 chunks) followed by a small write batch (20 items).
        // Cross-call delay should be capped to MaxBatchSize (20 items) worth = ~1000ms,
        // NOT 40 items worth = ~2000ms.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");

        // Call 1: 40-item write batch
        var largeOps = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();
        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client1.ExecuteBatchIndexedAsync(largeOps);

        // Call 2: 20-item write batch — cross-call delay should be ~1000ms (capped), not ~2000ms
        var smallOps = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client2.ExecuteBatchIndexedAsync(smallOps);
        sw.Stop();

        // Capped to 20 items at 20/sec = 1000ms max cross-call delay + intra-call pacing.
        // Without the cap, this would be ~2000ms cross-call + intra-call.
        // With the cap, total should be under 1500ms (cross-call ~1000ms + negligible HTTP).
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"Expected <1500ms with capped cross-call pacing, got {sw.ElapsedMilliseconds}ms. Cap may not be working.");
    }

    // ═══════════════════════════════════════════════════════════════
    // R3-1: Pacing state ordering — Volatile.Read/Write prevent
    // a concurrent reader from seeing new ticks with stale count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BatchWrite_Pacing_ConcurrentCallsAlwaysPace()
    {
        // R3-1: Prove the Volatile.Read/Write fix works under concurrent execution.
        //
        // Without proper memory fences, a concurrent reader can see new ticks but
        // stale count (0), causing the pacing guard to skip. Raw field access via
        // reflection confirmed 76,490 ordering violations in 100k iterations on
        // this hardware (ARM64 M-series).
        //
        // This test exercises the PRODUCTION code paths concurrently: 20 parallel
        // pairs of (write-batch → immediate second-batch). Each second batch must
        // observe the pacing state from SOME prior batch and apply delay. If any
        // second batch completes in <200ms, the pacing guard was skipped — meaning
        // the reader saw stale count=0 despite valid ticks.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation("/users", "POST", body))
            .ToArray();

        // Seed the pacing state so all concurrent readers have something to read
        var seed = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await seed.ExecuteBatchIndexedAsync(ops);

        // 20 concurrent calls — each should be paced because the static state has
        // count=20 and ticks=recent. If any completes in <200ms, pacing was skipped.
        var pacingSkipped = 0;
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var client = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
            await client.ExecuteBatchIndexedAsync(ops);
            sw.Stop();

            // Each call writes new pacing state on completion (line 346-347),
            // so subsequent readers will also see valid state.
            // If pacing was skipped (stale count=0), elapsed will be <50ms.
            if (sw.ElapsedMilliseconds < 200)
                Interlocked.Increment(ref pacingSkipped);
        });

        await Task.WhenAll(tasks);
        GraphBatchClient.ResetPacingState();

        Assert.True(pacingSkipped == 0,
            $"{pacingSkipped} of 20 concurrent batch calls completed in <200ms (pacing skipped). " +
            "This indicates Volatile.Read returned stale count=0, bypassing the pacing guard.");
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-3: High-risk missing tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_3a_AllItemsFail_20Items_ExhaustsRetries()
    {
        // All 20 items in a chunk return 429. Per-chunk retries (3) exhaust,
        // then Phase 2 batch-level retry fires.
        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts (initial + 3 retries), all 429
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(20, 429, retryAfterSeconds: 0));
        // Phase 2 batch-level retry: also all 429
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 429, retryAfterSeconds: 0));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        // All items should be present (with 429 status — failed but not lost)
        Assert.Equal(20, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(429, r.Response.Status));
        Assert.Equal(0, result.Telemetry.Succeeded);
        Assert.Equal(20, result.Telemetry.Failed);
        Assert.True(result.Telemetry.ItemRetries > 0);
        Assert.True(result.Telemetry.BatchLevelRetries > 0);
    }

    [Fact]
    public async Task R2_3a_AllItemsFail_500_NonRetryableForPost()
    {
        // All 20 POST items return 500. POST does NOT retry on 500 (only 429).
        // No per-chunk retries, no Phase 2 retry.
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 500));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(20, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(500, r.Response.Status));
        Assert.Equal(1, handler.RequestCount); // Only 1 HTTP call — no retries for POST+500
        Assert.Equal(0, result.Telemetry.Succeeded);
        Assert.Equal(20, result.Telemetry.Failed);
    }

    [Fact]
    public async Task R2_3b_PartialRetry_SomeFailSomeSucceed()
    {
        // Chunk with mixed results: items 1-10 succeed (201), items 11-20 fail (503).
        // Failed items should retry. On retry, all succeed.
        var mixedResponse = BuildMixedBatchResponse(10, 201, 10, 503);
        var allSuccessResponse = BuildBatchResponse(10, 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, mixedResponse);     // First attempt: mixed
        handler.SetDefaultResponse(HttpStatusCode.OK, allSuccessResponse); // Retries: all succeed

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(20, result.Results.Count);
        // All should eventually succeed (10 on first attempt, 10 on retry)
        Assert.Equal(20, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
        Assert.True(result.Telemetry.ItemRetries > 0);
        Assert.True(handler.RequestCount >= 2); // At least 2 HTTP calls
    }

    [Fact]
    public async Task R2_3c_MismatchedResponseIds_AreReportedAsAChunkFailure()
    {
        // Response IDs don't match request IDs — should throw InvalidOperationException
        var mismatchedResponse = """
        {
            "responses": [
                { "id": "99", "status": 200, "body": { "id": "user99" } },
                { "id": "98", "status": 200, "body": { "id": "user98" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // All 4 per-chunk attempts return mismatched IDs
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, mismatchedResponse);
        handler.SetDefaultResponse(HttpStatusCode.OK, mismatchedResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new[]
        {
            new BatchOperation("/users/1"),
            new BatchOperation("/users/2")
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        var ex = Assert.IsType<InvalidOperationException>(result.ChunkFailure);
        Assert.Contains("missing result", ex.Message);
    }

    [Fact]
    public async Task R2_3d_Phase2_TransportError_IsReportedWithTheResults()
    {
        // Per-chunk retries exhaust on 503. Phase 2 batch-level retry hits a transport error
        // (HttpRequestException). The pass runs once every chunk has been sent, so the run
        // already holds the outcome of the whole batch: the failure is reported alongside it.
        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts all 503
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(2, 503));
        // Phase 2: transport error
        handler.QueueException(new HttpRequestException("Connection refused"));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            AttemptTimeoutSeconds = 5,
            TotalTimeoutSeconds = 30
        });
        var batchClient = new GraphBatchClient(client);

        var ops = new[]
        {
            new BatchOperation("/users/1"),
            new BatchOperation("/users/2")
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.NotNull(result.ChunkFailure);
        // The 503s are what the server said about these two operations, and the retry pass
        // failing did not change that.
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(503, r.Response.Status));
        Assert.Equal(2, result.Telemetry.Failed);
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-5: Test with rate limiting enabled
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_5_BatchWithRateLimiting_Succeeds()
    {
        // All other batch tests use NoRateLimit=true. This one uses default
        // rate limiting (50/sec burst 200) to verify batch operations work
        // through the rate limiter.
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient); // Default options — rate limiter ON

        var batchClient = new GraphBatchClient(client);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(40, result.Results.Count);
        Assert.Equal(40, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-6: CancellationToken cancels during retry delays
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_6_CancellationDuringRetryDelay_CancelsPromptly()
    {
        // All items return 429 with Retry-After: 30. Cancel after 1 second.
        // Should cancel during the retry delay, not wait the full 30s.
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(2, 429, retryAfterSeconds: 30));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new[]
        {
            new BatchOperation("/users/1"),
            new BatchOperation("/users/2")
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batchClient.ExecuteBatchIndexedAsync(ops, cts.Token));

        sw.Stop();
        // Should cancel within ~3s, not wait for the 30s Retry-After
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"Cancellation took {sw.ElapsedMilliseconds}ms — should have cancelled within ~3s, not waited for Retry-After");
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-8: Chunk boundary tests (0, 1, 20, 21 items)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_8_ZeroItems_ReturnsEmpty()
    {
        var handler = new MockHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var result = await batchClient.ExecuteBatchIndexedAsync(
            Array.Empty<BatchOperation>(), CancellationToken.None);

        Assert.Empty(result.Results);
        Assert.Equal(0, handler.RequestCount); // No HTTP calls for empty input
    }

    [Fact]
    public async Task R2_8_OneItem_SingleChunk()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(1, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new[] { new BatchOperation("/users/1") };
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, handler.RequestCount); // Exactly 1 batch POST
    }

    [Fact]
    public async Task R2_8_TwentyItems_ExactlyOneChunk()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(20, result.Results.Count);
        Assert.Equal(1, handler.RequestCount); // Exactly 1 chunk
    }

    [Fact]
    public async Task R2_8_TwentyOneItems_TwoChunks()
    {
        var handler = new MockHttpHandler();
        // First chunk: 20 items, second chunk: 1 item
        handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(1, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 0); // No pacing delay for speed

        var ops = Enumerable.Range(1, 21)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(21, result.Results.Count);
        Assert.Equal(2, handler.RequestCount); // 2 chunks: 20 + 1
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: Build mixed batch response (some succeed, some fail)
    // ═══════════════════════════════════════════════════════════════

    private static string BuildMixedBatchResponse(
        int successCount, int successStatus,
        int failCount, int failStatus)
    {
        var items = new List<string>();
        for (int i = 1; i <= successCount; i++)
            items.Add($"{{ \"id\": \"{i}\", \"status\": {successStatus}, \"body\": {{ \"id\": \"item\" }} }}");
        for (int i = successCount + 1; i <= successCount + failCount; i++)
            items.Add($"{{ \"id\": \"{i}\", \"status\": {failStatus} }}");
        return $"{{ \"responses\": [{string.Join(",\n", items)}] }}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Dead-letter: RedactSensitiveFields tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RedactSensitiveFields_RedactsPasswordProfile()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "Test User",
            "passwordProfile": {
                "password": "Secret123!",
                "forceChangePasswordNextSignIn": true
            }
        }
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("Test User", obj["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["passwordProfile"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_RedactsNestedPassword()
    {
        var node = JsonNode.Parse("""
        {
            "accountEnabled": true,
            "nested": {
                "password": "hunter2"
            }
        }
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.True(obj["accountEnabled"]!.GetValue<bool>());
        var nested = obj["nested"]!.AsObject();
        Assert.Equal("***REDACTED***", nested["password"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_PreservesNonSensitiveFields()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "Test",
            "mailNickname": "test",
            "accountEnabled": false
        }
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("Test", obj["displayName"]!.GetValue<string>());
        Assert.Equal("test", obj["mailNickname"]!.GetValue<string>());
        Assert.False(obj["accountEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void RedactSensitiveFields_HandlesNullNode()
    {
        // Should not throw, and a body that was nothing is still nothing.
        Assert.Null(InvokeMgxBatchRequest.RedactSensitiveFields(null));
    }

    [Fact]
    public void RedactSensitiveFields_RedactsKeyAndPasswordCredentials()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "App",
            "keyCredentials": [{"key": "abc"}],
            "passwordCredentials": [{"secretText": "xyz"}]
        }
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("App", obj["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["keyCredentials"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["passwordCredentials"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_DeepNesting_FiveLevels()
    {
        var node = JsonNode.Parse("""
        {
            "a": { "b": { "c": { "d": { "password": "deep-secret" } } } }
        }
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var d = node!["a"]!["b"]!["c"]!["d"]!.AsObject();
        Assert.Equal("***REDACTED***", d["password"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_RedactsClientSecretAndAppPassword()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "App",
            "clientSecret": "s3cr3t-value",
            "appPassword": "app-pwd-123",
            "clientAssertion": "jwt-token-here"
        }
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("App", obj["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["clientSecret"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["appPassword"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["clientAssertion"]!.GetValue<string>());
    }

    // R3-4: RedactSensitiveFields must recurse into JsonArray elements
    [Fact]
    public void RedactSensitiveFields_RedactsInsideArrays()
    {
        var node = JsonNode.Parse("""
        {
            "users": [
                { "displayName": "Alice", "passwordProfile": { "password": "Secret123" } },
                { "displayName": "Bob", "clientSecret": "abc-def" }
            ]
        }
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var users = node!["users"]!.AsArray();
        Assert.Equal("Alice", users[0]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", users[0]!["passwordProfile"]!.GetValue<string>());
        Assert.Equal("Bob", users[1]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", users[1]!["clientSecret"]!.GetValue<string>());
    }

    /// <summary>
    /// The URL rule reads path segments. Split naively, an absolute URL hands its scheme and
    /// host to the same test as its path, and a tenant whose Graph host is called
    /// keyvault.example.com loses every body it ever dead-letters - the file keeps its
    /// Url/Method/Status and nothing to read.
    /// </summary>
    [Theory]
    [InlineData("https://keyvault.example.com/v1.0/groups/g1", false)]
    [InlineData("https://tokens.contoso.com/v1.0/users/u1", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/u1/resetPassword", true)]
    [InlineData("https://graph.microsoft.com/v1.0/applications/a1/addKey", true)]
    [InlineData("/trustFramework/keySets/ks1/uploadSecret", true)]
    [InlineData("/servicePrincipals/sp1/synchronization/secrets", true)]
    [InlineData("/users/u1/resetPassword", true)]
    [InlineData("/groups/g1?$select=displayName", false)]
    // A query string, not a path. On Unix a leading slash parses as a file URI and the parser
    // folds the query into the path, so these read as one more segment unless the scheme is
    // checked - and they would then answer differently here and on Windows.
    [InlineData("/users/u1?password=1", false)]
    [InlineData("/servicePrincipals/x?$select=keyCredentials", false)]
    [InlineData("users/u1?password=1", false)]
    // %50 is 'P'. Graph reads the path decoded, so this is the same reset the two rows above
    // are; the parser unescapes it in the absolute form and the batch envelope carries the
    // relative one, which is where a name could hide behind an escape.
    [InlineData("/users/u1/microsoft.graph.reset%50assword", true)]
    [InlineData("https://graph.microsoft.com/v1.0/users/u1/microsoft.graph.reset%50assword", true)]
    // An escape that decodes to nothing anyone named, and one that does not decode at all.
    [InlineData("/groups/g1/%6Dembers", false)]
    [InlineData("/groups/100%25", false)]
    public void UrlNamesSecret_ReadsPathSegmentsOnly(string url, bool expected)
    {
        Assert.Equal(expected, InvokeMgxBatchRequest.UrlNamesSecret(url));
    }

    [Fact]
    public void RedactSensitiveFields_RedactsRootLevelArray()
    {
        // If the body is a root-level JSON array (not wrapped in an object),
        // sensitive fields inside array elements must still be redacted.
        var node = JsonNode.Parse("""
        [
            { "displayName": "Alice", "password": "Secret123" },
            { "displayName": "Bob", "passwordProfile": { "password": "abc" } }
        ]
        """);

        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var arr = node!.AsArray();
        Assert.Equal("Alice", arr[0]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", arr[0]!["password"]!.GetValue<string>());
        Assert.Equal("Bob", arr[1]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", arr[1]!["passwordProfile"]!.GetValue<string>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Dead-letter: what a written line is allowed to carry
    // ═══════════════════════════════════════════════════════════════

    private const string OneItemRejected = """
    { "responses": [
        { "id": "1", "status": 400, "body": { "error": { "code": "BadRequest", "message": "rejected" } } }
    ] }
    """;

    private const string OneItemAccepted = """
    { "responses": [
        { "id": "1", "status": 200, "body": { "id": "u1" } }
    ] }
    """;

    private static System.Management.Automation.PowerShell CreateShell()
    {
        var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(InvokeMgxBatchRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    /// <summary>
    /// Reads the dead-letter file while a sibling run still holds it open to append to. Windows
    /// checks the sharing both ways round - what the reader asks of the file, and what it will
    /// let a handle already on it go on doing - so File.ReadAllLines, which asks for
    /// FileShare.Read and nothing more, is refused by the run this test left holding it. The
    /// sharing that holder asked for is offered back, which is what a reader of a file a live
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
    /// Sends one item the server refuses and returns the line the dead-letter file got for it.
    /// The assertions are about the file, not about the redactor: what a caller can read off
    /// disk is the whole question, and the URL rule only exists in the writer.
    /// </summary>
    private static string DeadLetterLineFor(string url, string method, System.Collections.Hashtable body)
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, OneItemRejected);
        using var transport = MgxTransportScope.Inject(handler);

        var path = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[]
              {
                  new System.Collections.Hashtable
                  {
                      ["Url"] = url,
                      ["Method"] = method,
                      ["Body"] = body
                  }
              })
              .AddParameter("DeadLetterPath", path);
            ps.Invoke();

            Assert.True(File.Exists(path), "dead-letter file was not written");
            return Assert.Single(File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RedactedDeadLetterLine_OmitsANewPasswordAtTheTopOfTheBody()
    {
        var line = DeadLetterLineFor("/users/u1", "PATCH", new System.Collections.Hashtable
        {
            ["displayName"] = "Keep This",
            ["newPassword"] = "sentinel-new-password"
        });

        Assert.DoesNotContain("sentinel-new-password", line);
        Assert.Contains("***REDACTED***", line);
        // The diagnostic half of the line is what the file is for; only the credential goes.
        Assert.Contains("Keep This", line);
    }

    [Fact]
    public void RedactedDeadLetterLine_OmitsAPreSharedKeyAndASharedSecretMidBody()
    {
        var line = DeadLetterLineFor("/deviceManagement/deviceConfigurations", "POST",
            new System.Collections.Hashtable
            {
                ["displayName"] = "Corp Wi-Fi",
                ["wifiSettings"] = new System.Collections.Hashtable
                {
                    ["preSharedKey"] = "sentinel-wifi-psk"
                },
                ["vpnSettings"] = new System.Collections.Hashtable
                {
                    ["sharedSecret"] = "sentinel-vpn-secret"
                }
            });

        Assert.DoesNotContain("sentinel-wifi-psk", line);
        Assert.DoesNotContain("sentinel-vpn-secret", line);
        Assert.Contains("Corp Wi-Fi", line);
    }

    [Fact]
    public void RedactedDeadLetterLine_OmitsASecretTextNestedInAPasswordCredential()
    {
        var line = DeadLetterLineFor("/applications/app1", "PATCH", new System.Collections.Hashtable
        {
            ["displayName"] = "Corp App",
            ["passwordCredential"] = new System.Collections.Hashtable
            {
                ["displayName"] = "rotation",
                ["secretText"] = "sentinel-secret-text"
            }
        });

        Assert.DoesNotContain("sentinel-secret-text", line);
        Assert.Contains("Corp App", line);
    }

    [Fact]
    public void RedactedDeadLetterLine_OmitsAPasswordInsideAnArrayOfArrays()
    {
        // An array directly inside an array: no object between them, so a redactor that only
        // steps from an array into an object never reaches the field. A -Body is whatever the
        // caller built, so the writer cannot assume a shape.
        var line = DeadLetterLineFor("/deviceManagement/configurationPolicies", "POST",
            new System.Collections.Hashtable
            {
                ["name"] = "Corp Baseline",
                ["settings"] = new object[]
                {
                    new object[]
                    {
                        new System.Collections.Hashtable { ["password"] = "sentinel-nested-array" }
                    }
                }
            });

        Assert.DoesNotContain("sentinel-nested-array", line);
        Assert.Contains("Corp Baseline", line);
    }

    [Fact]
    public void RedactedDeadLetterLine_OmitsEveryNameTheExactListCovered()
    {
        var line = DeadLetterLineFor("/users/u1", "PATCH", new System.Collections.Hashtable
        {
            ["displayName"] = "Corp User",
            ["passwordProfile"] = new System.Collections.Hashtable
            {
                ["password"] = "sentinel-passwordprofile"
            },
            ["password"] = "sentinel-password",
            ["secretText"] = "sentinel-secrettext",
            ["keyCredentials"] = new object[]
            {
                new System.Collections.Hashtable { ["key"] = "sentinel-keycredentials" }
            },
            ["passwordCredentials"] = new object[]
            {
                new System.Collections.Hashtable { ["secretText"] = "sentinel-passwordcredentials" }
            },
            ["clientSecret"] = "sentinel-clientsecret",
            ["appPassword"] = "sentinel-apppassword",
            ["clientAssertion"] = "sentinel-clientassertion"
        });

        foreach (var sentinel in new[]
        {
            "sentinel-passwordprofile", "sentinel-password", "sentinel-secrettext",
            "sentinel-keycredentials", "sentinel-passwordcredentials", "sentinel-clientsecret",
            "sentinel-apppassword", "sentinel-clientassertion"
        })
        {
            Assert.DoesNotContain(sentinel, line);
        }

        Assert.Contains("Corp User", line);
    }

    [Fact]
    public void RedactedDeadLetterLine_ReplacesTheWholeBodyWhenTheUrlNamesAnUploadSecret()
    {
        // The credential is the field called `k`; nothing about that name says so.
        var line = DeadLetterLineFor("/trustFramework/keySets/ks1/uploadSecret", "POST",
            new System.Collections.Hashtable
            {
                ["use"] = "sig",
                ["k"] = "sentinel-upload-secret"
            });

        Assert.DoesNotContain("sentinel-upload-secret", line);

        var written = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("***REDACTED***", written["Body"]!.GetValue<string>());
        // What the line is still worth: everything a caller needs to decide on the item.
        Assert.Equal("/trustFramework/keySets/ks1/uploadSecret", written["Url"]!.GetValue<string>());
        Assert.Equal("POST", written["Method"]!.GetValue<string>());
        Assert.Equal(400, written["Status"]!.GetValue<int>());
        Assert.Contains("BadRequest", written["Error"]!.GetValue<string>());
    }

    [Fact]
    public void RedactedDeadLetterLine_ReplacesTheWholeBodyForSynchronizationSecrets()
    {
        // Here the credential is one `value` among a list of them.
        var line = DeadLetterLineFor("/servicePrincipals/sp1/synchronization/secrets", "PUT",
            new System.Collections.Hashtable
            {
                ["value"] = new object[]
                {
                    new System.Collections.Hashtable
                    {
                        ["key"] = "ClientSecret",
                        ["value"] = "sentinel-sync-secret"
                    }
                }
            });

        Assert.DoesNotContain("sentinel-sync-secret", line);

        var written = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("***REDACTED***", written["Body"]!.GetValue<string>());
    }

    /// <summary>
    /// A dead-letter line re-piped the way -DeadLetterPath's recipe says to. A Body that is the
    /// marker whole is refused already - the marker is not JSON - but a Body carrying it in one
    /// field parses, and went to the tenant with the literal marker where the credential was.
    /// </summary>
    [Fact]
    public void RePipedBody_CarryingTheRedactionMarkerInAField_IsRefusedAndNotSent()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, OneItemRejected);
        using var transport = MgxTransportScope.Inject(handler);

        using var ps = CreateShell();
        ps.AddCommand("Invoke-MgxBatchRequest")
          .AddParameter("Uri", new object[]
          {
              new System.Collections.Hashtable
              {
                  ["Url"] = "/users/u1",
                  ["Method"] = "PATCH",
                  ["Body"] = new System.Collections.Hashtable
                  {
                      ["displayName"] = "Keep",
                      ["passwordProfile"] = "***REDACTED***"
                  }
              },
              new System.Collections.Hashtable
              {
                  ["Url"] = "/groups/g1",
                  ["Method"] = "PATCH",
                  ["Body"] = new System.Collections.Hashtable { ["displayName"] = "the-other-item" }
              }
          });
        ps.Invoke();

        var error = Assert.Single(ps.Streams.Error,
            e => e.FullyQualifiedErrorId.StartsWith("InvalidBatchItemBody", StringComparison.Ordinal));
        Assert.Contains("PATCH /users/u1", error.Exception.Message);
        // The field is named: it says which value the file took, and so which one to supply.
        Assert.Contains("passwordProfile", error.Exception.Message);

        // The rest of the file still goes out, and the marker never reaches the wire.
        var envelope = Assert.Single(handler.Requests).Content!
            .ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain("REDACTED", envelope);
        Assert.Contains("the-other-item", envelope);
    }

    /// <summary>
    /// An ordinary call, never a dead-letter line: a hashtable body a caller wrote by hand, whose
    /// own text happens to carry the marker. The refusal is the same one a re-piped line gets,
    /// because nothing in the text tells the two apart, so the message has to hold for both -
    /// naming the field, and both remedies: a re-piped line is rebuilt from its Url and Method,
    /// and a body of the caller's own must not carry that text at all.
    /// </summary>
    [Fact]
    public void OrdinaryBody_CarryingTheMarkerText_IsRefusedNamingTheFieldAndBothRemedies()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, """{"responses":[{"id":"1","status":200,"body":{}}]}""");
        using var transport = MgxTransportScope.Inject(handler);

        using var ps = CreateShell();
        ps.AddCommand("Invoke-MgxBatchRequest")
          .AddParameter("Uri", new object[]
          {
              new System.Collections.Hashtable
              {
                  ["Url"] = "/users/u1",
                  ["Method"] = "PATCH",
                  ["Body"] = new System.Collections.Hashtable
                  {
                      ["displayName"] = "Redaction demo ***REDACTED*** room"
                  }
              }
          });
        ps.Invoke();

        var error = Assert.Single(ps.Streams.Error,
            e => e.FullyQualifiedErrorId.StartsWith("InvalidBatchItemBody", StringComparison.Ordinal));
        Assert.Equal(
            "Body for PATCH /users/u1 carries the redaction marker '***REDACTED***' at "
            + "'displayName'. A dead-letter line is not the body that was sent: rebuild the "
            + "request from its Url and Method, supplying the credential again. A body of your "
            + "own must not carry that text.",
            error.Exception.Message);

        // A body that never came from the file is refused the same way, before anything is sent.
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The same recipe on a line whose marker is in a KEY, not a value. The writer cuts a
    /// pre-authenticated URL used as a field name at its capability parameter and re-adds the
    /// property under the cut name, suffixing #2 when two keys cut to the same string - so the
    /// marker can be in a line with no string value carrying it. A guard reading values only
    /// passed such a line through, and the tenant got the literal marker as a field name.
    /// </summary>
    [Fact]
    public void RePipedBody_CarryingTheRedactionMarkerInAKey_IsRefusedAndNothingIsSent()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, OneItemRejected);
        using var transport = MgxTransportScope.Inject(handler);

        using var ps = CreateShell();
        ps.AddCommand("Invoke-MgxBatchRequest")
          .AddParameter("Uri", new object[]
          {
              new System.Collections.Hashtable
              {
                  ["Url"] = "/users/u1",
                  ["Method"] = "PATCH",
                  ["Body"] = """{"https://h/f?sig=***REDACTED***#2":"1"}"""
              }
          });
        ps.Invoke();

        var error = Assert.Single(ps.Streams.Error,
            e => e.FullyQualifiedErrorId.StartsWith("InvalidBatchItemBody", StringComparison.Ordinal));
        Assert.Contains("PATCH /users/u1", error.Exception.Message);
        // The key as written, collision suffix and all: it is what has to be supplied again.
        Assert.Contains("https://h/f?sig=***REDACTED***#2", error.Exception.Message);

        // The only item was refused, so no chunk was built and nothing reached the wire.
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The same recipe on the shape the URL rule leaves, where the marker sits INSIDE a value:
    /// a pre-authenticated URL is cut after its capability parameter, so what stands in the
    /// file is the URL prefix with the marker after it and never the marker alone. Comparing a
    /// value to the marker whole let those lines through, and the tenant got a
    /// "?sig=***REDACTED***" URL no host will honor in place of the one the caller believes it
    /// supplied. Three shapes, because the writer cuts a URL in all three: a property's value,
    /// one nested in an array of objects, and a bare array element with no property name at all.
    /// </summary>
    [Fact]
    public void RePipedBody_CarryingACutCapabilityUrlInAValue_IsRefusedAndNothingIsSent()
    {
        (System.Collections.Hashtable Body, string Field)[] shapes =
        [
            (new System.Collections.Hashtable
            {
                ["uploadUri"] = "https://s.blob.core.windows.net/c/b?sv=2021&sig=sentinel-sas"
            }, "uploadUri"),
            (new System.Collections.Hashtable
            {
                ["files"] = new object[]
                {
                    new System.Collections.Hashtable
                    {
                        ["uploadUri"] = "https://h/f?tempauth=sentinel-jwt"
                    }
                }
            }, "files[0].uploadUri"),
            (new System.Collections.Hashtable
            {
                ["a"] = new object[] { "https://h/f?sig=sentinel-bare" }
            }, "a[0]")
        ];

        foreach (var (body, field) in shapes)
        {
            // A real failed item's line, read back the way -DeadLetterPath's recipe reads it.
            var written = JsonNode.Parse(DeadLetterLineFor("/users/u1", "PATCH", body))!;
            var rePiped = written["Body"]!.ToJsonString();
            Assert.Contains(InvokeMgxBatchRequest.RedactionMarker, rePiped, StringComparison.Ordinal);

            var handler = new MockHttpHandler();
            handler.SetDefaultResponse(HttpStatusCode.OK, OneItemRejected);
            using var transport = MgxTransportScope.Inject(handler);

            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[]
              {
                  new System.Collections.Hashtable
                  {
                      ["Url"] = "/users/u1",
                      ["Method"] = "PATCH",
                      ["Body"] = rePiped
                  }
              });
            ps.Invoke();

            var error = Assert.Single(ps.Streams.Error,
                e => e.FullyQualifiedErrorId.StartsWith("InvalidBatchItemBody", StringComparison.Ordinal));
            Assert.Contains("PATCH /users/u1", error.Exception.Message);
            // The field is named at its own depth, which is what says what to supply again.
            Assert.Contains(field, error.Exception.Message);

            // The only item was refused, so no chunk was built and nothing reached the wire.
            Assert.Empty(handler.Requests);
        }
    }

    /// <summary>
    /// A raw-string body the JSON parser accepts and the marker guard cannot read. An unpaired
    /// surrogate escape is a legal string token whose value has no UTF-16 form, so the parse
    /// succeeds and the read of that one string throws - and the guard reads every string of
    /// every body. It is one item's body, so it is refused as one, like a body that is not JSON
    /// at all: the guard is the only thing that decides whether a body may be sent, so a body
    /// it cannot read is a body it cannot clear.
    /// </summary>
    [Fact]
    public void Body_TheMarkerGuardCannotRead_IsRefusedAndTheOtherItemIsSent()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK,
            """{"responses":[{"id":"1","status":201,"body":{"id":"g1"}}]}""");
        using var transport = MgxTransportScope.Inject(handler);

        using var ps = CreateShell();
        ps.AddCommand("Invoke-MgxBatchRequest")
          .AddParameter("Uri", new object[]
          {
              new System.Collections.Hashtable
              {
                  ["Url"] = "/users",
                  ["Method"] = "POST",
                  ["Body"] = """{"displayName":"Jos\ud83d"}"""
              },
              new System.Collections.Hashtable
              {
                  ["Url"] = "/groups",
                  ["Method"] = "POST",
                  ["Body"] = new System.Collections.Hashtable { ["displayName"] = "the-other-item" }
              }
          });
        var output = ps.Invoke();

        var error = Assert.Single(ps.Streams.Error,
            e => e.FullyQualifiedErrorId.StartsWith("InvalidBatchItemBody", StringComparison.Ordinal));
        Assert.StartsWith(
            "Body for POST /users could not be read for the redaction check: ",
            error.Exception.Message, StringComparison.Ordinal);
        // The runtime's own words for what it could not read, and the remedy. The reason's
        // trailing period is trimmed rather than doubled onto the sentence quoting it.
        Assert.Contains("surrogate", error.Exception.Message, StringComparison.Ordinal);
        Assert.EndsWith(". Send it as a hashtable or a string this run can read.",
            error.Exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("..", error.Exception.Message, StringComparison.Ordinal);
        Assert.Equal("/users", error.TargetObject);

        // One item was refused; the other one went, was answered, and is in the output. The run
        // used to end on the unreadable body with nothing sent and no record of anything.
        var envelope = Assert.Single(handler.Requests).Content!
            .ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.Contains("the-other-item", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("Jos", envelope, StringComparison.Ordinal);

        var result = Assert.Single(output).BaseObject as System.Collections.Hashtable;
        Assert.NotNull(result);
        Assert.Equal("/groups", result["Url"]);
        Assert.Equal(201, result["Status"]);
    }

    [Fact]
    public void FindRedactionMarker_NamesTheFieldAtAnyDepth()
    {
        var nested = JsonSerializer.Deserialize<JsonElement>(
            """{"a":{"b":[{"c":"x"},{"keyCredentials":"***REDACTED***"}]}}""");
        Assert.Equal("a.b[1].keyCredentials", InvokeMgxBatchRequest.FindRedactionMarker(nested));

        // A value is matched as a substring, not compared whole. The writer's cut leaves the
        // URL up to the capability parameter in front of the marker, so a comparison saw only
        // the cuts that took a whole value; and nothing in the text tells the file's marker
        // from a caller's own, since the file's is a prefix plus the marker too. A caller who
        // writes "***REDACTED***x" himself is refused with the rest - renaming a field costs
        // less than a request the tenant cannot honor.
        var ownText = JsonSerializer.Deserialize<JsonElement>("""{"a":["***REDACTED***x"],"b":1}""");
        Assert.Equal("a[0]", InvokeMgxBatchRequest.FindRedactionMarker(ownText));

        // The cut form the URL rule actually leaves in a value, at the depth the writer reaches.
        var cutUrl = JsonSerializer.Deserialize<JsonElement>(
            """{"files":[{"uploadUri":"https://h/f?tempauth=***REDACTED***"}]}""");
        Assert.Equal("files[0].uploadUri", InvokeMgxBatchRequest.FindRedactionMarker(cutUrl));

        // A name is matched the same way: a key carrying the marker is never the marker alone,
        // and the path returned is the name as written.
        var inAKey = JsonSerializer.Deserialize<JsonElement>(
            """{"keep":"v","https://h/f?sig=***REDACTED***#2":"1"}""");
        Assert.Equal("https://h/f?sig=***REDACTED***#2",
            InvokeMgxBatchRequest.FindRedactionMarker(inAKey));

        // Nested, so the path is built the same way the value case builds it.
        var nestedKey = JsonSerializer.Deserialize<JsonElement>(
            """{"a":[{"https://h/f?tempauth=***REDACTED***":"1"}]}""");
        Assert.Equal("a[0].https://h/f?tempauth=***REDACTED***",
            InvokeMgxBatchRequest.FindRedactionMarker(nestedKey));
    }

    /// <summary>
    /// Intune's content-file body, on the line a caller reads off disk. The credential is the
    /// URL: azureStorageUri and uploadUrl fetch and fill the blob on the strength of the SAS
    /// alone, and no name in the body says so. A -Debug trace of the same body has hidden them
    /// all along; BodyRedactionTests holds the two side by side.
    /// </summary>
    [Fact]
    public void RedactedDeadLetterLine_CutsAPreAuthenticatedUrlAtItsCapabilityParameter()
    {
        var line = DeadLetterLineFor(
            "/deviceAppManagement/mobileApps/a1/contentVersions/1/files/f1", "PATCH",
            new System.Collections.Hashtable
            {
                ["azureStorageUri"] = "https://s.blob.core.windows.net/c/b?sv=2021-08-06&sig=sentinel-sas&se=2026",
                ["@microsoft.graph.downloadUrl"] = "https://public.bl.files.1drv.com/y4msentinel-path/f.bin"
            });

        Assert.DoesNotContain("sentinel-sas", line);
        Assert.DoesNotContain("sentinel-path", line);

        var body = JsonNode.Parse(line)!["Body"]!.AsObject();
        // The host and the parameter name stay. Which blob and which kind of URL is the half
        // the file is read for, and losing it would leave the line saying only that one failed.
        Assert.Equal("https://s.blob.core.windows.net/c/b?sv=2021-08-06&sig=***REDACTED***",
            body["azureStorageUri"]!.GetValue<string>());
        // The capability is in the path here, so the property name is what reaches it and the
        // whole value goes.
        Assert.Equal("***REDACTED***", body["@microsoft.graph.downloadUrl"]!.GetValue<string>());
    }

    /// <summary>
    /// A pre-authenticated URL used as the item URL rather than as a value inside a body. An
    /// upload session's uploadUrl and a @microsoft.graph.downloadUrl are requests a caller
    /// makes, so the URL a batch item names is as much a credential as one a body carries - and
    /// the line wrote it verbatim while cutting the identical URL beside it in the Body.
    ///
    /// Both halves, from the run's own artifacts: the line on disk, and the envelope as the
    /// trace formats it. NormalizeToRelativeUrl takes the scheme and host off before the POST,
    /// so the two forms are not the same text and each needed its own rule.
    /// </summary>
    [Fact]
    public void ACapabilityUrlUsedAsTheItemUrl_IsCutInTheFileAndInTheTrace()
    {
        const string itemUrl = "https://s.blob.core.windows.net/c/b?sv=2021&sig=sentinel-item-sas";

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, OneItemRejected);
        using var transport = MgxTransportScope.Inject(handler);

        var path = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        string line;
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[]
              {
                  new System.Collections.Hashtable
                  {
                      ["Url"] = itemUrl,
                      ["Method"] = "PUT",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "Keep This" }
                  }
              })
              .AddParameter("DeadLetterPath", path);
            ps.Invoke();

            Assert.True(File.Exists(path), "dead-letter file was not written");
            line = Assert.Single(File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.DoesNotContain("sentinel-item-sas", line);
        var written = JsonNode.Parse(line)!.AsObject();
        // Cut where a body's value would be cut: the host and the parameter name stay, so the
        // line still says which blob and which kind of URL failed.
        Assert.Equal("https://s.blob.core.windows.net/c/b?sv=2021&sig=***REDACTED***",
            written["Url"]!.GetValue<string>());
        // The rest of the line is what the file is read for.
        Assert.Equal("PUT", written["Method"]!.GetValue<string>());
        Assert.Contains("Keep This", line);

        // The same URL on the wire, relative by then, in the trace of the envelope that carried
        // it. A trace is what users paste into issue reports.
        var captured = Assert.Single(handler.CapturedRequests);
        var trace = GraphRequestTracer.FormatRequest(
            new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/$batch"),
            captured.Body, 1);
        Assert.DoesNotContain("sentinel-item-sas", trace, StringComparison.Ordinal);
        // The envelope's serializer escapes an ampersand, so the query in the trace is not the
        // query in the file - "?sv=2021\u0026sig=". What both keep is the parameter name, which
        // is what says which kind of URL went.
        Assert.Contains("/c/b?sv=2021", trace, StringComparison.Ordinal);
        Assert.Contains("sig=<redacted>", trace, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 429s a batch met and the time it spent in Retry-After sleeps, in the session view
    /// after a run that -ErrorAction Stop ended on an item error. Those counters describe what
    /// the wire did, which is settled by the time the batch returns; they used to be recorded at
    /// the very end of the reporting, past the dead-letter write, the chunk failure and one
    /// error record per failed item, so a run that stopped on any of those told Get-MgxTelemetry
    /// nothing - and a throttled, retried batch is exactly the run whose numbers a caller opens
    /// that view to find.
    ///
    /// Both preferences, because the point is that they agree: the stop must cost the caller
    /// nothing the run already measured.
    /// </summary>
    [Theory]
    [InlineData(System.Management.Automation.ActionPreference.Continue)]
    [InlineData(System.Management.Automation.ActionPreference.Stop)]
    public void SessionTelemetry_SurvivesAStopOnTheItemErrors(
        System.Management.Automation.ActionPreference errorAction)
    {
        const string throttledAndRejected = """
        { "responses": [
            { "id": "1", "status": 429, "headers": { "Retry-After": "0" } },
            { "id": "2", "status": 400, "body": { "error": { "code": "BadRequest", "message": "rejected" } } }
        ] }
        """;
        const string retrySucceeded = """
        { "responses": [ { "id": "1", "status": 201, "body": { "id": "u1" } } ] }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, throttledAndRejected);
        handler.QueueResponse(HttpStatusCode.OK, retrySucceeded);
        using var transport = MgxTransportScope.Inject(handler);

        MgxTelemetryCollector.Current.Reset();
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[]
              {
                  new System.Collections.Hashtable { ["Url"] = "/users", ["Method"] = "POST",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "a" } },
                  new System.Collections.Hashtable { ["Url"] = "/groups", ["Method"] = "POST",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "b" } }
              })
              .AddParameter("ErrorAction", errorAction);

            if (errorAction == System.Management.Automation.ActionPreference.Stop)
                Assert.ThrowsAny<Exception>(() => ps.Invoke());
            else
                ps.Invoke();

            var session = MgxTelemetryCollector.Current.GetSummary();
            // One item was answered 429 and retried, and the retry waited.
            Assert.Equal(1, session.BatchItemThrottles);
            Assert.True(session.RetryDelayMs > 0, $"RetryDelayMs was {session.RetryDelayMs}");
        }
        finally
        {
            MgxTelemetryCollector.Current.Reset();
        }
    }

    private const string TwoItemsRejected = """
    { "responses": [
        { "id": "1", "status": 400, "body": { "error": { "code": "BadRequest", "message": "rejected" } } },
        { "id": "2", "status": 400, "body": { "error": { "code": "BadRequest", "message": "rejected" } } }
    ] }
    """;

    /// <summary>
    /// Sends items the server refuses and returns every line the dead-letter file got, with the
    /// warnings the run wrote. Nothing here asserts a line count: what a body the writer cannot
    /// read costs the item and the items after it is the question. A run that ends on such a
    /// body throws out of EndProcessing, so ps.Invoke() throwing is itself the failure - and it
    /// names the exception, which is what the assertions below could not.
    /// </summary>
    private static (string[] Lines, string[] Warnings) DeadLetterRun(string response, params object[] items)
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, response);
        using var transport = MgxTransportScope.Inject(handler);

        var path = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", items)
              .AddParameter("DeadLetterPath", path);
            ps.Invoke();

            return (File.Exists(path) ? File.ReadAllLines(path) : [],
                    ps.Streams.Warning.Select(w => w.Message).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A raw-string body with the same property name twice. Graph takes it, and JsonNode.Parse
    /// takes it; enumerating the JsonObject it built is what throws, so the failure lands in
    /// the writer with the request already sent and answered. The line is owed either way.
    /// </summary>
    [Fact]
    public void DeadLetterLine_ForABodyTheRedactorCannotWalk_CarriesTheMarkerAndWarns()
    {
        var (lines, warnings) = DeadLetterRun(TwoItemsRejected,
            new System.Collections.Hashtable
            {
                ["Url"] = "/users",
                ["Method"] = "POST",
                ["Body"] = """{"displayName":"first","displayName":"second"}"""
            },
            new System.Collections.Hashtable
            {
                ["Url"] = "/groups",
                ["Method"] = "POST",
                ["Body"] = new System.Collections.Hashtable { ["displayName"] = "the-second-item" }
            });

        Assert.Equal(2, lines.Length);
        var first = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("/users", first["Url"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", first["Body"]!.GetValue<string>());
        // The body was never read, so nothing in it may be written - not even the half a
        // duplicate key left readable.
        Assert.DoesNotContain("first", lines[0]);

        // The item after it still gets its line: one unreadable body is not the run.
        Assert.Contains("the-second-item", lines[1]);

        // Not "Contains(withheld)" alone: the outcome warning now folds the withheld count
        // into its own text too, so that predicate would match both. "POST /users" is what
        // is unique to the per-item warning under test here.
        var warning = Assert.Single(warnings, w => w.Contains("POST /users") && w.Contains("withheld"));
        Assert.Contains("could not be read for redaction", warning);
    }

    /// <summary>
    /// The same body on the second of two items. The writer holds one StreamWriter open across
    /// the loop, so an exception in the middle of it is what decides whether the lines already
    /// written reach disk at all.
    /// </summary>
    [Fact]
    public void DeadLetterFile_IsNotCutShortByABodyTheRedactorCannotWalk()
    {
        var (lines, warnings) = DeadLetterRun(TwoItemsRejected,
            new System.Collections.Hashtable
            {
                ["Url"] = "/groups",
                ["Method"] = "POST",
                ["Body"] = new System.Collections.Hashtable { ["displayName"] = "the-first-item" }
            },
            new System.Collections.Hashtable
            {
                ["Url"] = "/users",
                ["Method"] = "POST",
                ["Body"] = """{"displayName":"first","displayName":"second"}"""
            });

        Assert.Equal(2, lines.Length);
        Assert.Contains("the-first-item", lines[0]);
        Assert.Equal("***REDACTED***", JsonNode.Parse(lines[1])!["Body"]!.GetValue<string>());
        Assert.Single(warnings, w => w.Contains("POST /users") && w.Contains("withheld"));
    }

    /// <summary>
    /// WriteWarning throws under -WarningAction Stop. The withheld-body warning used to be
    /// written right after the dead-letter file closed - before the item errors and before
    /// WriteBatchTelemetry - so that throw cut the run off before either ran: two refused items
    /// produced two dead-letter lines and zero error records. The warning now runs last, so the
    /// warning that actually stops this run is WriteBatchTelemetry's own outcome warning (it
    /// still has one to give, since both items failed), and reaching it proves the item errors
    /// and the telemetry it summarizes already ran. That outcome warning now folds the withheld
    /// count in too, so it is what proves this run was stopped there rather than by the
    /// dedicated per-item warning after it - the fold names the count without naming the item,
    /// and the dedicated warning (never reached here) is the other way around.
    /// </summary>
    [Fact]
    public void WarningActionStop_WithAnUnreadableBody_StillRecordsTheItemErrorsAndTheTelemetry()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TwoItemsRejected);
        using var transport = MgxTransportScope.Inject(handler);

        var path = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[]
              {
                  new System.Collections.Hashtable
                  {
                      ["Url"] = "/users",
                      ["Method"] = "POST",
                      ["Body"] = """{"displayName":"first","displayName":"second"}"""
                  },
                  new System.Collections.Hashtable
                  {
                      ["Url"] = "/groups",
                      ["Method"] = "POST",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "the-second-item" }
                  }
              })
              .AddParameter("DeadLetterPath", path)
              .AddParameter("WarningAction", System.Management.Automation.ActionPreference.Stop);

            var ex = Assert.ThrowsAny<Exception>(() => ps.Invoke());

            // Reaching WriteBatchTelemetry's own outcome warning means the per-item errors and
            // the telemetry summary it closes already happened. It is the outcome warning, not
            // the dedicated per-item one, because it names the count rather than the item.
            Assert.Contains("2 of 2 batch items failed", ex.Message);
            Assert.Contains("1 body was withheld from the dead-letter file", ex.Message);
            Assert.DoesNotContain("POST /users", ex.Message);

            var itemErrors = ps.Streams.Error
                .Where(e => e.FullyQualifiedErrorId.StartsWith("BatchItemError", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, itemErrors.Count);

            Assert.True(File.Exists(path), "dead-letter file was not written");
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A -DeadLetterPath the run cannot open, answered before the first POST. The open used to
    /// happen where the lines are written, at the end, so a path like this was discovered with
    /// the batch already applied: the caller had made every one of those changes and had no
    /// record of which to retry. Nothing is sent now, and the message says so - which is what
    /// makes the failure recoverable by fixing the path and running the same command again.
    ///
    /// An error record rather than a warning, at the default preference: a caller running
    /// -WarningAction SilentlyContinue would otherwise lose the only line saying the file the
    /// batch was to be recorded in is not there.
    /// </summary>
    [Fact]
    public void DeadLetterPathThatCannotBeOpened_IsRefusedBeforeAnythingIsSent()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TwoItemsRejected);
        using var transport = MgxTransportScope.Inject(handler);

        var dir = Path.Combine(Path.GetTempPath(), $"mgx-dl-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[]
              {
                  new System.Collections.Hashtable
                  {
                      ["Url"] = "/users",
                      ["Method"] = "POST",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "a" }
                  },
                  new System.Collections.Hashtable
                  {
                      ["Url"] = "/groups",
                      ["Method"] = "POST",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "b" }
                  }
              })
              .AddParameter("DeadLetterPath", dir);
            var output = ps.Invoke();

            // One record, and no item errors behind it: there are no items to have errors.
            var record = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("DeadLetterWriteFailed", record.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(System.Management.Automation.ErrorCategory.WriteError,
                record.CategoryInfo.Category);
            Assert.Equal(dir, record.TargetObject);
            Assert.StartsWith("Failed to write dead-letter file '", record.Exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(dir, record.Exception.Message);
            Assert.EndsWith(" before the batch was sent; nothing was sent.",
                record.Exception.Message, StringComparison.Ordinal);
            // The reason the open refused is kept, not flattened into the text alone.
            Assert.NotNull(record.Exception.InnerException);

            // Nothing went out, so there is no output and nothing to warn about.
            Assert.Empty(handler.Requests);
            Assert.Empty(output);
            Assert.Empty(ps.Streams.Warning);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>
    /// The same unusable path on a batch every item of which would have been applied. It is
    /// refused just the same, and refused before the batch: the file is part of what the caller
    /// asked for, and the run that could not have it is the run that has not yet spent anything.
    /// The old order had no way to be this: the path was found out at the end, with the batch
    /// applied, so refusing there would have told a scripted caller their batch failed when the
    /// whole of it had succeeded, and the failure was softened to a warning to avoid saying so.
    /// </summary>
    [Fact]
    public void DeadLetterPathThatCannotBeOpened_IsRefusedEvenWhenTheBatchWouldHaveSucceeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mgx-dl-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var handler = new MockHttpHandler();
            handler.SetDefaultResponse(HttpStatusCode.OK, BatchSuccessResponse);
            using var transport = MgxTransportScope.Inject(handler);

            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[] { "/users/u1", "/users/u2" })
              .AddParameter("DeadLetterPath", dir);
            var output = ps.Invoke();

            var record = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("DeadLetterWriteFailed", record.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.EndsWith(" before the batch was sent; nothing was sent.",
                record.Exception.Message, StringComparison.Ordinal);

            // And it is true of the run: the two GETs never went.
            Assert.Empty(handler.Requests);
            Assert.Empty(output);
            Assert.Empty(ps.Streams.Warning);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>
    /// The point of making it an error: under -ErrorAction Stop the run ends on it, and it ends
    /// before the batch, so the caller is told the file is missing rather than told which items
    /// to look for in it - and nothing has been applied to go looking for. The message names
    /// the resolved path, which is the only place the path appears.
    /// </summary>
    [Fact]
    public void ErrorActionStop_WithADeadLetterPathThatIsADirectory_TerminatesOnTheWriteFailure()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TwoItemsRejected);
        using var transport = MgxTransportScope.Inject(handler);

        var dir = Path.Combine(Path.GetTempPath(), $"mgx-dl-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[]
              {
                  new System.Collections.Hashtable
                  {
                      ["Url"] = "/users",
                      ["Method"] = "POST",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "a" }
                  },
                  new System.Collections.Hashtable
                  {
                      ["Url"] = "/groups",
                      ["Method"] = "POST",
                      ["Body"] = new System.Collections.Hashtable { ["displayName"] = "b" }
                  }
              })
              .AddParameter("DeadLetterPath", dir)
              .AddParameter("ErrorAction", System.Management.Automation.ActionPreference.Stop);

            var ex = Assert.ThrowsAny<System.Management.Automation.RuntimeException>(
                () => ps.Invoke());

            Assert.StartsWith("DeadLetterWriteFailed",
                ex.ErrorRecord.FullyQualifiedErrorId, StringComparison.Ordinal);
            // ActionPreferenceStopException wraps the record it stopped on, and its own message
            // prefixes the preference text - the record's is the message the cmdlet wrote.
            Assert.StartsWith("Failed to write dead-letter file '",
                ex.ErrorRecord.Exception.Message, StringComparison.Ordinal);
            Assert.Contains(dir, ex.ErrorRecord.Exception.Message);

            // Stopped on the open, so nothing was POSTed and there are no item errors to have
            // been cut off - which is the whole difference: the caller can fix the path and run
            // the same command again without deciding anything about duplicates.
            Assert.EndsWith(" before the batch was sent; nothing was sent.",
                ex.ErrorRecord.Exception.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Requests);
            Assert.DoesNotContain(ps.Streams.Error,
                e => e.FullyQualifiedErrorId.StartsWith("BatchItemError", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>
    /// -DeadLetterPath on a provider that is not the file system. The path was resolved without
    /// asking which provider answered, and every other provider resolves to something this run
    /// cannot append to - "Env:\MGX_DEAD_LETTER" comes back as "MGX_DEAD_LETTER", a name
    /// relative to nothing - so the run made a file of that name in whatever directory it was
    /// standing in and reported the dead letters written to it.
    /// </summary>
    [Fact]
    public void DeadLetterPathOnAnotherProvider_IsRefusedAndNothingIsSent()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TwoItemsRejected);
        using var transport = MgxTransportScope.Inject(handler);

        // Where the resolved name lands when nothing refuses it: a bare name is relative to
        // the current directory. Cleared first, because the file it names is exactly what a run
        // without this check leaves behind.
        var strayed = Path.Combine(Directory.GetCurrentDirectory(), "MGX_DEAD_LETTER");
        File.Delete(strayed);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[] { "/users/u1", "/users/u2" })
              .AddParameter("DeadLetterPath", @"Env:\MGX_DEAD_LETTER");

            var ex = Assert.ThrowsAny<System.Management.Automation.RuntimeException>(() => ps.Invoke());

            Assert.StartsWith("DeadLetterPathNotFileSystem",
                ex.ErrorRecord.FullyQualifiedErrorId, StringComparison.Ordinal);
            Assert.Equal(@"-DeadLetterPath 'Env:\MGX_DEAD_LETTER' is not a file system path.",
                ex.ErrorRecord.Exception.Message);
            Assert.Empty(handler.Requests);
            Assert.False(File.Exists(strayed), $"a file was written to {strayed}");
        }
        finally
        {
            File.Delete(strayed);
        }
    }

    /// <summary>
    /// The same resolution on an empty path, which answers with the working directory itself -
    /// so the run opened a directory to append to, or, where a provider gave it a name, made a
    /// file nobody asked for. Refused at binding, which is where a parameter with nothing in it
    /// belongs.
    /// </summary>
    [Fact]
    public void DeadLetterPathThatIsEmpty_IsRefusedAndNothingIsSent()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TwoItemsRejected);
        using var transport = MgxTransportScope.Inject(handler);

        using var ps = CreateShell();
        ps.AddCommand("Invoke-MgxBatchRequest")
          .AddParameter("Uri", new object[] { "/users/u1", "/users/u2" })
          .AddParameter("DeadLetterPath", string.Empty);

        var ex = Assert.ThrowsAny<Exception>(() => ps.Invoke());

        Assert.Contains("DeadLetterPath", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A batch in which nothing failed. The file is opened before the first POST, so it exists
    /// by the time the run knows whether it has a line for it - and a run that has none used to
    /// leave an empty file standing where a caller watching that path for failures would find
    /// one after every clean run. It is removed, and nothing is said about it: there was nothing
    /// in it to report.
    /// </summary>
    [Fact]
    public void ACleanBatch_LeavesNoDeadLetterFileBehind()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchSuccessResponse);
        using var transport = MgxTransportScope.Inject(handler);

        var path = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[] { "/users/u1", "/users/u2" })
              .AddParameter("DeadLetterPath", path);
            var output = ps.Invoke();

            Assert.Equal(2, output.Count);
            Assert.False(File.Exists(path), "an empty dead-letter file was left behind");
            Assert.Empty(ps.Streams.Error);
            Assert.Empty(ps.Streams.Warning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same clean run over a file that was already there. Those lines are an earlier run's
    /// record of what is still outstanding, and this run has no business removing them because
    /// its own items all succeeded.
    /// </summary>
    [Fact]
    public void ACleanBatch_LeavesAnExistingDeadLetterFileAlone()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchSuccessResponse);
        using var transport = MgxTransportScope.Inject(handler);

        var path = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "{\"Url\":\"/users/earlier\"}" + Environment.NewLine);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new object[] { "/users/u1", "/users/u2" })
              .AddParameter("DeadLetterPath", path);
            ps.Invoke();

            Assert.True(File.Exists(path), "an earlier run's dead-letter file was removed");
            Assert.Equal("{\"Url\":\"/users/earlier\"}", Assert.Single(File.ReadAllLines(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Two batches over one -DeadLetterPath, which is what running them in parallel gives. A
    /// opens the path first - creating it - and is held on the wire; B runs to completion with a
    /// refused item and appends its line beside A's open handle, which the share mode admits;
    /// then A is released with nothing of its own to write.
    ///
    /// A created the file and wrote no lines, so the removal is A's to consider - and B's dead
    /// letter is in it. The removal asks the file rather than the run: the length A's own handle
    /// reports is B's line, and the exclusive claim behind it is what a sibling still appending
    /// refuses. The file stays, and nothing is said about it: A has nothing to report about a
    /// record that is not its own.
    /// </summary>
    [Fact]
    public void ABatchThatCreatedTheFile_LeavesASiblingsDeadLetterAlone()
    {
        var release = new ManualResetEventSlim(false);
        var handler = new MockHttpHandler();
        handler.When(r => r.BodyText != null && r.BodyText.Contains("b-item"), "b")
               .Respond(HttpStatusCode.OK, OneItemRejected);
        handler.When(r => r.BodyText != null && r.BodyText.Contains("a-item"), "a")
               .Respond(_ =>
               {
                   release.Wait(TimeSpan.FromSeconds(30));
                   return new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(
                           OneItemAccepted, System.Text.Encoding.UTF8, "application/json")
                   };
               });
        using var transport = MgxTransportScope.Inject(handler);

        var path = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        var aWarnings = new List<string>();
        var aErrors = new List<string>();
        Exception? aFailure = null;
        var a = new Thread(() =>
        {
            try
            {
                using var ps = CreateShell();
                ps.AddCommand("Invoke-MgxBatchRequest")
                  .AddParameter("Uri", new object[] { "/users/a-item" })
                  .AddParameter("DeadLetterPath", path);
                ps.Invoke();
                aWarnings.AddRange(ps.Streams.Warning.Select(w => w.Message));
                aErrors.AddRange(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));
            }
            catch (Exception ex) { aFailure = ex; }
        });

        try
        {
            a.Start();

            // A opens the file before its first POST, so its creation precedes the hold.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Assert.True(File.Exists(path), "the held run never created the dead-letter file");

            using (var ps = CreateShell())
            {
                ps.AddCommand("Invoke-MgxBatchRequest")
                  .AddParameter("Uri", new object[] { "/users/b-item" })
                  .AddParameter("DeadLetterPath", path);
                ps.Invoke();
            }
            Assert.Contains("b-item", Assert.Single(ReadSharedLines(path)), StringComparison.Ordinal);

            release.Set();
            Assert.True(a.Join(TimeSpan.FromSeconds(60)), "the held run did not finish");
            Assert.Null(aFailure);

            Assert.True(File.Exists(path), "a sibling's dead-letter file was removed");
            Assert.Contains("b-item", Assert.Single(ReadSharedLines(path)), StringComparison.Ordinal);
            Assert.Empty(aErrors);
            Assert.Empty(aWarnings);
        }
        finally
        {
            release.Set();
            a.Join(TimeSpan.FromSeconds(60));
            release.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The other half of the fold, at the default preference: an unreadable body, not a write
    /// failure. Both the outcome warning's count and the per-item warning naming the item reach
    /// the stream, since nothing here stops the run early.
    ///
    /// The text is asserted whole, because the fold is punctuation and a Contains on either
    /// half of it passes either way. The base sentence used to close with its own period before
    /// the clause was appended, and nothing closed the sentence after it: "...batch items
    /// failed.; 1 body was withheld from the dead-letter file They failed after all retry
    /// attempts."
    /// </summary>
    [Fact]
    public void OutcomeWarning_CountsTheBodyItWithheld()
    {
        var (_, warnings) = DeadLetterRun(TwoItemsRejected,
            new System.Collections.Hashtable
            {
                ["Url"] = "/users",
                ["Method"] = "POST",
                ["Body"] = """{"displayName":"first","displayName":"second"}"""
            },
            new System.Collections.Hashtable
            {
                ["Url"] = "/groups",
                ["Method"] = "POST",
                ["Body"] = new System.Collections.Hashtable { ["displayName"] = "the-second-item" }
            });

        var outcome = Assert.Single(warnings, w => w.Contains("2 of 2 batch items failed"));
        Assert.Equal(
            "2 of 2 batch items failed; 1 body was withheld from the dead-letter file. "
            + "They failed after all retry attempts. Check $Error for details on each item.",
            outcome);
    }

    /// <summary>
    /// The duplicate key one level below where the old walk stopped. The array-of-array descent
    /// reaches it, which is what turns a body that used to be written unredacted into one that
    /// throws, so the two cases have to be pinned together.
    /// </summary>
    [Fact]
    public void DeadLetterLine_ForADuplicateKeyInsideAnArrayOfArrays_CarriesTheMarkerAndWarns()
    {
        var (lines, warnings) = DeadLetterRun(OneItemRejected,
            new System.Collections.Hashtable
            {
                ["Url"] = "/deviceManagement/configurationPolicies",
                ["Method"] = "POST",
                ["Body"] = """{"settings":[[{"displayName":"first","displayName":"second"}]]}"""
            });

        var line = Assert.Single(lines);
        Assert.Equal("***REDACTED***", JsonNode.Parse(line)!["Body"]!.GetValue<string>());
        Assert.DoesNotContain("settings", line);
        Assert.Single(warnings, w => w.Contains("POST /deviceManagement/configurationPolicies")
                                     && w.Contains("withheld"));
    }

    /// <summary>
    /// A body that is one bare string: a pre-authenticated URL and nothing else, which is a
    /// legal JSON document and what a PUT to an upload session can carry. The walk steps into an
    /// object's properties and an array's elements, and this body has neither, so it returned
    /// having read nothing and the live signature was written to disk - while the identical URL
    /// under any property name was cut, and a -Debug trace of the same body cut it too.
    /// </summary>
    [Fact]
    public void DeadLetterLine_CutsABodyThatIsOneBareCapabilityUrl()
    {
        var (lines, warnings) = DeadLetterRun(OneItemRejected,
            new System.Collections.Hashtable
            {
                ["Url"] = "/users/u1",
                ["Method"] = "PATCH",
                ["Body"] = "\"https://c-my.sharepoint.com/x?tempauth=sentinel-root-jwt\""
            });

        var line = Assert.Single(lines);
        Assert.DoesNotContain("sentinel-root-jwt", line);
        // Cut, not withheld whole: the host and the parameter name are the half the file is read
        // for, and nothing about this body was unreadable.
        Assert.Equal("https://c-my.sharepoint.com/x?tempauth=***REDACTED***",
            JsonNode.Parse(line)!["Body"]!.GetValue<string>());
        Assert.DoesNotContain(warnings, w => w.Contains("withheld"));
    }

    /// <summary>
    /// The order the -DeadLetterPath doc states, on the line a caller parses field by field.
    /// JsonObject writes properties in insertion order, so the order is the writer's to keep.
    /// </summary>
    [Fact]
    public void DeadLetterLine_HoldsItsFieldsInTheDocumentedOrder()
    {
        var line = DeadLetterLineFor("/groups/g1", "PATCH", new System.Collections.Hashtable
        {
            ["displayName"] = "Corp Group"
        });

        Assert.Equal(
            new[] { "Timestamp", "Url", "Method", "Status", "Body", "Error" },
            JsonNode.Parse(line)!.AsObject().Select(p => p.Key).ToArray());
    }

    [Fact]
    public void RedactedDeadLetterLine_KeepsTheBodyWhenNeitherTheUrlNorTheNamesSayCredential()
    {
        // Over-redaction is the accepted cost, not the default: a body with nothing to hide
        // reaches the file whole, which is what makes the file worth reading.
        var line = DeadLetterLineFor("/groups/g1", "PATCH", new System.Collections.Hashtable
        {
            ["displayName"] = "Corp Group",
            ["mailNickname"] = "corp-group"
        });

        Assert.DoesNotContain("***REDACTED***", line);
        Assert.Contains("Corp Group", line);
        Assert.Contains("corp-group", line);
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional batch edge case tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BatchMixed_GetAndPost_BothProcessed()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } },
                { "id": "2", "status": 201, "body": { "id": "new-user" } },
                { "id": "3", "status": 200, "body": { "id": "user2" } }
            ]
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation>
        {
            new("/users/user1", "GET"),
            new("/users", "POST", body),
            new("/users/user2", "PATCH", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(3, result.Results.Count);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(201, result.Results[1].Response.Status);
        Assert.Equal(200, result.Results[2].Response.Status);
    }

    [Fact]
    public async Task BatchResponseCountMismatch_IsReportedAsAChunkFailure()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation>
        {
            new("/users/1"),
            new("/users/2"),
            new("/users/3")
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        var ex = Assert.IsType<InvalidOperationException>(result.ChunkFailure);
        Assert.Contains("response count mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchEmptyOperations_ReturnsEmptyResult()
    {
        var handler = new MockHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var result = await batchClient.ExecuteBatchIndexedAsync(Array.Empty<BatchOperation>());

        Assert.Empty(result.Results);
        Assert.Equal(0, result.Telemetry.TotalRequests);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task BatchEmptyResponsesFromGraph_IsReportedAsAChunkFailure()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{ "responses": [] }""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var result = await batchClient.ExecuteBatchIndexedAsync(new[] { new BatchOperation("/users/1") });

        var ex = Assert.IsType<InvalidOperationException>(result.ChunkFailure);
        Assert.Contains("empty or malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
