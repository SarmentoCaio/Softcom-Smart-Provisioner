namespace SoftcomSmartProvisioner.Models;

public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string Notes { get; set; } = string.Empty;
}
