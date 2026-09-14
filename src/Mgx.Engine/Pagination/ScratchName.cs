namespace Mgx.Engine.Pagination;

/// <summary>
/// What stands at a scratch name. The same reading either way round: what a sweep took off the
/// name, or what an inspection found standing there and left.
/// </summary>
public enum ScratchEntry
{
    /// <summary>Nothing stands at the name.</summary>
    Clear,

    /// <summary>A link. Swept, the link goes and what it pointed at is left alone.</summary>
    Link,

    /// <summary>A regular file nothing holds - a leftover from a run that was killed.</summary>
    Leftover,

    /// <summary>
    /// A regular file this account cannot open read-write. Swept, since the name is nobody's to
    /// keep, but nothing here could look inside it to say more than that.
    /// </summary>
    Unopenable,

    /// <summary>
    /// Something that is not a file with a length and an offset in it - a pipe, a socket, a
    /// device. Swept: nothing can be written at the name while it stands there.
    /// </summary>
    NotAFile,

    /// <summary>
    /// A directory. Not this caller's to delete, and it could hold anything.
    /// </summary>
    Directory,

    /// <summary>A file another run has open.</summary>
    Held,

    /// <summary>
    /// The entry could not be read, or could not be unlinked. The exception says why.
    /// </summary>
    Unreadable,
}

/// <summary>
/// The scratch names a run makes beside a file it is writing - the write probe, the atomic
/// saves' staging file, the copy a promotion stages - cleared before anything is opened
/// through them.
/// <para>
/// No run leaves a file under one of those names on purpose: each is created, written and
/// renamed away or removed inside one operation, so an entry standing at one is a run that was
/// killed between two of those steps, or something a caller put there. Opening the name to
/// find out is what has to stop: an open that creates or truncates follows a symlink and
/// truncates the link's target, a FIFO with no reader blocks the open forever with no way for
/// a cancellation to reach it, and a create that refuses an existing name cannot say what kind
/// of entry refused it. So the entry is decided off the directory rather than through a
/// handle, and the kinds divide by what a run can honestly do about them: a link goes - the
/// link and never its target - and so does anything at the name that is not a regular file,
/// and a regular file no run holds. A directory and a file another run has open stop the
/// caller with the disk as it was found.
/// </para>
/// <para>
/// It lives here rather than beside the promotion that first needed it because the engine
/// cannot see the cmdlet base: the checkpoint and the delta state write scratch names of their
/// own, and one primitive is what keeps a pipe at a checkpoint's staging name from wedging a
/// run that a pipe at an output's staging name is refused at.
/// </para>
/// </summary>
public static class ScratchName
{
    /// <summary>
    /// Clear <paramref name="path"/>, or say why the caller stops there. Nothing of the
    /// caller's may be open over the name when this runs: the claim below asks for the entry
    /// exclusively, and a hard link at the name is an alias for whatever inode it points at -
    /// so run under a claim on that inode, the sweep reads its own caller's lock as a second
    /// run holding the entry.
    /// </summary>
    /// <param name="failure">
    /// The exception behind <see cref="ScratchEntry.Unreadable"/>, for a caller that words
    /// its own refusal from it; null for every other answer.
    /// </param>
    public static ScratchEntry Sweep(string path, out Exception? failure) =>
        Decide(path, remove: true, out failure);

    /// <summary>
    /// What stands at <paramref name="path"/>, with the name left exactly as it was found. The
    /// same reading <see cref="Sweep"/> gives, so a pass that reports what a run would take off
    /// the name reports the kind that run would name - and a preview that swept in order to find
    /// out made deletions of its own, which is the one thing it is asked not to do.
    /// </summary>
    /// <inheritdoc cref="Sweep" path="/param"/>
    public static ScratchEntry Inspect(string path, out Exception? failure) =>
        Decide(path, remove: false, out failure);

    private static ScratchEntry Decide(string path, bool remove, out Exception? failure)
    {
        // Asked again where the name moved under the claim below, and answered Held where it
        // keeps moving: a name being mutated right now is a name this caller leaves alone, which
        // is what every caller here already does with a name a second run holds. Each attempt is
        // a fresh open, so the run that took the name answers the next one with its own lock.
        for (var attempt = 1; ; attempt++)
        {
            if (DecideOnce(path, remove, out failure) is { } entry) return entry;
            if (attempt == HeldEntry.ClaimAttempts) return ScratchEntry.Held;
        }
    }

