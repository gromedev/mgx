using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Models;

namespace Mgx.Engine.Http;

/// <summary>
/// Sends batched requests to Graph /$batch endpoint (up to 20 per call).
/// Uses a two-layer retry design: Polly (via ResilientGraphClient) handles
/// transport-level retries on the outer $batch POST; this class handles
/// per-item retries within the 200-OK batch response body (429, 5xx for
/// idempotent methods). Graph always returns HTTP 200 for $batch, so Polly
/// never sees per-item errors.
/// Per-item retry avoids resending the entire batch when only one item is
/// throttled. MaxPerRequestRetries=3 gives each item 4 total attempts,
/// sufficient to survive sustained 429 throttle waves at 15k+ scale.
///
/// After all chunks complete, any items still failing with retryable status
/// are collected and retried as a single follow-up batch. Limited to one
/// batch-level retry pass.
/// </summary>
public sealed class GraphBatchClient
{
    private readonly ResilientGraphClient _client;
    private readonly string _batchUrl;
    private readonly int _maxRetryAfterSeconds;
    private const int MaxBatchSize = 20;
    private const int MaxPerRequestRetries = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Optional callback for verbose diagnostic messages.
    /// Set by the cmdlet layer to route messages to PowerShell's WriteVerbose.
    /// Messages are buffered in _pendingVerbose and drained on the pipeline thread
    /// via DrainVerboseMessages() to avoid PowerShell's _permittedToWriteThread violation.
    /// </summary>
    public Action<string>? VerboseWriter { get; set; }

    private readonly ConcurrentQueue<string> _pendingVerbose = new();

    /// <summary>
    /// Optional headers applied to each individual batch item.
    /// Graph requires per-item headers (e.g., ConsistencyLevel) inside the batch JSON body,
    /// not on the outer $batch POST request.
    /// </summary>
    public Dictionary<string, string>? ItemHeaders { get; set; }

    private readonly int _batchChunkConcurrency;
    private readonly int _batchItemsPerSecond;

    // Cross-call pacing state: tracks when the last batch completed and how many items
    // it processed so that successive small-batch calls (e.g., 20 items = 1 chunk) still
    // get paced. Without this, pacing only fires between chunks within a single call.
    private static long s_lastBatchCompletedTicks;
    private static int s_lastBatchItemCount;

    // Cross-call adaptive rate: when 429s trigger rate halving, persist the adapted rate
    // across Invoke-MgxBatchRequest calls so subsequent calls start at the reduced rate
    // instead of resetting to the configured BatchItemsPerSecond.
    private static int s_adaptedItemsPerSecond;

    public GraphBatchClient(ResilientGraphClient client, string graphBaseUrl = "https://graph.microsoft.com/v1.0",
        int maxRetryAfterSeconds = 120, int batchChunkConcurrency = 1, int batchItemsPerSecond = 0)
    {
        _client = client;
        _batchUrl = $"{graphBaseUrl}/$batch";
        _maxRetryAfterSeconds = maxRetryAfterSeconds;
        _batchChunkConcurrency = batchChunkConcurrency < 1 ? 1 : batchChunkConcurrency;
        _batchItemsPerSecond = batchItemsPerSecond < 0 ? 0 : batchItemsPerSecond;
    }

    private static bool HasWriteOperations(IReadOnlyList<BatchOperation> operations)
        => operations.Any(op => !string.Equals(op.Method, "GET", StringComparison.OrdinalIgnoreCase));

    /// <summary>Test-only: reset cross-call pacing state between tests.</summary>
    internal static void ResetPacingState()
    {
        Interlocked.Exchange(ref s_lastBatchCompletedTicks, 0);
        Volatile.Write(ref s_lastBatchItemCount, 0);
        Volatile.Write(ref s_adaptedItemsPerSecond, 0);
    }

