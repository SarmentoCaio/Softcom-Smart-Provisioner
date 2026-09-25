using System.Text.Json;

namespace SoftcomSmartProvisioner.SelfHostBridge;

public static class BridgeProtocol
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static BridgeCommand ParseCommand(string? input)
    {
        try
        {
            var clean = (input ?? string.Empty).Trim().TrimStart('\uFEFF');
            return JsonSerializer.Deserialize<BridgeCommand>(clean, Options)
                ?? throw new JsonException("JSON vazio.");
        }
        catch (JsonException)
        {
            throw new BridgeValidationException("invalid_json", "O comando recebido não contém JSON válido.");
        }
    }
}
