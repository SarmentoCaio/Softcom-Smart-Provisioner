using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public static class EnvironmentCatalog
{
    public const string DatabasePrefix = "softcoms_softcomshop_";

    public static readonly IReadOnlyDictionary<string, EnvironmentInfo> Environments =
        new Dictionary<string, EnvironmentInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["aws1"] = new(
                "aws1",
                "AWS 1",
                "softcomdb-mysql-hml.cluster-cyv0220iwox9.us-east-1.rds.amazonaws.com"),
            ["aws2"] = new(
                "aws2",
                "AWS 2",
                "softcomdb-mysql-prd-2-instance-1.cyv0220iwox9.us-east-1.rds.amazonaws.com")
        };

    public static EnvironmentInfo Get(string key)
    {
        if (!Environments.TryGetValue(key ?? string.Empty, out var environment))
        {
            throw new ArgumentException("Ambiente AWS invalido.");
        }

        return environment;
    }

    public static string NormalizeDatabaseName(string databaseName)
    {
        var value = (databaseName ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("Informe o nome do cliente ou banco.");
        }

        return value.StartsWith(DatabasePrefix, StringComparison.OrdinalIgnoreCase)
            ? value
            : DatabasePrefix + value;
    }

    public static string DatabaseSlug(string databaseName)
    {
        var value = NormalizeDatabaseName(databaseName);
        var slug = value[DatabasePrefix.Length..].Trim();
        if (slug.Length == 0)
        {
            throw new ArgumentException("Nao foi possivel identificar o cliente pelo nome do banco.");
        }

        return slug;
    }

    public static string DatabaseDisplayName(string databaseName) => DatabaseSlug(databaseName);

    public static string BuildSiteUrl(string databaseName) =>
        $"https://{DatabaseSlug(databaseName)}.meusoftcom.com.br";
}
