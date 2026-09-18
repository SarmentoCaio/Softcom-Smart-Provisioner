namespace SoftcomSmartProvisioner.Models;

public sealed record DeviceCatalogEntry(
    string VariableName,
    string Serial,
    string Acquirer,
    string TerminalModel,
    string FriendlyName,
    string ProvisioningProfile);

public sealed record DeviceIdentity(
    string Serial,
    string Acquirer,
    string TerminalModel,
    string FriendlyName,
    string ProvisioningProfile,
    bool IsKnownDevice,
    bool IsAmbiguous,
    string Status,
    IReadOnlyList<string> MatchingVariables)
{
    public static DeviceIdentity Unknown(string serial, string status = "Dispositivo não identificado") =>
        new(serial, string.Empty, string.Empty, "Dispositivo não identificado", "default", false, false, status, Array.Empty<string>());
}

public sealed record DeviceCatalogSnapshot(
    string? SourcePath,
    IReadOnlyList<DeviceCatalogEntry> Entries,
    IReadOnlyList<string> EmptyVariables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DuplicateSerials);

public sealed record SmartApkDescriptor(string SmartVersion, string Acquirer, string? PackagePath);
