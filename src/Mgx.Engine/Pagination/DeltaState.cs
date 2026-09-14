using System.Collections.Concurrent;
using System.Text;
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

    // Normalized Prefer tokens the enumeration was taken with (2.1 additive: state files
    // written before this property deserialize to null - no migration needed).
    [JsonPropertyName("prefer")]
    public string? Prefer { get; set; }

    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("lastSync")]
    public DateTimeOffset LastSync { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("itemCount")]
    public long ItemCount { get; set; }

    [JsonPropertyName("graphEndpoint")]
    public string GraphEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Graph API version the deltaLink was issued by, read from the link rather than from the
    /// request, so it names the version the token actually came from. Empty on state files
    /// written before 2.1.0 - including every 2.0.1 one - which is treated as "unknown" rather
    /// than a mismatch so upgrades keep working.
    /// </summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = string.Empty;

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows reports a denying ACL as UnauthorizedAccessException, which is not an
            // IOException - catching only the latter made unreadable state throw there and read
            // as absent everywhere else.
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
            try
            {
                // Cleared and then created, rather than written into whatever stands at the
                // name: an open that creates or truncates follows a symlink there and fills or
                // truncates the link's target with this state, and a FIFO with no reader blocks
                // the write forever - past a cancellation, since nothing on the pipeline thread
                // can reach a blocked open.
                using (var scratch = ScratchName.Create(tmpPath, "the delta state's staging file"))
                using (var writer = new StreamWriter(scratch, new UTF8Encoding(false, true),
                           1024, leaveOpen: true))
                {
                    writer.Write(json);
                }
                File.Move(tmpPath, normalizedPath, overwrite: true);
            }
            catch
            {
                // The staging file is not the state; leaving it behind only invites a later run
                // to wonder what it is.
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Validates write access to a path the run will write to. Call in BeginProcessing to fail
    /// fast before any HTTP calls.
    /// </summary>
    /// <param name="path">The file whose directory has to be writable.</param>
    /// <param name="what">
    /// What that file is, for the failure to name. The sync probes two of them, and one noun for
    /// both reported an -OutputFile directory it could not write to as the delta state path -
    /// sending the caller to a file that had just passed the same probe.
    /// </param>
    public static void ValidateWriteAccess(string path, string what = "delta state path")
    {
        // NOTE: a directory passes the probe below, because the probe writes "<path>.probe"
        // NEXT to the target rather than to it, so the fail-fast check succeeds and the real
        // write fails later. Rejecting a directory here would be better, but this method also
        // validates -OutputFile, where the late failure is what
        // DeltaQueryTests.Cmdlet_Checkpoint_MidPage_BoundsProgressLostWhenARunDiesInsideAPage
        // uses to reach a mid-page abort - and that test guards more than this costs. Left as
        // it is deliberately rather than by omission.
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // The probe name is cleared before it is created, and created by an open that refuses
        // to use anything already standing there. Written with Create/Write, the probe followed
        // a symlink at that name and truncated the link's target to nothing - a file outside
        // this directory, taken away by a check whose whole purpose is to touch nothing - and a
        // FIFO left there blocked the open in BeginProcessing, before the run had anything a
        // cancellation could unwind.
        var probe = path + ".probe";
        var swept = ScratchName.Sweep(probe, out var stood);

        // A file another run holds at that name is that run's own probe, taken out a moment ago
        // in this very directory - which is the question this one is asking. Two runs over one
        // -DeltaPath, or one -OutputFile, reach here together as a matter of course, and
        // refusing either of them for the other's sake would end a run over nothing that is
        // wrong with the path. The two refusals left say something about the path itself: a
        // directory at the name, or an entry the directory will not let go of.
        if (swept == ScratchEntry.Held) return;
        if (swept.Refused())
        {
            throw new InvalidOperationException(
                $"Cannot write to {what} '{path}': {swept.WhatStood(probe, stood)}.", stood);
        }

        FileStream created;
        try
        {
            created = ScratchName.CreateNew(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another run reached the name first, which on this name is its probe: a create
            // refuses a name that already exists, and a create that meets the claim the other
            // run is holding that name by is refused as a sharing violation. Either way it is
            // the answer the sweep above gives a held probe, for the reason it gives it.
            //
            // Read off the refusal as well as off the name, because the entry that refuses this
            // create is one the run that made it is about to take away: asked of the name alone,
            // the question arrives after the answer has gone. Two syncs released together over
            // one -DeltaPath had the second create refused by the first one's probe, found the
            // name clear an instruction later, and ended in BeginProcessing naming a path
            // nothing was wrong with.
            //
            // A directory is not that. It is the path's own refusal, it is not this run's to
            // remove, and it is still standing to be asked about - which is asked of the
            // filesystem, since a create meets one as an access failure on Windows and as a
            // name that already exists on Unix. Neither is a create the directory refused
            // outright, which is the failure this whole probe exists to reach.
            var reachedFirst = File.Exists(probe)
                || (ex is IOException refused
                    && (ScratchName.IsAlreadyExists(refused)
                        || ScratchName.IsSharingViolation(refused)));
            if (reachedFirst && !Directory.Exists(probe)) return;
            throw new InvalidOperationException(
                $"Cannot write to {what} '{path}': {ex.Message}", ex);
        }

        // Unlinked under the handle that holds it, so the name is never decided and unheld.
        ScratchName.Discard(probe, created);
    }
}
