using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoftcomSmartProvisioner.Services;

public sealed class SecretStore
{
    public const string DatabaseUsername = "DATABASE_USERNAME";
    public const string DatabasePassword = "DATABASE_PASSWORD";
    public const string VpnUsername = "VPN_USERNAME";
    public const string VpnPassword = "VPN_PASSWORD";
    public const string ApiClientId = "API_CLIENT_ID";
    public const string ApiClientSecret = "API_CLIENT_SECRET";

    private readonly string _filePath;
    private Dictionary<string, string> _encrypted = new(StringComparer.OrdinalIgnoreCase);

    public SecretStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "secrets.dat");
        Load();
    }

    public bool HasDatabaseCredentials =>
        !string.IsNullOrWhiteSpace(Get(DatabaseUsername)) &&
        !string.IsNullOrWhiteSpace(Get(DatabasePassword));

    public bool HasVpnCredentials =>
        !string.IsNullOrWhiteSpace(Get(VpnUsername)) &&
        !string.IsNullOrWhiteSpace(Get(VpnPassword));

    public bool HasApiCredentials =>
        !string.IsNullOrWhiteSpace(Get(ApiClientId)) &&
        !string.IsNullOrWhiteSpace(Get(ApiClientSecret));

    public string Get(string key)
    {
        if (!_encrypted.TryGetValue(key, out var encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            return EmbeddedAppSecrets.Get(key);
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(encoded);
            var clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch
        {
            return EmbeddedAppSecrets.Get(key);
        }
    }

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _encrypted.Remove(key);
            Save();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(
            bytes,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        _encrypted[key] = Convert.ToBase64String(protectedBytes);
        Save();
    }

    public void SetMany(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(pair.Value);
            var protectedBytes = ProtectedData.Protect(
                bytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            _encrypted[pair.Key] = Convert.ToBase64String(protectedBytes);
        }

        Save();
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            _encrypted = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _encrypted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_encrypted, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json, Encoding.UTF8);
    }
}
