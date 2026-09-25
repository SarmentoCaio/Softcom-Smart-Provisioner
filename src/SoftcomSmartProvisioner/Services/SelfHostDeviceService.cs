using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

/// <summary>
/// Integração administrativa de dispositivos SelfHost. A versão 4.0 lê Config2.json; a
/// versão 4.1+ lê o selfhost-config.db pelo SQLCipher distribuído na própria instalação.
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

    public sealed record SelfHostInstallationInfo(
        bool Installed,
        string Version,
        string Generation,
        string InstallPath,
        bool HasLegacyConfig,
        bool HasModernConfig,
        string ConfigDescription);

    public SelfHostInstallationInfo DetectInstallation()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Softcom Tecnologia", "SelfHost"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Softcom Tecnologia", "SelfHost")
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        var installPath = roots.FirstOrDefault(root => File.Exists(Path.Combine(root, "Selfhost.exe")));
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return new SelfHostInstallationInfo(false, string.Empty, "Nao instalado", string.Empty, false, false, "SelfHost nao localizado.");
        }

        var exe = Path.Combine(installPath, "Selfhost.exe");
        var version = FileVersionInfo.GetVersionInfo(exe).FileVersion?.Trim() ?? string.Empty;
        var generation = ClassifySelfHostGeneration(version);
        var legacy = File.Exists(Path.Combine(installPath, "Config2.json"));
        var modernDb = File.Exists(Path.Combine(installPath, "data", "selfhost-config.db"));
        var modernSettings = File.Exists(Path.Combine(installPath, "binaries", "appsettings.json"));
        var modern = modernDb || modernSettings;

        var description = generation == "SelfHost 4.0"
            ? (legacy
                ? "Config.json / Config2.json (SelfHost 4.0)"
                : "Configuracao 4.0 ainda nao gerada (Config.json / Config2.json)")
            : (modern
                ? "selfhost-config.db / binaries\\appsettings.json (SelfHost 4.1+)"
                : "Configuracao 4.1+ nao localizada");

        return new SelfHostInstallationInfo(true, version, generation, installPath, legacy, modern, description);
    }

    public static string ClassifySelfHostGeneration(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return "Versao nao identificada";
        var match = System.Text.RegularExpressions.Regex.Match(versionText, @"\d+(?:\.\d+){1,3}");
        if (!match.Success) return "Versao nao reconhecida";

        var components = match.Value.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (components.Count < 4) components.Add("0");
        if (components.Count > 4) components = components.Take(4).ToList();

        if (!Version.TryParse(string.Join('.', components), out var version)) return "Versao nao reconhecida";
        return version >= new Version(4, 1, 0, 0) ? "SelfHost 4.1+" : "SelfHost 4.0";
    }

    public SelfHostRootConfig LoadInstalledConfig()
    {
        var installation = DetectInstallation();
        if (!installation.Installed)
        {
            throw new InvalidOperationException("SelfHost instalado nao foi localizado em Program Files.");
        }

        if (installation.Generation == "SelfHost 4.1+")
        {
            return SelfHostModernConfigReader.Load(installation.InstallPath);
        }

        var file = Path.Combine(installation.InstallPath, "Config2.json");
        if (!File.Exists(file))
        {
            throw new InvalidOperationException(
                $"SelfHost {installation.Version} detectado, mas Config2.json nao foi localizado em {installation.InstallPath}.");
        }

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
            throw new InvalidOperationException("O SelfHost 4.0 esta instalado, mas ainda nao possui Softcomshop/client_id/client_secret configurados em Config2.json.");

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
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var nestedToken = First(data, "token", "access_token");
            if (!string.IsNullOrWhiteSpace(nestedToken)) return nestedToken;
        }
        var token = First(root, "token", "access_token");
        if (!string.IsNullOrWhiteSpace(token)) return token;
        throw new InvalidOperationException("Resposta de autenticação do SelfHost não contém token reconhecido.");
    }


    public async Task<OAuthClientInfo> CreateDeviceAsync(string name, string serie, string initialNumber, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        serie = (serie ?? string.Empty).Trim();
        initialNumber = (initialNumber ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Informe o nome do dispositivo SelfHost.", nameof(name));
        // O Gerenciador oficial 4.0 e 4.1+ cadastra filhos como SELFHOST_<nome>
        // e usa o mesmo prefixo para decidir quais dispositivos exibir.
        name = NormalizeDeviceName(name);
        if (name.Equals("SELFHOST_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Informe o nome do dispositivo após o prefixo SELFHOST_.", nameof(name));
        if (!int.TryParse(serie, out var serieValue) || serieValue < 0)
            throw new ArgumentException("Informe uma série NFC-e ou NF-e válida.", nameof(serie));
        if (!int.TryParse(initialNumber, out var numberValue) || numberValue < 1)
            throw new ArgumentException("Informe uma numeração inicial válida.", nameof(initialNumber));

        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);

        // Captura validada do Selfhost.Gerenciador: o cadastro filho usa um GUID próprio
        // como device_id e recebe explicitamente serie e numeracao_inicial do documento fiscal.
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
        if (res.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
            throw new InvalidOperationException(BuildApiError("Falha ao criar dispositivo no SelfHost", res.StatusCode, responseBody));

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "O SelfHost respondeu à criação com JSON inválido. Nenhum dispositivo foi adicionado à lista local.");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !First(root, "code").Equals("1", StringComparison.Ordinal))
            {
                var message = root.ValueKind == JsonValueKind.Object ? ExtractHumanMessage(root) : string.Empty;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                    ? "O SelfHost recusou a criação do dispositivo. Nenhum dispositivo foi adicionado à lista local."
                    : $"O SelfHost recusou a criação do dispositivo: {message}");
            }

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

            if (string.IsNullOrWhiteSpace(deviceName)) deviceName = name;
            _log?.Invoke($"Dispositivo SelfHost {deviceName} criado com série {serieValue}, numeração inicial {numberValue}.");
            return new OAuthClientInfo(clientId, deviceName, deviceId, empresaId, null, null, null, "SELFHOST");
        }
    }

    public static string NormalizeDeviceName(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        while (value.StartsWith("SELFHOST_", StringComparison.OrdinalIgnoreCase))
            value = value["SELFHOST_".Length..].TrimStart();
        return "SELFHOST_" + value;
    }

    public Task<IReadOnlyList<OAuthClientInfo>> ListDevicesAsync(CancellationToken ct = default) =>
        ListDevicesCoreAsync(onlySelfHostChildren: true, ct);

    /// <summary>
    /// Lista todos os dispositivos que a credencial raiz consegue administrar.
    /// A interface continua exibindo somente SELFHOST_, mas a procura de um
    /// device_id antigo nao pode ignorar dispositivos comuns da mesma empresa.
    /// </summary>
    public Task<IReadOnlyList<OAuthClientInfo>> ListAllDevicesAsync(CancellationToken ct = default) =>
        ListDevicesCoreAsync(onlySelfHostChildren: false, ct);

    private async Task<IReadOnlyList<OAuthClientInfo>> ListDevicesCoreAsync(
        bool onlySelfHostChildren,
        CancellationToken ct)
    {
        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);
        var all = new Dictionary<string, OAuthClientInfo>(StringComparer.Ordinal);

        for (var page = 1; page <= 200; page++)
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
            var rawItems = ExtractRawDeviceObjects(doc.RootElement).ToArray();
            var items = ExtractDeviceObjects(rawItems)
                .Where(x => !onlySelfHostChildren ||
                            x.Name.StartsWith("SELFHOST_", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var x in items)
            {
                if (!string.IsNullOrWhiteSpace(x.ClientId)) all[x.ClientId] = x;
            }

            var lastPage = FindInteger(doc.RootElement, "last_page", "lastPage");
            if (lastPage.HasValue && page >= lastPage.Value) break;
            if (!lastPage.HasValue && rawItems.Length == 0) break;
            if (page == 200)
                throw new InvalidOperationException("A paginação de dispositivos do SelfHost excedeu o limite de segurança.");
        }

        _log?.Invoke(onlySelfHostChildren
            ? $"{all.Count} dispositivo(s) SelfHost localizado(s) pela API do Softcomshop."
            : $"{all.Count} dispositivo(s) total localizado(s) para procurar vinculos anteriores.");
        return all.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static IEnumerable<JsonElement> ExtractRawDeviceObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "data", "items", "dispositivos" })
            {
                if (!root.TryGetProperty(name, out var child)) continue;
                if (child.ValueKind == JsonValueKind.Array) return child.EnumerateArray().ToArray();
                if (child.ValueKind == JsonValueKind.Object)
                {
                    var nested = ExtractRawDeviceObjects(child).ToArray();
                    if (nested.Length > 0) return nested;
                }
            }
        }
        return Array.Empty<JsonElement>();
    }

    private static IEnumerable<OAuthClientInfo> ExtractDeviceObjects(IEnumerable<JsonElement> rawItems)
    {
        foreach (var n in rawItems)
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

    private static int? FindInteger(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                if (property.Value.TryGetInt32(out var number)) return number;
                if (int.TryParse(property.Value.ToString(), out number)) return number;
            }
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindInteger(property.Value, names);
                if (nested.HasValue) return nested;
            }
        }
        return null;
    }

    private static string BuildApiError(string operation, HttpStatusCode status, string body)
    {
        var message = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                message = ExtractHumanMessage(doc.RootElement);
        }
        catch (JsonException)
        {
            // O corpo não estruturado nunca é propagado porque pode conter dados sensíveis.
        }
        return string.IsNullOrWhiteSpace(message)
            ? $"{operation} (HTTP {(int)status})."
            : $"{operation} (HTTP {(int)status}): {message}";
    }

    private static string ExtractHumanMessage(JsonElement root)
    {
        var message = First(root, "human", "message", "error");
        if (string.IsNullOrWhiteSpace(message) && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            message = First(data, "human", "message", "error");
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;

        message = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?i)(client_secret|access_token|authorization|cookie)\s*[:=]\s*[^\s,;]+",
            "$1=[oculto]");
        message = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?i)bearer\s+[A-Za-z0-9._~+/=-]+",
            "Bearer [oculto]");
        return message.Length <= 300 ? message : message[..300];
    }


    public async Task<bool> UnlinkDeviceAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("client_id do dispositivo SelfHost não informado.", nameof(clientId));

        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);
        var endpoint = cfg.SoftcomshopBaseUrl + "/softauth/api/nfenfce/nfce/dispositivo/desvincular";

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { client_id = clientId }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await _http.SendAsync(req, ct);
        var responseBody = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                BuildApiError("Falha ao desvincular dispositivo no SelfHost", res.StatusCode, responseBody));
        }

        // A API pode devolver erro funcional com HTTP 200. Quando houver um `code`
        // explicito diferente de 1, propagamos somente a mensagem humana sanitizada.
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var response = JsonDocument.Parse(responseBody);
                if (response.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var code = First(response.RootElement, "code");
                    if (!string.IsNullOrWhiteSpace(code) && !code.Equals("1", StringComparison.Ordinal))
                    {
                        var message = ExtractHumanMessage(response.RootElement);
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                            ? "O SelfHost recusou a desvinculacao do dispositivo."
                            : "O SelfHost recusou a desvinculacao do dispositivo: " + message);
                    }
                }
            }
            catch (JsonException)
            {
                // A consulta por client_id abaixo e a fonte de verdade. O corpo bruto
                // nunca e propagado nem registrado porque pode conter dados sensiveis.
            }
        }

        _log?.Invoke($"Solicitação de desvinculação enviada para o dispositivo SelfHost {clientId}.");
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(350, ct);
            var current = await GetDeviceAsync(cfg, token, clientId, ct);
            if (current is null || string.IsNullOrWhiteSpace(current.DeviceId))
            {
                _log?.Invoke($"Dispositivo SelfHost {clientId} confirmado como desvinculado.");
                return true;
            }
        }

        throw new InvalidOperationException(
            $"O SelfHost aceitou a desvinculacao, mas o dispositivo {clientId} ainda possui device_id.");
    }

    public async Task<OAuthClientInfo?> GetDeviceAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("client_id do dispositivo SelfHost não informado.", nameof(clientId));

        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);
        return await GetDeviceAsync(cfg, token, clientId, ct);
    }

    public async Task<OAuthClientInfo?> WaitForDeviceLinkAsync(
        string clientId,
        int maxAttempts,
        TimeSpan delay,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("client_id do dispositivo SelfHost não informado.", nameof(clientId));

        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);
        OAuthClientInfo? current = null;
        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            if (attempt > 0) await Task.Delay(delay, ct);
            current = await GetDeviceAsync(cfg, token, clientId, ct);
            if (current is not null && !string.IsNullOrWhiteSpace(current.DeviceId))
                return current;
        }

        return current;
    }

    private async Task<OAuthClientInfo?> GetDeviceAsync(
        SelfHostRootConfig cfg,
        string token,
        string clientId,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            cfg.SoftcomshopBaseUrl + "/softauth/api/nfenfce/nfce/dispositivo?client_id=" + Uri.EscapeDataString(clientId));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode)
        {
            var errorBody = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                BuildApiError("Falha ao consultar dispositivo no SelfHost", res.StatusCode, errorBody));
        }
        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement)
            ? dataElement
            : root;
        if (data.ValueKind == JsonValueKind.Array)
            return ExtractDeviceObjects(data.EnumerateArray()).FirstOrDefault();
        if (data.ValueKind != JsonValueKind.Object) return null;
        var direct = ExtractDeviceObjects(new[] { data }).FirstOrDefault();
        return direct ?? ExtractDeviceObjects(ExtractRawDeviceObjects(data)).FirstOrDefault();
    }

    public async Task<bool> DeleteDeviceAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("client_id do dispositivo SelfHost não informado.", nameof(clientId));

        var cfg = LoadInstalledConfig();
        var token = await GetTokenAsync(cfg, ct);
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            cfg.SoftcomshopBaseUrl + "/softauth/api/nfenfce/nfce/dispositivo?client_id=" + Uri.EscapeDataString(clientId));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await _http.SendAsync(req, ct);
        return res.IsSuccessStatusCode;
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
