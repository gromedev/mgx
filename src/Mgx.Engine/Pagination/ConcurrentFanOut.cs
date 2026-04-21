using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.Engine.Pagination;

/// <summary>
/// Result of a fan-out read operation. Contains successful results and per-URL errors.
/// Callers can inspect Errors to decide how to handle partial failures.
/// </summary>
public sealed class FanOutResult
{
    public Dictionary<string, JsonElement[]> Results { get; init; } = new();
    public Dictionary<string, Exception> Errors { get; init; } = new();
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Result of a bulk write operation. Contains success/failure counts, per-item errors,
/// and successful response bodies (e.g., created entities from POST).
/// </summary>
public sealed class BulkWriteResult
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<BulkWriteError> Errors { get; init; } = [];
    public IReadOnlyList<(string Id, JsonElement Response)> Responses { get; init; } = [];
    /// <summary>Wall-clock milliseconds for the entire BulkWriteAsync call.</summary>
    public long ElapsedMs { get; init; }
}

public sealed record BulkWriteError(string Id, int StatusCode, string Message);

/// <summary>
/// Parallel fan-out for per-entity operations (e.g., get members per group).
/// Uses SemaphoreSlim-bounded concurrency. Returns partial results on failure;
/// one URL failing does NOT discard successful URLs' data.
/// </summary>
public sealed class ConcurrentFanOut
{
    private readonly ResilientGraphClient _client;
    private readonly int _maxConcurrency;

    public ConcurrentFanOut(ResilientGraphClient client, int maxConcurrency = 5)
    {
        _client = client;
        _maxConcurrency = maxConcurrency < 1 ? 1 : maxConcurrency;
    }

    /// <summary>
    /// Fetch multiple URLs concurrently with bounded parallelism.
    /// Returns partial results: successful URLs' data is preserved even if other URLs fail.
    /// Check FanOutResult.Errors for per-URL failures.
    /// </summary>
    public async Task<FanOutResult> FetchAllAsync(
        IReadOnlyList<string> urls,
        int maxItemsPerUrl = 0,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, JsonElement[]>();
        var errors = new ConcurrentDictionary<string, Exception>();
        using var semaphore = new SemaphoreSlim(_maxConcurrency);

        var tasks = urls.Select(async url =>
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                acquired = true;

                var allItems = new List<JsonElement>();
                string? nextLink = url;
                int consecutiveEmptyPages = 0;

                // Extract expected host for nextLink validation (SSRF prevention)
                Uri? expectedHost = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null;

                while (nextLink != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await _client.GetCollectionPageAsync(nextLink, cancellationToken, headers);

                    if (page.Value.Length == 0)
                    {
                        consecutiveEmptyPages++;
                        if (consecutiveEmptyPages >= 3)
                            break;
                    }
                    else
                    {
                        consecutiveEmptyPages = 0;
                    }

                    allItems.AddRange(page.Value);

                    if (maxItemsPerUrl > 0 && allItems.Count >= maxItemsPerUrl)
                    {
                        allItems = allItems.Take(maxItemsPerUrl).ToList();
                        break;
                    }

                    // Validate nextLink host matches initial URL (prevents SSRF via
                    // crafted Graph responses redirecting authenticated requests)
                    nextLink = NextLinkValidator.Validate(page.NextLink, expectedHost);
                }

                results[url] = allItems.ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Let cancellation propagate, user pressed Ctrl+C
            }
            catch (Exception ex)
            {
                errors[url] = ex;
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new FanOutResult
        {
            Results = new Dictionary<string, JsonElement[]>(results),
            Errors = new Dictionary<string, Exception>(errors)
        };
    }

    /// <summary>
    /// Execute an action for each item with bounded parallelism.
    /// Streams results through a callback as they become available.
    /// Individual item failures are collected, not thrown.
    /// </summary>
    public async Task<Dictionary<T, Exception>> ForEachAsync<T>(
        IReadOnlyList<T> items,
        Func<T, CancellationToken, Task> action,
        CancellationToken cancellationToken = default) where T : notnull
    {
        var errors = new ConcurrentDictionary<T, Exception>();
        using var semaphore = new SemaphoreSlim(_maxConcurrency);

        var tasks = items.Select(async item =>
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                acquired = true;
                await action(item, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors[item] = ex;
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new Dictionary<T, Exception>(errors);
    }

    /// <summary>
    /// Execute write operations (POST/PATCH/PUT/DELETE) concurrently with bounded parallelism.
    /// Each operation gets a fresh HttpContent copy from the serialized body string.
    /// Returns partial results: successful operations are preserved even if others fail.
    /// </summary>
    public async Task<BulkWriteResult> BulkWriteAsync(
        HttpMethod method,
        IReadOnlyList<(string id, string url)> operations,
        string? serializedBody,
        Dictionary<string, string>? headers = null,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var errorBag = new ConcurrentBag<BulkWriteError>();
        var responseBag = new ConcurrentBag<(string Id, JsonElement Response)>();
        int succeeded = 0;
        var totalSw = Stopwatch.StartNew();
        using var semaphore = new SemaphoreSlim(_maxConcurrency);

        var tasks = operations.Select(async op =>
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                acquired = true;

                HttpContent? content = serializedBody != null
                    ? new StringContent(serializedBody, Encoding.UTF8, "application/json")
                    : null;

                try
                {
                    using var response = await _client.SendAsync(method, op.url, content, headers, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        string body;
                        using var errCts = _client.CreateBodyReadCts(cancellationToken);
                        try
                        {
                            body = await response.Content.ReadAsStringAsync(errCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            body = $"Response body read timed out (HTTP {(int)response.StatusCode})";
                        }
                        var errorMessage = TryExtractGraphError(body) ?? $"HTTP {(int)response.StatusCode}";
                        errorBag.Add(new BulkWriteError(op.id, (int)response.StatusCode, errorMessage));

                        var errCurrent = Volatile.Read(ref succeeded) + errorBag.Count;
                        onProgress?.Invoke(errCurrent, operations.Count);
                        return;
                    }

                    // Capture response body for POST/PATCH (created/updated entities)
                    if (response.StatusCode != HttpStatusCode.NoContent)
                    {
                        var bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        if (bodyBytes.Length > 0)
                        {
                            var json = JsonSerializer.Deserialize<JsonElement>(bodyBytes);
                            responseBag.Add((op.id, json));
                        }
                    }

                    // Increment AFTER deserialization to prevent double-counting if
                    // body parsing throws (succeeded would be incremented but error also added)
                    Interlocked.Increment(ref succeeded);
                }
                finally
                {
                    content?.Dispose();
                }

                var current = Volatile.Read(ref succeeded) + errorBag.Count;
                onProgress?.Invoke(current, operations.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var statusCode = ex is GraphServiceException gse ? (int)gse.StatusCode : 0;
                errorBag.Add(new BulkWriteError(op.id, statusCode, ex.Message));

                var current = Volatile.Read(ref succeeded) + errorBag.Count;
                onProgress?.Invoke(current, operations.Count);
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new BulkWriteResult
        {
            Succeeded = succeeded,
            Failed = errorBag.Count,
            Errors = errorBag.ToArray(),
            Responses = responseBag.ToArray(),
            ElapsedMs = totalSw.ElapsedMilliseconds
        };
    }

    private static string? TryExtractGraphError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (code != null && message != null)
                    return $"{code}: {message}";
                if (code != null || message != null)
                    return code ?? message;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
