using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mgx.Engine.Pagination;

public enum DeltaLoadResult { NotFound, Corrupt, Ok }

/// <summary>
/// Persistent delta query state for incremental sync.
/// Unlike PaginationCheckpoint (ephemeral, deleted on success), DeltaState
/// persists across successful completions to track the delta position.
/// Uses atomic write (temp file + rename) and per-path locking.
/// </summary>
public sealed class DeltaState
{
    [JsonPropertyName("deltaLink")]
    public string DeltaLink { get; set; } = string.Empty;

    [JsonPropertyName("select")]
    public string? Select { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("lastSync")]
    public DateTimeOffset LastSync { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("itemCount")]
    public long ItemCount { get; set; }

    [JsonPropertyName("graphEndpoint")]
    public string GraphEndpoint { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly ConcurrentDictionary<string, object> s_pathLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Load delta state with diagnostic result. Distinguishes "not found" from "corrupt".
    /// Does NOT acquire lock. Atomic writes (temp + rename) ensure reads always see
    /// a complete file. Locking reads would block the cmdlet thread for zero benefit.
    /// </summary>
    public static (DeltaState? State, DeltaLoadResult Result) LoadWithResult(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath)) return (null, DeltaLoadResult.NotFound);
        try
        {
            var json = File.ReadAllText(normalizedPath);
            var state = JsonSerializer.Deserialize<DeltaState>(json, JsonOptions);
            return state != null ? (state, DeltaLoadResult.Ok) : (null, DeltaLoadResult.Corrupt);
        }
        catch (JsonException)
        {
            return (null, DeltaLoadResult.Corrupt);
        }
        catch (IOException)
        {
            return (null, DeltaLoadResult.NotFound);
        }
    }

    /// <summary>
    /// Backward-compatible Load. Returns null for both "not found" and "corrupt".
    /// </summary>
    public static DeltaState? Load(string path) => LoadWithResult(path).State;

    /// <summary>
    /// Atomically save delta state. Writes to temp file first, then renames.
    /// Per-path lock prevents concurrent runspaces from corrupting the same file.
    /// </summary>
    public void Save(string path)
    {
        LastSync = DateTimeOffset.UtcNow;
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
    /// Delete delta state file and temp file. Acquires per-path lock to avoid
    /// racing with concurrent Save operations.
    /// </summary>
    /// <summary>
    /// Returns true if the file was deleted (or didn't exist), false if deletion failed.
    /// </summary>
    public static bool Delete(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        var lockObj = s_pathLocks.GetOrAdd(normalizedPath, _ => new object());
        lock (lockObj)
        {
            try
            {
                if (File.Exists(normalizedPath)) File.Delete(normalizedPath);
                var tmpPath = normalizedPath + ".tmp";
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Validates write access to the delta file path. Call in BeginProcessing
    /// to fail fast before any HTTP calls.
    /// </summary>
    public static void ValidateWriteAccess(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var probe = path + ".probe";
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot write to delta state path '{path}': {ex.Message}", ex);
        }
    }
}
