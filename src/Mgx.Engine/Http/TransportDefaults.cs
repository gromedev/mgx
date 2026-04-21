using System.Net;

namespace Mgx.Engine.Http;

/// <summary>
/// Default transport configuration values for SocketsHttpHandler and HttpClient.
/// Centralized so both MgxCmdletBase and tests reference the same values.
/// </summary>
public static class TransportDefaults
{
    /// <summary>TCP connect timeout. Prevents infinite hang on blackholed SYN.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>DNS refresh interval. Ensures Azure Front Door IP rotations are picked up.</summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Connection pool limit per (host, port, scheme). With HTTP/2 multiplexing,
    /// each connection handles hundreds of concurrent streams, so 20 is ample
    /// for fan-out concurrency of 5 + batch + retries.
    /// </summary>
    public const int MaxConnectionsPerServer = 20;

    /// <summary>Allow multiple HTTP/2 connections when stream limit is reached.</summary>
    public const bool EnableMultipleHttp2Connections = true;

    /// <summary>Supported decompression algorithms. Brotli is supported by Azure Front Door.</summary>
    public const DecompressionMethods Decompression =
        DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli;
}
