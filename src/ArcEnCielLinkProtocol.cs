namespace ArcEnCiel.Link.Swarm;

internal static class ArcEnCielLinkProtocol
{
    public const string Version = "2.5.1";
    public static string SerializePayload(object payload) => payload is Newtonsoft.Json.Linq.JToken token
        ? token.ToString(Newtonsoft.Json.Formatting.None)
        : System.Text.Json.JsonSerializer.Serialize(payload);
    public const int ProtocolVersion = 2;
    public const string ClientId = "swarmui";
    public const string PrivateDownloadGrantCapability = "private_download_grant_v1";
    public const string DownloadGrantHeader = "x-arcenciel-link-grant";
}
