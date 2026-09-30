using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class SmartPrinterProfileService
{
    private static readonly IReadOnlyList<SmartPrinterProfile> Profiles =
    [
        new("smart_comanda", "getnetp3", "P3", "GETNET", "BLUETOOTH"),
        new("smart_autopagamento", "totemk2", "K2", "K2_MINI", "BLUETOOTH"),
        new("smart_totem", "totemk2", "K2", "K2_MINI", "BLUETOOTH")
    ];

    public IReadOnlyList<SmartPrinterProfile> GetAll() => Profiles;

    public SmartPrinterProfile? Resolve(string? module, string? provisioningProfile) =>
        Profiles.FirstOrDefault(x =>
            string.Equals(x.Module, module?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ProvisioningProfile, provisioningProfile?.Trim(), StringComparison.OrdinalIgnoreCase));
}
