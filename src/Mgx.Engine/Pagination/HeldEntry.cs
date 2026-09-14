using System.Runtime.InteropServices;

namespace Mgx.Engine.Pagination;

/// <summary>
/// Whether the file a run holds is still the file standing at the name it asked for. A claim
/// taken on a name is finished here, and nowhere else.
/// <para>
/// .NET backs <see cref="FileShare.None"/> on Unix with a non-blocking <c>flock</c> taken AFTER
/// <c>open(2)</c>, so the two halves of a claim are not one instruction: the handle binds to
/// whatever inode the name resolved to at the open, and the exclusion over it is granted several
/// syscalls later. A run descheduled between them comes back holding an inode a second run
/// renamed its own file over in the interval - <c>rename(2)</c> honors no lock on either side -
/// and the lock is then granted over a file with no name left on it. Nothing about that handle
/// says so: it reads its own length, it takes appends, and the claim it stands for was over a
/// name that is now somebody else's. Two runs promoting over one output both answered Taken that
/// way, and the second renamed its copy over the first one's rows.
/// </para>
/// <para>
/// So the entry now at the name has to be the inode the handle holds, and that inode has to
/// still have a name. Both are asked of the platform after the lock is granted, and a caller
/// that cannot answer yes drops the handle and asks the name again rather than acting on it.
/// </para>
/// <para>
/// What that reaches is the writers that take a claim: a promotion's hold on the output it
/// renames onto and on the staging copy it renames from, the sweep of a scratch name, the hold
/// a resumed run keeps on the output it appends to, and the files a promotion takes back when it
/// is done - the temp it spent, which goes only where the promotion landed, and the staged copy
/// it wrote and the empty output it made in order to hold one, which go only where it stopped.
/// Between those, no run renames onto or unlinks a name whose current inode another run holds.
/// </para>
/// <para>
/// A writer that takes no claim is refused by none of it. This is a rule between claims, and
/// rename and unlink honor no lock in the first place: a plain export's final move onto its
/// output, the atomic saves of the checkpoint and the delta state - which let the staging claim
/// go before the rename that spends it - an older mgx, another tool, a shell. Each of those
/// replaces a name while a run holds what stands at it, and the run holding it goes on writing
/// into an inode that has left the directory. What the rule holds apart is two claims; it does
/// not hold the name still.
/// </para>
/// <para>
/// Windows needs none of it. The open there IS the exclusion, and the kernel refuses a rename
/// onto a name another handle has open and a rename of one too, so the question cannot come up
/// and every caller keeps the answer it had.
/// </para>
/// </summary>
public static class HeldEntry
{
    /// <summary>
    /// How many times a claim is asked again when the name moved under it before the caller is
    /// told the name is held. Each attempt is a fresh open, so a re-ask over a name a second run
    /// has taken meets that run's own lock and answers Held on its own; what the attempts are
    /// for is the other outcome, a name whose entry was replaced by a run that has since let go,
    /// where the next open is the one that succeeds. Three, because a run has to be descheduled
    /// between an open and a lock to lose one at all, and nothing waits between them: a name
    /// that moves under three consecutive claims is a name being mutated right now, which is
    /// what a hold means to every caller here.
    /// </summary>
    public const int ClaimAttempts = 3;

    /// <summary>
    /// Test seam: run in the instant between a claim being granted and the check that the name
    /// still resolves to it, with the path that was claimed. That instant is the whole defect
    /// and no test can hit it by racing for it, so it is fired here instead. Null on every
    /// shipping path; internal, assigned directly rather than reflected, and reset by the test
    /// that set it.
    /// </summary>
    internal static Action<string>? BeforeTheClaimIsVerified;

