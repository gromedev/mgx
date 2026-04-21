namespace Mgx.Engine;

/// <summary>
/// SDK version identifier injected into the SdkVersion HTTP header on all Graph requests.
/// Enables correlation of GraphExtended traffic in Microsoft's Graph API telemetry.
/// </summary>
internal static class MgxSdkVersion
{
    internal const string Value = "mgx/0.3.0";
}
