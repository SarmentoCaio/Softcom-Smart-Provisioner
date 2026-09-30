namespace SoftcomSmartProvisioner.Models;

public sealed record SmartPrinterProfile(
    string Module,
    string ProvisioningProfile,
    string Description,
    string Driver,
    string Type,
    string Address = "",
    int Id = 1)
{
    public string DisplayName => $"{Description} ({Driver})";
}

public sealed record SmartPrinterConfigurationResult(
    bool Success,
    string Stage,
    string Message,
    SmartPrinterProfile? Profile = null)
{
    public static SmartPrinterConfigurationResult Failed(string stage, string message, SmartPrinterProfile? profile = null) =>
        new(false, stage, message, profile);

    public static SmartPrinterConfigurationResult Completed(string message, SmartPrinterProfile profile) =>
        new(true, "printer-config", message, profile);
}
