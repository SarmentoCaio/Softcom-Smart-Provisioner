namespace SoftcomSmartProvisioner.Models;

public sealed record OAuthClientInfo(
    string ClientId,
    string Name,
    string DeviceId,
    long? EmpresaId,
    string? CreatedAt,
    string? UpdatedAt,
    string? PreviousDeviceId,
    string? TipoDispositivo)
{
    public bool IsLinked => !string.IsNullOrWhiteSpace(DeviceId);
}
