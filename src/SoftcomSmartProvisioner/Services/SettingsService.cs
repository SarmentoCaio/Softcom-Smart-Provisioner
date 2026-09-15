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

        NormalizeRecentOnlineClients(settings);
        NormalizeConfirmedSmartDeviceIds(settings);
        ApplyPackagedUpdateDefaults(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private static void NormalizeRecentOnlineClients(AppSettings settings)
    {
        settings.RecentOnlineClients ??= new List<string>();

        var normalized = settings.RecentOnlineClients
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(EnvironmentCatalog.NormalizeDatabaseName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        if (!string.IsNullOrWhiteSpace(settings.LastOnlineClient))
        {
            var last = EnvironmentCatalog.NormalizeDatabaseName(settings.LastOnlineClient);
            normalized.RemoveAll(x => x.Equals(last, StringComparison.OrdinalIgnoreCase));
            normalized.Insert(0, last);
        }

        settings.RecentOnlineClients = normalized.Take(30).ToList();
    }

    private static void NormalizeConfirmedSmartDeviceIds(AppSettings settings)
    {
        settings.ConfirmedSmartDeviceIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        settings.ConfirmedSmartDeviceIds = settings.ConfirmedSmartDeviceIds
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && IsSafeDeviceId(x.Value))
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Last().Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSafeDeviceId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.');

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