    /// <summary>
    /// One attempt at the name. Null where the claim below was granted over an entry that had
    /// left the name - the one answer that is neither what stands there nor a refusal, and the
    /// one asking again can change.
    /// </summary>
    private static ScratchEntry? DecideOnce(string path, bool remove, out Exception? failure)
    {
        failure = null;
        try
        {
            // Read off the directory entry rather than through an open, which is what lets a
            // dangling link and a symlink loop be answered at all: both are links and neither
            // opens. File.Delete unlinks the link and leaves what it pointed at alone.
            if (new FileInfo(path).LinkTarget != null)
            {
                if (remove) File.Delete(path);
                return ScratchEntry.Link;
            }

            if (Directory.Exists(path)) return ScratchEntry.Directory;

            FileStream? claim = null;
            try
            {
                bool seekable;
                try
                {
                    // FileAccess.ReadWrite, because it is the one access a FIFO answers without
                    // blocking: a read-only or write-only open of a pipe with nothing on the
                    // other end waits for a peer forever, and a run parked there holds
                    // everything it had claimed with no way for a cancellation to reach it.
                    // FileMode.Open, so nothing is created and nothing is truncated. And
                    // FileShare.None, so what comes back a leftover is an entry no second run
                    // is using.
                    claim = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.None);
                    // Something opened, and it is not a file with a length and an offset in it:
                    // a FIFO opens read-write without blocking and answers every question after
                    // that with a NotSupportedException. Told apart here rather than at the
                    // first write, where a caller has already committed to the name.
                    seekable = claim.CanSeek;
                }
                catch (FileNotFoundException) { return ScratchEntry.Clear; }
                catch (DirectoryNotFoundException)
                {
                    // The same answer one level up: ENOENT on a path component.
                    return ScratchEntry.Clear;
                }
                catch (UnauthorizedAccessException)
                {
                    // A file this account may not open. The name is still nobody's to keep, and
                    // the unlink asks the directory rather than the file.
                    if (remove) Unlink(path);
                    return ScratchEntry.Unopenable;
                }
                catch (IOException ex)
                {
                    // The sharing violation is the one failure with a second run behind it.
                    // Everything else - a socket, a symlink loop, an over-long path - is an
                    // entry nothing can be written at.
                    if (IsSharingViolation(ex)) return ScratchEntry.Held;
                    if (remove) Unlink(path);
                    return ScratchEntry.NotAFile;
                }

                // And the claim is not finished at the open. .NET takes the lock behind
                // FileShare.None after open(2) on Unix, so this handle can have come away over
                // an inode a second run renamed its own file over in between - the lock granted
                // on a file with no name left on it, and the unlink below then taking that run's
                // file off the name. What is held has to be what stands at the name.
                if (!HeldEntry.StillStandsAt(claim, path)) return null;

                var entry = seekable ? ScratchEntry.Leftover : ScratchEntry.NotAFile;
                if (!remove) return entry;

                // Held across the unlink rather than let go in front of it, so there is no
                // instant in which the name is decided and unheld: a second run sweeping the
                // same name in that interval created its own file at it and had it deleted from
                // under the handle it was holding it by. The entry unlinked is the entry
                // claimed - the line above is what makes that true - and unlink(2) honors no
                // lock, so holding it costs the delete nothing.
                //
                // Windows keeps the order it had: a file this process has open cannot be
                // unlinked there without FILE_SHARE_DELETE, so the handle goes in front of the
                // delete and the interval is that platform's.
                if (!OperatingSystem.IsWindows())
                {
                    File.Delete(path);
                    return entry;
                }

                claim.Dispose();
                claim = null;

                // And on the far side of that interval the delete can meet a second run instead
                // of the entry this sweep read: the name was let go, so a run sweeping it in
                // that instant created its own file at it and is holding it, and the delete
                // arrives at that run's claim. What stands there is not a leftover this sweep
                // can answer for - it is the answer the claim above gives a name another run
                // has, which every caller here already leaves the name alone on. Read as an
                // entry nothing could be made of, it stopped runs over a name that was simply
                // in use.
                var taken = DeleteWaitingOutADeleteInFlight(path);
                if (taken == null) return entry;
                failure = taken;
                return ScratchEntry.Held;
            }
            finally { claim?.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
            return ScratchEntry.Unreadable;
        }
    }

