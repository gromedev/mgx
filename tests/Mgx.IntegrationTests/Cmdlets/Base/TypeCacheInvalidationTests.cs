using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Mgx.Cmdlets;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests;

/// <summary>
/// FindType caches by full name, and a cached Type carries the identity of the assembly that
/// defined it. Invalidation is an AssemblyLoad hook that module removal detaches, so what has to
/// hold across a removal is that an import arms it again - once - and that the removal itself
/// leaves nothing resolved behind.
///
/// In the Pipeline collection because every test that imports the module assembly is, and
/// importing it runs the initializer these tests drive directly.
/// </summary>
[Collection("Pipeline")]
public class TypeCacheInvalidationTests
{
    private const string GraphSessionTypeName = "Microsoft.Graph.PowerShell.Authentication.GraphSession";

    /// <summary>
    /// An entry only this test writes and only this test reads. The cache is process-global and
    /// the collections running alongside this one both resolve types into it and load assemblies
    /// that clear it, so neither "empty" nor "not empty" is this test's own doing - but an entry
    /// put here and gone afterwards can only have been cleared.
    /// </summary>
    private const string Sentinel = "Mgx.IntegrationTests.TypeCacheInvalidationTests+Sentinel";

    /// <summary>
    /// What this test resolved, or put there itself, before a removal it expects to drop both.
    /// The releases a removal makes resolve types of their own on the way through, so the entry
    /// this test wrote is not enough on its own: it is cleared by any load, including one those
    /// releases trigger, while the entries they then resolve are exactly what the removal's own
    /// clear has to take out afterwards.
    /// </summary>
    private static readonly string[] Resolved = [GraphSessionTypeName, Sentinel];

    [Fact]
    public void An_assembly_loading_after_a_remove_and_re_import_clears_the_type_cache()
    {
        // Resolve something first: "cleared afterwards" has to mean the removal cleared the
        // cache, not that nothing had resolved a type yet.
        Assert.NotNull(MgxCmdletBase.FindType(GraphSessionTypeName));
        MgxCmdletBase.s_typeCache[Sentinel] = typeof(TypeCacheInvalidationTests);

        try
        {
            // A removal is ReleaseStaticState and then the detach; an import is OnImport.
            AlcInitializer.ReleaseStaticState();
            MgxCmdletBase.DetachAssemblyLoadHandler();
            Assert.False(AnyStillCached(), "module removal left resolved types cached");

            new AlcInitializer().OnImport();

            // A load from a collection running alongside this one clears the cache too, so the
            // resolved entry only has to end up there, not be there on the first attempt.
            var primed = false;
            for (var attempt = 0; attempt < 10 && !primed; attempt++)
            {
                Assert.NotNull(MgxCmdletBase.FindType(GraphSessionTypeName));
                primed = MgxCmdletBase.s_typeCache.ContainsKey(GraphSessionTypeName);
            }
            Assert.True(primed, "resolving a type after the import cached nothing");

            MgxCmdletBase.s_typeCache[Sentinel] = typeof(TypeCacheInvalidationTests);
            LoadAnAssembly("MgxTypeCacheProbe");

            Assert.False(AnyStillCached(),
                "a type resolved before the load is still cached, so a Microsoft.Graph.Authentication "
                + "loading now would keep resolving to the session of the one it replaced");
        }
        finally
        {
            MgxCmdletBase.s_typeCache.TryRemove(Sentinel, out _);
            MgxCmdletBase.AttachAssemblyLoadHandler();
        }
    }

    [Fact]
    public void A_second_import_leaves_one_subscription_for_a_removal_to_detach()
    {
        try
        {
            new AlcInitializer().OnImport();
            new AlcInitializer().OnImport();
            Assert.Equal(1, InvalidationSubscriptions());

            // One removal has to be enough. A second subscription would survive this and go on
            // clearing the cache from a module that is gone.
            MgxCmdletBase.DetachAssemblyLoadHandler();
            Assert.Equal(0, InvalidationSubscriptions());
        }
        finally
        {
            MgxCmdletBase.AttachAssemblyLoadHandler();
        }
    }

    /// <summary>
    /// The dependency resolver is the other subscription an import makes, and += is no more
    /// idempotent there. A second one survives the -= a removal does and goes on answering
    /// dependency loads out of a Dependencies folder belonging to a module the session removed.
    /// </summary>
    [Fact]
    public void A_second_import_leaves_one_dependency_resolver_for_a_removal_to_detach()
    {
        try
        {
            new AlcInitializer().OnImport();
            new AlcInitializer().OnImport();
            Assert.Equal(1, ResolverSubscriptions());

            // The removal itself, module argument and all - OnRemove never reads it.
            new AlcInitializer().OnRemove(null!);
            Assert.Equal(0, ResolverSubscriptions());
        }
        finally
        {
            new AlcInitializer().OnImport();
        }
    }

    /// <summary>Whether anything this test resolved, or wrote itself, is still cached.</summary>
    private static bool AnyStillCached() => Resolved.Any(MgxCmdletBase.s_typeCache.ContainsKey);

    /// <summary>
    /// How many times mgx's invalidation hook is subscribed. AppDomain.AssemblyLoad forwards to a
    /// static event on AssemblyLoadContext, and its invocation list is the only place the count
    /// shows: an extra subscription is silent until a removal detaches one and the other stays.
    /// </summary>
    private static int InvalidationSubscriptions()
    {
        var field = typeof(AssemblyLoadContext).GetField("AssemblyLoad", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(field != null, "AssemblyLoadContext no longer keeps its subscribers in a static field");
        var subscribers = (Delegate?)field!.GetValue(null);
        return subscribers?.GetInvocationList().Count(d => d.Method.DeclaringType == typeof(MgxCmdletBase)) ?? 0;
    }

    /// <summary>
    /// How many times mgx's dependency resolver is subscribed to the default load context.
    /// AssemblyLoadContext keeps its Resolving subscribers in a private instance field, and that
    /// invocation list is the only place the count shows: a second subscription is silent until a
    /// removal detaches one and the other stays.
    /// </summary>
    private static int ResolverSubscriptions()
    {
        var field = typeof(AssemblyLoadContext).GetField(
            "_resolving", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, "AssemblyLoadContext no longer keeps its Resolving subscribers in _resolving");
        var subscribers = (Delegate?)field!.GetValue(AssemblyLoadContext.Default);
        return subscribers?.GetInvocationList().Count(d => d.Method.DeclaringType == typeof(AlcInitializer)) ?? 0;
    }

    /// <summary>
    /// Loads an assembly, which is what invalidation keys off. A dynamic one raises the event
    /// exactly as a file-backed load does, and leaves nothing behind that the rest of the suite
    /// has to be able to resolve.
    /// </summary>
    private static void LoadAnAssembly(string name)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule(name)
            .DefineType($"{name}.Marker", TypeAttributes.Public)
            .CreateType();
    }
}
