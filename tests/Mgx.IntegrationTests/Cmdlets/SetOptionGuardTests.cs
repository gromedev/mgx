using System.Management.Automation;
using System.Net;
using System.Reflection;
using Mgx.Cmdlets;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Models;
using Mgx.Engine.Http;
using Microsoft.Graph.PowerShell.Authentication;

namespace Mgx.IntegrationTests;

/// <summary>
/// Set-MgxOption rebuilds the resilience pipeline, which discards circuit-breaker failure
/// history. It guards against doing that for nothing - but the guard counted every bound
/// parameter, and PowerShell binds the common ones too, so -Verbose alone looked like a change.
/// </summary>
[Collection("Pipeline")]
public class SetOptionGuardTests
{
    private static PowerShell Shell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Configuration.SetMgxOption).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    [Theory]
    [InlineData("Verbose")]
    [InlineData("ErrorAction")]
    public void A_common_parameter_alone_is_not_a_change(string parameter)
    {
        using var ps = Shell();
        var cmd = ps.AddCommand("Set-MgxOption").AddParameter("Verbose", true);
        if (parameter == "ErrorAction") cmd.AddParameter("ErrorAction", ActionPreference.Continue);
        ps.Invoke();

        var said = string.Join(" ", ps.Streams.Verbose.Select(v => v.Message));
        Assert.Contains("Options unchanged", said);
        Assert.DoesNotContain("Mgx options updated", said);
    }

    [Fact]
    public void A_real_parameter_still_updates()
    {
        using var ps = Shell();
        ps.AddCommand("Set-MgxOption")
          .AddParameter("RateLimitPerSecond", 42)
          .AddParameter("Verbose", true);
        ps.Invoke();

        var said = string.Join(" ", ps.Streams.Verbose.Select(v => v.Message));
        Assert.Contains("Mgx options updated", said);

        // put it back
        using var reset = Shell();
        reset.AddCommand("Set-MgxOption").AddParameter("Reset", true);
        reset.Invoke();
    }

    /// <summary>
    /// -NoRateLimit turns off batch item pacing too. That is a second mechanism, aimed at Graph's
    /// server-side write throttle rather than the client-side limiter, and it has its own
    /// documented off switch - so a caller setting both should be told which one won.
    /// </summary>
    [Fact]
    public void Setting_NoRateLimit_beside_BatchItemsPerSecond_says_which_one_wins()
    {
        try
        {
            using var ps = Shell();
            ps.AddCommand("Set-MgxOption")
              .AddParameter("NoRateLimit", true)
              .AddParameter("BatchItemsPerSecond", 20);
            ps.Invoke();

            var warned = string.Join(" ", ps.Streams.Warning.Select(w => w.Message));
            Assert.Contains("has no effect", warned);
            Assert.Contains("BatchItemsPerSecond", warned);
        }
        finally
        {
            using var reset = Shell();
            reset.AddCommand("Set-MgxOption").AddParameter("Reset", true);
            reset.Invoke();
        }
    }

    /// <summary>What Get-MgxOption reports right now.</summary>
    private static MgxOptionOutput Reported()
    {
        using var ps = Shell();
        ps.AddCommand("Get-MgxOption");
        var results = ps.Invoke();
        return Assert.IsType<MgxOptionOutput>(Assert.Single(results).BaseObject);
    }

    /// <summary>What Get-MgxTelemetry reports right now.</summary>
    private static MgxTelemetryOutput Telemetry()
    {
        using var ps = Shell();
        ps.AddCommand("Get-MgxTelemetry");
        var results = ps.Invoke();
        return Assert.IsType<MgxTelemetryOutput>(Assert.Single(results).BaseObject);
    }

    /// <summary>
    /// No parameters bound (beyond the common ones PowerShell always adds) is the guard's
    /// no-op case, and the gate now sits after that guard: a bare `Set-MgxOption` never reaches
    /// ShouldProcess, so it previews nothing and would prompt for nothing under -Confirm either.
    /// </summary>
    [Fact]
    public void No_parameters_previews_nothing()
    {
        var lines = RecordingHost.Preview(
            typeof(Mgx.Cmdlets.Cmdlets.Configuration.SetMgxOption).Assembly,
            ps => ps.AddCommand("Set-MgxOption")
                    .AddParameter("Verbose", true).AddParameter("WhatIf", true));

        Assert.DoesNotContain(lines, l => l.Contains("What if:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The target a preview names is what the run would set. It was built from every bound
    /// parameter, and PowerShell binds the common ones, so `Set-MgxOption -Verbose -WhatIf`
    /// used to preview a change to "MgxOptions (Verbose, WhatIf)" - naming two parameters that
    /// set no option at all, for a run the guard reports as unchanged. -Reset names what it
    /// drops rather than what it sets, so a bound option shows up there as "ignoring", not as
    /// part of the reset itself.
    /// </summary>
    [Theory]
    [InlineData(false, "MaxRetryAttempts", 3, "MgxOptions (MaxRetryAttempts)")]
    [InlineData(true, null, 0, "MgxOptions (reset to defaults)")]
    [InlineData(true, "RateLimitBurst", 500, "MgxOptions (reset to defaults; ignoring RateLimitBurst)")]
    public void A_preview_target_names_only_the_options_a_run_would_set(
        bool reset, string? option, int value, string expected)
    {
        var lines = RecordingHost.Preview(
            typeof(Mgx.Cmdlets.Cmdlets.Configuration.SetMgxOption).Assembly,
            ps =>
            {
                var command = ps.AddCommand("Set-MgxOption");
                if (reset) command.AddParameter("Reset", true);
                if (option != null) command.AddParameter(option, value);
                command.AddParameter("Verbose", true).AddParameter("WhatIf", true);
            });

        // The gate quotes its target, so the closing quote is what says the name ends there.
        Assert.Contains(lines, l => l.Contains($"\"{expected}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// -Reset beside a bound option drops the option - it always has - but used to do so without
    /// a word about it, which reads as the option having taken effect over the defaults it did
    /// not survive. The warning names what it ignored, and the value confirms it really was.
    /// </summary>
    [Fact]
    public void Reset_beside_an_option_warns_and_names_it()
    {
        try
        {
            using var ps = Shell();
            ps.AddCommand("Set-MgxOption")
              .AddParameter("Reset", true)
              .AddParameter("RateLimitBurst", 500);
            ps.Invoke();

            var warned = string.Join(" ", ps.Streams.Warning.Select(w => w.Message));
            Assert.Contains("-Reset resets every option", warned);
            Assert.Contains("-RateLimitBurst", warned);
            Assert.Contains("ignored", warned);

            Assert.Equal(ResilientGraphClientOptions.Default.RateLimitBurst, Reported().RateLimitBurst);
        }
        finally
        {
            using var reset = Shell();
            reset.AddCommand("Set-MgxOption").AddParameter("Reset", true);
            reset.Invoke();
        }
    }

    /// <summary>
    /// -NoRateLimit is sticky: it stays off until something turns it back on, and every later
    /// call starts from the options it left behind. A caller who then tunes the limiter is
    /// asking for a limiter, so a bound -RateLimitBurst or -RateLimitPerSecond clears it -
    /// otherwise the burst lands on an option nothing reads and the session keeps running
    /// unlimited with a rate on the books.
    /// </summary>
    [Theory]
    [InlineData("RateLimitBurst", 777)]
    [InlineData("RateLimitPerSecond", 77)]
    public void A_rate_parameter_turns_rate_limiting_back_on(string parameter, int value)
    {
        try
        {
            using (var off = Shell())
            {
                off.AddCommand("Set-MgxOption").AddParameter("NoRateLimit", true);
                off.Invoke();
            }

            // What the second call has to clear. Without this the assertions below hold on a
            // session that never turned the limiter off in the first place.
            Assert.True(Reported().NoRateLimit,
                "-NoRateLimit did not take, so nothing below is being cleared");

            using (var tune = Shell())
            {
                tune.AddCommand("Set-MgxOption").AddParameter(parameter, value);
                tune.Invoke();
            }

            var reported = Reported();
            Assert.False(reported.NoRateLimit);
            Assert.Equal(value, parameter == "RateLimitBurst"
                ? reported.RateLimitBurst
                : reported.RateLimitPerSecond);
        }
        finally
        {
            using var reset = Shell();
            reset.AddCommand("Set-MgxOption").AddParameter("Reset", true);
            reset.Invoke();
        }
    }

    /// <summary>
    /// The counters are per-process too, and they are one session's record of what it sent: a
    /// removal that leaves them set has Get-MgxTelemetry crediting a fresh import with the
    /// requests of the import before it - the same shape as the options and the endpoint a
    /// removal already puts back.
    /// </summary>
    [Fact]
    public void A_module_removal_releases_the_telemetry_counters()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, "{\"value\":[]}");

        MgxTelemetryCollector.Current.Reset();
        using (MgxTransportScope.Inject(wire))
        using (var request = Shell())
        {
            request.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users");
            request.Invoke();
            Assert.Empty(request.Streams.Error.Select(e => e.FullyQualifiedErrorId));
        }

        // What the removal has to release: this import's own traffic, reported as current.
        Assert.True(Telemetry().Requests > 0,
            "the request the seam served was never counted, so the release below proves nothing");

        // A removal is ReleaseStaticState and then the detach; an import is OnImport.
        AlcInitializer.ReleaseStaticState();
        new AlcInitializer().OnImport();

        Assert.Equal(0L, Telemetry().Requests);
    }

    /// <summary>
    /// The options a session sets are per-process state, and a removal that leaves them set gives
    /// the next import somebody else's tuning under a fresh module - with Get-MgxOption reporting
    /// it as current, so nothing says the reset the re-import looks like never happened.
    /// </summary>
    [Fact]
    public void A_module_removal_puts_the_options_back_to_their_defaults()
    {
        try
        {
            using (var set = Shell())
            {
                set.AddCommand("Set-MgxOption")
                  .AddParameter("RateLimitBurst", 999)
                  .AddParameter("RateLimitPerSecond", 42)
                  .AddParameter("NoRateLimit", true)
                  .AddParameter("RateLimitQueueLimit", 12345)
                  .AddParameter("NoAdaptivePacing", true)
                  .AddParameter("MaxRetryAfterSeconds", 7)
                  .AddParameter("MaxRetryAttempts", 3)
                  .AddParameter("TotalTimeoutSeconds", 111)
                  .AddParameter("AttemptTimeoutSeconds", 9)
                  .AddParameter("CircuitBreakerDurationSeconds", 8)
                  .AddParameter("CircuitBreakerFailureRatio", 0.55)
                  .AddParameter("CircuitBreakerMinThroughput", 6)
                  .AddParameter("CircuitBreakerSamplingDurationSeconds", 11)
                  .AddParameter("BatchChunkConcurrency", 4)
                  .AddParameter("BatchItemsPerSecond", 0);
                set.Invoke();
            }

            // Every settable option away from its default, so the removal below has all of the
            // option surface to put back - not just the one property this test used to read.
            var changed = Reported();
            Assert.Equal(999, changed.RateLimitBurst);
            Assert.Equal(42, changed.RateLimitPerSecond);
            Assert.True(changed.NoRateLimit);
            Assert.Equal(12345, changed.RateLimitQueueLimit);
            Assert.True(changed.NoAdaptivePacing);
            Assert.Equal(7, changed.MaxRetryAfterSeconds);
            Assert.Equal(3, changed.MaxRetryAttempts);
            Assert.Equal(111, changed.TotalTimeoutSeconds);
            Assert.Equal(9, changed.AttemptTimeoutSeconds);
            Assert.Equal(8, changed.CircuitBreakerDurationSeconds);
            Assert.Equal(0.55, changed.CircuitBreakerFailureRatio);
            Assert.Equal(6, changed.CircuitBreakerMinThroughput);
            Assert.Equal(11, changed.CircuitBreakerSamplingDurationSeconds);
            Assert.Equal(4, changed.BatchChunkConcurrency);
            Assert.Equal(0, changed.BatchItemsPerSecond);

            // A removal is ReleaseStaticState and then the detach; an import is OnImport.
            AlcInitializer.ReleaseStaticState();
            new AlcInitializer().OnImport();

            // The whole surface back to Default, not only the property this test used to check.
            var restored = Reported();
            var defaults = ResilientGraphClientOptions.Default;
            Assert.Equal(defaults.RateLimitBurst, restored.RateLimitBurst);
            Assert.Equal(defaults.RateLimitPerSecond, restored.RateLimitPerSecond);
            Assert.Equal(defaults.NoRateLimit, restored.NoRateLimit);
            Assert.Equal(defaults.RateLimitQueueLimit, restored.RateLimitQueueLimit);
            Assert.Equal(defaults.NoAdaptivePacing, restored.NoAdaptivePacing);
            Assert.Equal(defaults.MaxRetryAfterSeconds, restored.MaxRetryAfterSeconds);
            Assert.Equal(defaults.MaxRetryAttempts, restored.MaxRetryAttempts);
            Assert.Equal(defaults.TotalTimeoutSeconds, restored.TotalTimeoutSeconds);
            Assert.Equal(defaults.AttemptTimeoutSeconds, restored.AttemptTimeoutSeconds);
            Assert.Equal(defaults.CircuitBreakerDurationSeconds, restored.CircuitBreakerDurationSeconds);
            Assert.Equal(defaults.CircuitBreakerFailureRatio, restored.CircuitBreakerFailureRatio);
            Assert.Equal(defaults.CircuitBreakerMinThroughput, restored.CircuitBreakerMinThroughput);
            Assert.Equal(defaults.CircuitBreakerSamplingDurationSeconds, restored.CircuitBreakerSamplingDurationSeconds);
            Assert.Equal(defaults.BatchChunkConcurrency, restored.BatchChunkConcurrency);
            Assert.Equal(defaults.BatchItemsPerSecond, restored.BatchItemsPerSecond);
        }
        finally
        {
            using var reset = Shell();
            reset.AddCommand("Set-MgxOption").AddParameter("Reset", true);
            reset.Invoke();
        }
    }

    /// <summary>
    /// The timeout the last client build sized its transport to. MgxCmdletBase keeps it private,
    /// and nothing above it reports what a build is comparing against, so the field is the state
    /// under test.
    /// </summary>
    private static FieldInfo CachedTimeoutField()
    {
        var field = typeof(MgxCmdletBase).GetField(
            "s_cachedTotalTimeoutSeconds", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(field != null,
            "MgxCmdletBase no longer keeps the cached timeout in s_cachedTotalTimeoutSeconds");
        return field!;
    }

    /// <summary>
    /// The endpoint and that timeout are per-process too, and derived rather than configured: a
    /// removal that leaves them set has the next import composing URLs against the cloud the
    /// import before it connected to, and its first build comparing against a
    /// TotalTimeoutSeconds nobody in the new session chose, until that build derives both again.
    /// </summary>
    [Fact]
    public void A_module_removal_releases_the_endpoint_and_timeout_a_build_derived()
    {
        const string sovereign = "https://microsoftgraph.chinacloudapi.cn";
        var timeout = CachedTimeoutField();
        var previousEndpoint = MgxCmdletBase.s_graphEndpoint;
        var previousTimeout = timeout.GetValue(null);
        try
        {
            // Where a build against a sovereign cloud leaves the process.
            MgxCmdletBase.s_graphEndpoint = sovereign;
            timeout.SetValue(null, 900);

            // A removal is ReleaseStaticState and then the detach; an import is OnImport.
            AlcInitializer.ReleaseStaticState();
            new AlcInitializer().OnImport();

            Assert.Equal(MgxCmdletBase.DefaultGraphEndpoint, MgxCmdletBase.s_graphEndpoint);
            Assert.Equal(0, timeout.GetValue(null));

            // And the endpoint the next build derives comes off the session it finds, not off
            // anything the removal left behind. Asserted on the static a build writes, through a
            // build: GetGraphEndpoint on its own reads the session and nothing else, so it answers
            // the same whether the release ran or not, and whether anything drives the
            // re-derivation or not.
            var wire = new MockHttpHandler();
            wire.SetDefaultResponse(HttpStatusCode.OK, "{\"value\":[]}");
            using var sessionClient = new HttpClient(wire);

            // No transport seam: it sits above the auth probe in GetClient, and the endpoint is
            // derived on the build below it. GetClient cannot build a client of its own against a
            // session this fake, so it borrows the one on the session - which is the mock.
            using var scope = GraphSessionScope.Arm(
                sessionClient, GraphSessionScope.AuthContextFor(
                    "11111111-1111-1111-1111-111111111111",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
            scope.Session.Environment = new FakeGraphEnvironment { GraphEndpoint = sovereign };

            using (var request = Shell())
            {
                request.AddCommand("Invoke-MgxRequest")
                       .AddParameter("Uri", "/users")
                       .AddParameter("WarningAction", ActionPreference.SilentlyContinue);
                request.Invoke();
                Assert.Empty(request.Streams.Error.Select(e => e.FullyQualifiedErrorId));
            }

            Assert.Equal(sovereign, MgxCmdletBase.s_graphEndpoint);

            // The static is what the cmdlets compose from, so the request that went out is the
            // reading of it that matters.
            Assert.StartsWith(
                $"{sovereign}/v1.0/users",
                Assert.Single(wire.CapturedRequests).Uri, StringComparison.Ordinal);
        }
        finally
        {
            MgxCmdletBase.s_graphEndpoint = previousEndpoint;
            timeout.SetValue(null, previousTimeout);
        }
    }
}
