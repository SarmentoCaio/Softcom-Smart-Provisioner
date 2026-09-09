using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class OnlineAuthenticationRequiredException : Exception
{
    public OnlineAuthenticationRequiredException(string message) : base(message) { }
}

public sealed class OnlineSoftcomshopService
{
    private readonly CoreWebView2CookieManager _cookieManager;
    private readonly Action<string>? _log;

    public OnlineSoftcomshopService(CoreWebView2CookieManager cookieManager, Action<string>? log = null)
    {
        _cookieManager = cookieManager;
        _log = log;
    }

    public async Task<IReadOnlyList<CompanyInfo>> GetCompaniesAsync(string database, CancellationToken cancellationToken = default)
    {
        var baseUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var companies = new Dictionary<long, CompanyInfo>();

        for (var page = 1; page <= 50; page++)
        {
            var path = page == 1 ? "/cadastro/empresa" : $"/cadastro/empresa?page={page}";
            var html = await GetHtmlAsync(baseUrl, path, cancellationToken);
            EnsureAuthenticatedHtml(html, "empresas");

            var foundOnPage = 0;
            var newOnPage = 0;
            foreach (Match match in Regex.Matches(html, "<tr(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var attrs = match.Groups["attrs"].Value;
                var idText = GetAttribute(attrs, "data-id");
                var name = WebUtility.HtmlDecode(GetAttribute(attrs, "data-nome") ?? string.Empty).Trim();
                var cnpj = DigitsOnly(WebUtility.HtmlDecode(GetAttribute(attrs, "data-cnpj") ?? string.Empty));
                if (!long.TryParse(idText, out var id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                foundOnPage++;
                if (!companies.ContainsKey(id)) newOnPage++;
                companies[id] = new CompanyInfo(id, name, cnpj);
            }

            if (foundOnPage == 0 || newOnPage == 0 || foundOnPage < 20)
            {
                break;
            }
        }

        if (companies.Count == 0)
        {
            throw new InvalidOperationException("A pagina de empresas foi acessada, mas nenhuma empresa foi identificada no HTML do Softcomshop.");
        }

        return companies.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<OAuthClientInfo>> GetOAuthClientsAsync(
        string database,
        long companyId,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var result = new Dictionary<string, OAuthClientInfo>(StringComparer.Ordinal);

        for (var page = 1; page <= 50; page++)
        {
            var path = page == 1
                ? $"/softauth?empresa_id={companyId}"
                : $"/softauth?empresa_id={companyId}&page={page}";
            var html = await GetHtmlAsync(baseUrl, path, cancellationToken);
            EnsureAuthenticatedHtml(html, "dispositivos");

            var pageItems = ParseOAuthClients(html, companyId);
            var newCount = 0;
            foreach (var item in pageItems)
            {
                if (result.TryAdd(item.ClientId, item))
                {
                    newCount++;
                }
            }

            if (pageItems.Count == 0 || newCount == 0 || pageItems.Count < 20)
            {
                break;
            }
        }

        return result.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<OAuthClientInfo> CreateOAuthClientAsync(
        string database,
        long companyId,
        string name,
        CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Informe o nome do novo dispositivo.");
        }

        var baseUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var formHtml = await GetHtmlAsync(baseUrl, $"/softauth/device/novo?empresa_id={companyId}", cancellationToken);
        EnsureAuthenticatedHtml(formHtml, "novo dispositivo");

        var token = FindInputValue(formHtml, "_token") ?? ExtractCsrfToken(formHtml);
        var clientId = FindInputValue(formHtml, "client_id");
        var clientSecret = FindInputValue(formHtml, "client_secret");
        var formCompanyId = FindInputValue(formHtml, "empresa_id") ?? companyId.ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("O Softcomshop nao retornou os dados necessarios para criar o dispositivo.");
        }

        var fields = new Dictionary<string, string>
        {
            ["_token"] = token,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["name"] = name,
            ["empresa_id"] = formCompanyId
        };

        var response = await PostFormAsync(
            baseUrl,
            "/softauth/device/salvar",
            fields,
            token,
            $"/cadastro/empresa/{companyId}/editar",
            cancellationToken,
            allowRedirectAsSuccess: true);

        if ((int)response.StatusCode >= 400)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Falha ao criar dispositivo no Softcomshop (HTTP {(int)response.StatusCode}). {ExtractServerMessage(body)}");
        }

        return new OAuthClientInfo(clientId, name, string.Empty, companyId, null, null, null, null);
    }

    public async Task<bool> UnlinkOAuthClientAsync(
        string database,
        long companyId,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var token = await GetCsrfTokenAsync(baseUrl, cancellationToken);
        var response = await PostFormAsync(
            baseUrl,
            "/softauth/device/desvincular",
            new Dictionary<string, string> { ["id"] = clientId },
            token,
            $"/cadastro/empresa/{companyId}/editar",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Falha ao desvincular dispositivo no Softcomshop (HTTP {(int)response.StatusCode}). {ExtractServerMessage(body)}");
        }

        return body.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(body);
    }

    public async Task<string> GetDeviceUrlAsync(
        string database,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var html = await GetHtmlAsync(
            baseUrl,
            $"/softauth/device/anexar?client_id={Uri.EscapeDataString(clientId)}",
            cancellationToken);
        EnsureAuthenticatedHtml(html, "QR Code/URL do dispositivo");

        var url = FindInputValue(html, "url-full-value");
        if (string.IsNullOrWhiteSpace(url))
        {
            var match = Regex.Match(
                html,
                "https://[^\\\"'<>\\s]+/softauth/device/add\\?[^\\\"'<>\\s]+",
                RegexOptions.IgnoreCase);
            url = match.Success ? match.Value : null;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("O Softcomshop abriu a configuracao do dispositivo, mas a URL de vinculo nao foi localizada.");
        }

        return WebUtility.HtmlDecode(url).Trim();
    }

    public async Task<IReadOnlyList<FiscalSeriesInfo>> GetFiscalSeriesAsync(
        string database,
        long companyId,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var token = await GetCsrfTokenAsync(baseUrl, cancellationToken);
        var response = await PostFormAsync(
            baseUrl,
            $"/serie-dispositivo/nfenfce/novo?oauth_client_id={Uri.EscapeDataString(clientId)}",
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["empresa_id"] = companyId.ToString()
            },
            token,
            $"/cadastro/empresa/{companyId}/editar",
            cancellationToken);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Falha ao consultar series do dispositivo (HTTP {(int)response.StatusCode}).");
        }
        EnsureAuthenticatedHtml(html, "series do dispositivo");
        return ParseFiscalSeries(html, companyId, clientId);
    }

