using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mgx.Engine.Models;

/// <summary>
/// Generic wrapper for OData collection responses from Microsoft Graph.
/// </summary>
public sealed class GraphCollectionResponse<T>
{
    [JsonPropertyName("value")]
    public T[] Value { get; set; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }

    [JsonPropertyName("@odata.count")]
    public long? Count { get; set; }

    [JsonPropertyName("@odata.context")]
    public string? Context { get; set; }
}

/// <summary>
/// Raw JSON collection response for deserialization before model binding.
/// </summary>
public sealed class GraphRawCollectionResponse
{
    [JsonPropertyName("value")]
    public JsonElement[] Value { get; set; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }

    [JsonPropertyName("@odata.count")]
    public long? Count { get; set; }

    [JsonPropertyName("@odata.deltaLink")]
    public string? DeltaLink { get; set; }
}

/// <summary>
/// Batch request body for /$batch endpoint.
/// </summary>
public sealed class GraphBatchRequest
{
    [JsonPropertyName("requests")]
    public List<GraphBatchRequestItem> Requests { get; set; } = [];
}

public sealed class GraphBatchRequestItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Body { get; set; }
}

/// <summary>
/// Structured batch operation for GraphBatchClient.
/// Supports any HTTP method with optional body.
/// </summary>
public record BatchOperation(string Url, string Method = "GET", JsonElement? Body = null);

/// <summary>
/// Batch response from /$batch endpoint.
/// </summary>
public sealed class GraphBatchResponse
{
    [JsonPropertyName("responses")]
    public List<GraphBatchResponseItem> Responses { get; set; } = [];
}

public sealed class GraphBatchResponseItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
}

/// <summary>
/// Result of a batch execution including per-item results and operational telemetry.
/// </summary>
public sealed class BatchExecutionResult
{
    public required IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> Results { get; init; }
    public required BatchTelemetry Telemetry { get; init; }
}

/// <summary>
/// Operational telemetry for a batch execution.
/// Tracks retry counts, throttle encounters, and batch-level retry activity.
/// Thread-safe: counters use Interlocked for concurrent chunk updates.
/// </summary>
public sealed class BatchTelemetry
{
    public int TotalRequests { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }

    /// <summary>Total per-item retries across all chunks (each item retry attempt counts as 1).</summary>
    private int _itemRetries;
    public int ItemRetries { get => _itemRetries; set => _itemRetries = value; }
    public void AddItemRetries(int count) => Interlocked.Add(ref _itemRetries, count);

    /// <summary>Number of individual 429 responses encountered across all attempts.</summary>
    private int _throttleEncounters;
    public int ThrottleEncounters { get => _throttleEncounters; set => _throttleEncounters = value; }
    public void AddThrottleEncounters(int count) => Interlocked.Add(ref _throttleEncounters, count);

    /// <summary>Number of items retried in the batch-level retry pass.</summary>
    public int BatchLevelRetries { get; set; }

    /// <summary>Wall-clock milliseconds from first chunk send to last result received.</summary>
    public long TotalElapsedMs { get; set; }

    /// <summary>Total milliseconds spent in per-item and cross-chunk retry delays (not HTTP time).</summary>
    private long _totalRetryDelayMs;
    public long TotalRetryDelayMs { get => _totalRetryDelayMs; set => _totalRetryDelayMs = value; }
    public void AddRetryDelayMs(long ms) => Interlocked.Add(ref _totalRetryDelayMs, ms);
}
