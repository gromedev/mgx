using System.Reflection;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Cmdlets.Models;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Enable-MgxResilience swaps the SDK's HttpClient for a wrapper that bridges to the
/// original. Invoke-MgGraphRequest resolves a relative -Uri against the active client's
/// BaseAddress before any handler runs, so the wrapper must carry the original client's
/// configuration or every relative-URI SDK call dies before reaching the wire.
/// </summary>
[Collection("Pipeline")]
public class ResilienceWrapTests
{
    private static HttpClient? BuildWrapper(HttpClient sdkClient, List<string> warnings)
    {
        try
        {
            return EnableMgxResilience.BuildResilientSdkClient(sdkClient, warnings.Add);
        }
        finally
        {
            EnableMgxResilience.ActiveHandler = null;
            ResiliencePipelineFactory.Reset();
        }
    }

    [Fact]
    public void The_wrapper_keeps_the_sdk_clients_base_address()
    {
        using var sdkClient = new HttpClient
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        var warnings = new List<string>();

        var wrapper = BuildWrapper(sdkClient, warnings);

        Assert.NotNull(wrapper);
        Assert.Empty(warnings);
        Assert.Equal(sdkClient.BaseAddress, wrapper.BaseAddress);
    }

    [Fact]
    public void The_wrapper_keeps_the_sdk_clients_timeout()
    {
        using var sdkClient = new HttpClient { Timeout = TimeSpan.FromSeconds(123) };
        var warnings = new List<string>();

        var wrapper = BuildWrapper(sdkClient, warnings);

        Assert.NotNull(wrapper);
        Assert.Equal(sdkClient.Timeout, wrapper.Timeout);
    }

    /// <summary>Counts what the wire actually saw, and what it answered with.</summary>
    private sealed class ThrottleThenOk : HttpMessageHandler
    {
        private readonly int _throttles;
        public int Calls { get; private set; }
        public ThrottleThenOk(int throttles) => _throttles = throttles;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= _throttles)
            {
                var r = new HttpResponseMessage((System.Net.HttpStatusCode)429) { RequestMessage = request };
                r.Headers.TryAddWithoutValidation("Retry-After", "0");
                return Task.FromResult(r);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{\"value\":[]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// A 429 answered through the bridge must be visible to mgx: the adaptive pacer learns from
    /// throttles, and Get-MgxTelemetry reports them.
    ///
    /// Scope: this covers the BRIDGE, with the wire directly beneath it. A live session also has
    /// the SDK's own RetryHandler inside the wrapped chain, which may answer a 429 before the
    /// outer pipeline ever sees it - that is not exercised here, because Kiota's handlers are
    /// only constructed by a real Graph client. What this pins is that the bridge itself does
    /// not swallow a throttle on the way past.
    /// </summary>
    [Fact]
    public void A_throttle_through_the_wrapper_is_visible_to_mgx()
    {
        var wire = new ThrottleThenOk(throttles: 1);
        using var sdkClient = new HttpClient(wire) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var warnings = new List<string>();

        MgxTelemetryCollector.Current.Reset();
        var wrapper = BuildWrapper(sdkClient, warnings);
        Assert.NotNull(wrapper);

        using var resp = wrapper!.GetAsync("users").GetAwaiter().GetResult();

        var snapshot = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(resp.IsSuccessStatusCode, $"final status was {(int)resp.StatusCode}");
        Assert.Equal(2, wire.Calls);                       // the 429 and the retry both hit the wire
        Assert.True(snapshot.ThrottleRetries > 0,
            $"mgx recorded {snapshot.ThrottleRetries} throttle retries for a 429 the wire served");
    }

    // --- the preview ---

    /// <summary>
    /// Puts the process where a successful Enable-MgxResilience leaves it, and then has the SDK
    /// replace the wrapper - a Connect-MgGraph or a Set-MgRequestContext does that. It is the one
    /// arrangement whose Enable does more than report itself already active, so it is the one that
    /// says what a preview costs.
    /// </summary>
    private static HttpClient InjectThenLetTheSdkReplaceIt(
        GraphSessionScope scope, HttpClient genuine, HttpClient replacement)
    {
        var wrapper = InjectAndLeaveItInstalled(scope, genuine);
        scope.Session.GraphHttpClient = replacement;
        return wrapper;
    }

    /// <summary>
    /// Puts the process where a successful Enable-MgxResilience leaves it and stops there: the
    /// wrapper is still the client the SDK is sending through, so it is this module's to take off.
    /// </summary>
    private static HttpClient InjectAndLeaveItInstalled(GraphSessionScope scope, HttpClient genuine)
    {
        var wrapper = EnableMgxResilience.BuildResilientSdkClient(genuine, _ => { })!;
        EnableMgxResilience.OriginalSdkClient = genuine;
        EnableMgxResilience.ResilientSdkClient = wrapper;
        EnableMgxResilience.IsEnabled = true;
        scope.Session.GraphHttpClient = wrapper;
        return wrapper;
    }

    /// <summary>Runs a cmdlet where PowerShell runs it, and returns the warnings it wrote.</summary>
    private static IReadOnlyList<string> RunCmdlet(string name, bool whatIf = false)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(EnableMgxResilience).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        var command = ps.AddCommand(name);
        if (whatIf) command.AddParameter("WhatIf", true);
        ps.Invoke();

        Assert.Empty(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));
        return [.. ps.Streams.Warning.Select(w => w.Message)];
    }