    /// <summary>
    /// Whether <paramref name="held"/> is the entry standing at <paramref name="path"/>: the
    /// same inode on the same device, with a name still on it. False says the name moved under
    /// the handle - unlinked, renamed away, or replaced by another run's file - and a caller
    /// that acts on the name anyway acts on a file that is not the one it claimed.
    /// <para>
    /// The fd is read off <paramref name="held"/>, and reading it hands the file whatever that
    /// stream still has buffered to write. So a claim is verified with the buffer written out,
    /// and a caller that reaches this with rows waiting in one pays for that write here and
    /// meets its failure here rather than where it writes. One route does: a promotion whose
    /// copy into the staged file fails part way through returns without reaching the flush that
    /// follows the loop, and the discard in its finally verifies that writer - so the rows still
    /// in the buffer are written out here, and a failure of that write is met here, in a discard
    /// whose caller has already answered. The discard swallows that failure; the dispose that
    /// follows it re-attempts the same write and lets the failure out, to the promotion's own
    /// catch. Every other caller reaches this with nothing waiting, or with the buffer flushed an
    /// instruction earlier.
    /// </para>
    /// </summary>
    /// <exception cref="IOException">
    /// The question could not be answered: an entry point that is not there on this host, or a
    /// failure from the call that is not the name resolving to nothing. Raised naming the call
    /// and the errno rather than answered "still there", because a claim that cannot be verified
    /// is not a claim, and every caller here reads a raised IOException as a file it has to
    /// leave alone.
    /// </exception>
    public static bool StillStandsAt(FileStream held, string path)
    {
        BeforeTheClaimIsVerified?.Invoke(path);

        // The open is the exclusion on Windows and the kernel keeps the name still under it.
        if (OperatingSystem.IsWindows()) return true;

        // The handle first, so its link count is read before the name is looked at: an entry
        // that answers for the name afterwards is one this handle held while it still had a
        // name, and a rename landing between the two readings takes this inode's last name with
        // it and is read here.
        var mine = OfTheHandle(held, path);
        if (mine.Links == 0) return false;

        var there = AtTheName(path);
        return there is { } entry && entry.Device == mine.Device && entry.Inode == mine.Inode;
    }

    /// <summary>What a stat answered: the file's identity, and how many names it has.</summary>
    private readonly record struct Identity(long Device, ulong Inode, ulong Links);

    private static Identity OfTheHandle(FileStream held, string path)
    {
        var handle = held.SafeFileHandle;
        var counted = false;
        try
        {
            // The fd, held for as long as the call takes: a handle disposed on another thread in
            // the middle of it would otherwise be a stat of whatever fd the runtime handed that
            // number out to next.
            handle.DangerousAddRef(ref counted);
            var fd = (int)handle.DangerousGetHandle();
            var buffer = new byte[StatBufferBytes];
            int answer;
            try
            {
                answer = OperatingSystem.IsMacOS()
                    ? (MacPlainNamesAreIno64 ? MacFstat(fd, buffer) : MacFstatIno64(fd, buffer))
                    : LinuxStatxOfFd(fd, string.Empty, AtEmptyPath, StatxWanted, buffer);
            }
            catch (EntryPointNotFoundException ex) { throw NotOnThisHost(FdCall, path, ex); }

            if (answer != 0) throw Failed(FdCall, path, Marshal.GetLastPInvokeError());
            return OperatingSystem.IsMacOS() ? MacIdentity(buffer) : LinuxIdentity(buffer, path);
        }
        finally
        {
            if (counted) handle.DangerousRelease();
        }
    }

    /// <summary>
    /// What stands at the name, or null where the name resolves to nothing. Symlinks are
    /// followed, because the open that took the claim followed them too: the question is whether
    /// this handle is what that same name reaches now.
    /// </summary>
    private static Identity? AtTheName(string path)
    {
        var buffer = new byte[StatBufferBytes];
        int answer;
        try
        {
            answer = OperatingSystem.IsMacOS()
                ? (MacPlainNamesAreIno64 ? MacStat(path, buffer) : MacStatIno64(path, buffer))
                : LinuxStatx(AtFdCwd, path, 0, StatxWanted, buffer);
        }
        catch (EntryPointNotFoundException ex) { throw NotOnThisHost(NameCall, path, ex); }

        if (answer == 0)
        {
            return OperatingSystem.IsMacOS() ? MacIdentity(buffer) : LinuxIdentity(buffer, path);
        }

        var errno = Marshal.GetLastPInvokeError();
        if (NameResolvesToNothing(errno)) return null;
        throw Failed(NameCall, path, errno);
    }

    /// <summary>
    /// The call each half is asked with, for the sentence a failure is raised in.
    /// </summary>
    private static string FdCall => OperatingSystem.IsMacOS() ? "fstat(2)" : "statx(2)";

    /// <inheritdoc cref="FdCall"/>
    private static string NameCall => OperatingSystem.IsMacOS() ? "stat(2)" : "statx(2)";

    /// <summary>
    /// ENOENT and ENOTDIR, the two failures that say the name reaches no entry rather than that
    /// the question failed - a name that has gone, and a name whose parent is no longer a
    /// directory. Both numbers are the same on macOS and on Linux. Every other failure is
    /// raised: an answer this cannot read is not an answer that the file is still there.
    /// </summary>
    private static bool NameResolvesToNothing(int errno) => errno is 2 or 20;

    /// <summary>
    /// A libc without the symbol is a host this cannot verify a claim on. Raised rather than
    /// answered "still there", which is the silence this whole file exists to remove.
    /// </summary>
    private static IOException NotOnThisHost(string named, string path, Exception ex) =>
        new($"{named} is not available on this host, so the claim on '{path}' cannot be "
            + "verified.", ex);

