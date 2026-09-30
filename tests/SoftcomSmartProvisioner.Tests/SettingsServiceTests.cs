using System.Text.Json;
using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private const string PublicManifestUrl =
        "https://github.com/SarmentoCaio/Softcom-Smart-Provisioner-Releases/releases/latest/download/latest.json";
    private const string LegacyPrivateManifestUrl =
        "https://raw.githubusercontent.com/SarmentoCaio/Softcom-Smart-Provisioner/refs/heads/main/update/latest.json";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "smart-provisioner-settings-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_UsesPackagedManifest_WhenSettingIsEmpty()
    {
        var (settingsDirectory, baseDirectory) = CreateDirectories();
        WriteDefaults(baseDirectory);

        var settings = new SettingsService(settingsDirectory, baseDirectory).Load();

        Assert.Equal(PublicManifestUrl, settings.StableManifestUrl);
    }

    [Fact]
    public void Load_MigratesLegacyPrivateManifest_ToPublicReleaseFeed()
    {
        var (settingsDirectory, baseDirectory) = CreateDirectories();
        WriteDefaults(baseDirectory);
        WriteSettings(settingsDirectory, new AppSettings
        {
            StableManifestUrl = LegacyPrivateManifestUrl
        });

        var settings = new SettingsService(settingsDirectory, baseDirectory).Load();

        Assert.Equal(PublicManifestUrl, settings.StableManifestUrl);
    }

    [Fact]
    public void Load_PreservesCustomManifestUrl()
    {
        const string customUrl = "https://updates.example.test/stable/latest.json";
        var (settingsDirectory, baseDirectory) = CreateDirectories();
        WriteDefaults(baseDirectory);
        WriteSettings(settingsDirectory, new AppSettings { StableManifestUrl = customUrl });

        var settings = new SettingsService(settingsDirectory, baseDirectory).Load();

        Assert.Equal(customUrl, settings.StableManifestUrl);
    }

    private (string SettingsDirectory, string BaseDirectory) CreateDirectories()
    {
        var settingsDirectory = Path.Combine(_root, "settings");
        var baseDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(settingsDirectory);
        Directory.CreateDirectory(baseDirectory);
        return (settingsDirectory, baseDirectory);
    }

    private static void WriteDefaults(string baseDirectory)
    {
        File.WriteAllText(
            Path.Combine(baseDirectory, "update-defaults.json"),
            JsonSerializer.Serialize(new
            {
                stableManifestUrl = PublicManifestUrl,
                betaManifestUrl = string.Empty
            }));
    }

    private static void WriteSettings(string settingsDirectory, AppSettings settings)
    {
        File.WriteAllText(
            Path.Combine(settingsDirectory, "settings.json"),
            JsonSerializer.Serialize(settings));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
