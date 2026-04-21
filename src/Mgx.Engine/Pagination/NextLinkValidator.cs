namespace Mgx.Engine.Pagination;

/// <summary>
/// Validates @odata.nextLink URLs to prevent SSRF attacks.
/// A poisoned nextLink (from a crafted Graph response or tampered checkpoint)
/// pointing to an attacker's server would leak the bearer token.
/// </summary>
public static class NextLinkValidator
{
    /// <summary>
    /// Returns the nextLink unchanged if it passes all validation checks,
    /// or null if it should be rejected (stopping pagination).
    /// </summary>
    public static string? Validate(string? nextLink, Uri? expectedHost, string? expectedPathPrefix = null)
    {
        if (nextLink == null || expectedHost == null) return null;

        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri))
            return null;

        // Reject non-HTTPS: prevents scheme-downgrade attacks that would
        // send the bearer token over plaintext HTTP
        if (!string.Equals(nextUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return null;

        // Compare Authority (host + port) to catch port-based redirects
        if (!string.Equals(nextUri.Authority, expectedHost.Authority, StringComparison.OrdinalIgnoreCase))
            return null;

        // Optional: validate path prefix to prevent same-host cross-resource redirection.
        // A tampered checkpoint could redirect /users pagination to /me/messages on the
        // same host, exfiltrating different data with the user's token.
        if (expectedPathPrefix != null &&
            !nextUri.AbsolutePath.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return nextLink;
    }
}
