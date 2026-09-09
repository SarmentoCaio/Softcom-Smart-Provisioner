namespace SoftcomSmartProvisioner.Models;

public sealed class AppSettings
{
    public string VpnProfilePath { get; set; } = string.Empty;
    public string SmartPackageName { get; set; } = string.Empty;
    public string LastEnvironment { get; set; } = "aws1";
    public string LastDatabase { get; set; } = string.Empty;
    public long? LastCompanyId { get; set; }
    public string AccessMode { get; set; } = "online";
    public string LastOnlineClient { get; set; } = string.Empty;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable";
    public string StableManifestUrl { get; set; } = string.Empty;
    public string BetaManifestUrl { get; set; } = string.Empty;
}
