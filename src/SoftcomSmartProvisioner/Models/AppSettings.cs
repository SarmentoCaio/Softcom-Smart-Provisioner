namespace SoftcomSmartProvisioner.Models;

public sealed class AppSettings
{
    public string VpnProfilePath { get; set; } = string.Empty;
    public string SmartPackageName { get; set; } = string.Empty;
    public Dictionary<string, string> SmartPackageNamesBySerial { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LastEnvironment { get; set; } = "aws1";
    public string LastDatabase { get; set; } = string.Empty;
    public long? LastCompanyId { get; set; }
    public string AccessMode { get; set; } = "online";
    public string LastOnlineClient { get; set; } = string.Empty;
    public List<string> RecentOnlineClients { get; set; } = new();
    public Dictionary<string, string> ConfirmedSmartDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SmartTefDeviceName { get; set; } = "SMART 1";
    public string SmartTefCnpj { get; set; } = string.Empty;
    public string SmartTefEmpresaId { get; set; } = string.Empty;
    public bool SaveSmartTefConfiguration { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable";
    public string StableManifestUrl { get; set; } = string.Empty;
    public string BetaManifestUrl { get; set; } = string.Empty;
}
