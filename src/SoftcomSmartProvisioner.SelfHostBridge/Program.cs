using System.Text.Json;
using System.Text.Json.Serialization;
using SoftcomSmartProvisioner.SelfHostBridge;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

BridgeResponse response;
try
{
    var input = await Console.In.ReadToEndAsync();
    response = BridgeApplication.Execute(BridgeProtocol.ParseCommand(input));
}
catch (BridgeValidationException ex)
{
    response = new BridgeResponse { Success = false, ErrorCode = ex.Code, Message = SecretSanitizer.Clean(ex.Message) };
}
catch (Exception ex)
{
    response = new BridgeResponse { Success = false, ErrorCode = "bridge_error", Message = SecretSanitizer.Clean(ex.GetBaseException().Message) };
}

await Console.Out.WriteAsync(JsonSerializer.Serialize(response, jsonOptions));
Environment.ExitCode = response.Success ? 0 : 1;
