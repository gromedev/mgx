using System.Text.RegularExpressions;

namespace Mgx.Engine.Http;

/// <summary>
/// The name fragments that mark a value as a credential, matched case-insensitively as
/// substrings. Graph spells its secrets differently on nearly every resource - newPassword on
/// an administrative reset, preSharedKey and sharedSecret on Wi-Fi and VPN profiles,
/// uploadSecret on a trust framework key set - so a list of exact names is behind the surface
/// the day it is written. Substrings over-redact, and that is the cheaper error: a redacted
/// diagnostic field costs a line of context, an unredacted PSK is a credential sitting in a
/// file.
///
/// One definition, two readers - and three rules, not one. The fragments are the first. A
/// property whose name carries "downloadurl" and a string value that is a URL carrying one of
/// the capability parameters are the other two, and they are here for the same reason: they say
/// credential without saying password, and each reader needs the same answer from them.
/// GraphRequestTracer applies all three to -Debug output as text; Invoke-MgxBatchRequest applies
/// all three to a dead-letter body as parsed JSON, and the fragments again to a request path.
///
/// The value rule agrees on the form it is written in, since a URL is text either way, and on
/// both forms it comes in: a $batch item's own url is on the wire with its scheme and host
/// stripped, and a caller who was handed a relative URL by Graph passes it on as one, so a path
/// carrying a capability parameter is read as such by GraphRequestTracer and by
/// RedactCapabilityUrl below - which the file reads a body's keys and values through - alike.
/// The two name rules agree only where the value beside the named key is itself a quoted string
/// - all a trace regex can match.
/// Invoke-MgxBatchRequest clears that value whatever it holds, so a number, a bool, an array or
/// an object beside a password or a downloadUrl field is missing from the file and still stands
/// whole in a trace.
/// </summary>
public static class SensitiveNames
{
    private static readonly string[] TokenList =
    [
        "password", "secret", "credential", "key", "token", "assertion", "passphrase",
        "connectionstring"
    ];

    /// <summary>The fragments, lowercase. Compare case-insensitively.</summary>
    public static IReadOnlyList<string> Tokens => TokenList;

    /// <summary>True when a field name or URL path segment carries one of the fragments.</summary>
    public static bool Matches(string name)
    {
        foreach (var token in TokenList)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The fragments as a regex alternation, for a caller matching names inside text rather
    /// than one name at a time.
    /// </summary>
    internal static string RegexAlternation { get; } =
        string.Join('|', TokenList.Select(Regex.Escape));

    /// <summary>
    /// The name fragment that says a property's value is a pre-authenticated URL even when the
    /// value exposes no parameter to recognize it by - the short-form download hosts put the
    /// capability in the path, so "@microsoft.graph.downloadUrl" and its older spelling
    /// "@content.downloadUrl" are credentials under a name no fragment above reaches.
    /// </summary>
    internal const string DownloadUrlFragment = "downloadurl";

    /// <summary>
    /// The query parameters that carry a pre-authenticated URL's capability, as a regex
    /// alternation rather than a list of names because `sig` needs a lookbehind to be read as a
    /// parameter of its own. Deliberately not every URL: @odata.nextLink is a URL too, and
    /// losing it would remove the thing paging bugs are diagnosed with.
    /// </summary>
    internal const string CapabilityUrlParameters =
        "tempauth|guestaccesstoken|authkey|X-Amz-Signature|(?<![a-z])sig";

    /// <summary>True when a property name says its value is a pre-authenticated URL.</summary>
    public static bool NamesDownloadUrl(string name) =>
        name.Contains(DownloadUrlFragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The capability-parameter rule on one unescaped value, for the reader holding parsed JSON
    /// rather than text. Returns the value cut after the first capability parameter's `=` with
    /// <paramref name="marker"/> in place of the rest - the shape the tracer's replacement
    /// makes, which keeps the parameter name readable so it is still clear which kind of URL
    /// went - or null when the value carries no capability parameter and may be kept whole.
    ///
    /// The pattern differs from the tracer's only in what bounds the value: a quote ends a
    /// string in the JSON text the tracer reads, and bounds nothing in a value that has already
    /// been unescaped. The parameters both of them test are the definition above, and so are the
    /// two forms a URL arrives in - with its scheme, or with a leading slash standing in for one,
    /// which is what says the string is a path rather than prose that happens to carry a
    /// parameter.
    /// </summary>
    public static string? RedactCapabilityUrl(string value, string marker)
    {
        var match = CapabilityUrl.Match(value);
        return match.Success ? match.Groups[1].Value + marker : null;
    }

    private static readonly Regex CapabilityUrl = new(
        $"^((?:https?://|/).*?(?:{CapabilityUrlParameters})=)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