    /// <inheritdoc cref="NotOnThisHost"/>
    private static IOException Failed(string named, string path, int errno) =>
        new($"{named} on '{path}' failed with errno {errno}, so the claim on it cannot be "
            + "verified.");

    // --- macOS -------------------------------------------------------------------------------
    //
    // struct stat with 64-bit inodes, which is the only shape either supported architecture
    // reaches: st_dev int32 at 0, st_mode uint16 at 4, st_nlink uint16 at 6, st_ino uint64 at 8.
    // The entry point differs by architecture and the struct does not. arm64 has never had the
    // 32-bit-inode form, so the plain names ARE the 64-bit ones there; x86_64 carries both, and
    // the plain names there are the old shape - a different struct, whose st_ino is 32 bits at
    // offset 4 - so the "$INODE64" symbols are the ones asked for. This is what <sys/cdefs.h>
    // does to every stat call at compile time (__DARWIN_INODE64, suffixed on x86_64 and empty
    // on arm64); nothing here can be compiled through that header, so the choice is made once,
    // at runtime, off the architecture the process is actually running as - which is x64 under
    // Rosetta, where the x64 libSystem is the one loaded.

    private static bool MacPlainNamesAreIno64 =>
        RuntimeInformation.ProcessArchitecture != Architecture.X64;

    private static Identity MacIdentity(byte[] buffer) => new(
        BitConverter.ToInt32(buffer, 0),
        BitConverter.ToUInt64(buffer, 8),
        BitConverter.ToUInt16(buffer, 6));

    [DllImport(Libc, EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFstat(int fd, byte[] buffer);

    [DllImport(Libc, EntryPoint = "stat", SetLastError = true)]
    private static extern int MacStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        byte[] buffer);

    [DllImport(Libc, EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacFstatIno64(int fd, byte[] buffer);

    [DllImport(Libc, EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int MacStatIno64([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        byte[] buffer);

    // --- Linux -------------------------------------------------------------------------------
    //
    // statx, and not stat: glibc exported no plain "stat" symbol before 2.33 - the header turned
    // it into __xstat with a struct version argument - and struct stat's own layout is per
    // architecture, so a fixed set of offsets would be a different struct on x86_64, arm64 and
    // arm. struct statx is one shape everywhere and its offsets are the kernel's ABI:
    // stx_nlink uint32 at 16, stx_ino uint64 at 32, and stx_dev_major / stx_dev_minor uint32 at
    // 136 / 140, past four 16-byte timestamps and the two rdev halves at 128 / 132. The wrapper
    // has been in glibc since 2.28 and in musl since 1.2, which is under every distribution
    // .NET 8 supports; a host without it raises rather than answering.

    private const int AtFdCwd = -100;
    private const int AtEmptyPath = 0x1000;

    /// <summary>STATX_NLINK (0x4) and STATX_INO (0x100), the two fields read out of it.</summary>
    private const uint StatxWanted = 0x4 | 0x100;

    private static Identity LinuxIdentity(byte[] buffer, string path)
    {
        // What the kernel actually filled in. Asking for a field is not being given it - a
        // filesystem that cannot answer clears its bit - and reading a field that was never
        // written would compare this run's claim against a zero.
        var answered = BitConverter.ToUInt32(buffer, 0);
        if ((answered & StatxWanted) != StatxWanted)
        {
            throw new IOException(
                $"statx(2) on '{path}' answered without the inode or the link count, so the "
                + "claim on it cannot be verified.");
        }
        var major = BitConverter.ToUInt32(buffer, 136);
        var minor = BitConverter.ToUInt32(buffer, 140);
        return new Identity(
            ((long)major << 32) | minor,
            BitConverter.ToUInt64(buffer, 32),
            BitConverter.ToUInt32(buffer, 16));
    }

    [DllImport(Libc, EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(int dirFd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);

    /// <inheritdoc cref="LinuxStatx"/>
    /// <summary>
    /// The same call over an open file: the fd in the directory slot, an empty path, and
    /// AT_EMPTY_PATH to say the fd is the file rather than the directory to resolve in.
    /// </summary>
    [DllImport(Libc, EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatxOfFd(int fd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);

    /// <summary>
    /// libSystem on macOS and the C library on Linux, under the name .NET resolves on both.
    /// </summary>
    private const string Libc = "libc";

    /// <summary>
    /// Room for either struct: 144 bytes for macOS's stat with 64-bit inodes, and 256 for
    /// Linux's statx, which is 256 bytes by the kernel's ABI - the spare fields at its tail are
    /// part of that size, and a field a later kernel adds is one of those spares being given a
    /// name. One size for both, since the fields read out of each are at the offsets above and
    /// nothing here walks off the end of what the call wrote.
    /// </summary>
    private const int StatBufferBytes = 256;
}
