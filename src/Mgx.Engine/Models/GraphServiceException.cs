using System.Net;
using System.Text.Json;

namespace Mgx.Engine.Models;

/// <summary>
/// Exception thrown when the Graph API returns an error response.
/// Parses the { "error": { "code": "...", "message": "..." } } body.
/// </summary>
public class GraphServiceException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }

    public GraphServiceException(HttpStatusCode statusCode, string responseBody)
        : base(FormatAndExtract(statusCode, responseBody, out var code))
    {
        StatusCode = statusCode;
        ErrorCode = code;
    }

    /// <summary>
    /// Parse the Graph error response body once, extracting both the formatted message and error code.
    /// Appends guidance hint when available for known error codes.
    /// </summary>
    private static string FormatAndExtract(HttpStatusCode statusCode, string responseBody, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrEmpty(responseBody))
            return $"HTTP {(int)statusCode}: {statusCode}";

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                var code = errorObj.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = errorObj.TryGetProperty("message", out var m) ? m.GetString() : null;
                errorCode = code;

                // Build formatted message from whatever Graph provided
                var formatted = !string.IsNullOrEmpty(code)
                    ? $"{code}: {message}"
                    : !string.IsNullOrEmpty(message)
                        ? message
                        : $"HTTP {(int)statusCode}: {statusCode}";

                var guidance = GetGuidanceForCode(code);
                if (guidance != null)
                    formatted += $"\nHint: {guidance}";
                return formatted;
            }
        }
        catch (JsonException) { }
        return $"HTTP {(int)statusCode}: {statusCode}";
    }

    /// <summary>
    /// Maps common Graph error codes to user-facing guidance strings.
    /// Returns null for unrecognized codes.
    /// </summary>
    internal static string? GetGuidanceForCode(string? errorCode) => errorCode switch
    {
        "Authorization_RequestDenied" => "Check your Graph scopes with Get-MgContext. The required permission may not be consented.",
        "Request_ResourceNotFound" => "Verify the URI path and that the resource exists. Use -SkipNotFound to suppress in fan-out.",
        "Request_BadRequest" => "Check $filter syntax, property names, and $search quoting. Use -ConsistencyLevel eventual for advanced queries.",
        "InvalidAuthenticationToken" => "Session may have expired. Run Connect-MgGraph to re-authenticate.",
        "Authentication_ExpiredToken" => "Token has expired. Run Connect-MgGraph to re-authenticate.",
        "ErrorAccessDenied" => "Insufficient permissions. Check required scopes at https://learn.microsoft.com/graph/permissions-reference.",
        "Forbidden" => "Access denied. This may require admin consent or an application permission (not delegated).",
        "TooManyRequests" or "activityLimitReached" => "Throttled by Graph API. Mgx handles this automatically; increase -TotalTimeoutSeconds if retries are exhausted.",
        "ServiceNotAvailable" => "Graph service is temporarily unavailable. Mgx retries automatically; check https://status.cloud.microsoft.com for outages.",
        "BadRequest" => "Malformed request. Check $filter, $select, $orderby syntax and property name spelling.",
        _ => null
    };
}