    private static HttpClient NewSdkClient() =>
        new() { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };

    /// <summary>What Get-MgxResilience reports right now.</summary>
    private static MgxResilienceOutput Reported()
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(EnableMgxResilience).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Get-MgxResilience");
        var results = ps.Invoke();
        return Assert.IsType<MgxResilienceOutput>(Assert.Single(results).BaseObject);
    }

    /// <summary>
    /// -WhatIf reports what a run would do; it does not do any of it. The injection is shared
    /// state - the wrapper the SDK is sending through, the handler holding it, and the pipeline
    /// factory's circuit-breaker history, rate limiter and learned pacing - and a session that
    /// asks what would happen keeps all of it.
    /// </summary>
    [Fact]
    public void A_preview_leaves_the_injection_and_the_pipeline_where_it_found_them()
    {
        using var scope = GraphSessionScope.Arm();
        using var genuine = NewSdkClient();
        using var replacement = NewSdkClient();
        var wrapper = InjectThenLetTheSdkReplaceIt(scope, genuine, replacement);

        var handler = EnableMgxResilience.ActiveHandler;
        Assert.NotNull(handler);
        var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);

        Assert.Empty(RunCmdlet("Enable-MgxResilience", whatIf: true));

        Assert.Same(wrapper, EnableMgxResilience.ResilientSdkClient);
        Assert.Same(handler, EnableMgxResilience.ActiveHandler);
        Assert.Same(genuine, EnableMgxResilience.OriginalSdkClient);
        Assert.True(EnableMgxResilience.IsEnabled);
        Assert.Same(replacement, scope.Session.GraphHttpClient);

        // The factory hands back what it is holding, so the same pipeline and the same limiter
        // mean nothing rebuilt them - and rebuilding is how the history in them is lost.
        var (pipelineAfter, rateLimiterAfter) =
            ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);
        Assert.Same(pipeline, pipelineAfter);
        Assert.Same(rateLimiter, rateLimiterAfter);
    }

    /// <summary>
    /// The acceptance for the ShouldProcess gate says a preview performs none of the run's state
    /// changes. Two of those changes are the pre-initialization of mgx's own client and the SDK
    /// probe - and the probe is the one that sends a real Graph request and makes the SDK build a
    /// client on the session. This drives the one arrangement where the probe would run: a
    /// connected session whose GraphHttpClient is not built yet.
    /// </summary>
    [Fact]
    public void A_preview_runs_neither_the_pre_init_nor_the_sdk_probe()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"mgx-preview-probe-{Guid.NewGuid():N}.txt");

        using var scope = GraphSessionScope.Arm(
            graphHttpClient: null,
            authContext: GraphSessionScope.AuthContextFor("tenant-a", "app-a"));

        var (pipeline, limiter) = ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);

        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(EnableMgxResilience).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        // Stands in for the SDK cmdlet the init probe shells out to. Its only job is to record
        // that the probe reached the wire at all.
        ps.AddScript(
            "function global:Invoke-MgGraphRequest { param($Method,$Uri,$ErrorAction,$WarningAction) "
            + $"Set-Content -LiteralPath '{marker}' -Value 'probe ran' }}");
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Enable-MgxResilience").AddParameter("WhatIf", true);
        var thrown = Record.Exception(() => ps.Invoke());

        try
        {
            Assert.False(File.Exists(marker),
                "-WhatIf ran the SDK init probe: it sent a Graph request and made the SDK build a client");
            Assert.Null(thrown);
            Assert.Empty(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));
            Assert.Null(scope.Session.GraphHttpClient);
            Assert.False(EnableMgxResilience.IsEnabled);
            Assert.Null(EnableMgxResilience.ResilientSdkClient);
            Assert.Null(EnableMgxResilience.ActiveHandler);

            var (pipelineAfter, limiterAfter) =
                ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);
            Assert.Same(pipeline, pipelineAfter);
            Assert.Same(limiter, limiterAfter);
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    /// <summary>
    /// Disable-MgxResilience deliberately leaves the pipeline factory alone: its circuit-breaker
    /// history, rate limiter and learned pacing are shared with mgx's own cmdlets, which go on
    /// running after the SDK injection comes off. A preview of the Enable must not do to that
    /// state what the Disable itself refuses to do, and must leave the Disable the wrapper it is
    /// there to release.
    /// </summary>
    [Fact]
    public void A_disable_after_a_preview_releases_the_wrapper_the_preview_left()
    {
        using var scope = GraphSessionScope.Arm();
        using var genuine = NewSdkClient();
        using var replacement = NewSdkClient();
        var wrapper = InjectThenLetTheSdkReplaceIt(scope, genuine, replacement);
        var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);

        RunCmdlet("Enable-MgxResilience", whatIf: true);

        // What the Disable is about to release, still there for it to release.
        Assert.Same(wrapper, EnableMgxResilience.ResilientSdkClient);

        RunCmdlet("Disable-MgxResilience");

        Assert.Same(replacement, scope.Session.GraphHttpClient);
        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Null(EnableMgxResilience.ResilientSdkClient);
        Assert.Null(EnableMgxResilience.ActiveHandler);

        var (pipelineAfter, rateLimiterAfter) =
            ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);
        Assert.Same(pipeline, pipelineAfter);
        Assert.Same(rateLimiter, rateLimiterAfter);
    }

    // --- whose client is on the session when the injection comes off ---

    /// <summary>
    /// A Connect-MgGraph to another tenant builds a fresh SDK client and hands it to the session,
    /// so the client Enable-MgxResilience saved is the previous tenant's. Installing that on the
    /// way out puts those credentials back under every SDK cmdlet - and says the session was
    /// restored to original behavior while it does it. The injection's own state still goes: what
    /// the session is holding is not this module's, but the handler, the wrapper and the flag are.
    /// </summary>
    [Fact]
    public void A_disable_leaves_a_client_the_sdk_replaced_after_the_injection()
    {
        using var scope = GraphSessionScope.Arm();
        using var genuine = NewSdkClient();
        using var replacement = NewSdkClient();
        InjectThenLetTheSdkReplaceIt(scope, genuine, replacement);

        var warnings = RunCmdlet("Disable-MgxResilience");

        Assert.Same(replacement, scope.Session.GraphHttpClient);
        Assert.Contains(warnings, w => w.Contains("left as it was found", StringComparison.Ordinal));
        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Null(EnableMgxResilience.ResilientSdkClient);
        Assert.Null(EnableMgxResilience.OriginalSdkClient);
        Assert.Null(EnableMgxResilience.ActiveHandler);

        // The state Get-MgxResilience reports is the state the release left, not a session that
        // still has something to re-inject.
        var reported = Reported();
        Assert.False(reported.IsEnabled);
        Assert.False(reported.IsActive);
        Assert.Null(reported.Warning);
    }

    /// <summary>
    /// The other branch, unchanged: the wrapper is still the client the SDK is sending through,
    /// so it is this module's to take off and the client it bridges to is the one to put back.
    /// </summary>
    [Fact]
    public void A_disable_restores_the_original_while_the_wrapper_is_still_installed()
    {
        using var scope = GraphSessionScope.Arm();
        using var genuine = NewSdkClient();
        var wrapper = InjectAndLeaveItInstalled(scope, genuine);

        var warnings = RunCmdlet("Disable-MgxResilience");

        Assert.Empty(warnings);
        Assert.Same(genuine, scope.Session.GraphHttpClient);
        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Null(EnableMgxResilience.ResilientSdkClient);
        Assert.Null(EnableMgxResilience.OriginalSdkClient);
        Assert.Null(EnableMgxResilience.ActiveHandler);

        var reported = Reported();
        Assert.False(reported.IsEnabled);
        Assert.False(reported.IsActive);
        Assert.Null(reported.Warning);
    }

    /// <summary>What the gate wrote when Disable-MgxResilience was previewed.</summary>
    private static List<string> PreviewTheDisable() =>
        RecordingHost.Preview(
            typeof(EnableMgxResilience).Assembly,
            ps => ps.AddCommand("Disable-MgxResilience").AddParameter("WhatIf", true));

    private const string RestoreAction = "Restore original SDK HttpClient";
    private const string LeaveAloneAction = "leave the SDK HttpClient as found";

    /// <summary>
    /// A preview of the branch above. It restores nothing - the client the session is holding is
    /// not this module's to take off - so a caller told a restore would happen was told the one
    /// thing this run will not do, and the report is all -WhatIf gives them to act on.
    /// </summary>
    [Fact]
    public void A_preview_over_a_client_the_sdk_replaced_names_the_leave_alone()
    {
        using var scope = GraphSessionScope.Arm();
        using var genuine = NewSdkClient();
        using var replacement = NewSdkClient();
        var wrapper = InjectThenLetTheSdkReplaceIt(scope, genuine, replacement);

        var lines = PreviewTheDisable();

        Assert.Contains(lines, l => l.Contains(LeaveAloneAction, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(RestoreAction, StringComparison.Ordinal));

        // And it did none of it: the release the action names is still the Disable's to do.
        Assert.Same(replacement, scope.Session.GraphHttpClient);
        Assert.Same(wrapper, EnableMgxResilience.ResilientSdkClient);
        Assert.True(EnableMgxResilience.IsEnabled);
    }

    /// <summary>
    /// The other branch previewed. Here the wrapper is still installed, so a restore is exactly
    /// what a real run would do and exactly what the gate has to say.
    /// </summary>
    [Fact]
    public void A_preview_over_the_wrapper_still_installed_names_the_restore()
    {
        using var scope = GraphSessionScope.Arm();
        using var genuine = NewSdkClient();
        var wrapper = InjectAndLeaveItInstalled(scope, genuine);

        var lines = PreviewTheDisable();

        Assert.Contains(lines, l => l.Contains(RestoreAction, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(LeaveAloneAction, StringComparison.Ordinal));

        Assert.Same(wrapper, scope.Session.GraphHttpClient);
        Assert.Same(genuine, EnableMgxResilience.OriginalSdkClient);
        Assert.True(EnableMgxResilience.IsEnabled);
    }
}