    /// <summary>
    /// Whether the entry stops the caller rather than being one a sweep clears.
    /// </summary>
    public static bool Refused(this ScratchEntry entry) =>
        entry is ScratchEntry.Directory or ScratchEntry.Held or ScratchEntry.Unreadable;

    /// <summary>Whether a sweep takes the entry off the name.</summary>
    public static bool Removable(this ScratchEntry entry) =>
        entry is ScratchEntry.Link or ScratchEntry.Leftover or ScratchEntry.Unopenable
            or ScratchEntry.NotAFile;

    /// <summary>
    /// What stood at the name, for a caller that stops on it to name inside its own sentence.
    /// Punctuated by the caller.
    /// </summary>
    public static string WhatStood(this ScratchEntry entry, string path, Exception? failure)
    {
        var name = Path.GetFileName(path);
        return entry switch
        {
            ScratchEntry.Directory => $"a directory stands at '{name}'",
            ScratchEntry.Held => $"another run has '{name}' open",
            _ => $"'{name}' could not be cleared: "
                 + FirstLine(failure?.Message ?? "the reason is not known").TrimEnd('.'),
        };
    }

    /// <summary>
    /// The name, cleared and then made by this caller: the handle that comes back is over an
    /// entry this run created, since the create refuses to use anything already standing there.
    /// </summary>
    /// <exception cref="IOException">
    /// The name could not be cleared - a directory at it, another run holding it - or the
    /// create was refused. A caller with a sentence of its own to say asks
    /// <see cref="Sweep"/> and creates the file itself.
    /// </exception>
    public static FileStream Create(string path, string what)
    {
        var swept = Sweep(path, out var failure);
        if (swept.Refused())
        {
            throw new IOException(
                $"Cannot write {what} '{path}': {swept.WhatStood(path, failure)}.", failure);
        }
        return CreateNew(path);
    }

    /// <summary>
    /// How many times a scratch name is asked again when Windows answers that this account may
    /// not touch it, and how long each wait is. A delete of a name that has been started and has
    /// not finished leaves the entry in the directory with every open of it refused, and both a
    /// create and an unlink meet that as ERROR_ACCESS_DENIED - the same code a directory this
    /// account cannot write answers with, so the two cannot be told apart from the exception.
    /// <para>
    /// What tells them apart is time. The pending state ends when the last handle on the entry
    /// goes, which is the next few milliseconds; a directory this account cannot write is still
    /// refusing an hour later. So the name is asked again a few times over a few tens of
    /// milliseconds, and only a refusal that outlasts that is the path's own - the one this
    /// probe exists to reach, and it costs that path 40 ms it was going to stop on anyway.
    /// </para>
    /// <para>
    /// Measured on Windows Server 2022, four runs creating and unlinking one name together: over
    /// 40 runs of that shape, 154 creates and 64 unlinks were refused this way and not one of
    /// them was anything about the directory.
    /// </para>
    /// <para>
    /// Unix has a delete in flight of its own, and the same wait answers it. A create there is
    /// two steps - the entry is made, and the lock over it is taken after - and a second run
    /// sweeping the same name in between opens the entry this one just made, is granted the lock
    /// first, reads a file nothing holds and takes it off the name. What comes back to the
    /// create is the refused lock, over an entry that is on its way out of the directory; the
    /// "already exists" a step later is the same instant with that unlink not yet landed. Both
    /// are waited out, and only after a refused lock - a name a second run simply has its own
    /// copy standing at is answered at once, as it always was.
    /// </para>
    /// </summary>
    private const int DeleteInFlightAttempts = 6;

    /// <inheritdoc cref="DeleteInFlightAttempts"/>
    private const int DeleteInFlightWaitMs = 8;

