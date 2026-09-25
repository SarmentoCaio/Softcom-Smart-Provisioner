using System.Collections.Specialized;
using System.Web;

namespace SoftcomSmartProvisioner.SelfHostBridge;

public sealed record RootDeviceUrl(
    string FullUrl,
    string BaseUrl,
    string ClientId,
    string DeviceName,
    string CompanyName)
{
    public static RootDeviceUrl Parse(string? value)
    {
        var full = (value ?? string.Empty).Trim();
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BridgeValidationException("invalid_root_device_url", "A URL do dispositivo raiz é inválida.");
        }

        var softauthIndex = full.IndexOf("/softauth", StringComparison.OrdinalIgnoreCase);
        if (softauthIndex <= 0)
            throw new BridgeValidationException("invalid_root_device_url", "A URL não pertence ao fluxo /softauth do Softcomshop.");

        var baseUrlText = full[..softauthIndex].TrimEnd('/');
        if (!Uri.TryCreate(baseUrlText, UriKind.Absolute, out var baseUri))
            throw new BridgeValidationException("invalid_root_device_url", "A URL base do Softcomshop é inválida.");

        NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
        var clientId = (query["client_id"] ?? string.Empty).Trim();
        var deviceName = (query["device_name"] ?? string.Empty).Trim();
        var companyName = (query["empresa_name"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(companyName))
            throw new BridgeValidationException("invalid_root_device_url", "A URL não contém client_id, device_name e empresa_name.");
        if (deviceName.StartsWith("SELFHOST_", StringComparison.OrdinalIgnoreCase))
            throw new BridgeValidationException("invalid_root_device", "Um dispositivo filho SELFHOST_ não pode configurar o próprio SelfHost.");

        return new RootDeviceUrl(full, baseUri.AbsoluteUri.TrimEnd('/'), clientId, deviceName, companyName);
    }
}
