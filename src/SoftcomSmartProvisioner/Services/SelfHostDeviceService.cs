using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

/// <summary>
/// Integração de dispositivos usados pelo SelfHost. O SelfHost instalado mantém um
/// dispositivo raiz do Softcomshop em Config2.json. Essas credenciais são usadas pelo
/// próprio SelfHost para consultar os dispositivos NFC-e/Smart vinculados ao serviço.
/// O Provisioner apenas lê a configuração existente e consome os endpoints já usados
/// pelo SelfHost; não altera a configuração administrativa do aplicativo.
/// </summary>
public sealed class SelfHostDeviceService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Action<string>? _log;

    public SelfHostDeviceService(Action<string>? log = null) => _log = log;

    public sealed record SelfHostRootConfig(
        string SoftcomshopBaseUrl,
        string RootClientId,
        string RootClientSecret,
        string CompanyName,
        string CompanyCnpj,
        int Port);

    public SelfHostRootConfig LoadInstalledConfig()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Softcom Tecnologia", "SelfHost", "Config2.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Softcom Tecnologia", "SelfHost", "Config2.json")
        };

        var file = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("SelfHost instalado não foi localizado. Esperado em Program Files (x86)\\Softcom Tecnologia\\SelfHost\\Config2.json.");

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var r = doc.RootElement;
        static string Get(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? (p.GetString() ?? string.Empty).Trim() : string.Empty;

        var baseUrl = Get(r, "SoftcomShopUrlBase").TrimEnd('/');
        var clientId = Get(r, "SoftcomShopClientId");
        var secret = Get(r, "SoftcomShopSecretId");
        var company = Get(r, "SoftcomShopEmpresa");
        var cnpj = Get(r, "SoftcomShopEmpresaCnpj");
        var port = r.TryGetProperty("PortaHTTP", out var portEl) && portEl.TryGetInt32(out var v) ? v : 7711;

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("O SelfHost está instalado, mas ainda não possui Softcomshop/client_id/client_secret configurados em Config2.json.");

        return new SelfHostRootConfig(baseUrl, clientId, secret, company, cnpj, port);
    }

    private async Task<string> GetTokenAsync(SelfHostRootConfig cfg, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = cfg.RootClientId,
            ["client_secret"] = cfg.RootClientSecret
        });
        using var res = await _http.PostAsync(cfg.SoftcomshopBaseUrl + "/softauth/authentication/token", form, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"SelfHost não conseguiu autenticar o dispositivo raiz no Softcomshop (HTTP {(int)res.StatusCode}).");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("token", out var t))
            return t.GetString() ?? throw new InvalidOperationException("Token vazio retornado pelo Softcomshop.");
        if (root.TryGetProperty("token", out var t2)) return t2.GetString() ?? string.Empty;
        throw new InvalidOperationException("Resposta de autenticação do SelfHost não contém token reconhecido.");
    }


    public async Task<OAuthClientInfo> CreateDeviceAsync(string name, string serie, string initialNumber, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        serie = (serie ?? string.Empty).Trim();
        initialNumber = (initialNumber ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Informe o nome do dispositivo SelfHost.", nameof(name));
        if (!int.TryParse(serie, out var serieValue) || serieValue < 0)
            throw new ArgumentException("Informe uma série NFC-e válida.", nameof(serie));
        if (!int.TryParse(initialNumber, out var numberValue) || numberValue < 1)
            throw new ArgumentException("Informe um próximo número NFC-e válido.", nameof(initialNumber));

        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);

        // Captura validada do Selfhost.Gerenciador: o cadastro filho usa um GUID próprio
        // como device_id e recebe explicitamente serie e numeracao_inicial da NFC-e.
        var generatedDeviceId = Guid.NewGuid().ToString();
        var body = JsonSerializer.Serialize(new
        {
            device_id = generatedDeviceId,
            nome_dispositivo = name,
            serie = serieValue.ToString(),
            numeracao_inicial = numberValue.ToString()
        });

        using var req = new HttpRequestMessage(HttpMethod.Post,
            cfg.SoftcomshopBaseUrl + "/softauth/api/nfenfce/nfce/dispositivo");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        var responseBody = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao criar dispositivo no SelfHost (HTTP {(int)res.StatusCode}). {ExtractHumanMessage(responseBody)}".Trim());

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
            ? dataEl
            : root;

        var clientId = First(data, "client_id", "clientId");
        var deviceName = First(data, "device_name", "nome_dispositivo", "name");
        var deviceId = First(data, "device_id", "deviceId");
        long? empresaId = null;
        if (data.TryGetProperty("empresa_id", out var empresaEl))
        {
            if (empresaEl.ValueKind == JsonValueKind.Number && empresaEl.TryGetInt64(out var ev)) empresaId = ev;
            else if (long.TryParse(empresaEl.ToString(), out var parsed)) empresaId = parsed;
        }

        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("O SelfHost criou o dispositivo, mas a resposta não retornou client_id.");

        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = name;
        _log?.Invoke($"Dispositivo SelfHost {deviceName} criado com NFC-e série {serieValue}, próximo número {numberValue}.");
        return new OAuthClientInfo(clientId, deviceName, deviceId, empresaId, null, null, null, "SELFHOST");
    }

    private static string ExtractHumanMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            return First(r, "human", "message", "error");
        }
        catch
        {
            return body.Length <= 300 ? body : body[..300];
        }
    }

    public async Task<IReadOnlyList<OAuthClientInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);
        var all = new Dictionary<string, OAuthClientInfo>(StringComparer.Ordinal);

        for (var page = 1; page <= 50; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                cfg.SoftcomshopBaseUrl + $"/softauth/api/nfenfce/nfce/dispositivo?page={page}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Falha ao listar dispositivos do SelfHost (HTTP {(int)res.StatusCode}).");

            using var doc = JsonDocument.Parse(body);
            var items = ExtractDeviceObjects(doc.RootElement).ToArray();
            var before = all.Count;
            foreach (var x in items)
            {
                if (!string.IsNullOrWhiteSpace(x.ClientId)) all[x.ClientId] = x;
            }
            if (items.Length == 0 || all.Count == before || items.Length < 20) break;
        }

        _log?.Invoke($"{all.Count} dispositivo(s) SelfHost localizado(s) pela API do Softcomshop.");
        return all.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static IEnumerable<JsonElement> ExtractRawDeviceObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();
        if (root.ValueKind == JsonValueKind.Object && TryArray(root, "data", out var dataArray))
            return dataArray.EnumerateArray().ToArray();
        if (root.ValueKind == JsonValueKind.Object && TryArray(root, "items", out var itemsArray))
            return itemsArray.EnumerateArray().ToArray();
        if (root.ValueKind == JsonValueKind.Object && TryArray(root, "dispositivos", out var devicesArray))
            return devicesArray.EnumerateArray().ToArray();
        return Array.Empty<JsonElement>();
    }

    private static IEnumerable<OAuthClientInfo> ExtractDeviceObjects(JsonElement root)
    {
        foreach (var n in ExtractRawDeviceObjects(root))
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            var clientId = First(n, "client_id", "clientId", "ClientId", "oauth_client_id");
            var name = First(n, "device_name", "deviceName", "name", "nome", "nome_dispositivo", "DeviceName");
            var deviceId = First(n, "device_id", "deviceId", "DeviceId");
            if (string.IsNullOrWhiteSpace(clientId)) continue;
            if (string.IsNullOrWhiteSpace(name)) name = clientId;
            yield return new OAuthClientInfo(clientId, name, deviceId, null, null, null, null, "SELFHOST");
        }
    }

    private static bool TryArray(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array) return true;
        value = default;
        return false;
    }

    private static string First(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var p)) continue;
            if (p.ValueKind == JsonValueKind.String) return p.GetString()?.Trim() ?? string.Empty;
            if (p.ValueKind == JsonValueKind.Number || p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False) return p.ToString();
        }
        return string.Empty;
    }


    public async Task<bool> UnlinkDeviceAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("client_id do dispositivo SelfHost não informado.", nameof(clientId));

        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);
        var endpoint = cfg.SoftcomshopBaseUrl + "/softauth/api/nfenfce/nfce/dispositivo/desvincular";

        // O SelfHost possui versões que enviam o client_id em formatos diferentes.
        // Tentamos as formas conhecidas, sempre parando na primeira resposta de sucesso.
        var attempts = new Func<HttpRequestMessage>[]
        {
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Content = new StringContent(JsonSerializer.Serialize(new { client_id = clientId }), Encoding.UTF8, "application/json");
                return req;
            },
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId });
                return req;
            },
            () => new HttpRequestMessage(HttpMethod.Post, endpoint + "?client_id=" + Uri.EscapeDataString(clientId))
        };

        HttpStatusCode? lastStatus = null;
        string lastBody = string.Empty;
        foreach (var factory in attempts)
        {
            using var req = factory();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await _http.SendAsync(req, ct);
            lastStatus = res.StatusCode;
            lastBody = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode)
            {
                _log?.Invoke($"Solicitação de desvinculação enviada para o dispositivo SelfHost {clientId}.");

                // Confirma pela listagem real, evitando considerar apenas HTTP 2xx como sucesso.
                for (var i = 0; i < 8; i++)
                {
                    await Task.Delay(350, ct);
                    var items = await ListDevicesAsync(ct);
                    var current = items.FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.Ordinal));
                    if (current is null || string.IsNullOrWhiteSpace(current.DeviceId))
                    {
                        _log?.Invoke($"Dispositivo SelfHost {clientId} confirmado como desvinculado.");
                        return true;
                    }
                }

                _log?.Invoke($"O endpoint respondeu com sucesso, mas o dispositivo SelfHost {clientId} ainda possui device_id.");
                return false;
            }

            // 400/405/415/422 podem indicar apenas diferença no formato aceito pela versão instalada.
            if ((int)res.StatusCode is not (400 or 405 or 415 or 422))
                break;
        }

        _log?.Invoke($"Falha ao desvincular dispositivo SelfHost {clientId} (HTTP {(int?)lastStatus ?? 0}). {lastBody}".Trim());
        return false;
    }

    public string BuildUrl(OAuthClientInfo device, CompanyInfo company, string? requestedBaseUrl = null)
    {
        var cfg = LoadInstalledConfig();
        var baseUrl = string.IsNullOrWhiteSpace(requestedBaseUrl)
            ? $"http://127.0.0.1:{cfg.Port}"
            : requestedBaseUrl.Trim().TrimEnd('/');
        return $"{baseUrl}/device/add?client_id={Uri.EscapeDataString(device.ClientId)}&empresa_name={Uri.EscapeDataString(company.Name)}&empresa_cnpj={Uri.EscapeDataString(company.Cnpj ?? string.Empty)}&device_name={Uri.EscapeDataString(device.Name)}";
    }

    public void Dispose() => _http.Dispose();
}
