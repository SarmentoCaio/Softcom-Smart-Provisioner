using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public static class ProvisioningService
{
    public static string BuildDeviceUrl(
        string database,
        CompanyInfo company,
        OAuthClientInfo oauthClient)
    {
        var siteUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var pairs = new[]
        {
            ("client_id", oauthClient.ClientId),
            ("empresa_name", company.Name),
            ("empresa_cnpj", company.Cnpj),
            ("device_name", oauthClient.Name)
        };

        var query = string.Join(
            "&",
            pairs.Select(pair =>
                $"{Uri.EscapeDataString(pair.Item1)}={Uri.EscapeDataString(pair.Item2 ?? string.Empty)}"));

        return $"{siteUrl}/softauth/device/add?{query}";
    }

    public static object EvaluateLink(
        OAuthClientInfo oauthClient,
        DeviceInfo? androidDevice)
    {
        if (androidDevice is null)
        {
            return new
            {
                status = "warning",
                title = "Selecione um Android",
                detail = "Ainda nao foi escolhido um dispositivo ADB."
            };
        }

        if (string.IsNullOrWhiteSpace(oauthClient.DeviceId))
        {
            return new
            {
                status = "ready",
                title = "Cadastro disponivel",
                detail = $"O dispositivo WEB esta sem vinculo. Android ADB selecionado: {androidDevice.Serial}."
            };
        }

        return new
        {
            status = "ready",
            title = "Cadastro sera reutilizado",
            detail = $"O dispositivo WEB possui device_id {oauthClient.DeviceId}. Antes do novo vinculo, o Provisioner ira localizar e remover automaticamente o vinculo anterior em oauth_clients."
        };
    }
}