    /// <summary>
    /// The create every scratch name is made by: FileMode.CreateNew refuses to use anything
    /// already standing at the name, so the handle that comes back is over an entry this run
    /// made and no other - which is what lets a caller unlink it afterwards without asking whose
    /// it is. FileShare.None from the instant the entry exists, for the same reason.
    /// </summary>
    /// <inheritdoc cref="DeleteInFlightAttempts" path="/summary/para"/>
    public static FileStream CreateNew(string path)
    {
        var takenInTheGap = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()
                                                      && attempt < DeleteInFlightAttempts
                                                      && !Directory.Exists(path))
            {
                Thread.Sleep(DeleteInFlightWaitMs);
            }
            catch (IOException ex) when (!OperatingSystem.IsWindows()
                                         && attempt < DeleteInFlightAttempts
                                         && (IsSharingViolation(ex)
                                             || (takenInTheGap && IsAlreadyExists(ex))))
            {
                // The entry this create made, claimed by a second run in the instant before the
                // lock over it was taken - and read by that run as the one thing it could not
                // be, a file nothing holds. It is being unlinked as this waits, and the name is
                // this run's again on the other side of that.
                takenInTheGap = true;
                Thread.Sleep(DeleteInFlightWaitMs);
            }
        }
    }

    /// <summary>
    /// The unlink a Windows sweep takes the name back with, which answers for itself: a name
    /// that cannot be cleared is one the caller stops on, so unlike <see cref="Unlink"/> this
    /// does not swallow the failure. Two refusals are not that. A delete another run has already
    /// started on the name is the name going, which is all the sweep wanted, and it is waited
    /// out. A second run holding its own file at the name is that run's to answer for, and it is
    /// reported as the hold it is.
    /// </summary>
    /// <returns>
    /// Null where the name is clear. The sharing violation where a second run has taken the name
    /// since this sweep let go of it. Any other refusal is raised, for the caller to report as
    /// an entry it could not read.
    /// </returns>
    /// <inheritdoc cref="DeleteInFlightAttempts" path="/summary/para"/>
    private static IOException? DeleteWaitingOutADeleteInFlight(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return null;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                return ex;
            }
            catch (UnauthorizedAccessException) when (attempt < DeleteInFlightAttempts
                                                      && !Directory.Exists(path))
            {
                Thread.Sleep(DeleteInFlightWaitMs);
            }
        }
    }

    /// <summary>
    /// Take the name back, under the handle that holds it. On Unix the entry is unlinked while
    /// this caller still holds it, so the name is never decided and unheld: let go in front of
    /// the delete, it is a name a second run can sweep, create its own file at and have deleted
    /// from under it. Windows refuses to unlink a file this process has open without
    /// FILE_SHARE_DELETE, so there the handle goes first and the interval is that platform's.
    /// <para>
    /// Holding the entry is not on its own knowing that the name still reaches it - the lock
    /// behind FileShare.None is granted after the open that resolved the name, and a handle can
    /// come away over a file another run has since renamed its own copy over. So the name is
    /// taken back only while <paramref name="held"/> is what stands at it, and left exactly as
    /// it is otherwise: what is there belongs to the run that put it there. A discard with no
    /// handle has nothing standing in this caller's name and removes nothing.
    /// </para>
    /// <para>
    /// And a claim that cannot be verified is not a claim: where the question above fails rather
    /// than answers - a host without the call, a parent directory that has stopped answering for
    /// the name, a filesystem that fails it for a reason this cannot read - nothing is removed
    /// and the name is left for the next sweep, whatever this caller is holding. So the temp a
    /// promotion spends, the copy it staged and the write probe are taken back under the claim
    /// where the claim can be finished, and left standing where it cannot.
    /// </para>
    /// <para>
    /// Best effort, both ways round: a name that cannot be unlinked is a leftover the next
    /// sweep takes, and a caller that has already answered must not fail on it.
    /// </para>
    /// </summary>
    public static void Discard(string path, FileStream? held)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (held != null && StillHolds(held, path)) Unlink(path);
            held?.Dispose();
            return;
        }
        held?.Dispose();
        Unlink(path);
    }

    /// <inheritdoc cref="Discard"/>
    /// <summary>
    /// Whether the handle is still what stands at the name, for a caller that must not fail. A
    /// question that cannot be answered is answered no: the entry is left, which costs a leftover
    /// the next sweep takes - where removing a file this run turns out not to hold costs the run
    /// that does hold it its rows.
    /// </summary>
    private static bool StillHolds(FileStream held, string path)
    {
        try { return HeldEntry.StillStandsAt(held, path); }
        catch (IOException) { return false; }
    }

    private static void Unlink(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Whether an IOException from opening a file is the sharing violation the share mode exists
    /// to produce, rather than one of the other ways an open fails. Named positively, because
    /// the sharing case is the only one that means nothing is wrong with the file and another
    /// run has it, and the only one a caller answers by waiting: a failure nothing here
    /// recognizes is reported as a file this run cannot open, which is true of it whatever the
    /// cause, instead of as a second run the caller can go and look for and not find.
    /// <para>
    /// Windows carries the win32 code in the low half of the HRESULT. Unix puts the raw errno
    /// there and nothing beside it, so the two forms cannot be read for each other: .NET backs
    /// <see cref="FileShare.None"/> on Unix with a non-blocking flock, and a refused LOCK_EX
    /// arrives as EWOULDBLOCK - 35 on macOS, measured for a second opener both inside the
    /// process and outside it, and 11 on Linux, where the name EAGAIN shares that value. The
    /// subclasses .NET maps by hand carry a win32-shaped HRESULT even on Unix - an over-long
    /// path is a PathTooLongException with 0x800700CE in it - and are one more failure this
    /// cannot mistake for a second run.
    /// </para>
    /// <para>
    /// Each of those numbers is read on the platform that uses it and nowhere else, because the
    /// two are a pair either way round: 11 is EDEADLK on macOS and 35 is EDEADLK on Linux. Taken
    /// as a union, whichever platform this ran on read a deadlock the kernel had just refused to
    /// enter as another run holding the file, and told the caller to wait for it.
    /// </para>
    /// </summary>
    public static bool IsSharingViolation(IOException ex) => ex.HResult switch
    {
        unchecked((int)0x80070020) => true,                  // ERROR_SHARING_VIOLATION
        unchecked((int)0x80070021) => true,                  // ERROR_LOCK_VIOLATION
        35 => OperatingSystem.IsMacOS(),                     // EWOULDBLOCK (EDEADLK on Linux)
        11 => OperatingSystem.IsLinux(),                     // EAGAIN (EDEADLK on macOS)
        _ => false,
    };

    /// <summary>
    /// Whether an IOException from a create is the runtime's refusal of a name that already
    /// exists, rather than one of the other ways a create fails. Read off the exception and not
    /// off the name, which is the one thing a second look cannot answer: a create refused by
    /// another run's scratch file is refused by an entry that run is about to take away, so by
    /// the time the name can be asked about it is clear again - and a caller that decided from
    /// that answer called a directory it could write in a directory it could not.
    /// <para>
    /// What WEARS the name is a different question, and this cannot answer it: a file, a
    /// directory and a link all refuse a create alike. A caller that has to tell them apart asks
    /// the filesystem, which is honest about the entries that are still there to be asked about.
    /// </para>
    /// <para>
    /// Windows carries the win32 code in the low half of the HRESULT and Unix puts the raw errno
    /// there, the same pair <see cref="IsSharingViolation"/> reads. EEXIST is 17 on both Unixes
    /// this runs on, so unlike the sharing errnos there is nothing here to tell macOS and Linux
    /// apart; the platform is asked anyway, so that a win32 HRESULT can never be read as an
    /// errno on the one host where the two are shaped alike.
    /// </para>
    /// </summary>
    public static bool IsAlreadyExists(IOException ex) => ex.HResult switch
    {
        unchecked((int)0x80070050) => true,                  // ERROR_FILE_EXISTS
        unchecked((int)0x800700B7) => true,                  // ERROR_ALREADY_EXISTS
        17 => !OperatingSystem.IsWindows(),                  // EEXIST
        _ => false,
    };

    /// <summary>The runtime's own first line of a message a caller quotes inside a sentence.</summary>
    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}
