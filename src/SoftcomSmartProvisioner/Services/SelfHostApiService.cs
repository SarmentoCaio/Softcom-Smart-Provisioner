using System.Net.Http.Headers;
using System.Text.Json;

namespace SoftcomSmartProvisioner.Services;

/// <summary>
/// Cliente base para a SelfHost.API v2.
/// Esta primeira etapa cobre somente os endpoints documentados na collection oficial usada no projeto.
/// A configuracao administrativa do aplicativo SelfHost (banco, modulo Smart Comanda, servico etc.)
/// sera adicionada quando os endpoints locais correspondentes forem identificados; nao ha automacao visual aqui.
/// </summary>
public sealed class SelfHostApiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private string? _accessToken;

    public SelfHostApiService(string baseUrl, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Informe a URL base do SelfHost.", nameof(baseUrl));

        baseUrl = baseUrl.Trim().TrimEnd('/');
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_accessToken);

    public async Task AuthenticateAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Informe o client_id do dispositivo SelfHost.");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Informe o client_secret do dispositivo SelfHost.");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret
        });

        using var response = await _httpClient.PostAsync("authentication/token", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Falha ao autenticar no SelfHost (HTTP {(int)response.StatusCode}).");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        string? token = null;

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("token", out var dataToken))
        {
            token = dataToken.GetString();
        }
        else if (root.TryGetProperty("token", out var directToken))
        {
            token = directToken.GetString();
        }
        else if (root.TryGetProperty("access_token", out var accessToken))
        {
            token = accessToken.GetString();
        }

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("O SelfHost autenticou a requisicao, mas nao retornou um token reconhecido.");

        _accessToken = token;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<JsonDocument> GetRestaurantConfigurationAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        using var response = await _httpClient.GetAsync("api/v2/restaurantes/configuracao", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao consultar configuracao de restaurante do SelfHost (HTTP {(int)response.StatusCode}).");

        return JsonDocument.Parse(body);
    }

    public async Task<JsonDocument> GetCompanyAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        using var response = await _httpClient.GetAsync("api/v2/empresa?gzip=true", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao consultar empresa do SelfHost (HTTP {(int)response.StatusCode}).");

        return JsonDocument.Parse(body);
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("O SelfHost ainda nao foi autenticado.");
    }

    public void Dispose() => _httpClient.Dispose();
}
