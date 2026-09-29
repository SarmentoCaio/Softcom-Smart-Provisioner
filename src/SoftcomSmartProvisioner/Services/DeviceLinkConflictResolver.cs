using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public enum DeviceRegistrySource
{
    Softcomshop,
    SelfHost
}

public sealed record DeviceRegistryEntry(
    OAuthClientInfo Device,
    DeviceRegistrySource Source);

/// <summary>
/// Cruza os cadastros WEB do Softcomshop com os cadastros expostos pela API
/// administrativa do SelfHost. Um mesmo client_id pode aparecer nas duas
/// fontes; nesse caso a API administrativa e a autoridade preferida para a
/// desvinculacao e ambas as fontes continuam sendo relidas para confirmacao.
/// </summary>
public static class DeviceLinkConflictResolver
{
    public static IReadOnlyList<DeviceRegistryEntry> FindConflicts(
        IEnumerable<DeviceRegistryEntry> entries,
        string? selectedClientId,
        params string?[] deviceIds)
    {
        var ids = deviceIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entries
            .Where(x => x.Device.IsLinked &&
                        ((!string.IsNullOrWhiteSpace(selectedClientId) &&
                          string.Equals(x.Device.ClientId, selectedClientId, StringComparison.Ordinal)) ||
                         ids.Contains(x.Device.DeviceId.Trim())))
            .GroupBy(x => x.Device.ClientId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(x => x.Source == DeviceRegistrySource.SelfHost)
                .First())
            .OrderBy(x => x.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static string SourceName(DeviceRegistrySource source) =>
        source == DeviceRegistrySource.SelfHost
            ? "SelfHost"
            : "Softcomshop";
}
