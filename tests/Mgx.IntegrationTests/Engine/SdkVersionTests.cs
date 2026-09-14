using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Mgx.Engine;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// The SdkVersion header identifies Mgx in Microsoft's Graph telemetry and is shown in the -Debug
/// trace, so a stale value is both misleading and hard to notice. It is derived from the assembly
/// version; these tests pin it to the module manifest so the two cannot drift apart.
/// </summary>
[Collection("Pipeline")]
public class SdkVersionTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>ModuleVersion from the module manifest, the version users install.</summary>
    private static string ManifestVersion()
    {
        var manifest = FindRepositoryFile(Path.Combine("module", "mgx.psd1"));
        var match = Regex.Match(File.ReadAllText(manifest), @"ModuleVersion\s*=\s*'([^']+)'");

        Assert.True(match.Success, $"ModuleVersion not found in {manifest}");
        return match.Groups[1].Value;
    }

    /// <summary>The file that marks the repository root.</summary>
    private const string RepositoryMarker = "Mgx.slnx";

    /// <summary>
    /// Resolve a repository-relative file under the repository root - the first ancestor of the
    /// test binaries holding Mgx.slnx. Nothing above that root is consulted, so a module folder
    /// beside a worktree checked out under it cannot supply the manifest this pins against.
    /// </summary>
    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, RepositoryMarker)))
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;

                throw new FileNotFoundException(
                    $"Could not locate '{relativePath}' under repository root {dir.FullName}");
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {RepositoryMarker} above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void Reports_the_assembly_version()
    {
        var informational = typeof(MgxSdkVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = informational.Split('+')[0];

        Assert.Equal($"mgx/{expected}", MgxSdkVersion.Value);
    }

    [Fact]
    public void Matches_the_module_manifest_version()
    {
        // Assembly version lives in Directory.Build.props, ModuleVersion in the psd1.
        // Bumping one without the other silently mislabels every Graph request.
        Assert.Equal($"mgx/{ManifestVersion()}", MgxSdkVersion.Value);
    }

    [Fact]
    public void Is_a_three_part_version_and_not_the_default()
    {
        Assert.Matches(@"^mgx/\d+\.\d+\.\d+$", MgxSdkVersion.Value);
        // 1.0.0 is what the SDK stamps when <Version> is missing
        Assert.NotEqual("mgx/1.0.0", MgxSdkVersion.Value);
    }

    [Fact]
    public async Task Is_sent_on_every_request()
    {
        string? sent = null;
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            sent = request.Headers.TryGetValues("SdkVersion", out var values) ? values.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var client = new ResilientGraphClient(http, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            CircuitBreakerMinThroughput = 1000,
            AttemptTimeoutSeconds = 10,
            TotalTimeoutSeconds = 60
        });

        using var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users",
            cancellationToken: Ct);

        Assert.Equal(MgxSdkVersion.Value, sent);
    }
}
