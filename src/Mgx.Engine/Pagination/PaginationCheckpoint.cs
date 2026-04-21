using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mgx.Engine.Pagination;

/// <summary>
/// Checkpoint state for resumable pagination.
/// Saved as JSON after each page; auto-deleted on successful completion.
/// Uses atomic write (write to .tmp, then rename) to prevent corruption.
/// </summary>
public sealed class PaginationCheckpoint
{
    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }

    [JsonPropertyName("itemsCollected")]
    public long ItemsCollected { get; set; }

    /// <summary>
    /// Number of items from the current page already written to disk.
    /// On resume, this many items are skipped from the first fetched page
    /// to prevent duplicates. Defaults to 0 (backward compatible with old checkpoints).
    /// </summary>
    [JsonPropertyName("pageItemsAlreadyWritten")]
    public int PageItemsAlreadyWritten { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // Per-path lock prevents concurrent runspaces from corrupting the same checkpoint (RD-H7)
    private static readonly ConcurrentDictionary<string, object> s_pathLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Load a checkpoint from disk. Returns null if the file doesn't exist or is corrupt.
    /// </summary>
    public static PaginationCheckpoint? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PaginationCheckpoint>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Corrupt checkpoint file (e.g., partial write from crash).
            // Treat as no checkpoint; caller will start fresh.
            return null;
        }
        catch (IOException)
        {
            // File locked or inaccessible; treat as no checkpoint.
            return null;
        }
    }

    /// <summary>
    /// Atomically save checkpoint to disk. Writes to a temp file first, then renames.
    /// This prevents corruption if the process crashes mid-write.
    /// Per-path lock prevents concurrent runspaces from corrupting the same file.
    /// </summary>
    public void Save(string path)
    {
        Timestamp = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var normalizedPath = Path.GetFullPath(path);
        var lockObj = s_pathLocks.GetOrAdd(normalizedPath, _ => new object());
        lock (lockObj)
        {
            var tmpPath = normalizedPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, normalizedPath, overwrite: true);
        }
    }

    /// <summary>
    /// Returns true if deleted (or didn't exist), false if deletion failed.
    /// </summary>
    public static bool Delete(string path)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var lockObj = s_pathLocks.GetOrAdd(normalizedPath, _ => new object());
            lock (lockObj)
            {
                if (File.Exists(normalizedPath)) File.Delete(normalizedPath);
                var tmpPath = normalizedPath + ".tmp";
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
