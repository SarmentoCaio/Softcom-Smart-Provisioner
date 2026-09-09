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
    string AndroidId)
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
}
