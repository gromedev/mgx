using System.Collections.Concurrent;
using System.Text;
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

    /// <summary>
    /// File name (not path) of the temp the writing run was appending to, relative to the
    /// output's directory - or null when that run wrote straight to the output. This is what
    /// makes a checkpoint answerable: without it, "no temp on disk" cannot be told apart from
    /// "a temp from some other run is on disk", and neither from "the run was appending".
    /// </summary>
    [JsonPropertyName("tempFile")]
    public string? TempFile { get; set; }

    /// <summary>
    /// Path of the output the writing run was collecting into. DataLength is a byte offset into
    /// "the output" and nothing else here says WHICH one, so a checkpoint path used by two runs
    /// let one run's offset cut the other run's file. The whole path and not the name: two
    /// exports to "users.jsonl" in different directories share a name and share nothing else.
    /// Null on checkpoints written before this was recorded, which say nothing about ownership
    /// and are read as evidence of it only where the files on disk corroborate them.
    /// </summary>
    [JsonPropertyName("outputFile")]
    public string? OutputFile { get; set; }

    /// <summary>
    /// Byte length of that file at the moment this checkpoint was saved, captured after the
    /// writer was flushed. Null on checkpoints written before this was recorded, which are
    /// treated as unverifiable rather than as zero-length.
    /// </summary>
    [JsonPropertyName("dataLength")]
    public long? DataLength { get; set; }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked, or the account cannot read it; treat as no checkpoint either way. Windows
            // reports a denying ACL as UnauthorizedAccessException, which is not an IOException,
            // so catching only the latter made an unreadable checkpoint throw on Windows and
            // resume cleanly everywhere else.
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
            try
            {
                // Cleared and then created, rather than written into whatever stands at the
                // name: an open that creates or truncates follows a symlink there and fills or
                // truncates the link's target with this checkpoint, and a FIFO with no reader
                // blocks the write forever - past a cancellation, since nothing on the pipeline
                // thread can reach a blocked open.
                using (var scratch = ScratchName.Create(tmpPath, "the checkpoint's staging file"))
                using (var writer = new StreamWriter(scratch, new UTF8Encoding(false, true),
                           1024, leaveOpen: true))
                {
                    writer.Write(json);
                }
                File.Move(tmpPath, normalizedPath, overwrite: true);
            }
            catch
            {
                // Same reasoning as the delta state: a staging file that never became the
                // checkpoint is litter, and adoption already has to reason about stray files.
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
