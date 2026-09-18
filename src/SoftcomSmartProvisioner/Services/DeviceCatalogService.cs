using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class DeviceCatalogService
{
    private static readonly IReadOnlyDictionary<string, (string Acquirer, string Model)> KnownVariables =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["CIELO_DX8000_UDID"] = ("Cielo", "DX8000"),
            ["REDE_L400_UDID"] = ("Rede", "L400"),
            ["REDE_N960K_UDID"] = ("Rede", "N960K"),
            ["GETNET_DX8000_UDID"] = ("Getnet", "DX8000"),
            ["GETNET_P2_UDID"] = ("Getnet", "P2"),
            ["GETNET_P3_UDID"] = ("Getnet", "P3"),
            ["STONE_UDID"] = ("Stone", ""),
            ["PAGBANK_A7_1_UDID"] = ("PagBank", "A7"),
            ["PAGBANK_A11_UDID"] = ("PagBank", "A11"),
            ["PAGBANK_G780S_UDID"] = ("PagBank", "G780S"),
            ["FISERV_UDID"] = ("Fiserv", ""),
            ["SIPAG_P2_UDID"] = ("Sipag", "P2"),
            ["SIPAG_X990_UDID"] = ("Sipag", "X990"),
            ["SIPAG_DX8000_UDID"] = ("Sipag", "DX8000"),
            ["SAFRA_UDID"] = ("Safra", ""),
            ["MERCADOPAGO_UDID"] = ("Mercado Pago", "N950"),
            ["QUICKPAY_A910_UDID"] = ("QuickPay", "A910"),
            ["CLOVER_UDID"] = ("Clover", ""),
            ["DEFAULT_UDID"] = ("Emulador", ""),
            ["TOTEM_K2_UDID"] = ("Totem", "K2")
        };

    private readonly string? _explicitPath;

    public DeviceCatalogService(string? explicitPath = null) => _explicitPath = explicitPath;

    public DeviceCatalogSnapshot Load()
    {
        var path = ResolveCatalogPath();
        return path is null
            ? new DeviceCatalogSnapshot(null, Array.Empty<DeviceCatalogEntry>(), KnownVariables.Keys.Order().ToArray(),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase))
            : Parse(File.ReadLines(path), path);
    }

    public static DeviceCatalogSnapshot Parse(IEnumerable<string> lines, string? sourcePath = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)) line = line[7..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            if (!name.EndsWith("_UDID", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Unquote(line[(separator + 1)..].Trim());
            values[name] = value;
        }

        var entries = new List<DeviceCatalogEntry>();
        var empty = new List<string>();
        foreach (var mapping in KnownVariables)
        {
            if (!values.TryGetValue(mapping.Key, out var serial) ||
                string.IsNullOrWhiteSpace(serial) ||
                IsPlaceholder(serial))
            {
                empty.Add(mapping.Key);
                continue;
            }

            var (acquirer, model) = mapping.Value;
            var friendly = string.IsNullOrWhiteSpace(model) ? acquirer : $"{acquirer} - {model}";
            entries.Add(new DeviceCatalogEntry(mapping.Key, serial.Trim(), acquirer, model, friendly, ToProfileKey(acquirer, model)));
        }

        var duplicates = entries
            .GroupBy(x => x.Serial, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.VariableName).Order().ToArray(), StringComparer.OrdinalIgnoreCase);

        return new DeviceCatalogSnapshot(sourcePath, entries, empty.Order().ToArray(), duplicates);
    }

    public static DeviceIdentity Identify(string serial, DeviceCatalogSnapshot snapshot, string? androidModel = null)
    {
        if (string.IsNullOrWhiteSpace(serial)) return DeviceIdentity.Unknown(string.Empty);
        var matches = snapshot.Entries.Where(x => string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return DeviceIdentity.Unknown(serial);
        if (matches.Length > 1)
        {
            var normalizedAndroidModel = NormalizeModel(androidModel);
            var modelMatches = string.IsNullOrWhiteSpace(normalizedAndroidModel)
                ? Array.Empty<DeviceCatalogEntry>()
                : matches.Where(x => NormalizeModel(x.TerminalModel) == normalizedAndroidModel).ToArray();
            if (modelMatches.Length == 1)
            {
                var resolved = modelMatches[0];
                return new DeviceIdentity(serial, resolved.Acquirer, resolved.TerminalModel, resolved.FriendlyName,
                    resolved.ProvisioningProfile, true, false,
                    "UDID duplicado resolvido pelo modelo Android", new[] { resolved.VariableName });
            }

            return new DeviceIdentity(serial, string.Empty, string.Empty, "Dispositivo não identificado", "default", false, true,
                "UDID duplicado no catálogo [VALIDAR]", matches.Select(x => x.VariableName).Order().ToArray());
        }

        var item = matches[0];
        return new DeviceIdentity(serial, item.Acquirer, item.TerminalModel, item.FriendlyName, item.ProvisioningProfile, true, false,
            "Identificado pelo catálogo de UDIDs", new[] { item.VariableName });
    }

    public string? ResolveCatalogPath()
    {
        var configured = Environment.GetEnvironmentVariable("SMART_PROVISIONER_DEVICE_ENV");
        var candidates = new List<string?>
        {
            _explicitPath,
            configured,
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "softcom-smart-automation", ".env"))
        };
        AddSiblingCandidates(candidates, Directory.GetCurrentDirectory());
        AddSiblingCandidates(candidates, AppContext.BaseDirectory);
        return candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetFullPath(x!)).FirstOrDefault(File.Exists);
    }

    private static void AddSiblingCandidates(ICollection<string?> candidates, string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        for (var level = 0; directory is not null && level < 9; level++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "softcom-smart-automation", ".env"));
        }
    }

    private static string Unquote(string value) => value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static bool IsPlaceholder(string value)
    {
        var normalized = value.Trim();
        return normalized.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("SEU_", StringComparison.OrdinalIgnoreCase) ||
               (normalized.StartsWith('<') && normalized.EndsWith('>'));
    }

    private static string ToProfileKey(string acquirer, string model) =>
        string.Concat((acquirer + model).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string NormalizeModel(string? model) =>
        string.Concat((model ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
