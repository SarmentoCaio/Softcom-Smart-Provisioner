using System.Text.Json;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class SettingsService
{
    private readonly string _filePath;

    public SettingsService(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        AppSettings settings;
        if (!File.Exists(_filePath))
        {
            settings = new AppSettings();
        }
        else
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath))
                    ?? new AppSettings();
            }
            catch
            {
                settings = new AppSettings();
            }
        }

        ApplyPackagedUpdateDefaults(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private static void ApplyPackagedUpdateDefaults(AppSettings settings)
    {
        try
        {
            var defaultsPath = Path.Combine(AppContext.BaseDirectory, "update-defaults.json");
            if (!File.Exists(defaultsPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(defaultsPath));
            var root = document.RootElement;

            if (string.IsNullOrWhiteSpace(settings.StableManifestUrl) &&
                root.TryGetProperty("stableManifestUrl", out var stable) &&
                stable.ValueKind == JsonValueKind.String)
            {
                settings.StableManifestUrl = stable.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(settings.BetaManifestUrl) &&
                root.TryGetProperty("betaManifestUrl", out var beta) &&
                beta.ValueKind == JsonValueKind.String)
            {
                settings.BetaManifestUrl = beta.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Defaults empacotados sao opcionais; falha neles nao deve impedir o Provisioner.
        }
    }
}
