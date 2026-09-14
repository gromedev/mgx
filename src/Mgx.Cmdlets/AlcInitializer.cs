using System.Management.Automation;
using System.Reflection;
using System.Runtime.Loader;
using Mgx.Engine.Http;

namespace Mgx.Cmdlets;

/// <summary>
/// Assembly Load Context initializer for dependency isolation.
/// Reuses assemblies already loaded in any ALC (including Microsoft.Graph's
/// msgraph-load-context) to avoid type identity conflicts. Only loads from
/// the Dependencies folder for assemblies not found anywhere.
/// Pattern adopted from Mge project's ALC coexistence investigation.
///
/// Dependencies/ is not only mgx's own third-party packages. It also carries assemblies the
/// module never compiles against but a host may not supply - System.IO.Pipelines, which the
/// System.Text.Json of a newer PowerShell needs and net8.0's framework does not include. Those
/// arrive here by the same route: nothing has them loaded, so the fallback below is the only
/// answer, and an empty Dependencies/ is a FileNotFoundException at first use.
/// </summary>
public class AlcInitializer : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    private static readonly string DepsPath = Path.Combine(
        Path.GetDirectoryName(typeof(AlcInitializer).Assembly.Location)!,
        "Dependencies");

    // Whether ResolveDependency is subscribed. A process can import the module more than once,
    // and += is not idempotent: the second subscription would outlive the -= a removal does and
    // go on answering dependency loads out of this module's Dependencies folder for a session
    // that no longer has the module.
    private static int s_resolverHooked;

    public void OnImport()
    {
        if (Interlocked.Exchange(ref s_resolverHooked, 1) == 0)
            AssemblyLoadContext.Default.Resolving += ResolveDependency;

        // Re-arms type-cache invalidation. The hook is attached from a static constructor that
        // has long since run by the time a second import happens, and removal detaches it, so
        // without this every FindType after the first removal would answer from entries resolved
        // before it - including a GraphSession belonging to a module that has been replaced.
        Base.MgxCmdletBase.AttachAssemblyLoadHandler();
    }

    private static Assembly? ResolveDependency(AssemblyLoadContext defaultAlc, AssemblyName name)
    {
        try
        {
            // If the assembly is already loaded in ANY ALC (including
            // msgraph-load-context or other module ALCs), return that instance,
            // but only if the major version is compatible. Returning an older
            // major version could cause MissingMethodException at runtime.
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                var loadedName = loaded.GetName();
                if (!string.Equals(loadedName.Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                // If the requested version is unknown or the loaded version meets
                // the minimum version (same major, >= minor), reuse it to avoid type identity splits.
                // Requiring same major prevents MissingMethodException from breaking API changes.
                if (name.Version == null || loadedName.Version == null
                    || (loadedName.Version.Major == name.Version.Major
                        && loadedName.Version >= name.Version))
                {
                    return loaded;
                }
            }

            // Not loaded anywhere (or only an incompatible version):
            // load from our Dependencies folder into Default ALC
            var dllPath = Path.Combine(DepsPath, $"{name.Name}.dll");
            return File.Exists(dllPath) ? defaultAlc.LoadFromAssemblyPath(dllPath) : null;
        }
        catch (Exception ex)
        {
            // Resolver must never throw; return null to let the runtime continue
            // its normal resolution process.
            System.Diagnostics.Debug.WriteLine($"[Mgx ALC] Failed to resolve '{name.Name}': {ex.Message}");
            return null;
        }
    }

    public void OnRemove(PSModuleInfo module)
    {
        // Static-state cleanup MUST happen before the resolver is detached below.
        //
        // ResetHttpClient JIT-compiles code referencing ResilientGraphClient, whose fields
        // include Polly types. Polly.Core ships in Dependencies/ and is reachable ONLY via
        // ResolveDependency. It is also loaded lazily, so in a session where no Graph request
        // ever ran it is absent from the AppDomain entirely.
        //
        // This cleanup used to live in the mgx.psm1 OnRemove scriptblock, which PowerShell
        // invokes AFTER this callback. That ordering left the resolver already detached, so
        // ResetHttpClient threw FileNotFoundException for Polly.Core, Remove-Module failed,
        // and the module could never be unloaded. Owning the cleanup here makes the ordering
        // a property of the code rather than of PowerShell's callback sequence.
        //
        // ResetHttpClient also calls ResiliencePipelineFactory.Reset internally, so both
        // pieces of static state are released by this single call.
        try
        {
            ReleaseStaticState();
        }
        catch (Exception ex)
        {
            // Never let teardown throw: a failure here would block module removal,
            // which is the exact defect this ordering fixes.
            System.Diagnostics.Debug.WriteLine($"[Mgx ALC] Cleanup on remove failed: {ex.Message}");
        }

        // One -= per subscription, and the flag is what says there is one to take off: a removal
        // that ran without an import behind it has nothing to detach.
        if (Interlocked.Exchange(ref s_resolverHooked, 0) == 1)
            AssemblyLoadContext.Default.Resolving -= ResolveDependency;

        // After the resolver: this only detaches an event handler, needs no dependency
        // resolution, and must not run before ResetHttpClient (which may trigger loads
        // that the type cache should still observe).
        Base.MgxCmdletBase.DetachAssemblyLoadHandler();
    }

    /// <summary>
    /// Everything module removal releases except the assembly-load hook, which OnRemove
    /// detaches itself. Two reasons, neither of them that the detach cannot be undone - it can,
    /// AttachAssemblyLoadHandler subscribes again and an import calls it. It has to run after
    /// ResetHttpClient, whose loads the type cache should still observe; and this is the seam
    /// tests drive a removal through, which is not the same as asking them to unsubscribe the
    /// process's only invalidation hook.
    /// <para>
    /// ResetHttpClient releases mgx's own client and the pipeline factory. The resilience
    /// injection is separate state: it is installed on GraphSession, which belongs to another
    /// module and outlives this one, so releasing it means putting the session back on the
    /// genuine SDK client and only then letting go. Dropping the references alone would leave
    /// the wrapper installed and the SDK sending through a handler belonging to an unloaded
    /// module; the bridge-target map survives removal, so a later re-import's
    /// Disable-MgxResilience can still take it off - the restore here just makes that
    /// recovery unnecessary on the normal path.
    /// </para>
    /// <para>
    /// The Set-MgxOption surface goes with them, and so do the endpoint and the timeout a client
    /// build derives, and the telemetry counters a session accumulates. They are the per-process
    /// state a removal used to leave set: a fresh import started on the previous import's tuning,
    /// with Get-MgxOption reporting it as current - a re-import that looks like a reset was not
    /// one - the VersionedBaseUrl its cmdlets composed named the cloud the import before it had
    /// connected to until its own first build read the session again, and Get-MgxTelemetry
    /// answered for traffic the session had never sent.
    /// </para>
    /// </summary>
    internal static void ReleaseStaticState()
    {
        Base.MgxCmdletBase.ResetHttpClient();
        Base.MgxCmdletBase.SetClientOptions(ResilientGraphClientOptions.Default);
        Base.MgxCmdletBase.ReleaseEndpointAndTimeout();

        // Same shape again: the counters are one process's running total, so a removal that
        // leaves them set has Get-MgxTelemetry crediting a fresh import with the requests,
        // retries and waits of the import before it. Reset leaves the adaptive pacer's control
        // state alone, which is right here too - that describes the tenant's current throttle
        // regime rather than anything the departing import accumulated.
        MgxTelemetryCollector.Current.Reset();

        Cmdlets.Configuration.EnableMgxResilience.ReleaseInjection();

        // Last: the calls above resolve types through this cache, so clearing it earlier would
        // only refill it with the entries the removal exists to drop.
        Base.MgxCmdletBase.ClearTypeCache();
    }
}
