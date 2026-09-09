using System.Text.RegularExpressions;

namespace SoftcomSmartProvisioner.Services;

public static class LegacySecretsImportService
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        SecretStore.DatabaseUsername,
        SecretStore.DatabasePassword,
        SecretStore.VpnUsername,
        SecretStore.VpnPassword,
        SecretStore.ApiClientId,
        SecretStore.ApiClientSecret
    };

    private static readonly Regex AssignmentRegex = new(
        @"^\s*(?<key>[A-Z0-9_]+)\s*=\s*(?<quote>['""])(?<value>.*)\k<quote>\s*$",
        RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, string> Read(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo de configuracao do Recuperador nao encontrado.", filePath);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(filePath))
        {
            var match = AssignmentRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value;
            if (!AllowedKeys.Contains(key))
            {
                continue;
            }

            result[key] = match.Groups["value"].Value;
        }

        if (!result.ContainsKey(SecretStore.DatabaseUsername) ||
            !result.ContainsKey(SecretStore.DatabasePassword))
        {
            throw new InvalidOperationException(
                "O arquivo foi lido, mas as credenciais do banco nao foram localizadas.");
        }

        return result;
    }
}
