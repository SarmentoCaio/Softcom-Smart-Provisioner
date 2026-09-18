namespace SoftcomSmartProvisioner.Models;

public sealed record DeviceInfo(
    string Serial,
    string State,
    string Model,
    string Product,
    string Transport,
    string AndroidVersion,
    int? Battery,
    long? LatencyMs,
    string AndroidId,
    string SmartVersion,
    string SmartFlow)
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
    public string ConfirmedSmartDeviceId { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string AndroidSdk { get; init; } = string.Empty;
    public string Acquirer { get; init; } = string.Empty;
    public string TerminalModel { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = "Dispositivo não identificado";
    public bool IsKnownDevice { get; init; }
    public bool IsAmbiguousIdentity { get; init; }
    public string IdentificationStatus { get; init; } = "Não identificado";
    public string Resolution { get; init; } = string.Empty;
    public string Density { get; init; } = string.Empty;
    public string SmartPackage { get; init; } = string.Empty;
    public IReadOnlyList<string> SmartPackageCandidates { get; init; } = Array.Empty<string>();
    public string CurrentActivity { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> RelevantPermissions { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string ProvisioningProfile { get; init; } = "default";
}
