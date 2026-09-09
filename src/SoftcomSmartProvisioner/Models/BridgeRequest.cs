using System.Text.Json;

namespace SoftcomSmartProvisioner.Models;

public sealed class BridgeRequest
{
    public string Action { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}
