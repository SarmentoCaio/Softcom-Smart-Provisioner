using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public static class SelfHostClientMatcher
{
    public static bool Matches(SelfHostConfigurationSummary? configuration, string database)
    {
        if (configuration is null) return false;
        if (IsDesktop(configuration)) return true;

        var configuredHost = GetConfiguredHost(configuration);
        var expectedHost = new Uri(EnvironmentCatalog.BuildSiteUrl(database)).Host;
        return configuredHost.Length > 0 &&
               configuredHost.Equals(expectedHost, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDesktop(SelfHostConfigurationSummary? configuration) =>
        configuration?.TipoBancoDados?.Contains("desktop", StringComparison.OrdinalIgnoreCase) == true;

    public static string GetConfiguredClient(SelfHostConfigurationSummary? configuration)
    {
        var host = GetConfiguredHost(configuration);
        const string suffix = ".meusoftcom.com.br";
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? host[..^suffix.Length]
            : host;
    }

    public static string DescribeMismatch(SelfHostConfigurationSummary? configuration, string database)
    {
        var configured = GetConfiguredClient(configuration);
        if (string.IsNullOrWhiteSpace(configured)) configured = "um cliente não identificado";
        var selected = EnvironmentCatalog.DatabaseDisplayName(database);
        return $"O SelfHost está configurado para o cliente {configured}, mas o cliente selecionado é {selected}. " +
               $"Reconfigure o SelfHost para {selected} antes de listar ou provisionar dispositivos SelfHost.";
    }

    private static string GetConfiguredHost(SelfHostConfigurationSummary? configuration)
    {
        var value = configuration?.SoftcomShopUrlBase?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.Host.TrimEnd('.')
            : string.Empty;
    }
}
