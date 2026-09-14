using Mgx.Engine.Errors;
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

    /// <summary>
    /// Runs once a parallel run has stopped sending: a chunk's own POST failed, the failure is
    /// recorded and the stop its siblings read is canceled. Null on every shipping path: the
    /// suite holds a wire answer here so that what a sibling does next happens on the far side
    /// of the stop, rather than a moment either way of it. Per client rather than static, so a
    /// test arms only the run it built.
    /// </summary>
    internal Action? SendingStopped { get; set; }

    /// <summary>
    /// Runs as a chunk enters the wait between two of its own attempts, with the stop already
    /// linked into that wait. Null on every shipping path: the suite answers a peer chunk here
    /// so a refusal lands on a backoff that is running, which is the only state the question of
    /// whether the stop ends it can be asked from. Per client rather than static, so a test arms
    /// only the run it built.
    /// </summary>
    internal Action? RetryWaitStarted { get; set; }

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

    // When the last 429 was seen, so the adapted rate can expire. Without an expiry a single
    // throttling episode kept every later batch in the process at the reduced rate for the
    // lifetime of the session, with no way to recover short of restarting PowerShell.
    private static long s_lastThrottleTicks;

    // Chunk-loop AIMD tuning. The shared math (halve on throttle, additive recovery, expiry
    // window) lives in AdaptivePacing so the request-level pacer applies identical rules.
    private const int AdaptiveRecoveryCleanChunks = 2;

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

    /// <summary>
    /// Clear cross-call batch pacing state. Called by ResiliencePipelineFactory.Reset when the
    /// credential changes - this state is learned per tenant and must not outlive it.
    /// </summary>
    internal static void ResetPacingState()
    {
        Interlocked.Exchange(ref s_lastBatchCompletedTicks, 0);
        Interlocked.Exchange(ref s_lastThrottleTicks, 0);
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
    /// <summary>
    /// Status recorded for an operation that was never sent, because another chunk failed
    /// first - not necessarily one before it, since chunks can run in parallel. Distinct from
    /// any HTTP status so a caller can tell "not attempted" from "attempted and refused" - the
    /// difference between a write that may have landed and one that certainly did not.
    /// </summary>
    public const int NotSentStatus = 0;

    /// <summary>
    /// Status recorded for the items of a chunk whose own POST failed without a status of its
    /// own. Two refusals land on it and it cannot separate them: a POST that went out and whose
    /// answer never completed - a stalled response body, a transport failure mid-flight - and a
    /// POST that never left, refused by an open circuit or by a rate limiter with no permit for
    /// it. It is read as the first, because that is the reading a caller can act on safely: the
    /// writes may have been applied, so resending them is a decision about duplicates.
    /// NotSentStatus is kept for the refusals nothing went out for and the code knows it - a
    /// chunk the run's stop reached before its first attempt, and the chunks never POSTed at
    /// all - where claiming the opposite would be the more expensive error.
    /// </summary>
    private const int RefusedWithoutStatus = 503;

    /// <summary>
    /// What a chunk spent on its way to whatever happened to it. Held by the caller of the
    /// attempt loop rather than returned from it, so a chunk that fails still reports what it
    /// cost - the throttles it met are the reason it failed, not a detail lost with it.
    /// </summary>
    private sealed class ChunkAttemptCounts
    {
        public int ItemRetries;
        public int ThrottleEncounters;
        public long RetryDelayMs;
    }

    /// <summary>
    /// A chunk's own $batch call failed. Carries the per-item results the earlier attempts of
    /// that same chunk already collected - the server answered those items, and after the
    /// failure they are the only record of what it applied - and what those attempts cost.
    /// </summary>
    private sealed class ChunkSendFailedException : Exception
    {
        public ChunkSendFailedException(
            Exception cause,
            (BatchOperation Operation, GraphBatchResponseItem Response)?[] partial,
            int refusedStatus,
            ChunkAttemptCounts counts)
            : base(cause.Message, cause)
        {
            Cause = cause;
            Partial = partial;
            Counts = counts;
            // Below 400 is not a status a refused item may carry: a success is counted in
            // [200,400) and the dead-letter file keeps nothing under 400, so a 304 from a proxy
            // or a captive portal reported twenty refused writes as twenty applied ones. Graph
            // answers $batch with 200 or an error; anything else came from something between.
            RefusedStatus = refusedStatus >= 400 ? refusedStatus : RefusedWithoutStatus;
        }

        /// <summary>The failure itself, as the caller is to see it.</summary>
        public Exception Cause { get; }

        /// <summary>By chunk-relative index; null where no sub-response arrived.</summary>
        public (BatchOperation Operation, GraphBatchResponseItem Response)?[] Partial { get; }

        /// <summary>Status for the items of this chunk that have no sub-response.</summary>
        public int RefusedStatus { get; }

        /// <summary>The retries, throttles and delay this chunk had spent when it failed.</summary>
        public ChunkAttemptCounts Counts { get; }
    }

    /// <summary>
    /// Writes a refused chunk's own slots: an item the server answered keeps that answer, the
    /// rest take the refusal status, covering both a POST that went out unanswered and one an
    /// open circuit or a rate limiter stopped before it left - read as the first, so none of
    /// them may be reported as never sent.
    /// </summary>
    private static void FillRefusedChunk(
        (BatchOperation Operation, GraphBatchResponseItem Response)?[] results,
        BatchOperation[] chunk, int offset, ChunkSendFailedException failure)
    {
        for (int i = 0; i < chunk.Length; i++)
        {
            var answered = i < failure.Partial.Length ? failure.Partial[i] : null;
            results[offset + i] = answered ?? (chunk[i], new GraphBatchResponseItem
            {
                Id = (offset + i).ToString(),
                Status = failure.RefusedStatus
            });
        }
    }

    /// <summary>
    /// Writes back what the batch-level retry pass got answers for before its own POST was
    /// refused. Every item the pass carries already holds a status the first pass gave it, so
    /// unlike a refused first-pass chunk there is nothing to fill in for the rest: an item this
    /// pass got no answer of its own for keeps the one it came in with. Left out, an item the
    /// pass confirmed is reported with the failure that sent it here - for a write the server
    /// applied, that is an instruction to apply it a second time.
    /// </summary>
    private static void FillAnsweredRetryChunk(
        (BatchOperation Operation, GraphBatchResponseItem Response)?[] results,
        IReadOnlyList<(int OriginalIndex, BatchOperation Op)> candidates, int offset,
        ChunkSendFailedException failure)
    {
        for (int i = 0; i < failure.Partial.Length; i++)
        {
            var answered = failure.Partial[i];
            if (answered != null)
                results[candidates[offset + i].OriginalIndex] = answered;
        }
    }

    /// <summary>
    /// A failed chunk's retries, throttles and delay. They are the run's, not the chunk's: the
    /// chunk that met the throttles is often the one that then failed, and reading them only
    /// off the chunks that succeeded reported a batch throttled into refusal as an untroubled
    /// one that happened to end badly.
    /// </summary>
    private static void RecordChunkCosts(BatchTelemetry telemetry, ChunkSendFailedException failure)
    {
        telemetry.AddItemRetries(failure.Counts.ItemRetries);
        telemetry.AddThrottleEncounters(failure.Counts.ThrottleEncounters);
        telemetry.AddRetryDelayMs(failure.Counts.RetryDelayMs);
    }

    public async Task<BatchExecutionResult> ExecuteBatchIndexedAsync(
        IReadOnlyList<BatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0)
            return new BatchExecutionResult { Results = [], Telemetry = new BatchTelemetry() };

        var results = new (BatchOperation Operation, GraphBatchResponseItem Response)?[operations.Count];
        var telemetry = new BatchTelemetry { TotalRequests = operations.Count };
        var batchSw = Stopwatch.StartNew();

        // A chunk's own POST can fail after the chunks before it were applied. Their results are
        // the only record of what landed, so they are returned with the failure rather than lost
        // to it.
        Exception? chunkFailure = null;
        var notSent = new List<BatchOperation>();

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
            if (adapted > 0 && AdaptivePacing.AdaptedRateHasExpired(
                    Interlocked.Read(ref s_lastThrottleTicks), Stopwatch.GetTimestamp()))
            {
                Volatile.Write(ref s_adaptedItemsPerSecond, 0);
                adapted = 0;
                _pendingVerbose.Enqueue($"Adaptive pacing: no throttling for {AdaptivePacing.AdaptiveRecoveryWindow.TotalMinutes:0} min, restored configured rate ({_batchItemsPerSecond} items/sec)");
            }
            int effectiveItemsPerSecond = (adapted > 0 && adapted < _batchItemsPerSecond)
                ? adapted
                : _batchItemsPerSecond;
            int cleanChunks = 0;
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
                IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> chunkResults;
                int throttleDelay, chunkRetries, chunkThrottles;
                long chunkRetryDelayMs;
                try
                {
                    (chunkResults, throttleDelay, chunkRetries, chunkThrottles, chunkRetryDelayMs) =
                        await SendBatchWithRetryAsync(chunk, cancellationToken);
                }
                // Any way this chunk's own call can fail, an envelope that answers nothing that
                // was sent included. Each of them is a failure of this chunk and is reported as
                // one; none of them is a reason to lose the chunks applied before it.
                catch (ChunkSendFailedException ex)
                {
                    // The chunks before this one were applied on the server. Letting this
                    // exception out discards their results, which is the only record of what
                    // landed - and for writes that is the thing the caller most needs. Stop
                    // here, keep them, and hand the failure back alongside.
                    chunkFailure = ex.Cause;
                    RecordChunkCosts(telemetry, ex);
                    FillRefusedChunk(results, chunk, globalOffset, ex);

                    // Only the chunks after this one were never sent. The unsent slots are filled
                    // rather than dropped: callers line results up against their own input list
                    // by position, so a short list would misattribute every error after the gap.
                    for (int i = globalOffset + chunk.Length; i < operations.Count; i++)
                    {
                        notSent.Add(operations[i]);
                        results[i] = (operations[i], new GraphBatchResponseItem
                        {
                            Id = i.ToString(),
                            Status = NotSentStatus
                        });
                    }
                    break;
                }
                prevChunkElapsedMs = chunkSw.ElapsedMilliseconds;
                crossChunkDelaySeconds = throttleDelay;
                telemetry.AddItemRetries(chunkRetries);
                telemetry.AddThrottleEncounters(chunkThrottles);
                telemetry.AddRetryDelayMs(chunkRetryDelayMs);

                // Adaptive pacing: halve on 429 (floor at MinAdaptiveItemsPerSecond), and climb
                // back after a run of clean chunks so a past throttle does not cap the rate
                // for the rest of the session.
                if (chunkThrottles > 0)
                {
                    Interlocked.Exchange(ref s_lastThrottleTicks, Stopwatch.GetTimestamp());
                    cleanChunks = 0;
                    if (effectiveItemsPerSecond > AdaptivePacing.MinAdaptiveRate)
                    {
                        var prev = effectiveItemsPerSecond;
                        effectiveItemsPerSecond = AdaptivePacing.ReduceRate(effectiveItemsPerSecond);
                        Volatile.Write(ref s_adaptedItemsPerSecond, effectiveItemsPerSecond);
                        _pendingVerbose.Enqueue($"Adaptive pacing: {chunkThrottles} throttle(s) in chunk {chunkIndex}, reducing rate {prev} -> {effectiveItemsPerSecond} items/sec (persisted)");
                    }
                }
                else if (effectiveItemsPerSecond < _batchItemsPerSecond
                    && ++cleanChunks >= AdaptiveRecoveryCleanChunks)
                {
                    cleanChunks = 0;
                    var prev = effectiveItemsPerSecond;
                    effectiveItemsPerSecond = AdaptivePacing.RecoverRate(effectiveItemsPerSecond, _batchItemsPerSecond);
                    // Persist 0 once fully recovered, so the next call starts clean instead of
                    // pinning itself to a rate that merely equals the configured one today.
                    Volatile.Write(ref s_adaptedItemsPerSecond,
                        effectiveItemsPerSecond >= _batchItemsPerSecond ? 0 : effectiveItemsPerSecond);
                    _pendingVerbose.Enqueue($"Adaptive pacing: {AdaptiveRecoveryCleanChunks} clean chunk(s), raising rate {prev} -> {effectiveItemsPerSecond} items/sec");
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
            Exception? parallelFailure = null;
            // Cancelled when a chunk's own POST fails. The chunks still running read it to stop
            // before their next attempt, and wait on it alongside their backoff - a chunk part
            // way through a Retry-After has stopped sending, and holding the call open for the
            // rest of a delay it will not act on serves nobody.
            using var stopSource = new CancellationTokenSource();
            var tasks = chunks.Select(async (chunk, chunkIndex) =>
            {
                bool acquired = false;
                try
                {
                    // A chunk has already failed its own POST. The sequential path stops there
                    // instead of sending the rest, and so does this one: what is in flight
                    // finishes and is kept, nothing further goes out. Read before queueing as
                    // well as after acquiring - a chunk that waited out the queue must not send
                    // on the strength of a check it made before the refusal happened.
                    if (Volatile.Read(ref parallelFailure) != null) return;
                    await semaphore.WaitAsync(cancellationToken);
                    acquired = true;
                    if (Volatile.Read(ref parallelFailure) != null) return;

                    var (chunkResults, _, chunkRetries, chunkThrottles, chunkRetryDelayMs) =
                        await SendBatchWithRetryAsync(chunk, cancellationToken,
                            stopRequested: stopSource.Token);
                    telemetry.AddItemRetries(chunkRetries);
                    telemetry.AddThrottleEncounters(chunkThrottles);
                    telemetry.AddRetryDelayMs(chunkRetryDelayMs);

                    var offset = chunkOffsets[chunkIndex];
                    for (int i = 0; i < chunkResults.Count; i++)
                        results[offset + i] = chunkResults[i];
                }
                // A failure of the chunk's own call, as in the sequential branch. Task.WhenAll
                // would rethrow it and take the populated results array with it - the results of
                // the chunks that DID land, which for writes is the record the caller most needs.
                catch (ChunkSendFailedException ex)
                {
                    // Chunks fail concurrently, and only the first can be the one failure the
                    // caller is handed. The others are still things that happened to the run,
                    // and dropping them leaves a chunk whose items all failed with no statement
                    // anywhere of why.
                    if (Interlocked.CompareExchange(ref parallelFailure, ex.Cause, null) != null)
                        _pendingVerbose.Enqueue($"Batch chunk {chunkIndex} also failed: {ex.Cause.Message}");
                    stopSource.Cancel();
                    SendingStopped?.Invoke();
                    RecordChunkCosts(telemetry, ex);
                    FillRefusedChunk(results, chunk, chunkOffsets[chunkIndex], ex);
                }
                finally
                {
                    if (acquired) semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            if (parallelFailure != null)
            {
                chunkFailure = parallelFailure;
                // A failed chunk filled its own slots above, so every slot still empty belongs
                // to a chunk that was never sent. Filled rather than dropped, for the same reason
                // as above: callers line results up against their own input list by position.
                for (int i = 0; i < results.Length; i++)
                {
                    if (results[i] != null) continue;
                    notSent.Add(operations[i]);
                    results[i] = (operations[i], new GraphBatchResponseItem
                    {
                        Id = i.ToString(),
                        Status = NotSentStatus
                    });
                }
            }
        }

        // Batch-level retry for items that exhausted per-chunk retries
        // but have retryable status codes. Throttle pressure may have subsided.
        //
        // Not after a chunk failure. The run stopped sending and this pass is a send, so it is
        // skipped for every candidate rather than for the refused chunk's own. For a candidate
        // a refusal carried, resending is the duplicate write the stop exists to prevent; for
        // one a chunk that was POSTed and answered in full left failing, it is an extra attempt
        // the run no longer has. Both are handed back as they stand, for the caller to resubmit
        // knowing what it is resubmitting.
        var failedRetryable = new List<(int OriginalIndex, BatchOperation Op)>();
        if (chunkFailure is null)
        {
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] == null) continue;
                var (op, response) = results[i]!.Value;
                if (IsRetryable(response.Status, op.Method))
                    failedRetryable.Add((i, op));
            }
        }

        if (failedRetryable.Count > 0)
        {
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
                IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> chunkResults;
                int chunkThrottleDelay, chunkRetries, chunkThrottles;
                long chunkRetryDelayMs;
                // Counted as each chunk goes out, for the same reason item retries are: the
                // pass can be refused on its first chunk, and the candidates queued behind that
                // chunk are then never POSTed. Read off the candidate list before the pass ran,
                // the count reported forty batch-level retries over a wire that carried twenty.
                // Here rather than after the call, so a chunk the server refused still counts -
                // its POST went out, and what it carried may have been applied.
                telemetry.BatchLevelRetries += chunk.Length;
                try
                {
                    (chunkResults, chunkThrottleDelay, chunkRetries, chunkThrottles, chunkRetryDelayMs) =
                        await SendBatchWithRetryAsync(chunk, cancellationToken);
                }
                // This pass exists to improve results that are already in hand. Its own POST
                // failing does not unmake them: the items it carried keep the statuses the
                // server gave them, which is what the caller has to act on. Thrown, it takes
                // the whole array with it - every chunk that landed included - and the run ends
                // with nothing said about writes the server applied. What its earlier attempts
                // did get answers for is written back before it stops: those items were answered
                // in this pass, and a later attempt of it being refused does not unmake that.
                catch (ChunkSendFailedException ex)
                {
                    chunkFailure = ex.Cause;
                    RecordChunkCosts(telemetry, ex);
                    FillAnsweredRetryChunk(results, failedRetryable, retryOffset, ex);
                    break;
                }
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
        if (chunkFailure is null && nullIndex >= 0)
        {
            throw new InvalidOperationException(
                $"Batch result slot {nullIndex} was not populated after processing all chunks. "
                + "This indicates an internal logic error in ExecuteBatchIndexedAsync.");
        }

        var finalResults = results.Select(r => r!.Value).ToList();

        // Compute success/failure counts and total elapsed. An operation that was never sent has
        // no status and is neither: counting it as a success reported a run that stopped after
        // one chunk as one in which everything worked.
        telemetry.Succeeded = finalResults.Count(r => r.Response.Status is >= 200 and < 400);
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
            Telemetry = telemetry,
            ChunkFailure = chunkFailure,
            NotSent = notSent
        };
    }

    /// <summary>
    /// Returns (Results, ThrottleDelaySeconds, ItemRetries, ThrottleEncounters, RetryDelayMs).
    /// ThrottleDelaySeconds: highest Retry-After seen, for cross-chunk backpressure.
    /// ItemRetries: total individual item retries in this chunk.
    /// ThrottleEncounters: number of 429 responses seen across all attempts.
    /// RetryDelayMs: total milliseconds spent in retry delays within this chunk.
    /// <paramref name="stopRequested"/>, once cancelled, ends the chunk before its next attempt
    /// goes out and ends any backoff it is already waiting out. The items still pending keep
    /// the last status the server gave them.
    /// </summary>
    private async Task<(IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> Results, int ThrottleDelaySeconds, int ItemRetries, int ThrottleEncounters, long RetryDelayMs)> SendBatchWithRetryAsync(
        BatchOperation[] operations,
        CancellationToken cancellationToken,
        CancellationToken stopRequested = default)
    {
        // Results by original index position, held out here so that however the send fails it
        // fails with them: they are the record of what the server applied, and no shape of
        // failure is a reason to discard it.
        var results = new (BatchOperation Operation, GraphBatchResponseItem Response)?[operations.Length];
        // Held out here for the same reason as the results: a chunk that fails has still spent
        // its retries and met its throttles, and the run's telemetry is where that is reported.
        var counts = new ChunkAttemptCounts();
        try
        {
            return await SendChunkAttemptsAsync(operations, results, counts, cancellationToken, stopRequested);
        }
        // The caller cancelled. Not a chunk failure, and reporting it as one would bury it.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChunkSendFailedException)
        {
            throw;
        }
        // Every other shape - a stalled response body, an open circuit, an envelope that does
        // not answer what was sent - is this chunk's failure and not the whole run's. Letting
        // it out would take the results array with it, and every other chunk's outcome in it.
        catch (Exception ex)
        {
            throw new ChunkSendFailedException(ex, results, RefusalStatus(ex), counts);
        }
    }

    /// <summary>
    /// The status the items of a failed chunk take when the failure carries none of its own.
    /// </summary>
    private static int RefusalStatus(Exception ex) => ex switch
    {
        GraphServiceException graph => (int)graph.StatusCode,
        HttpRequestException { StatusCode: { } status } => (int)status,
        _ => RefusedWithoutStatus,
    };

    private async Task<(IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> Results, int ThrottleDelaySeconds, int ItemRetries, int ThrottleEncounters, long RetryDelayMs)> SendChunkAttemptsAsync(
        BatchOperation[] operations,
        (BatchOperation Operation, GraphBatchResponseItem Response)?[] results,
        ChunkAttemptCounts counts,
        CancellationToken cancellationToken,
        CancellationToken stopRequested)
    {
        // Track which indices are still pending
        var pendingIndices = Enumerable.Range(0, operations.Length).ToList();
        // Track last-seen throttle delay for cross-chunk backpressure
        int lastThrottleDelaySeconds = 0;

        for (int attempt = 0; attempt <= MaxPerRequestRetries; attempt++)
        {
            if (pendingIndices.Count == 0) break;
            // A chunk that was already sending when another chunk was refused must not send
            // again: after the refusal the run is stopping, and a POST into it is a write
            // nothing will report on. Read at the first attempt as well as the later ones -
            // the refusal can land in the window between the permit and the first POST.
            if (stopRequested.IsCancellationRequested)
            {
                // At the first attempt nothing has gone out at all. Returning no results leaves
                // the chunk's slots to the run's never-sent handling, which is what happened to
                // it; a status here would claim its writes were attempted.
                if (attempt == 0) return ([], 0, 0, 0, 0);
                break;
            }

            var batchRequest = new GraphBatchRequest();
            var idToIndex = new Dictionary<string, int>();
            // What the previous attempt got for the items this one is about to re-send. Put back
            // if this attempt is stopped before it reaches the wire: an attempt that never went
            // out does not unmake the answer before it.
            var withheld = new (BatchOperation Operation, GraphBatchResponseItem Response)?[pendingIndices.Count];

            for (int i = 0; i < pendingIndices.Count; i++)
            {
                var originalIndex = pendingIndices[i];
                var op = operations[originalIndex];
                var id = (i + 1).ToString();
                idToIndex[id] = originalIndex;

                // In flight again, so an earlier attempt's answer is no longer the last thing
                // that happened to it. If this attempt fails the item is attempted-unknown,
                // not whatever it was before.
                withheld[i] = results[originalIndex];
                results[originalIndex] = null;

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

            // Counted here rather than when the retry was decided on: between the two the run
            // can stop, and a retry counted in front of a backoff that the stop then ends is a
            // request the summary claims went out over one that never did.
            if (attempt > 0) counts.ItemRetries += pendingIndices.Count;

            var json = JsonSerializer.Serialize(batchRequest, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpSw = Stopwatch.StartNew();
            // paceGate: false - this class owns batch throughput (cross-call pacing, item-level
            // AIMD). Running the request pacer's gate on top would stack two independent AIMD
            // controllers on one workload. Outer responses still feed pacer signal state.
            //
            // stopRetries carries the same stop the loop above reads. The transport retries this
            // POST on its own for a throttled envelope, and it did so after the run had stopped
            // sending: the chunk waited out the Retry-After the server asked for and then put
            // its writes back on the wire. It reaches only the decision to send again, never the
            // attempt in flight - that one finishes and keeps its answer.
            HttpResponseMessage response;
            try
            {
                response = await _client.PostAsync(_batchUrl, content, cancellationToken,
                    paceGate: false, stopRetries: stopRequested);
            }
            // The stop reached this attempt while it was still waiting for its turn to send - a
            // permit from the token bucket - so nothing of it went out. The same situation as
            // the check at the top of the loop, a moment later, and answered the same way.
            catch (RequestNotSentException)
            {
                // At the first attempt nothing of the chunk has gone out at all, so its slots
                // belong to the run's never-sent handling rather than to a status here.
                if (attempt == 0) return ([], 0, 0, 0, 0);

                // A later attempt was counted above in front of a POST that then never left. The
                // run reports the requests that went out, not the ones it had ready.
                counts.ItemRetries -= pendingIndices.Count;
                for (int i = 0; i < pendingIndices.Count; i++)
                    results[pendingIndices[i]] = withheld[i];
                break;
            }
            using var responseScope = response;
            var httpMs = httpSw.ElapsedMilliseconds;
            if (httpMs > 5000)
                _pendingVerbose.Enqueue($"Batch HTTP slow: {httpMs}ms for {pendingIndices.Count} items (attempt {attempt + 1})");

            if (!response.IsSuccessStatusCode)
            {
                using var errCts = _client.CreateBodyReadCts(cancellationToken);
                Exception refusal;
                try
                {
                    var errorBody = await response.Content.ReadAsStringAsync(errCts.Token);
                    refusal = new GraphServiceException(response.StatusCode, errorBody);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    refusal = new HttpRequestException(ResilientGraphClient.BodyReadTimedOutMessage);
                }
                // A refusal on a later attempt does not unmake the sub-responses the earlier
                // attempts got: those items were answered, and for a write that answer is the
                // record of what the server did. They travel with the failure.
                throw new ChunkSendFailedException(refusal, results, (int)response.StatusCode, counts);
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
                throw new HttpRequestException(ResilientGraphClient.BodyReadTimedOutMessage);
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
            // Sub-responses the envelope gave no status of its own. Collected rather than
            // recorded, and answered for below.
            List<string>? statusless = null;

            foreach (var item in batchResponse.Responses)
            {
                if (!idToIndex.TryGetValue(item.Id, out var originalIndex)) continue;
                var op = operations[originalIndex];

                // The deserializer cannot tell a "status" member the envelope left out from one
                // set to 0, and neither of those is a status a server answers with - while 0 is
                // exactly NotSentStatus. A sub-response exists only for a request that went out,
                // so recording it would hand back an answered write as one that certainly never
                // reached the server, with a dead-letter line inviting the caller to send it
                // again. Left out of the results, for the refusal below to fill.
                if (item.Status <= 0)
                {
                    (statusless ??= []).Add(item.Id);
                    continue;
                }

                // Count all 429s regardless of retry budget
                if (item.Status == 429)
                {
                    counts.ThrottleEncounters++;

                    // Parse Retry-After for delay calculation. Handles both formats:
                    // - delta-seconds: "120" (integer seconds)
                    // - HTTP-date: "Wed, 21 Oct 2015 07:28:00 GMT"
                    if (item.Headers != null)
                    {
                        var retryAfterValue = item.Headers
                            .FirstOrDefault(h => string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                            .Value;
                        {
                            if (RetryAfterPolicy.TryResolveSeconds(retryAfterValue, _maxRetryAfterSeconds,
                                    out var seconds, out var clamped))
                            {
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

                // Recorded even when it is about to be retried: the next attempt overwrites it,
                // and if there is no next attempt this is what the server last said about the
                // item - which is not the same thing as never having sent it.
                results[originalIndex] = (op, item);
                if (IsRetryable(item.Status, op.Method) && attempt < MaxPerRequestRetries)
                    retryIndices.Add(originalIndex);
            }

            // An envelope that answers a request without saying what happened to it has not
            // answered it, and is reported the way the count and id checks above report an
            // envelope that answers the wrong requests: as this chunk's failure. The items it
            // did answer keep those answers, and the rest take the refusal status - which says
            // the write may have landed, not that it certainly did not.
            if (statusless != null)
            {
                throw new InvalidOperationException(
                    $"Graph $batch answered {statusless.Count} of {batchResponse.Responses.Count} requests "
                    + $"without a status (id {string.Join(", ", statusless)}) "
                    + $"(attempt {attempt + 1}/{MaxPerRequestRetries + 1}). "
                    + "The response may have been truncated or rewritten in transit.");
            }

            pendingIndices = retryIndices;

            lastThrottleDelaySeconds = Math.Max(lastThrottleDelaySeconds, maxRetryAfterSeconds);

            if (retryIndices.Count > 0 && attempt < MaxPerRequestRetries)
            {
                // Waiting out a backoff for an attempt that will not be sent holds the whole
                // batch open for a Retry-After the run has already stopped honoring.
                if (stopRequested.IsCancellationRequested) break;

                var baseDelaySeconds = maxRetryAfterSeconds > 0
                    ? maxRetryAfterSeconds
                    : (int)Math.Pow(2, attempt);
                // C4: Add 0-50% jitter to prevent thundering herd on batch retries
                var jitter = baseDelaySeconds * Random.Shared.NextDouble() * 0.5;
                var retrySw = Stopwatch.StartNew();
                // A refusal that lands mid-wait ends the wait. Checked only before it started,
                // a chunk that stopped sending 300ms into a peer's Retry-After still held the
                // call open for the rest of it - a minute, and up to the cap plus jitter.
                using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, stopRequested))
                {
                    try
                    {
                        RetryWaitStarted?.Invoke();
                        await Task.Delay(TimeSpan.FromSeconds(baseDelaySeconds + jitter), delayCts.Token);
                    }
                    // The stop, not the caller's cancellation. The loop continues to the checks
                    // at the top of the next attempt, which end the chunk where it stands.
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }
                counts.RetryDelayMs += retrySw.ElapsedMilliseconds;
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
        return (results.Select(r => r!.Value).ToList(), lastThrottleDelaySeconds,
            counts.ItemRetries, counts.ThrottleEncounters, counts.RetryDelayMs);
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
    /// Whether a batch response item should be retried: the same classifier and policy the
    /// request pipeline uses, so the two cannot drift apart again. Batch items carry a
    /// status and inline headers, never an exception - that is the whole difference.
    /// </summary>
    private static bool IsRetryable(int statusCode, string method)
        => MgxErrorPolicy.ShouldRetry(
            MgxErrorClassifier.Classify(statusCode).Class,
            isIdempotent: !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase));
}
