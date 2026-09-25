using System.Net.Http.Headers;
using System.Text.Json;

namespace SoftcomSmartProvisioner.SelfHostBridge;

public static class SelfHostValidator
{
    public static ConfigurationValidation Validate(IConfigurationDocument config, string? localBaseUrl, string generation)
    {
        var port = int.TryParse(config.Get("PortaHTTP")?.ToString(), out var parsedPort) ? parsedPort : 7711;
        var configuredUrl = $"http://127.0.0.1:{port}";
        var baseUrl = string.IsNullOrWhiteSpace(localBaseUrl) ? configuredUrl : NormalizeLocalUrl(localBaseUrl, port);
        var clientId = config.Get("SoftcomShopClientId")?.ToString();
        var clientSecret = config.Get("SoftcomShopSecretId")?.ToString();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new BridgeValidationException("validation_credentials_missing", "A configuração não possui credenciais raiz para validar a API local.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        ExpectOk(http, baseUrl + "/api/v2/healthcheck", "healthcheck");
        ExpectOk(http, baseUrl + "/api/v2/healthcheck/info", "healthcheck_info");

        // No modo Desktop não existe dispositivo raiz do Softcomshop. A autenticação
        // local é feita com as chaves dos dispositivos gravados no banco Softshop,
        // portanto a configuração inicial consegue validar somente a subida da API.
        if (config.Get("TipoBancoDados")?.ToString()?.Contains("desktop", StringComparison.OrdinalIgnoreCase) == true)
            return new ConfigurationValidation(true, true, false, false, "desktop_healthchecks");

        if (string.Equals(generation, "SelfHost 4.0", StringComparison.Ordinal))
            return ValidateLegacyRootAgainstSoftcomshop(http, config, clientId, clientSecret);

        string token;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });
            using var response = http.PostAsync(baseUrl + "/authentication/token", form).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new BridgeValidationException("validation_authentication_failed", $"A autenticação local respondeu HTTP {(int)response.StatusCode}.");
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            token = ExtractToken(body)
                ?? throw new BridgeValidationException("validation_authentication_failed", "A autenticação local não retornou token reconhecido.");
        }
        catch (BridgeValidationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeValidationException("validation_authentication_failed", "Não foi possível autenticar na API local do SelfHost.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v2/empresa?gzip=true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                throw new BridgeValidationException("validation_company_failed", $"A validação da empresa respondeu HTTP {(int)response.StatusCode}.");
        }
        catch (BridgeValidationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeValidationException("validation_company_failed", "Não foi possível validar a empresa na API local do SelfHost.");
        }
        finally
        {
            token = string.Empty;
            clientSecret = string.Empty;
        }

        return new ConfigurationValidation(true, true, true, true, "complete");
    }

    private static ConfigurationValidation ValidateLegacyRootAgainstSoftcomshop(
        HttpClient http,
        IConfigurationDocument config,
        string clientId,
        string clientSecret)
    {
        var softcomshopBaseUrl = config.Get("SoftcomShopUrlBase")?.ToString()?.Trim().TrimEnd('/');
        if (!Uri.TryCreate(softcomshopBaseUrl, UriKind.Absolute, out var softcomshopUri) ||
            (softcomshopUri.Scheme != Uri.UriSchemeHttp && softcomshopUri.Scheme != Uri.UriSchemeHttps))
            throw new BridgeValidationException("validation_softcomshop_url_failed", "A URL base do Softcomshop configurada é inválida.");

        string token;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });
            using var response = http.PostAsync(softcomshopUri.AbsoluteUri.TrimEnd('/') + "/softauth/authentication/token", form).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new BridgeValidationException("validation_authentication_failed", $"A autenticação do dispositivo raiz respondeu HTTP {(int)response.StatusCode}.");
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            token = ExtractToken(body)
                ?? throw new BridgeValidationException("validation_authentication_failed", "O Softcomshop não autenticou o dispositivo raiz.");
        }
        catch (BridgeValidationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeValidationException("validation_authentication_failed", "Não foi possível autenticar o dispositivo raiz no Softcomshop.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, softcomshopUri.AbsoluteUri.TrimEnd('/') + "/softauth/api/empresa");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                throw new BridgeValidationException("validation_company_failed", $"A validação da empresa respondeu HTTP {(int)response.StatusCode}.");
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!IsSuccessfulSoftcomshopPayload(body))
                throw new BridgeValidationException("validation_company_failed", "O Softcomshop não confirmou a empresa do dispositivo raiz.");
        }
        catch (BridgeValidationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeValidationException("validation_company_failed", "Não foi possível validar a empresa do dispositivo raiz.");
        }
        finally
        {
            token = string.Empty;
            clientSecret = string.Empty;
        }

        return new ConfigurationValidation(true, true, true, true, "complete");
    }

    private static void ExpectOk(HttpClient http, string url, string stage)
    {
        try
        {
            using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new BridgeValidationException("validation_" + stage + "_failed", $"O endpoint {stage} respondeu HTTP {(int)response.StatusCode}.");
        }
        catch (BridgeValidationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeValidationException("validation_" + stage + "_failed", $"Não foi possível acessar o endpoint {stage} da API local.");
        }
    }

    private static string NormalizeLocalUrl(string value, int configuredPort)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new BridgeValidationException("invalid_local_url", "A URL local do SelfHost é inválida.");
        if (!uri.IsLoopback && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new BridgeValidationException("invalid_local_url", "A validação de configuração aceita somente a API local do SelfHost.");
        var builder = new UriBuilder(uri) { Port = uri.IsDefaultPort ? configuredPort : uri.Port, Path = string.Empty, Query = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    private static string? ExtractToken(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindToken(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static bool IsSuccessfulSoftcomshopPayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("code", out var code)) return false;
            return code.ToString() == "1";
        }
        catch (JsonException) { return false; }
    }

    private static string? FindToken(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if ((property.NameEquals("token") || property.NameEquals("access_token")) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
            if (property.Value.ValueKind == JsonValueKind.Object && FindToken(property.Value) is { } nested) return nested;
        }
        return null;
    }
}
