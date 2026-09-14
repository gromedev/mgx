using System.Text;
using System.Text.RegularExpressions;

namespace Mgx.Engine.Http;

/// <summary>
/// Formats HTTP request/response traces for -Debug output. Credentials are redacted and bodies
/// truncated so a trace never leaks a secret or dumps a full page of Graph data.
/// </summary>
internal static partial class GraphRequestTracer
{
    /// <summary>Bodies longer than this are cut, with the omitted length noted.</summary>
    internal const int MaxBodyChars = 4096;

    /// <summary>
    /// Response headers worth tracing. Everything else (caching, CORS, transport) is noise.
    /// Prefix entries match any header starting with the value.
    /// </summary>
    private static readonly string[] ResponseHeaderNames =
    [
        "request-id", "client-request-id", "x-ms-ags-diagnostic", "Retry-After",
        "x-ms-resource-unit", "Location", "OData-Version"
    ];

    private static readonly string[] ResponseHeaderPrefixes = ["RateLimit-", "x-ms-throttle-"];

    /// <summary>
    /// Properties whose NAME says credential. The fragments come from SensitiveNames, the same
    /// list the dead-letter writer reads, so a clientAssertion or a connectionString is missing
    /// from a trace and from the file alike. Not source-generated, because the pattern is that
    /// list: written out here it would be a second copy of it, free to drift.
    /// </summary>
    /// Compiled, because the other two patterns are source-generated and this one runs over the
    /// same bodies: interpreted, it is the slowest thing in a -Debug trace of a large response.
    private static readonly Regex SensitiveJsonValue = new(
        $"\"([^\"]*(?:{SensitiveNames.RegexAlternation})[^\"]*)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A pre-authenticated URL is a credential whose property name says nothing about it:
    /// "@microsoft.graph.downloadUrl" carries none of the SensitiveNames fragments, so
    /// SensitiveJsonValue leaves it whole and the tempauth JWT reaches -Debug output. Match on
    /// the VALUE instead - a URL carrying one of the capability parameters Graph, SharePoint and
    /// the download hosts actually use. Those parameters are SensitiveNames', the definition the
    /// dead-letter writer reads too, so a SAS URL missing from a trace is missing from the file;
    /// that class says why the list is not simply every URL.
    /// </summary>
    [GeneratedRegex($"\"(https?://[^\"]*?(?:{SensitiveNames.CapabilityUrlParameters})=)[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex CapabilityUrlValue();

    /// <summary>
    /// A capability URL used as a property NAME, which takes the value beside it with it. A
    /// pre-authenticated URL as a field name is itself a credential, so whatever a body files
    /// under one is the thing that URL was minted for, and the dead-letter writer clears it.
    /// CapabilityUrlValue() below finds such a key too - a quoted string is a quoted string,
    /// key or value - but it stops at that string's closing quote and leaves the value standing.
    /// The colon is what says the string was a key: a JSON value is followed by a comma or a
    /// closing brace or bracket, never by one.
    ///
    /// That trailing `"[^"]*"` only reaches the value when it is itself a quoted string. The
    /// dead-letter writer clears the value beside a cut key whatever it holds, so a number, a
    /// bool, an array or an object beside a capability-URL key is missing from the dead-letter
    /// line and still stands whole in -Debug output - this pattern has nothing to match there.
    /// </summary>
    [GeneratedRegex($"\"(https?://[^\"]*?(?:{SensitiveNames.CapabilityUrlParameters})=)[^\"]*\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex CapabilityUrlProperty();

    /// <summary>
    /// The value rule again on a URL that has lost its scheme and host. A $batch envelope
    /// carries each item's url relative - Invoke-MgxBatchRequest normalizes it before the POST,
    /// which is what Graph's batch format takes - so a pre-authenticated URL a caller passed as
    /// an item URL reaches a trace as "/x?tempauth=..." and the two patterns above, anchored on
    /// the scheme, have nothing in it to match. The leading slash stands in for the scheme: it
    /// says the quoted string is a path rather than prose that happens to carry a parameter.
    ///
    /// A value only, with no property counterpart. The dead-letter writer reads a body's keys
    /// and values through SensitiveNames.RedactCapabilityUrl, which reads the same two forms, so
    /// a relative path is cut on both sides: in the file, where a value written whole is a live
    /// credential on disk, and here. Where the two are read side by side is the item URL, which
    /// is absolute in the file where a caller gave it absolute, relative on the wire, and cut
    /// either way.
    /// </summary>
    [GeneratedRegex($"\"(/[^\"]*?(?:{SensitiveNames.CapabilityUrlParameters})=)[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex RelativeCapabilityUrlValue();

    /// <summary>
    /// Property names that carry a pre-authenticated URL even when the value does not expose a
    /// recognizable parameter - the short-form download hosts put the capability in the path.
    /// The fragment is SensitiveNames', which the writer reads one property name at a time.
    /// </summary>
    [GeneratedRegex($"\"([^\"]*{SensitiveNames.DownloadUrlFragment}[^\"]*)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex DownloadUrlProperty();

    /// <summary>
    /// Trace line for an outgoing request. <paramref name="attempt"/> is 1-based so retries are visible.
    /// </summary>
    internal static string FormatRequest(HttpRequestMessage request, byte[]? body, int attempt)
    {
        var sb = new StringBuilder();
        var label = attempt > 1 ? $"Request (attempt {attempt})" : "Request";
        sb.Append("[Mgx] ").Append(label).Append(": ")
          .Append(request.Method.Method).Append(' ').Append(request.RequestUri);

        sb.AppendLine().Append("  Headers:");
        // The bearer token is attached further down the pipeline by the auth handler,
        // so it is not on this HttpRequestMessage. Show it for completeness, never its value.
        sb.AppendLine().Append("    Authorization: Bearer <redacted>");
        foreach (var header in request.Headers)
            sb.AppendLine().Append("    ").Append(header.Key).Append(": ").Append(Join(header.Value));
        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
                sb.AppendLine().Append("    ").Append(header.Key).Append(": ").Append(Join(header.Value));
        }

        if (body is { Length: > 0 })
        {
            sb.AppendLine().Append("  Body (").Append(body.Length).Append(" bytes):")
              .AppendLine().Append(Sanitize(Encoding.UTF8.GetString(body)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Trace line for a response. <paramref name="body"/> is null when the body was not buffered.
    /// </summary>
    internal static string FormatResponse(HttpResponseMessage response, long elapsedMs, string? body)
    {
        var sb = new StringBuilder();
        sb.Append("[Mgx] Response: ").Append((int)response.StatusCode).Append(' ')
          .Append(response.StatusCode).Append(" in ").Append(elapsedMs).Append(" ms");

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(h => IsTraced(h.Key))
            .ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine().Append("  Headers:");
            foreach (var header in headers)
                sb.AppendLine().Append("    ").Append(header.Key).Append(": ")
                  .Append(RedactHeaderValue(header.Key, Join(header.Value)));
        }

        if (!string.IsNullOrEmpty(body))
        {
            sb.AppendLine().Append("  Body (").Append(Encoding.UTF8.GetByteCount(body)).Append(" bytes):")
              .AppendLine().Append(Sanitize(body));
        }

        return sb.ToString();
    }

    private static bool IsTraced(string name) =>
        ResponseHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || ResponseHeaderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    /// <summary>
    /// A content 302's Location is a pre-authenticated URL: it grants the file bytes without a
    /// bearer token, so tracing it verbatim writes a live credential into -Debug output. The
    /// capability is not always in the query - some download hosts carry it in the path - so
    /// keep only scheme and host, which is the whole diagnostic value of the header anyway.
    /// The JSON-body redaction cannot reach header values, hence this hook.
    /// </summary>
    private static string RedactHeaderValue(string name, string value)
    {
        if (!name.Equals("Location", StringComparison.OrdinalIgnoreCase)) return value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.IdnHost}/<redacted>"
            : "<redacted>";
    }

    /// <summary>Redact credential-looking JSON properties and pre-authenticated URLs, then truncate.</summary>
    private static string Sanitize(string body)
    {
        var redacted = SensitiveJsonValue.Replace(body, "\"$1\": \"<redacted>\"");
        // Property-name match first: covers a downloadUrl whose capability sits in the path.
        redacted = DownloadUrlProperty().Replace(redacted, "\"$1\": \"<redacted>\"");
        // A capability URL used as a key, and the value beside it, before the pass below cuts
        // such a key on its own and leaves that value standing.
        redacted = CapabilityUrlProperty().Replace(redacted, "\"$1<redacted>\": \"<redacted>\"");
        // Then any remaining URL value carrying a capability parameter, keeping the parameter
        // name visible so a trace still shows WHICH kind of URL was redacted.
        redacted = CapabilityUrlValue().Replace(redacted, "\"$1<redacted>\"");
        // And the same value with its scheme and host gone, which is the form a $batch item's
        // own url has by the time it is on the wire.
        redacted = RelativeCapabilityUrlValue().Replace(redacted, "\"$1<redacted>\"");
        return redacted.Length <= MaxBodyChars
            ? redacted
            : redacted[..MaxBodyChars] + $"... [truncated, {redacted.Length - MaxBodyChars} more chars]";
    }
}