    public async Task<FiscalSeriesInfo> SaveFiscalSeriesAsync(
        string database,
        long companyId,
        string clientId,
        string documentType,
        long? id,
        string series,
        int initialNumber,
        int environment,
        CancellationToken cancellationToken = default)
    {
        series = (series ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(series))
        {
            throw new InvalidOperationException("Informe a serie.");
        }
        if (initialNumber < 1)
        {
            throw new InvalidOperationException("Informe um numero inicial valido.");
        }
        if (environment is not (1 or 2))
        {
            throw new InvalidOperationException("Ambiente fiscal invalido. Use Producao (1) ou Homologacao (2).");
        }

        var current = (await GetFiscalSeriesAsync(database, companyId, clientId, cancellationToken)).ToList();
        var nfce = current.FirstOrDefault(x => x.DocumentType == "NFCe");
        var nfe = current.FirstOrDefault(x => x.DocumentType == "NFe");

        var normalizedType = documentType.Equals("nfce", StringComparison.OrdinalIgnoreCase) ||
                             documentType.Equals("NFCe", StringComparison.OrdinalIgnoreCase)
            ? "NFCe"
            : "NFe";

        var edited = new FiscalSeriesInfo(
            id ?? 0,
            normalizedType,
            companyId,
            series,
            initialNumber,
            environment,
            false,
            clientId,
            null);

        if (normalizedType == "NFCe") nfce = edited; else nfe = edited;

        var baseUrl = EnvironmentCatalog.BuildSiteUrl(database);
        var token = await GetCsrfTokenAsync(baseUrl, cancellationToken);
        var fields = new Dictionary<string, string>
        {
            ["_token"] = token,
            ["empresa_id"] = companyId.ToString(),
            ["oauth_client_id"] = clientId,
            ["padrao_nfce"] = "0",
            ["tipo_serie_nfce"] = "DISPOSITIVO",
            ["padrao_nfe"] = "0",
            ["tipo_serie_nfe"] = "DISPOSITIVO"
        };
        AddSeriesFields(fields, "nfce", nfce);
        AddSeriesFields(fields, "nfe", nfe);

        var response = await PostFormAsync(
            baseUrl,
            "/serie-dispositivo/nfenfce/salvar",
            fields,
            token,
            $"/cadastro/empresa/{companyId}/editar",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Falha ao salvar serie no Softcomshop (HTTP {(int)response.StatusCode}). {ExtractServerMessage(body)}");
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False)
            {
                throw new InvalidOperationException("O Softcomshop recusou a gravacao da serie.");
            }
        }
        catch (JsonException)
        {
            // Algumas versoes podem responder HTML/sem JSON; o status HTTP continua sendo a fonte principal.
        }

