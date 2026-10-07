namespace NexusPipeline.ControlPlane.Http;

internal sealed record WebServerOptions(bool ServeWebUi, bool AllowRemoteAccess)
{
    public static WebServerOptions FromSettings(bool allowRemoteAccess)
    {
        return new WebServerOptions(
            ServeWebUi: true,
            AllowRemoteAccess: allowRemoteAccess);
    }
}