    /// <summary>
    /// Execute multiple GET requests as batches (convenience overload).
    /// Returns responses keyed by the original URL.
    /// </summary>
    public async Task<Dictionary<string, GraphBatchResponseItem>> ExecuteBatchAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default)
    {
        var operations = urls.Select(u => new BatchOperation(u)).ToList();
        var result = await ExecuteBatchIndexedAsync(operations, cancellationToken);

        // Convert to URL-keyed dictionary (backward compatible)
        var results = new Dictionary<string, GraphBatchResponseItem>(result.Results.Count);
        foreach (var (op, response) in result.Results)
            results[op.Url] = response;
        return results;
    }

    /// <summary>
    /// Execute multiple requests (any HTTP method) as batches.
    /// Returns BatchExecutionResult with per-item results and operational telemetry.
    /// Auto-chunks into 20-request batches per Graph API limit.
    /// Supports duplicate URLs (e.g., multiple POSTs to /users).
    /// After all chunks complete, items that exhausted per-chunk retries but have
    /// retryable status codes are retried once more as a follow-up batch.
    /// </summary>
    public async Task<BatchExecutionResult> ExecuteBatchIndexedAsync(
        IReadOnlyList<BatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0)
            return new BatchExecutionResult { Results = [], Telemetry = new BatchTelemetry() };

        var results = new (BatchOperation Operation, GraphBatchResponseItem Response)?[operations.Count];
        var telemetry = new BatchTelemetry { TotalRequests = operations.Count };
        var batchSw = Stopwatch.StartNew();

        // Cross-call pacing: if a previous write batch completed recently, delay to maintain
        // target throughput. Only applies to batches containing writes (POST/PATCH/DELETE) -
        // GET-only batches don't hit Graph's write throttle. Delay is capped to one chunk's
        // worth (MaxBatchSize items) to smooth the gap between calls, not re-pace the whole
        // previous batch.
        var hasWrites = HasWriteOperations(operations);
        if (_batchItemsPerSecond > 0 && hasWrites)
        {
            var lastTicks = Interlocked.Read(ref s_lastBatchCompletedTicks);
            var lastItems = Volatile.Read(ref s_lastBatchItemCount);
            if (lastTicks > 0 && lastItems > 0)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - lastTicks) * 1000.0 / Stopwatch.Frequency;
                var effectiveItems = Math.Min(lastItems, MaxBatchSize);
                var targetMs = effectiveItems / (double)_batchItemsPerSecond * 1000;
                var pacingMs = (int)(targetMs - elapsedMs);
                if (pacingMs > 0)
                {
                    _pendingVerbose.Enqueue($"Batch pacing (cross-call): waiting {pacingMs}ms (target: {_batchItemsPerSecond} items/sec)");
                    await Task.Delay(pacingMs, cancellationToken);
                }
            }
        }

        // Process in chunks of MaxBatchSize
        var chunks = operations.Chunk(MaxBatchSize).ToArray();

        if (_batchChunkConcurrency <= 1)
        {
            // Sequential mode (default): cross-chunk backpressure delays between chunks
            int globalOffset = 0;
            int crossChunkDelaySeconds = 0;
            long prevChunkElapsedMs = 0;
            int chunkIndex = 0;
            // Adaptive pacing: starts at the persisted adapted rate (if any), otherwise
            // the configured rate. Halves on each 429 encounter. Persisted across calls
            // so that successive Invoke-MgxBatchRequest invocations in a loop don't reset
            // to the full rate and immediately get re-throttled.
            var adapted = Volatile.Read(ref s_adaptedItemsPerSecond);
            int effectiveItemsPerSecond = (adapted > 0 && adapted < _batchItemsPerSecond)
                ? adapted
                : _batchItemsPerSecond;
            const int MinAdaptiveItemsPerSecond = 2;
            foreach (var chunk in chunks)
            {
                var iterSw = Stopwatch.StartNew();
                long pacingActualMs = 0;
                long backpressureActualMs = 0;

                if (crossChunkDelaySeconds > 0)
                {
                    var baseDelay = Math.Min(crossChunkDelaySeconds, _maxRetryAfterSeconds);
                    var jitter = baseDelay * Random.Shared.NextDouble() * 0.5;
                    var delaySw = Stopwatch.StartNew();
                    await Task.Delay(TimeSpan.FromSeconds(baseDelay + jitter), cancellationToken);
                    backpressureActualMs = delaySw.ElapsedMilliseconds;
                    telemetry.AddRetryDelayMs(backpressureActualMs);
                }
                else if (globalOffset > 0 && effectiveItemsPerSecond > 0 && hasWrites)
                {
                    // Inter-chunk pacing: smooth write throughput to avoid burst-and-stall.
                    var targetMs = (int)(chunk.Length / (double)effectiveItemsPerSecond * 1000);
                    var pacingMs = targetMs - (int)prevChunkElapsedMs;
                    if (pacingMs > 0)
                    {
                        var delaySw = Stopwatch.StartNew();
                        await Task.Delay(pacingMs, cancellationToken);
                        pacingActualMs = delaySw.ElapsedMilliseconds;
                        if (pacingActualMs > pacingMs * 2)
                            _pendingVerbose.Enqueue($"Batch pacing: requested {pacingMs}ms, actual {pacingActualMs}ms (DELAYED {pacingActualMs - pacingMs}ms) prevChunk={prevChunkElapsedMs}ms target={targetMs}ms");
                    }
                }

                var chunkSw = Stopwatch.StartNew();
                var (chunkResults, throttleDelay, chunkRetries, chunkThrottles, chunkRetryDelayMs) =
                    await SendBatchWithRetryAsync(chunk, cancellationToken);
                prevChunkElapsedMs = chunkSw.ElapsedMilliseconds;
                crossChunkDelaySeconds = throttleDelay;
                telemetry.AddItemRetries(chunkRetries);
                telemetry.AddThrottleEncounters(chunkThrottles);
                telemetry.AddRetryDelayMs(chunkRetryDelayMs);

                // Adaptive pacing: halve rate on 429, floor at MinAdaptiveItemsPerSecond
                if (chunkThrottles > 0 && effectiveItemsPerSecond > MinAdaptiveItemsPerSecond)
                {
                    var prev = effectiveItemsPerSecond;
                    effectiveItemsPerSecond = Math.Max(effectiveItemsPerSecond / 2, MinAdaptiveItemsPerSecond);
                    Volatile.Write(ref s_adaptedItemsPerSecond, effectiveItemsPerSecond);
                    _pendingVerbose.Enqueue($"Adaptive pacing: {chunkThrottles} throttle(s) in chunk {chunkIndex}, reducing rate {prev} -> {effectiveItemsPerSecond} items/sec (persisted)");
                }

                for (int i = 0; i < chunkResults.Count; i++)
                    results[globalOffset + i] = chunkResults[i];
                globalOffset += chunk.Length;

                var iterMs = iterSw.ElapsedMilliseconds;
                if (iterMs > 5000 || chunkIndex % 10 == 0)
                    _pendingVerbose.Enqueue($"Chunk[{chunkIndex}/{chunks.Length}]: iter={iterMs}ms http={prevChunkElapsedMs}ms pacing={pacingActualMs}ms backpressure={backpressureActualMs}ms throttles={chunkThrottles} retryDelay={chunkRetryDelayMs}ms rate={effectiveItemsPerSecond}/sec");
                chunkIndex++;
            }
        }
        else
        {
            // Parallel mode: SemaphoreSlim-bounded concurrent chunk execution
            // No cross-chunk backpressure; chunks run independently
            var chunkOffsets = new int[chunks.Length];
            int runningOffset = 0;
            for (int j = 0; j < chunks.Length; j++)
            {
                chunkOffsets[j] = runningOffset;
                runningOffset += chunks[j].Length;
            }

            using var semaphore = new SemaphoreSlim(_batchChunkConcurrency);
            var tasks = chunks.Select(async (chunk, chunkIndex) =>
            {
                bool acquired = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken);
                    acquired = true;

                    var (chunkResults, _, chunkRetries, chunkThrottles, chunkRetryDelayMs) =
                        await SendBatchWithRetryAsync(chunk, cancellationToken);
                    telemetry.AddItemRetries(chunkRetries);
                    telemetry.AddThrottleEncounters(chunkThrottles);
                    telemetry.AddRetryDelayMs(chunkRetryDelayMs);

                    var offset = chunkOffsets[chunkIndex];
                    for (int i = 0; i < chunkResults.Count; i++)
                        results[offset + i] = chunkResults[i];
                }
                finally
                {
                    if (acquired) semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }

        // Batch-level retry for items that exhausted per-chunk retries
        // but have retryable status codes. Throttle pressure may have subsided.
        var failedRetryable = new List<(int OriginalIndex, BatchOperation Op)>();
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i] == null) continue;
            var (op, response) = results[i]!.Value;
            if (IsRetryable(response.Status, op.Method))
                failedRetryable.Add((i, op));
        }

        if (failedRetryable.Count > 0)
        {
            telemetry.BatchLevelRetries = failedRetryable.Count;

            // Backpressure delay before the batch-level retry pass.
            // Minimum 2s pause to let throttle pressure subside before retrying failed items.
            var backpressureDelay = 2;
            var backpressureJitter = backpressureDelay * Random.Shared.NextDouble() * 0.5;
            _pendingVerbose.Enqueue(
                $"Batch-level retry: {failedRetryable.Count} items exhausted per-chunk retries. "
                + $"Waiting {backpressureDelay + backpressureJitter:F1}s before follow-up batch.");
            var bpSw = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(backpressureDelay + backpressureJitter), cancellationToken);
            telemetry.AddRetryDelayMs(bpSw.ElapsedMilliseconds);

            var retryOps = failedRetryable.Select(f => f.Op).ToArray();
            int retryOffset = 0;
            int phase2ThrottleDelay = 0;
            long phase2PrevChunkMs = 0;
            foreach (var chunk in retryOps.Chunk(MaxBatchSize))
            {
                // Cross-chunk backpressure (sequential, mirrors initial chunk processing path)
                if (phase2ThrottleDelay > 0)
                {
                    var p2Delay = Math.Min(phase2ThrottleDelay, _maxRetryAfterSeconds);
                    var p2Jitter = p2Delay * Random.Shared.NextDouble() * 0.5;
                    var p2Sw = Stopwatch.StartNew();
                    await Task.Delay(TimeSpan.FromSeconds(p2Delay + p2Jitter), cancellationToken);
                    telemetry.AddRetryDelayMs(p2Sw.ElapsedMilliseconds);
                }
                else if (retryOffset > 0 && _batchItemsPerSecond > 0 && hasWrites)
                {
                    var targetMs = (int)(chunk.Length / (double)_batchItemsPerSecond * 1000);
                    var pacingMs = targetMs - (int)phase2PrevChunkMs;
                    if (pacingMs > 0)
                    {
                        _pendingVerbose.Enqueue($"Batch pacing (retry pass): waiting {pacingMs}ms (target: {_batchItemsPerSecond} items/sec)");
                        await Task.Delay(pacingMs, cancellationToken);
                    }
                }

                var p2ChunkSw = Stopwatch.StartNew();
                var (chunkResults, chunkThrottleDelay, chunkRetries, chunkThrottles, chunkRetryDelayMs) =
                    await SendBatchWithRetryAsync(chunk, cancellationToken);
                phase2PrevChunkMs = p2ChunkSw.ElapsedMilliseconds;
                phase2ThrottleDelay = chunkThrottleDelay;
                telemetry.AddItemRetries(chunkRetries);
                telemetry.AddThrottleEncounters(chunkThrottles);
                telemetry.AddRetryDelayMs(chunkRetryDelayMs);

                for (int i = 0; i < chunkResults.Count; i++)
                {
                    var originalIndex = failedRetryable[retryOffset + i].OriginalIndex;
                    results[originalIndex] = chunkResults[i];
                }
                retryOffset += chunk.Length;
            }
        }

        // Validate all slots populated
        var nullIndex = Array.FindIndex(results, r => r == null);
        if (nullIndex >= 0)
        {
            throw new InvalidOperationException(
                $"Batch result slot {nullIndex} was not populated after processing all chunks. "
                + "This indicates an internal logic error in ExecuteBatchIndexedAsync.");
        }

        var finalResults = results.Select(r => r!.Value).ToList();

        // Compute success/failure counts and total elapsed
        telemetry.Succeeded = finalResults.Count(r => r.Response.Status < 400);
        telemetry.Failed = finalResults.Count(r => r.Response.Status >= 400);
        telemetry.TotalElapsedMs = batchSw.ElapsedMilliseconds;

        // Record completion for cross-call pacing (writes only)
        if (_batchItemsPerSecond > 0 && hasWrites)
        {
            Volatile.Write(ref s_lastBatchItemCount, operations.Count);
            Interlocked.Exchange(ref s_lastBatchCompletedTicks, Stopwatch.GetTimestamp());
        }

        return new BatchExecutionResult
        {
            Results = finalResults,
            Telemetry = telemetry
        };
    }

    /// <summary>
    /// Returns (Results, ThrottleDelaySeconds, ItemRetries, ThrottleEncounters, RetryDelayMs).
    /// ThrottleDelaySeconds: highest Retry-After seen, for cross-chunk backpressure.
    /// ItemRetries: total individual item retries in this chunk.
    /// ThrottleEncounters: number of 429 responses seen across all attempts.
    /// RetryDelayMs: total milliseconds spent in retry delays within this chunk.
    /// </summary>
    private async Task<(IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> Results, int ThrottleDelaySeconds, int ItemRetries, int ThrottleEncounters, long RetryDelayMs)> SendBatchWithRetryAsync(
        BatchOperation[] operations,
        CancellationToken cancellationToken)
    {
        // Track results by original index position
        var results = new (BatchOperation Operation, GraphBatchResponseItem Response)?[operations.Length];
        // Track which indices are still pending
        var pendingIndices = Enumerable.Range(0, operations.Length).ToList();
        // Track last-seen throttle delay for cross-chunk backpressure
        int lastThrottleDelaySeconds = 0;
        int totalItemRetries = 0;
        int totalThrottleEncounters = 0;
        long totalRetryDelayMs = 0;

        for (int attempt = 0; attempt <= MaxPerRequestRetries; attempt++)
        {
            if (pendingIndices.Count == 0) break;

            var batchRequest = new GraphBatchRequest();
            var idToIndex = new Dictionary<string, int>();

            for (int i = 0; i < pendingIndices.Count; i++)
            {
                var originalIndex = pendingIndices[i];
                var op = operations[originalIndex];
                var id = (i + 1).ToString();
                idToIndex[id] = originalIndex;

                var item = new GraphBatchRequestItem
                {
                    Id = id,
                    Method = op.Method,
                    Url = op.Url,
                    Body = op.Body
                };

                // Build per-item headers: Content-Type for body requests + any shared item headers
                Dictionary<string, string>? headers = null;
                if (ItemHeaders != null)
                    headers = new Dictionary<string, string>(ItemHeaders);
                if (op.Body.HasValue)
                    (headers ??= new())["Content-Type"] = "application/json";
                if (headers != null)
                    item.Headers = headers;

                batchRequest.Requests.Add(item);
            }

            var json = JsonSerializer.Serialize(batchRequest, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpSw = Stopwatch.StartNew();
            using var response = await _client.PostAsync(_batchUrl, content, cancellationToken);
            var httpMs = httpSw.ElapsedMilliseconds;
            if (httpMs > 5000)
                _pendingVerbose.Enqueue($"Batch HTTP slow: {httpMs}ms for {pendingIndices.Count} items (attempt {attempt + 1})");

            if (!response.IsSuccessStatusCode)
            {
                using var errCts = _client.CreateBodyReadCts(cancellationToken);
                try
                {
                    var errorBody = await response.Content.ReadAsStringAsync(errCts.Token);
                    throw new GraphServiceException(response.StatusCode, errorBody);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new HttpRequestException($"Response body read timed out for error response (HTTP {(int)response.StatusCode}).");
                }
            }

            GraphBatchResponse? batchResponse;
            using var bodyCts = _client.CreateBodyReadCts(cancellationToken);
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(bodyCts.Token);
                batchResponse = await JsonSerializer.DeserializeAsync<GraphBatchResponse>(stream, JsonOptions, bodyCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Body read timed out but user didn't cancel - treat as transient network error
                throw new HttpRequestException("Response body read timed out. The server sent headers but the body stream stalled.");
            }

            // RD-H3: Validate batch response is not null/empty
            if (batchResponse?.Responses == null || batchResponse.Responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Graph $batch returned an empty or malformed response (attempt {attempt + 1}/{MaxPerRequestRetries + 1}). "
                    + "Expected a 'responses' array with per-request results.");
            }

            // Validate response count matches request count
            if (batchResponse.Responses.Count != pendingIndices.Count)
            {
                throw new InvalidOperationException(
                    $"Graph $batch response count mismatch: sent {pendingIndices.Count} requests "
                    + $"but received {batchResponse.Responses.Count} responses "
                    + $"(attempt {attempt + 1}/{MaxPerRequestRetries + 1}). "
                    + "The response may have been truncated by a proxy or CDN.");
            }

            var retryIndices = new List<int>();
            int maxRetryAfterSeconds = 0;

            foreach (var item in batchResponse.Responses)
            {
                if (!idToIndex.TryGetValue(item.Id, out var originalIndex)) continue;
                var op = operations[originalIndex];

                // Count all 429s regardless of retry budget
                if (item.Status == 429)
                {
                    totalThrottleEncounters++;

                    // Parse Retry-After for delay calculation. Handles both formats:
                    // - delta-seconds: "120" (integer seconds)
                    // - HTTP-date: "Wed, 21 Oct 2015 07:28:00 GMT"
                    if (item.Headers != null)
                    {
                        var retryAfterValue = item.Headers
                            .FirstOrDefault(h => string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                            .Value;
                        if (retryAfterValue != null &&
                            System.Net.Http.Headers.RetryConditionHeaderValue.TryParse(retryAfterValue, out var ra))
                        {
                            var delay = ra.Delta
                                ?? (ra.Date.HasValue ? ra.Date.Value - DateTimeOffset.UtcNow : (TimeSpan?)null);
                            if (delay.HasValue)
                            {
                                var seconds = (int)Math.Ceiling(delay.Value.TotalSeconds);
                                var clamped = Math.Min(Math.Max(seconds, 0), _maxRetryAfterSeconds);
                                if (clamped < seconds && attempt < MaxPerRequestRetries)
                                {
                                    _pendingVerbose.Enqueue(
                                        $"Batch retry: server requested {seconds}s Retry-After, clamped to {_maxRetryAfterSeconds}s (attempt {attempt + 1})");
                                }
                                maxRetryAfterSeconds = Math.Max(maxRetryAfterSeconds, clamped);
                            }
                        }
                    }
                }

                if (IsRetryable(item.Status, op.Method) && attempt < MaxPerRequestRetries)
                {
                    retryIndices.Add(originalIndex);
                }
                else
                {
                    results[originalIndex] = (op, item);
                }
            }

            pendingIndices = retryIndices;
            totalItemRetries += retryIndices.Count;

            lastThrottleDelaySeconds = Math.Max(lastThrottleDelaySeconds, maxRetryAfterSeconds);

            if (retryIndices.Count > 0 && attempt < MaxPerRequestRetries)
            {
                var baseDelaySeconds = maxRetryAfterSeconds > 0
                    ? maxRetryAfterSeconds
                    : (int)Math.Pow(2, attempt);
                // C4: Add 0-50% jitter to prevent thundering herd on batch retries
                var jitter = baseDelaySeconds * Random.Shared.NextDouble() * 0.5;
                var retrySw = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(baseDelaySeconds + jitter), cancellationToken);
                totalRetryDelayMs += retrySw.ElapsedMilliseconds;
            }
        }

        // With count-mismatch validation, all slots should be filled. If Graph returns
        // the right count but with mismatched IDs, some slots will remain null.
        var nullIndex = Array.FindIndex(results, r => r == null);
        if (nullIndex >= 0)
        {
            var missingOp = operations[nullIndex];
            throw new InvalidOperationException(
                $"Batch response missing result for request at index {nullIndex} (URL: {missingOp.Url}). "
                + "Graph returned the expected number of responses but with non-matching IDs.");
        }
        return (results.Select(r => r!.Value).ToList(), lastThrottleDelaySeconds, totalItemRetries, totalThrottleEncounters, totalRetryDelayMs);
    }

    /// <summary>
    /// Drains buffered verbose messages on the calling (pipeline) thread.
    /// Must be called after ExecuteBatchIndexedAsync completes, on the same thread
    /// that created the cmdlet instance. Matches ResilientGraphClient.DrainVerboseMessages().
    /// </summary>
    public void DrainVerboseMessages()
    {
        if (VerboseWriter == null)
        {
            while (_pendingVerbose.TryDequeue(out _)) { }
            return;
        }
        while (_pendingVerbose.TryDequeue(out var msg))
            VerboseWriter(msg);
    }

    /// <summary>
    /// Determines if a batch response item should be retried.
    /// POST is non-idempotent: only retry on 429 (matches Kiota SDK behavior), not on 5xx/408 (could create duplicates).
    /// Other methods (GET, PATCH, PUT, DELETE) retry on 429/408/500/502/503/504 (aligned with ResiliencePipelineFactory).
    /// </summary>
    private static bool IsRetryable(int statusCode, string method)
    {
        if (statusCode == 429) return true;
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        return statusCode is 408 or 500 or 502 or 503 or 504;
    }
}