        var refreshed = await GetFiscalSeriesAsync(database, companyId, clientId, cancellationToken);
        return refreshed.FirstOrDefault(x => x.DocumentType == normalizedType)
            ?? edited;
    }

    private static void AddSeriesFields(Dictionary<string, string> fields, string type, FiscalSeriesInfo? item)
    {
        var idKey = $"{type}_serie_id";
        var seriesKey = $"serie_{type}";
        var autoKey = $"auto_serie_{type}";
        var numberKey = $"numeracao_inicial_{type}";
        var environmentKey = $"ambiente_{type}";

        if (item is null || string.IsNullOrWhiteSpace(item.Series))
        {
            fields[idKey] = string.Empty;
            fields[seriesKey] = string.Empty;
            fields[autoKey] = string.Empty;
            fields[numberKey] = "1";
            fields[environmentKey] = "2";
            return;
        }

        if (item.Id > 0)
        {
            fields[idKey] = item.Id.ToString();
            fields[seriesKey] = item.Series;
        }
        else
        {
            fields[idKey] = string.Empty;
            fields[seriesKey] = string.Empty;
            fields[autoKey] = item.Series;
        }
        fields[numberKey] = item.InitialNumber.ToString();
        fields[environmentKey] = item.Environment.ToString();
    }

    private async Task<string> GetCsrfTokenAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var html = await GetHtmlAsync(baseUrl, "/cadastro/empresa", cancellationToken);
        EnsureAuthenticatedHtml(html, "sessao/CSRF");
        var token = ExtractCsrfToken(html);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Nao foi possivel localizar o token CSRF da sessao do Softcomshop.");
        }
        return token;
    }

    private async Task<string> GetHtmlAsync(string baseUrl, string path, CancellationToken cancellationToken)
    {
        using var client = await CreateClientAsync(baseUrl, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (IsAuthenticationFailure(response))
        {
            throw new OnlineAuthenticationRequiredException("Sessao do Softcomshop nao autenticada ou expirada.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Softcomshop respondeu HTTP {(int)response.StatusCode} ao acessar {path}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> PostFormAsync(
        string baseUrl,
        string path,
        IReadOnlyDictionary<string, string> fields,
        string? csrfToken,
        string refererPath,
        CancellationToken cancellationToken,
        bool allowRedirectAsSuccess = false)
    {
        var client = await CreateClientAsync(baseUrl, cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Origin", baseUrl);
        request.Headers.Referrer = new Uri(baseUrl + refererPath);
        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        request.Dispose();
        client.Dispose();

        if (IsAuthenticationFailure(response))
        {
            response.Dispose();
            throw new OnlineAuthenticationRequiredException("Sessao do Softcomshop nao autenticada ou expirada.");
        }
        if (!allowRedirectAsSuccess && (int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new OnlineAuthenticationRequiredException("O Softcomshop redirecionou a operacao. Autentique novamente e tente outra vez.");
        }
        return response;
    }

    private async Task<HttpClient> CreateClientAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var uri = new Uri(baseUrl);
        var container = new CookieContainer();
        var cookies = await _cookieManager.GetCookiesAsync(baseUrl);
        foreach (var cookie in cookies)
        {
            try
            {
                var netCookie = new Cookie(cookie.Name, cookie.Value, string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path)
                {
                    Secure = cookie.IsSecure,
                    HttpOnly = cookie.IsHttpOnly
                };
                container.Add(uri, netCookie);
            }
            catch
            {
                // Um cookie invalido nao deve impedir o uso dos demais cookies validos da sessao.
            }
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = container,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SoftcomSmartProvisioner/1.0.1");
        return client;
    }

    private static bool IsAuthenticationFailure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is 401 or 403 or 419)
        {
            return true;
        }

        if (status is < 300 or >= 400)
        {
            return false;
        }

        // O Softcomshop usa /softauth para o cadastro de dispositivos.
        // Um POST bem-sucedido em /softauth/device/salvar, por exemplo,
        // responde 302 para /softauth?empresa_id=..., e isso NAO e falha de autenticacao.
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        string path;
        if (Uri.TryCreate(location, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
        }
        else
        {
            path = location.Split('?', '#')[0];
        }

        path = path.TrimEnd('/');
        return path.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/login", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/auth", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/authentication/login", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureAuthenticatedHtml(string html, string operation)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException($"O Softcomshop retornou uma resposta vazia ao consultar {operation}.");
        }

        if ((html.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("type='password'", StringComparison.OrdinalIgnoreCase)) &&
            html.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            throw new OnlineAuthenticationRequiredException("Sessao do Softcomshop nao autenticada ou expirada.");
        }
    }

    private static IReadOnlyList<OAuthClientInfo> ParseOAuthClients(string html, long companyId)
    {
        var result = new List<OAuthClientInfo>();
        foreach (Match match in Regex.Matches(html, "<tr(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups["attrs"].Value;
            var clientId = WebUtility.HtmlDecode(GetAttribute(attrs, "data-client_id") ?? string.Empty).Trim();
            var name = WebUtility.HtmlDecode(GetAttribute(attrs, "data-name") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var deviceId = WebUtility.HtmlDecode(GetAttribute(attrs, "data-device_id") ?? string.Empty).Trim();
            result.Add(new OAuthClientInfo(clientId, name, deviceId, companyId, null, null, null, null));
        }
        return result;
    }

    private static IReadOnlyList<FiscalSeriesInfo> ParseFiscalSeries(string html, long companyId, string clientId)
    {
        var result = new List<FiscalSeriesInfo>();
        Add("NFCe", "nfce");
        Add("NFe", "nfe");
        return result;

        void Add(string documentType, string suffix)
        {
            var id = ParseLong(FindInputValue(html, $"{suffix}_serie_id")) ?? 0;
            var series = FindInputValue(html, $"serie_{suffix}");
            if (string.IsNullOrWhiteSpace(series))
            {
                series = FindInputValue(html, $"auto_serie_{suffix}");
            }
            if (string.IsNullOrWhiteSpace(series))
            {
                return;
            }

            var number = (int)(ParseLong(FindInputValue(html, $"numeracao_inicial_{suffix}")) ?? 1);
            var environment = (int)(ParseLong(FindInputValue(html, $"ambiente_{suffix}")) ?? 2);
            if (environment is not (1 or 2)) environment = 2;
            result.Add(new FiscalSeriesInfo(id, documentType, companyId, series, Math.Max(number, 1), environment, false, clientId, null));
        }
    }

    private static string? ExtractCsrfToken(string html)
    {
        var meta = Regex.Match(
            html,
            "<meta(?=[^>]*name=[\"']csrf-token[\"'])(?<attrs>[^>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (meta.Success)
        {
            var value = GetAttribute(meta.Groups["attrs"].Value, "content");
            if (!string.IsNullOrWhiteSpace(value)) return WebUtility.HtmlDecode(value);
        }
        return FindInputValue(html, "_token");
    }

    private static string? FindInputValue(string html, string idOrName)
    {
        foreach (Match match in Regex.Matches(html, "<input(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups["attrs"].Value;
            var id = GetAttribute(attrs, "id");
            var name = GetAttribute(attrs, "name");
            if (!string.Equals(id, idOrName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, idOrName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return WebUtility.HtmlDecode(GetAttribute(attrs, "value") ?? string.Empty);
        }
        return null;
    }

    private static string? GetAttribute(string attrs, string name)
    {
        var match = Regex.Match(
            attrs,
            $"(?:^|\\s){Regex.Escape(name)}\\s*=\\s*([\"'])(?<value>.*?)\\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static long? ParseLong(string? value) => long.TryParse(value, out var parsed) ? parsed : null;
    private static string DigitsOnly(string value) => new(value.Where(char.IsDigit).ToArray());

    private static string ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? string.Empty;
            }
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        var text = Regex.Replace(body, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(Regex.Replace(text, "\\s+", " ")).Trim();
        return text.Length <= 220 ? text : text[..220] + "...";
    }
}
