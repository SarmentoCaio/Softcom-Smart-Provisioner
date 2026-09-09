using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner;

public sealed class MainForm : Form
{
    private static string AppVersion => AppVersionInfo.Current;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebView2 _webView = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AdbService _adbService;
    private readonly ScrcpyService _scrcpyService;
    private readonly SmartUiAutomationService _smartAutomationService;
    private readonly DatabaseService _databaseService;
    private readonly SecretStore _secretStore;
    private readonly SettingsService _settingsService;
    private readonly VpnService _vpnService;
    private readonly DockerDbBridgeService _dockerDbBridgeService;
    private readonly UpdateService _updateService;
    private UpdateManifest? _availableUpdate;
    private readonly AppLogService _logService;
    private readonly string _appDataDirectory;
    private CoreWebView2Environment? _webEnvironment;
    private OnlineSoftcomshopService? _onlineSoftcomshopService;
    private readonly SelfHostDeviceService _selfHostDeviceService;

    private IReadOnlyList<DeviceInfo> _androidDevices = Array.Empty<DeviceInfo>();
    private bool _pageReady;

    public MainForm()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var toolsDirectory = Path.Combine(baseDirectory, "tools");
        _appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Softcom",
            "SmartProvisioner");
        Directory.CreateDirectory(_appDataDirectory);

        _logService = new AppLogService(_appDataDirectory);
        _settingsService = new SettingsService(_appDataDirectory);
        _secretStore = new SecretStore(_appDataDirectory);
        _databaseService = new DatabaseService(_secretStore);
        _selfHostDeviceService = new SelfHostDeviceService(message => WriteLog("SELFHOST", message));
        _adbService = new AdbService(toolsDirectory);
        _scrcpyService = new ScrcpyService(toolsDirectory);
        _smartAutomationService = new SmartUiAutomationService(_adbService);
        _vpnService = new VpnService(_secretStore, _settingsService, _appDataDirectory, message => WriteLog("VPN", message));
        _dockerDbBridgeService = new DockerDbBridgeService(
            _secretStore,
            _settingsService,
            _appDataDirectory,
            Path.Combine(baseDirectory, "tools", "db-bridge"),
            message => WriteLog("DOCKER", message));
        _updateService = new UpdateService(AppVersion, _appDataDirectory, baseDirectory, message => WriteLog("UPDATE", message));
        WriteLog("APP", $"Softcom Smart Provisioner {AppVersion} iniciado.");

        Text = $"Softcom Smart Provisioner {AppVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1480;
        Height = 920;
        MinimumSize = new Size(1100, 720);
        BackColor = Color.FromArgb(10, 16, 28);

        var iconPath = Path.Combine(baseDirectory, "Assets", "App.ico");
        if (File.Exists(iconPath))
        {
            Icon = new Icon(iconPath);
        }

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);

        Load += OnFormLoad;
        FormClosing += OnFormClosing;
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(_appDataDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);

            _webEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _webView.EnsureCoreWebView2Async(_webEnvironment);
            _onlineSoftcomshopService = new OnlineSoftcomshopService(
                _webView.CoreWebView2.CookieManager,
                message => WriteLog("ONLINE", message));

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.NavigationCompleted += OnNavigationCompleted;

            var indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException("A interface local nao foi encontrada.", indexPath);
            }

            _webView.Source = new Uri(indexPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Nao foi possivel iniciar o Softcom Smart Provisioner.\n\n" +
                ex.Message +
                "\n\nVerifique se o Microsoft Edge WebView2 Runtime esta instalado.",
                "Softcom Smart Provisioner",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }

        _pageReady = true;
        SendBootstrap();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var request = JsonSerializer.Deserialize<BridgeRequest>(e.WebMessageAsJson, JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.Action))
            {
                return;
            }

            switch (request.Action)
            {
                case "appReady":
                    // Mantem a thread principal livre durante a inicializacao.
                    // O WebView2 continua responsivo enquanto ADB e atualizacoes sao
                    // verificados em segundo plano.
                    SendBootstrap();
                    _ = RefreshAndroidAsync();
                    if (_settingsService.Load().AutoCheckUpdates)
                    {
                        _ = CheckForUpdatesAsync(false);
                    }
                    break;

                case "refreshAndroid":
                    await RefreshAndroidAsync();
                    break;

                case "openScrcpy":
                    OpenScrcpy(request.Payload);
                    break;

                case "loadDatabases":
                    await LoadDatabasesAsync(request.Payload);
                    break;

                case "loadCompanies":
                    await LoadCompaniesAsync(request.Payload);
                    break;

                case "connectOnline":
                    await ConnectOnlineAsync(request.Payload);
                    break;

                case "loadOauthClients":
                    await LoadOauthClientsAsync(request.Payload);
                    break;

                case "createOauthClient":
                    await CreateOauthClientAsync(request.Payload);
                    break;

                case "loadFiscalSeries":
                    await LoadFiscalSeriesAsync(request.Payload);
                    break;

                case "saveFiscalSeries":
                    await SaveFiscalSeriesAsync(request.Payload);
                    break;

                case "connectVpn":
                    await ConnectVpnAsync(request.Payload);
                    break;

                case "connectDocker":
                    await ConnectDockerAsync(request.Payload);
                    break;

                case "chooseVpnProfile":
                    ChooseVpnProfile();
                    break;

                case "saveDatabaseCredentials":
                    SaveDatabaseCredentials(request.Payload);
                    break;

                case "importLegacySecrets":
                    ImportLegacySecrets();
                    break;

                case "saveSmartPackage":
                    SaveSmartPackage(request.Payload);
                    break;

                case "detectSmartPackages":
                    await DetectSmartPackagesAsync(request.Payload);
                    break;

                case "clearSmartData":
                    await ClearSmartDataAsync(request.Payload);
                    break;

                case "generateUrl":
                    await GenerateUrlAsync(request.Payload);
                    break;

                case "evaluateLink":
                    EvaluateLink(request.Payload);
                    break;

                case "validatePreparation":
                    await ValidatePreparationAsync(request.Payload);
                    break;

                case "prepareSmart":
                    await PrepareSmartAsync(request.Payload);
                    break;

                case "copyText":
                    CopyText(request.Payload);
                    break;

                case "openClientSite":
                    OpenClientSite(request.Payload);
                    break;

                case "saveUpdateSettings":
                    SaveUpdateSettings(request.Payload);
                    break;

                case "checkForUpdates":
                    await CheckForUpdatesAsync(true);
                    break;

                case "installUpdate":
                    await InstallUpdateAsync();
                    break;

                case "clearLogs":
                    _logService.Clear();
                    PostEvent("logsCleared", new { });
                    break;

                case "openLogFolder":
                    Process.Start(new ProcessStartInfo(_logService.LogDirectory) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            WriteLog("ERRO", ex.Message, "ERROR");
            PostError(ex.Message);
        }
    }

    private void SendBootstrap()
    {
        var settings = _settingsService.Load();
        PostEvent("bootstrap", new
        {
            app = new
            {
                name = "Softcom Smart Provisioner",
                version = AppVersion,
                architecture = ".NET 8 + WebView2",
                phase = "Fase 7 - Atualizacao automatica"
            },
            environments = EnvironmentCatalog.Environments.Values,
            selfHost = new
            {
                defaultBaseUrl = DetectSelfHostBaseUrl(),
                port = 7711,
                note = "Usado somente por Smart Comanda e Smart Autopagamento."
            },
            settings,
            logs = _logService.GetRecent(),
            capabilities = new
            {
                adbAvailable = _adbService.IsAvailable,
                scrcpyAvailable = _scrcpyService.IsAvailable,
                databaseCredentials = _secretStore.HasDatabaseCredentials,
                vpnCredentials = _secretStore.HasVpnCredentials,
                apiCredentials = _secretStore.HasApiCredentials,
                vpnProfile = _vpnService.ResolveProfile(),
                openVpn = _vpnService.FindOpenVpn(),
                dockerAvailable = _dockerDbBridgeService.IsDockerAvailable()
            }
        });
    }

    private async Task RefreshAndroidAsync()
    {
        try
        {
            PostBusy("android", true);
            if (!_adbService.IsAvailable)
            {
                throw new InvalidOperationException("ADB nao localizado na pasta tools.");
            }

            // A descoberta ADB faz varias chamadas em sequencia. Em algumas maquinas
            // essas chamadas podem concluir de forma sincrona e manter a continuacao
            // presa na thread da interface, deixando inclusive minimizar/maximizar/fechar
            // sem resposta. Executa todo o diagnostico ADB fora da thread principal.
            var devices = await Task.Run(async () =>
            {
                await _adbService.StartServerAsync(_shutdown.Token).ConfigureAwait(false);
                return await _adbService.GetDevicesAsync(_shutdown.Token).ConfigureAwait(false);
            }, _shutdown.Token);

            _androidDevices = devices;
            PostEvent("androidDevices", new
            {
                items = _androidDevices,
                onlineCount = _androidDevices.Count(x => x.IsOnline)
            });
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Encerramento normal do aplicativo.
        }
        catch (Exception ex)
        {
            WriteLog("ADB", "Falha ao atualizar dispositivos Android: " + ex.Message, "ERROR");
            PostError("Nao foi possivel atualizar os dispositivos Android. Consulte a aba Logs.");
        }
        finally
        {
            PostBusy("android", false);
        }
    }

    private async Task LoadDatabasesAsync(JsonElement payload)
    {
        var accessMode = ReadAccessMode(payload);
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        try
        {
            PostBusy("databases", true);
            WriteLog("BANCO", $"Busca de clientes solicitada em {environmentKey.ToUpperInvariant()} via {accessMode}.");

            await EnsureDatabaseAccessAsync(environmentKey, "buscar os bancos", accessMode);
            var items = await _databaseService.GetDatabasesAsync(environmentKey, _shutdown.Token);
            WriteLog("BANCO", $"{items.Count} banco(s) Softcomshop localizado(s).");
            PostEvent("databases", new { environment = environmentKey, items, accessMode });
        }
        catch (Exception ex)
        {
            WriteLog("BANCO", "Falha ao buscar bancos: " + ex.Message, "ERROR");
            PostEvent("databaseError", new { message = ex.Message, environment = environmentKey, reachable = false, accessMode });
        }
        finally
        {
            PostBusy("databases", false);
        }
    }

    private async Task LoadCompaniesAsync(JsonElement payload)
    {
        var accessMode = ReadAccessMode(payload);
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Selecione um cliente.");

        PostBusy("companies", true);
        try
        {
            var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
            IReadOnlyList<CompanyInfo> items;

            if (IsOnlineMode(accessMode))
            {
                WriteLog("ONLINE", $"Consultando empresas de {EnvironmentCatalog.DatabaseDisplayName(normalizedDatabase)} pela sessao WEB.");
                items = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "consultar empresas",
                    service => service.GetCompaniesAsync(normalizedDatabase, _shutdown.Token));

                var settings = _settingsService.Load();
                settings.AccessMode = "online";
                settings.LastOnlineClient = normalizedDatabase;
                settings.LastDatabase = normalizedDatabase;
                _settingsService.Save(settings);

                PostEvent("onlineState", new
                {
                    connected = true,
                    client = EnvironmentCatalog.DatabaseDisplayName(normalizedDatabase),
                    message = "Sessao Softcomshop autenticada. Empresas carregadas sem VPN."
                });
            }
            else
            {
                await EnsureDatabaseAccessAsync(environmentKey, "consultar empresas", accessMode);
                items = await _databaseService.GetCompaniesAsync(environmentKey, normalizedDatabase, _shutdown.Token);
                var settings = _settingsService.Load();
                settings.AccessMode = accessMode;
                settings.LastEnvironment = environmentKey;
                settings.LastDatabase = normalizedDatabase;
                _settingsService.Save(settings);
            }

            PostEvent("companies", new { items, accessMode });
        }
        finally
        {
            PostBusy("companies", false);
        }
    }

    private async Task ConnectOnlineAsync(JsonElement payload)
    {
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Informe o cliente Softcomshop.");

        PostBusy("online", true);
        try
        {
            var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
            var items = await ExecuteOnlineAsync(
                normalizedDatabase,
                "conectar ao Softcomshop",
                service => service.GetCompaniesAsync(normalizedDatabase, _shutdown.Token));

            var settings = _settingsService.Load();
            settings.AccessMode = "online";
            settings.LastOnlineClient = normalizedDatabase;
            settings.LastDatabase = normalizedDatabase;
            _settingsService.Save(settings);

            WriteLog("ONLINE", $"Sessao autenticada em {EnvironmentCatalog.DatabaseDisplayName(normalizedDatabase)}. {items.Count} empresa(s) localizada(s).");
            PostEvent("onlineState", new
            {
                connected = true,
                client = EnvironmentCatalog.DatabaseDisplayName(normalizedDatabase),
                message = "Conectado ao Softcomshop sem VPN."
            });
            PostEvent("companies", new { items, accessMode = "online" });
        }
        finally
        {
            PostBusy("online", false);
        }
    }

    private async Task LoadOauthClientsAsync(JsonElement payload)
    {
        var accessMode = ReadAccessMode(payload);
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Selecione um cliente.");
        var companyId = ReadLong(payload, "companyId")
            ?? throw new InvalidOperationException("Selecione uma empresa.");
        var module = ReadString(payload, "module") ?? "smart_pdv";

        PostBusy("oauth", true);
        try
        {
            var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
            IReadOnlyList<OAuthClientInfo> items;
            if (ShouldUseSelfHost(payload, module))
            {
                items = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                WriteLog("SELFHOST", $"{items.Count} dispositivo(s) carregado(s) usando as credenciais raiz do SelfHost instalado.");
            }
            else if (IsOnlineMode(accessMode))
            {
                items = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "listar dispositivos",
                    service => service.GetOAuthClientsAsync(normalizedDatabase, companyId, _shutdown.Token));
                WriteLog("ONLINE", $"{items.Count} dispositivo(s) carregado(s) pela pagina Softcomshop para a empresa {companyId}.");
            }
            else
            {
                await EnsureDatabaseAccessAsync(environmentKey, "listar dispositivos", accessMode);
                items = await _databaseService.GetOAuthClientsAsync(
                    environmentKey,
                    normalizedDatabase,
                    companyId,
                    _shutdown.Token);
            }

            var settings = _settingsService.Load();
            settings.LastEnvironment = environmentKey;
            settings.LastDatabase = normalizedDatabase;
            settings.LastCompanyId = companyId;
            settings.AccessMode = accessMode;
            if (IsOnlineMode(accessMode)) settings.LastOnlineClient = normalizedDatabase;
            _settingsService.Save(settings);
            PostEvent("oauthClients", new { items, accessMode });
        }
        finally
        {
            PostBusy("oauth", false);
        }
    }

    private async Task CreateOauthClientAsync(JsonElement payload)
    {
        var accessMode = ReadAccessMode(payload);
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Selecione um cliente.");
        var companyId = ReadLong(payload, "companyId")
            ?? throw new InvalidOperationException("Selecione uma empresa.");
        var name = ReadString(payload, "name")?.Trim()
            ?? throw new InvalidOperationException("Informe o nome do novo dispositivo.");
        var module = ReadString(payload, "module") ?? "smart_pdv";
        var useSelfHost = ShouldUseSelfHost(payload, module);

        PostBusy("createDevice", true);
        try
        {
            var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
            OAuthClientInfo item;
            IReadOnlyList<OAuthClientInfo> items;

            if (useSelfHost)
            {
                var series = ReadString(payload, "series")?.Trim()
                    ?? throw new InvalidOperationException("Informe a série NFC-e do dispositivo SelfHost.");
                var initialNumber = ReadString(payload, "initialNumber")?.Trim()
                    ?? throw new InvalidOperationException("Informe o próximo número NFC-e do dispositivo SelfHost.");
                var nfeSeries = ReadString(payload, "nfeSeries")?.Trim() ?? string.Empty;
                var nfeInitialNumberText = ReadString(payload, "nfeInitialNumber")?.Trim() ?? string.Empty;

                item = await _selfHostDeviceService.CreateDeviceAsync(name, series, initialNumber, _shutdown.Token);

                if (!string.IsNullOrWhiteSpace(nfeSeries))
                {
                    if (!int.TryParse(nfeInitialNumberText, out var nfeInitialNumber) || nfeInitialNumber < 1)
                        throw new InvalidOperationException("O dispositivo SelfHost foi criado, mas informe um próximo número NF-e válido para concluir a série NF-e.");

                    // A criação REST do SelfHost já grava a NFC-e. Para NF-e reutilizamos o fluxo
                    // fiscal web já validado pelo Provisioner, preservando o mesmo oauth_client_id.
                    var currentSeries = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        "consultar ambiente fiscal do dispositivo SelfHost",
                        service => service.GetFiscalSeriesAsync(normalizedDatabase, companyId, item.ClientId, _shutdown.Token));

                    var fiscalEnvironment = currentSeries
                        .FirstOrDefault(x => x.DocumentType.Equals("NFCe", StringComparison.OrdinalIgnoreCase))?.Environment
                        ?? currentSeries.FirstOrDefault()?.Environment;

                    if (fiscalEnvironment is not (1 or 2))
                        throw new InvalidOperationException("O dispositivo SelfHost foi criado, mas não foi possível determinar o ambiente fiscal para vincular a NF-e. Atualize a lista e configure a NF-e novamente.");

                    await ExecuteOnlineAsync(
                        normalizedDatabase,
                        "salvar série NF-e do dispositivo SelfHost",
                        service => service.SaveFiscalSeriesAsync(
                            normalizedDatabase,
                            companyId,
                            item.ClientId,
                            "nfe",
                            null,
                            nfeSeries,
                            nfeInitialNumber,
                            fiscalEnvironment.Value,
                            _shutdown.Token));

                    WriteLog("SELFHOST", $"NF-e série {nfeSeries}, próximo número {nfeInitialNumber}, vinculada ao dispositivo {item.Name}.");
                }

                // Atualiza a lista uma única vez após a criação. Se a API ainda não refletir
                // o cadastro, mantemos o item retornado pela própria criação no seletor local.
                var refreshed = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                items = refreshed.Any(x => x.ClientId == item.ClientId)
                    ? refreshed
                    : refreshed.Concat(new[] { item }).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

                WriteLog("SELFHOST", $"Dispositivo {item.Name} criado no SelfHost com NFC-e série {series}, próximo número {initialNumber}{(string.IsNullOrWhiteSpace(nfeSeries) ? "" : $", NF-e série {nfeSeries}")}, e lista atualizada.");
            }
            else if (IsOnlineMode(accessMode))
            {
                item = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "criar dispositivo",
                    service => service.CreateOAuthClientAsync(normalizedDatabase, companyId, name, _shutdown.Token));
                items = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "atualizar dispositivos",
                    service => service.GetOAuthClientsAsync(normalizedDatabase, companyId, _shutdown.Token));
                WriteLog("ONLINE", $"Dispositivo {item.Name} criado pelo endpoint /softauth/device/salvar.");
            }
            else
            {
                await EnsureDatabaseAccessAsync(environmentKey, "criar o dispositivo", accessMode);
                item = await _databaseService.CreateOAuthClientAsync(
                    environmentKey,
                    normalizedDatabase,
                    companyId,
                    name,
                    _shutdown.Token);
                items = await _databaseService.GetOAuthClientsAsync(
                    environmentKey,
                    normalizedDatabase,
                    companyId,
                    _shutdown.Token);
                WriteLog("BANCO", $"Dispositivo {item.Name} criado em oauth_clients para a empresa {companyId}.");
            }

            PostEvent("oauthClientCreated", new { item, items, accessMode });
        }
        finally
        {
            PostBusy("createDevice", false);
        }
    }

    private async Task LoadFiscalSeriesAsync(JsonElement payload)
    {
        var accessMode = ReadAccessMode(payload);
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Selecione um cliente.");
        var companyId = ReadLong(payload, "companyId")
            ?? throw new InvalidOperationException("Selecione uma empresa.");
        var clientId = ReadString(payload, "clientId")?.Trim()
            ?? throw new InvalidOperationException("Selecione um dispositivo Softcomshop.");

        PostBusy("series", true);
        try
        {
            var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
            IReadOnlyList<FiscalSeriesInfo> items;
            if (IsOnlineMode(accessMode))
            {
                items = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "consultar series",
                    service => service.GetFiscalSeriesAsync(normalizedDatabase, companyId, clientId, _shutdown.Token));
            }
            else
            {
                await EnsureDatabaseAccessAsync(environmentKey, "consultar as series do dispositivo", accessMode);
                items = await _databaseService.GetFiscalSeriesAsync(
                    environmentKey,
                    normalizedDatabase,
                    companyId,
                    clientId,
                    _shutdown.Token);
            }
            PostEvent("fiscalSeries", new { clientId, items, accessMode });
        }
        finally
        {
            PostBusy("series", false);
        }
    }

    private async Task SaveFiscalSeriesAsync(JsonElement payload)
    {
        var accessMode = ReadAccessMode(payload);
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Selecione um cliente.");
        var companyId = ReadLong(payload, "companyId")
            ?? throw new InvalidOperationException("Selecione uma empresa.");
        var clientId = ReadString(payload, "clientId")?.Trim()
            ?? throw new InvalidOperationException("Selecione um dispositivo Softcomshop.");
        var documentType = ReadString(payload, "documentType")?.Trim()
            ?? throw new InvalidOperationException("Informe o tipo da serie.");
        var id = ReadLong(payload, "id");
        var series = ReadString(payload, "series")?.Trim()
            ?? throw new InvalidOperationException("Informe a serie.");
        var initialNumberRaw = ReadLong(payload, "initialNumber")
            ?? throw new InvalidOperationException("Informe o numero inicial/proximo numero.");
        var environmentValueRaw = ReadLong(payload, "fiscalEnvironment") ?? 2;

        if (initialNumberRaw is < 1 or > int.MaxValue)
            throw new InvalidOperationException("Numero inicial/proximo numero invalido.");
        if (environmentValueRaw is not (1 or 2))
            throw new InvalidOperationException("Ambiente fiscal invalido. Selecione Producao ou Homologacao.");

        PostBusy("series", true);
        try
        {
            var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
            FiscalSeriesInfo item;
            IReadOnlyList<FiscalSeriesInfo> items;

            if (IsOnlineMode(accessMode))
            {
                item = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "salvar serie",
                    service => service.SaveFiscalSeriesAsync(
                        normalizedDatabase,
                        companyId,
                        clientId,
                        documentType,
                        id,
                        series,
                        (int)initialNumberRaw,
                        (int)environmentValueRaw,
                        _shutdown.Token));
                items = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "atualizar series",
                    service => service.GetFiscalSeriesAsync(normalizedDatabase, companyId, clientId, _shutdown.Token));
                WriteLog("ONLINE", $"Serie {item.DocumentType} {item.Series} salva pelo endpoint do Softcomshop.");
            }
            else
            {
                await EnsureDatabaseAccessAsync(environmentKey, "salvar a serie do dispositivo", accessMode);
                item = await _databaseService.SaveFiscalSeriesAsync(
                    environmentKey,
                    normalizedDatabase,
                    companyId,
                    clientId,
                    documentType,
                    id,
                    series,
                    (int)initialNumberRaw,
                    (int)environmentValueRaw,
                    _shutdown.Token);
                items = await _databaseService.GetFiscalSeriesAsync(
                    environmentKey,
                    normalizedDatabase,
                    companyId,
                    clientId,
                    _shutdown.Token);
            }

            PostEvent("fiscalSeriesSaved", new { item, items, clientId, accessMode });
        }
        finally
        {
            PostBusy("series", false);
        }
    }

    private async Task<T> ExecuteOnlineAsync<T>(
        string database,
        string operation,
        Func<OnlineSoftcomshopService, Task<T>> action)
    {
        var service = _onlineSoftcomshopService
            ?? throw new InvalidOperationException("O modo Online ainda nao esta disponivel. Reinicie o Provisioner e tente novamente.");

        try
        {
            return await action(service);
        }
        catch (OnlineAuthenticationRequiredException)
        {
            WriteLog("ONLINE", $"Sessao necessaria para {operation}. Tentando autenticacao automatica no Softcomshop.");
            PostEvent("onlineState", new
            {
                connected = false,
                client = EnvironmentCatalog.DatabaseDisplayName(database),
                message = "Autenticacao necessaria. Tentando login automatico no Softcomshop..."
            });

            await ShowOnlineLoginAsync(database);
            return await action(service);
        }
    }

    private Task ShowOnlineLoginAsync(string database)
    {
        if (_webEnvironment is null)
        {
            throw new InvalidOperationException("O WebView2 ainda nao foi inicializado.");
        }

        var target = EnvironmentCatalog.BuildSiteUrl(database) + "/cadastro/empresa";
        using var login = new SoftcomshopLoginForm(_webEnvironment, target);
        var result = login.ShowDialog(this);
        if (result != DialogResult.OK)
        {
            throw new InvalidOperationException("Login do Softcomshop cancelado.");
        }

        WriteLog("ONLINE", $"Autenticacao finalizada para {EnvironmentCatalog.DatabaseDisplayName(database)}.");
        return Task.CompletedTask;
    }

    private static string ReadAccessMode(JsonElement payload)
    {
        var mode = ReadString(payload, "accessMode")?.Trim().ToLowerInvariant();
        return mode switch
        {
            "database" => "database",
            "docker" => "docker",
            _ => "online"
        };
    }

    private static bool IsOnlineMode(string accessMode) =>
        string.Equals(accessMode, "online", StringComparison.OrdinalIgnoreCase);

    private static bool IsDockerMode(string accessMode) =>
        string.Equals(accessMode, "docker", StringComparison.OrdinalIgnoreCase);

    private async Task EnsureDatabaseAccessAsync(string environmentKey, string operation, string accessMode)
    {
        if (IsDockerMode(accessMode))
        {
            _databaseService.SetConnectionOverride("127.0.0.1", DockerDbBridgeService.LocalPort);
            if (await _dockerDbBridgeService.IsReadyForEnvironmentAsync(environmentKey, _shutdown.Token))
                return;

            WriteLog("DOCKER", $"Preparando DB Bridge isolado para {EnvironmentCatalog.Get(environmentKey).Label} durante: {operation}.");
            PostEvent("dockerState", new { connected = false, message = $"Preparando Docker isolado para {EnvironmentCatalog.Get(environmentKey).Label}..." });
            var message = await _dockerDbBridgeService.StartAsync(environmentKey, _shutdown.Token);
            PostEvent("dockerState", new { connected = true, message });
            return;
        }

        _databaseService.SetConnectionOverride(null, null);
        var environment = EnvironmentCatalog.Get(environmentKey);
        if (await _vpnService.CanReachAsync(environment.Host, environment.Port))
            return;

        WriteLog("VPN", $"Acesso ao banco indisponivel para {operation}. Conectando VPN automaticamente.");
        PostEvent("vpnState", new { connected = false, message = $"Conectando VPN para {operation}..." });
        var vpnMessage = await _vpnService.ConnectAsync(environmentKey, _shutdown.Token);
        PostEvent("vpnState", new { connected = true, message = vpnMessage });
    }

    private async Task ConnectDockerAsync(JsonElement payload)
    {
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        PostBusy("docker", true);
        try
        {
            _databaseService.SetConnectionOverride("127.0.0.1", DockerDbBridgeService.LocalPort);
            var message = await _dockerDbBridgeService.StartAsync(environmentKey, _shutdown.Token);
            PostEvent("dockerState", new { connected = true, message });
        }
        finally
        {
            PostBusy("docker", false);
        }
    }

    private async Task ConnectVpnAsync(JsonElement payload)
    {
        var environmentKey = ReadString(payload, "environment") ?? "aws1";
        PostBusy("vpn", true);
        try
        {
            WriteLog("VPN", "Conexao manual solicitada.");
            var message = await _vpnService.ConnectAsync(environmentKey, _shutdown.Token);
            PostEvent("vpnConnected", new { message });
        }
        finally
        {
            PostBusy("vpn", false);
        }
    }

    private void ChooseVpnProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Perfil OpenVPN (*.ovpn)|*.ovpn|Todos os arquivos (*.*)|*.*",
            Title = "Selecione o perfil OpenVPN"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var settings = _settingsService.Load();
        settings.VpnProfilePath = dialog.FileName;
        _settingsService.Save(settings);
        SendBootstrap();
        PostEvent("toast", new { type = "success", message = "Perfil VPN salvo." });
    }

    private void SaveDatabaseCredentials(JsonElement payload)
    {
        var username = ReadString(payload, "username")?.Trim() ?? string.Empty;
        var password = ReadString(payload, "password") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Informe usuario e senha do banco.");
        }

        _secretStore.Set(SecretStore.DatabaseUsername, username);
        _secretStore.Set(SecretStore.DatabasePassword, password);
        SendBootstrap();
        PostEvent("credentialsSaved", new
        {
            message = "Acesso do banco salvo neste computador."
        });
    }

    private void ImportLegacySecrets()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Configuracao do Recuperador (*.py)|*.py|Todos os arquivos (*.*)|*.*",
            Title = "Selecione configuracao_segredos_build.py"
        };

        var legacyDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "configuracao_segredos_build.py");
        if (File.Exists(legacyDefault))
        {
            dialog.FileName = legacyDefault;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var values = LegacySecretsImportService.Read(dialog.FileName);
        _secretStore.SetMany(values);
        SendBootstrap();
        PostEvent("toast", new
        {
            type = "success",
            message = "Credenciais importadas e protegidas pelo usuario do Windows."
        });
    }

    private void SaveUpdateSettings(JsonElement payload)
    {
        var settings = _settingsService.Load();
        var channel = ReadString(payload, "channel")?.Trim().ToLowerInvariant();
        settings.UpdateChannel = channel == "beta" ? "beta" : "stable";
        settings.AutoCheckUpdates = ReadBool(payload, "autoCheck", true);
        settings.AutoInstallUpdates = ReadBool(payload, "autoInstall", true);
        settings.StableManifestUrl = ReadString(payload, "stableManifestUrl")?.Trim() ?? string.Empty;
        settings.BetaManifestUrl = ReadString(payload, "betaManifestUrl")?.Trim() ?? string.Empty;
        _settingsService.Save(settings);
        SendBootstrap();
        PostEvent("updateSettingsSaved", new
        {
            message = "Configuracao de atualizacoes salva.",
            channel = settings.UpdateChannel,
            autoCheck = settings.AutoCheckUpdates,
            autoInstall = settings.AutoInstallUpdates
        });
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        PostBusy("update", true);
        try
        {
            var settings = _settingsService.Load();
            var result = await _updateService.CheckAsync(settings, _shutdown.Token);
            _availableUpdate = result.UpdateAvailable ? result.Manifest : null;
            PostEvent("updateStatus", new
            {
                configured = result.Configured,
                available = result.UpdateAvailable,
                currentVersion = AppVersion,
                latestVersion = result.Manifest?.Version ?? string.Empty,
                required = result.Manifest?.Required ?? false,
                notes = result.Manifest?.Notes ?? string.Empty,
                message = result.Message,
                userInitiated
            });
            if (!userInitiated && result.UpdateAvailable && settings.AutoInstallUpdates && _availableUpdate is not null)
            {
                WriteLog("UPDATE", $"Atualizacao automatica habilitada. Instalando {_availableUpdate.Version}.");
                await InstallUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            WriteLog("UPDATE", ex.Message, "WARN");
            PostEvent("updateStatus", new
            {
                configured = true,
                available = false,
                currentVersion = AppVersion,
                latestVersion = string.Empty,
                required = false,
                notes = string.Empty,
                message = "Nao foi possivel verificar atualizacoes: " + ex.Message,
                userInitiated,
                error = true
            });
        }
        finally
        {
            PostBusy("update", false);
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(true);
            if (_availableUpdate is null)
            {
                throw new InvalidOperationException("Nenhuma atualizacao disponivel para instalar.");
            }
        }

        PostBusy("updateInstall", true);
        try
        {
            PostEvent("updateInstallProgress", new { message = $"Baixando versao {_availableUpdate.Version}..." });
            var packagePath = await _updateService.DownloadAsync(_availableUpdate, _shutdown.Token);
            PostEvent("updateInstallProgress", new { message = "Pacote validado. Reiniciando para aplicar a atualizacao..." });
            WriteLog("UPDATE", $"Pacote {_availableUpdate.Version} pronto. Iniciando Updater.");
            _updateService.LaunchUpdater(packagePath);
            BeginInvoke(new Action(Close));
        }
        catch
        {
            PostBusy("updateInstall", false);
            throw;
        }
    }

    private void SaveSmartPackage(JsonElement payload)
    {
        var packageName = ReadString(payload, "packageName")?.Trim() ?? string.Empty;
        var settings = _settingsService.Load();
        settings.SmartPackageName = packageName;
        _settingsService.Save(settings);
        SendBootstrap();
        PostEvent("toast", new { type = "success", message = "Package name salvo." });
    }

    private async Task DetectSmartPackagesAsync(JsonElement payload)
    {
        var serial = ReadString(payload, "serial")
            ?? throw new InvalidOperationException("Selecione um Android.");

        PostBusy("packages", true);
        try
        {
            var items = await _adbService.FindLikelySmartPackagesAsync(serial, _shutdown.Token);
            var foregroundPackage = await _adbService.GetForegroundPackageAsync(serial, _shutdown.Token);

            if (!string.IsNullOrWhiteSpace(foregroundPackage) &&
                items.Contains(foregroundPackage, StringComparer.OrdinalIgnoreCase))
            {
                var settings = _settingsService.Load();
                settings.SmartPackageName = foregroundPackage;
                _settingsService.Save(settings);
                SendBootstrap();
            }

            PostEvent("smartPackages", new { items, serial, foregroundPackage });
        }
        finally
        {
            PostBusy("packages", false);
        }
    }

    private async Task ClearSmartDataAsync(JsonElement payload)
    {
        var serial = ReadString(payload, "serial")
            ?? throw new InvalidOperationException("Selecione um Android.");
        var settings = _settingsService.Load();
        var packageName = settings.SmartPackageName;

        if (string.IsNullOrWhiteSpace(packageName))
        {
            var foregroundPackage = await _adbService.GetForegroundPackageAsync(serial, _shutdown.Token);
            if (!string.IsNullOrWhiteSpace(foregroundPackage) &&
                (foregroundPackage.Contains("softcom", StringComparison.OrdinalIgnoreCase) ||
                 foregroundPackage.Contains("smart", StringComparison.OrdinalIgnoreCase)))
            {
                packageName = foregroundPackage;
            }
            else if (await _adbService.IsPackageInstalledAsync(serial, "softcom.mobile.smart2", _shutdown.Token))
            {
                packageName = "softcom.mobile.smart2";
            }
        }

        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new InvalidOperationException(
                "Nao foi possivel identificar automaticamente o package do Smart. Abra o Smart no Android e tente novamente.");
        }

        if (!string.Equals(settings.SmartPackageName, packageName, StringComparison.OrdinalIgnoreCase))
        {
            settings.SmartPackageName = packageName;
            _settingsService.Save(settings);
            SendBootstrap();
        }

        var result = await _adbService.ClearPackageAsync(serial, packageName, _shutdown.Token);
        if (!result.Success || !result.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? "O Android nao confirmou a limpeza dos dados."
                    : result.CombinedOutput);
        }

        PostEvent("toast", new { type = "success", message = "Dados locais do Smart limpos pelo ADB." });
    }

    private void OpenScrcpy(JsonElement payload)
    {
        var serial = ReadString(payload, "serial")
            ?? throw new InvalidOperationException("Selecione um Android.");
        _scrcpyService.PruneExitedSessions();
        _scrcpyService.OpenDevices(new[] { serial }, new MirrorOptions());
        PostEvent("toast", new { type = "success", message = $"scrcpy aberto para {serial}." });
    }

    private async Task GenerateUrlAsync(JsonElement payload)
    {
        var accessMode = ReadAccessMode(payload);
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Selecione um cliente.");
        var company = ReadObject<CompanyInfo>(payload, "company")
            ?? throw new InvalidOperationException("Selecione uma empresa.");
        var oauthClient = ReadObject<OAuthClientInfo>(payload, "oauthClient")
            ?? throw new InvalidOperationException("Selecione um dispositivo Softcomshop.");

        var module = ReadString(payload, "module") ?? "smart_pdv";
        var selfHostBaseUrl = ReadString(payload, "selfHostBaseUrl");

        if (ShouldUseSelfHost(payload, module))
        {
            var selfHostUrl = _selfHostDeviceService.BuildUrl(oauthClient, company, selfHostBaseUrl);
            WriteLog("SELFHOST", $"URL gerada com client_id real do SelfHost para {oauthClient.Name}.");
            PostEvent("generatedUrl", new { url = selfHostUrl, accessMode, module, selfHost = true });
            return;
        }

        string url;
        if (IsOnlineMode(accessMode))
        {
            var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
            url = await ExecuteOnlineAsync(
                normalizedDatabase,
                "obter URL do dispositivo",
                service => service.GetDeviceUrlAsync(normalizedDatabase, oauthClient.ClientId, _shutdown.Token));
            WriteLog("ONLINE", $"URL do dispositivo {oauthClient.Name} obtida por /softauth/device/anexar.");
        }
        else
        {
            url = ProvisioningService.BuildDeviceUrl(database, company, oauthClient);
        }

        PostEvent("generatedUrl", new { url, accessMode, module, selfHost = false });
    }

    private void EvaluateLink(JsonElement payload)
    {
        var oauthClient = ReadObject<OAuthClientInfo>(payload, "oauthClient")
            ?? throw new InvalidOperationException("Selecione um dispositivo Softcomshop.");
        var serial = ReadString(payload, "serial");
        var android = _androidDevices.FirstOrDefault(
            x => string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase));

        PostEvent("linkEvaluation", ProvisioningService.EvaluateLink(oauthClient, android));
    }

    private async Task ValidatePreparationAsync(JsonElement payload)
    {
        PostBusy("validation", true);
        try
        {
            var accessMode = ReadAccessMode(payload);
            var database = ReadString(payload, "database")
                ?? throw new InvalidOperationException("Selecione um cliente.");
            var company = ReadObject<CompanyInfo>(payload, "company")
                ?? throw new InvalidOperationException("Selecione uma empresa.");
            var oauthClient = ReadObject<OAuthClientInfo>(payload, "oauthClient")
                ?? throw new InvalidOperationException("Selecione um dispositivo Softcomshop.");
            var serial = ReadString(payload, "serial")
                ?? throw new InvalidOperationException("Selecione um Android.");
            var module = ReadString(payload, "module") ?? "smart_pdv";
            var selfHostBaseUrl = ReadString(payload, "selfHostBaseUrl");

            var android = _androidDevices.FirstOrDefault(
                x => string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase));
            if (android is null)
            {
                throw new InvalidOperationException(
                    "O Android selecionado nao esta mais disponivel no ADB. Clique em Atualizar e tente novamente.");
            }

            var evaluation = ProvisioningService.EvaluateLink(oauthClient, android);
            string url;
            if (ShouldUseSelfHost(payload, module))
            {
                url = _selfHostDeviceService.BuildUrl(oauthClient, company, selfHostBaseUrl);
            }
            else if (IsOnlineMode(accessMode))
            {
                var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
                url = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "validar URL do dispositivo",
                    service => service.GetDeviceUrlAsync(normalizedDatabase, oauthClient.ClientId, _shutdown.Token));
            }
            else
            {
                url = ProvisioningService.BuildDeviceUrl(database, company, oauthClient);
            }

            PostEvent("preparationValidated", new
            {
                evaluation,
                url,
                module,
                accessMode,
                database = EnvironmentCatalog.DatabaseDisplayName(database),
                company = company.Name,
                oauthClient = oauthClient.Name,
                android = new
                {
                    android.Serial,
                    android.Model,
                    android.AndroidId
                }
            });
        }
        finally
        {
            PostBusy("validation", false);
        }
    }

    private async Task PrepareSmartAsync(JsonElement payload)
    {
        PostBusy("provision", true);
        var vpnDisconnected = false;
        try
        {
            var accessMode = ReadAccessMode(payload);
            var environment = ReadString(payload, "environment") ?? "aws1";
            var serial = ReadString(payload, "serial")
                ?? throw new InvalidOperationException("Selecione um Android.");
            var module = ReadString(payload, "module") ?? "smart_pdv";
            var selfHostBaseUrl = ReadString(payload, "selfHostBaseUrl");
            var clearData = ReadBool(payload, "clearData", true);

            var android = _androidDevices.FirstOrDefault(
                x => string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase));
            if (android is null)
            {
                throw new InvalidOperationException(
                    "O Android selecionado nao esta mais disponivel no ADB. Clique em Atualizar e tente novamente.");
            }

            void Progress(string stage, string message)
            {
                WriteLog("SMART", $"[{stage}] {message}");
                PostEvent("provisionProgress", new { stage, message });
            }

            // Smart TEF possui fluxo proprio e nao usa URL/oauth_clients nesta etapa.
            // O XML confirmado do app RedeFlex expoe Nome do dispositivo, CNPJ, Empresa ID e Token.
            if (string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase))
            {
                var deviceName = ReadString(payload, "tefDeviceName")?.Trim();
                var cnpj = ReadString(payload, "tefCnpj")?.Trim();
                var empresaId = ReadString(payload, "tefEmpresaId")?.Trim();
                var token = ReadString(payload, "tefToken")?.Trim();

                if (string.IsNullOrWhiteSpace(deviceName))
                    throw new InvalidOperationException("Informe o Nome do dispositivo do Smart TEF.");
                if (string.IsNullOrWhiteSpace(cnpj))
                    throw new InvalidOperationException("Informe o CNPJ do Smart TEF.");
                if (string.IsNullOrWhiteSpace(empresaId))
                    throw new InvalidOperationException("Informe o Empresa ID do Smart TEF.");
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Informe o Token do Smart TEF.");

                if (_vpnService.IsOpenVpnRunning())
                {
                    Progress("vpn-off", "Desativando a VPN antes de configurar o Smart TEF...");
                    var vpnDisconnect = await _vpnService.DisconnectAsync(_shutdown.Token);
                    vpnDisconnected = true;
                    PostEvent("vpnState", new { connected = false, message = vpnDisconnect });
                    await Task.Delay(1200, _shutdown.Token);
                }

                var tefAutomation = await _smartAutomationService.SubmitSmartTefAsync(
                    serial,
                    deviceName,
                    cnpj,
                    empresaId,
                    token,
                    clearData,
                    Progress,
                    _shutdown.Token);

                WriteLog(
                    "SMART TEF",
                    tefAutomation.Success
                        ? $"Configuracao enviada para {tefAutomation.PackageName}. Device ID: {tefAutomation.SmartDeviceId}."
                        : $"Falha na configuracao: {tefAutomation.Message}",
                    tefAutomation.Success ? "INFO" : "WARN");

                PostEvent("smartPreparationFinished", new
                {
                    success = tefAutomation.Success,
                    module = "smart_tef",
                    tefAutomation.Stage,
                    message = tefAutomation.Message,
                    tefAutomation.PackageName,
                    tefAutomation.UiSummary,
                    tefAutomation.SmartDeviceId,
                    adbAndroidId = android.AndroidId,
                    tefDeviceName = deviceName
                });
                return;
            }

            var database = ReadString(payload, "database")
                ?? throw new InvalidOperationException("Selecione um cliente.");
            var company = ReadObject<CompanyInfo>(payload, "company")
                ?? throw new InvalidOperationException("Selecione uma empresa.");
            var oauthClient = ReadObject<OAuthClientInfo>(payload, "oauthClient")
                ?? throw new InvalidOperationException(ShouldUseSelfHost(payload, module) ? "Selecione um dispositivo SelfHost." : "Selecione um dispositivo Softcomshop.");

            if (ShouldUseSelfHost(payload, module))
            {
                // Quando o vínculo usa SelfHost, ele precisa ser liberado no conjunto de
                // dispositivos do próprio SelfHost antes de enviar a URL ao Smart. Não abortamos
                // mais apenas porque o cadastro selecionado já possui device_id.
                if (oauthClient.IsLinked)
                {
                    Progress("selfhost-unlink-selected", $"Desvinculando {oauthClient.Name} do Device ID {oauthClient.DeviceId}...");
                    var selectedUnlinked = await _selfHostDeviceService.UnlinkDeviceAsync(oauthClient.ClientId, _shutdown.Token);
                    if (!selectedUnlinked)
                        throw new InvalidOperationException($"O SelfHost não confirmou a desvinculação de {oauthClient.Name}.");
                    Progress("selfhost-unlink-selected", "Cadastro SelfHost selecionado desvinculado com sucesso.");
                }

                var selfHostUrl = _selfHostDeviceService.BuildUrl(oauthClient, company, selfHostBaseUrl);
                Progress("selfhost-url", $"Usando client_id do próprio SelfHost: {oauthClient.Name}.");
                Progress("selfhost-url", $"Destino local: {new Uri(selfHostUrl).GetLeftPart(UriPartial.Authority)}/device/add.");

                var selfHostSettings = _settingsService.Load();
                async Task BeforeSubmitSelfHostAsync(string smartDeviceId)
                {
                    var host = new Uri(selfHostUrl).Host;
                    Progress("selfhost-check", $"Preparando vínculo local para Device ID {smartDeviceId} em {host}...");

                    // O mesmo Device ID pode estar preso em outro cadastro SelfHost. Esse é o
                    // cenário que faz /device/add responder que o dispositivo já está em uso.
                    // Localizamos e liberamos todos os conflitos ANTES de confirmar a URL.
                    var currentDevices = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                    var conflicts = currentDevices
                        .Where(x => !string.IsNullOrWhiteSpace(x.DeviceId) &&
                                    string.Equals(x.DeviceId, smartDeviceId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    foreach (var conflict in conflicts)
                    {
                        Progress("selfhost-unlink-conflict", $"Device ID {smartDeviceId} já está vinculado em {conflict.Name}. Desvinculando...");
                        var unlinked = await _selfHostDeviceService.UnlinkDeviceAsync(conflict.ClientId, _shutdown.Token);
                        if (!unlinked)
                            throw new InvalidOperationException($"Não foi possível liberar o Device ID {smartDeviceId} do cadastro SelfHost {conflict.Name}.");
                    }

                    if (conflicts.Length > 0)
                        Progress("selfhost-unlink-conflict", $"Device ID {smartDeviceId} liberado. Continuando o novo vínculo...");

                    if (_vpnService.IsOpenVpnRunning())
                    {
                        var vpnDisconnect = await _vpnService.DisconnectAsync(_shutdown.Token);
                        vpnDisconnected = true;
                        PostEvent("vpnState", new { connected = false, message = vpnDisconnect });
                    }
                }

                var selfHostAutomation = await _smartAutomationService.SubmitDeviceUrlAsync(
                    serial, selfHostUrl, module, selfHostSettings.SmartPackageName, clearData, Progress, BeforeSubmitSelfHostAsync, _shutdown.Token);

                if (!selfHostAutomation.Success)
                {
                    PostEvent("smartPreparationFinished", new { success = false, module, accessMode = "selfhost", selfHostAutomation.Stage, message = selfHostAutomation.Message, selfHostAutomation.PackageName, selfHostAutomation.UiSummary, selfHostAutomation.SmartDeviceId, adbAndroidId = android.AndroidId, url = selfHostUrl });
                    return;
                }

                var selfHostExpectedDeviceId = !string.IsNullOrWhiteSpace(selfHostAutomation.SmartDeviceId) ? selfHostAutomation.SmartDeviceId : android.AndroidId;
                Progress("selfhost-verify", $"Aguardando o SelfHost registrar o Device ID {selfHostExpectedDeviceId}...");
                OAuthClientInfo? selfHostRefreshed = null;
                IReadOnlyList<OAuthClientInfo> selfHostItems = Array.Empty<OAuthClientInfo>();
                for (var attempt = 0; attempt < 15; attempt++)
                {
                    await Task.Delay(1000, _shutdown.Token);
                    selfHostItems = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                    selfHostRefreshed = selfHostItems.FirstOrDefault(x => string.Equals(x.ClientId, oauthClient.ClientId, StringComparison.Ordinal));
                    if (selfHostRefreshed is not null && !string.IsNullOrWhiteSpace(selfHostRefreshed.DeviceId)) break;
                }

                var selfHostLinked = selfHostRefreshed is not null && string.Equals(selfHostRefreshed.DeviceId, selfHostExpectedDeviceId, StringComparison.OrdinalIgnoreCase);
                PostEvent("oauthClients", new { items = selfHostItems, companyId = company.Id, accessMode = "selfhost" });
                PostEvent("smartPreparationFinished", new
                {
                    success = selfHostLinked, module, accessMode = "selfhost", stage = selfHostLinked ? "linked" : "verify",
                    message = selfHostLinked ? $"Dispositivo SelfHost vinculado com sucesso. device_id = {selfHostExpectedDeviceId}." : $"A URL do SelfHost foi enviada, mas o vínculo ainda não foi confirmado para {selfHostExpectedDeviceId}.",
                    packageName = selfHostAutomation.PackageName, smartDeviceId = selfHostAutomation.SmartDeviceId, adbAndroidId = android.AndroidId, url = selfHostUrl
                });
                return;
            }

            if (IsOnlineMode(accessMode))
            {
                var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);

                // Se o cadastro selecionado ja estiver vinculado, liberamos esse vinculo antes
                // de abrir/limpar o Smart. Isso evita depender do callback da automacao para
                // remover o device_id do proprio cadastro que sera reutilizado.
                if (oauthClient.IsLinked)
                {
                    Progress("online-unlink-selected", $"Desvinculando o cadastro selecionado {oauthClient.Name} antes da preparacao...");
                    var selectedUnlinked = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        $"desvincular {oauthClient.Name}",
                        service => service.UnlinkOAuthClientAsync(
                            normalizedDatabase,
                            company.Id,
                            oauthClient.ClientId,
                            _shutdown.Token));

                    if (!selectedUnlinked)
                    {
                        throw new InvalidOperationException($"O Softcomshop nao confirmou a desvinculacao de {oauthClient.Name}.");
                    }

                    var afterSelectedUnlink = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        "confirmar desvinculacao do cadastro selecionado",
                        service => service.GetOAuthClientsAsync(normalizedDatabase, company.Id, _shutdown.Token));
                    var stillLinkedSelected = afterSelectedUnlink.FirstOrDefault(x =>
                        string.Equals(x.ClientId, oauthClient.ClientId, StringComparison.Ordinal));
                    if (stillLinkedSelected is not null && stillLinkedSelected.IsLinked)
                    {
                        throw new InvalidOperationException(
                            $"O cadastro {oauthClient.Name} ainda aparece vinculado no Softcomshop apos a tentativa de desvinculacao.");
                    }

                    Progress("online-unlink-selected", "Cadastro selecionado desvinculado com sucesso.");
                    PostEvent("oauthClients", new { items = afterSelectedUnlink, companyId = company.Id, accessMode = "online" });
                }

                var onlineUrl = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "obter URL para preparar o Smart",
                    service => service.GetDeviceUrlAsync(normalizedDatabase, oauthClient.ClientId, _shutdown.Token));
                if (ShouldUseSelfHost(payload, module))
                {
                    onlineUrl = BuildSelfHostDeviceUrl(onlineUrl, selfHostBaseUrl);
                    Progress("selfhost-url", $"Usando Selfhost em {new Uri(onlineUrl).GetLeftPart(UriPartial.Authority)} para gerar o vinculo do {module}.");
                }
                var onlineSettings = _settingsService.Load();

                async Task BeforeSubmitOnlineAsync(string smartDeviceId)
                {
                    Progress("online-unlink-check", $"Verificando vinculos do Device ID {smartDeviceId} pelo Softcomshop...");
                    var items = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        "verificar vinculos anteriores",
                        service => service.GetOAuthClientsAsync(normalizedDatabase, company.Id, _shutdown.Token));

                    var toUnlink = new Dictionary<string, OAuthClientInfo>(StringComparer.Ordinal);

                    // Aqui tratamos apenas conflitos do Device ID informado pelo Smart.
                    // O cadastro selecionado ja foi liberado antes do inicio da automacao.
                    foreach (var conflict in items.Where(x =>
                                 string.Equals(x.DeviceId, smartDeviceId, StringComparison.OrdinalIgnoreCase)))
                    {
                        toUnlink[conflict.ClientId] = conflict;
                    }

                    if (toUnlink.Count > 0)
                    {
                        Progress("online-unlink", "Desvinculando pelo Softcomshop: " + string.Join(", ", toUnlink.Values.Select(x => x.Name)) + "...");
                        foreach (var item in toUnlink.Values)
                        {
                            var changed = await ExecuteOnlineAsync(
                                normalizedDatabase,
                                $"desvincular {item.Name}",
                                service => service.UnlinkOAuthClientAsync(
                                    normalizedDatabase,
                                    company.Id,
                                    item.ClientId,
                                    _shutdown.Token));
                            if (!changed)
                            {
                                throw new InvalidOperationException($"O Softcomshop nao confirmou a desvinculacao de {item.Name}.");
                            }
                        }
                    }
                    else
                    {
                        Progress("online-unlink", "Nenhum vinculo anterior precisa ser removido.");
                    }

                    var refreshed = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        "confirmar desvinculacao",
                        service => service.GetOAuthClientsAsync(normalizedDatabase, company.Id, _shutdown.Token));
                    var remaining = refreshed.Where(x =>
                        string.Equals(x.DeviceId, smartDeviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (remaining.Length > 0)
                    {
                        throw new InvalidOperationException(
                            "O Device ID ainda aparece vinculado no Softcomshop: " + string.Join(", ", remaining.Select(x => x.Name)) + ".");
                    }

                    if (_vpnService.IsOpenVpnRunning())
                    {
                        Progress("vpn-off", "Uma sessao OpenVPN esta ativa. Desativando antes de o Smart consumir a URL publica...");
                        var vpnDisconnect = await _vpnService.DisconnectAsync(_shutdown.Token);
                        vpnDisconnected = true;
                        PostEvent("vpnState", new { connected = false, message = vpnDisconnect });
                    }

                    var publicHost = new Uri(onlineUrl).Host;
                    var resolved = false;
                    for (var attempt = 0; attempt < 8; attempt++)
                    {
                        await Task.Delay(attempt == 0 ? 900 : 650, _shutdown.Token);
                        if (await _adbService.CanResolveHostAsync(serial, publicHost, _shutdown.Token))
                        {
                            resolved = true;
                            break;
                        }
                    }
                    if (!resolved)
                    {
                        throw new InvalidOperationException($"O Android nao consegue resolver {publicHost}. A configuracao foi interrompida antes de confirmar a URL.");
                    }
                    Progress(
                        ShouldUseSelfHost(payload, module) ? "selfhost-ready" : "online-ready",
                        ShouldUseSelfHost(payload, module)
                            ? $"Softcomshop preparado e Android com acesso ao Selfhost em {publicHost}:7711."
                            : "Softcomshop preparado e Android com acesso publico. Nenhuma VPN/banco foi utilizado.");
                }

                var onlineAutomation = await _smartAutomationService.SubmitDeviceUrlAsync(
                    serial,
                    onlineUrl,
                    module,
                    onlineSettings.SmartPackageName,
                    clearData,
                    Progress,
                    BeforeSubmitOnlineAsync,
                    _shutdown.Token);

                if (!onlineAutomation.Success)
                {
                    PostEvent("smartPreparationFinished", new
                    {
                        success = false,
                        module,
                        accessMode = "online",
                        onlineAutomation.Stage,
                        message = onlineAutomation.Message,
                        onlineAutomation.PackageName,
                        onlineAutomation.UiSummary,
                        onlineAutomation.SmartDeviceId,
                        adbAndroidId = android.AndroidId,
                        url = onlineUrl
                    });
                    return;
                }

                if (!string.Equals(onlineSettings.SmartPackageName, onlineAutomation.PackageName, StringComparison.OrdinalIgnoreCase))
                {
                    onlineSettings.SmartPackageName = onlineAutomation.PackageName;
                    _settingsService.Save(onlineSettings);
                    SendBootstrap();
                }

                var onlineExpectedDeviceId = !string.IsNullOrWhiteSpace(onlineAutomation.SmartDeviceId)
                    ? onlineAutomation.SmartDeviceId
                    : android.AndroidId;

                Progress("online-verify", $"Aguardando o Softcomshop registrar o Device ID {onlineExpectedDeviceId}...");
                OAuthClientInfo? refreshedClient = null;
                IReadOnlyList<OAuthClientInfo> refreshedItems = Array.Empty<OAuthClientInfo>();
                for (var attempt = 0; attempt < 15; attempt++)
                {
                    await Task.Delay(1000, _shutdown.Token);
                    refreshedItems = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        "validar vinculo",
                        service => service.GetOAuthClientsAsync(normalizedDatabase, company.Id, _shutdown.Token));
                    refreshedClient = refreshedItems.FirstOrDefault(x =>
                        string.Equals(x.ClientId, oauthClient.ClientId, StringComparison.Ordinal));
                    if (refreshedClient is not null && !string.IsNullOrWhiteSpace(refreshedClient.DeviceId))
                    {
                        break;
                    }
                }

                var onlineLinked = refreshedClient is not null &&
                             string.Equals(refreshedClient.DeviceId, onlineExpectedDeviceId, StringComparison.OrdinalIgnoreCase);
                if (onlineLinked)
                {
                    WriteLog("ONLINE", $"Vinculo confirmado pelo Softcomshop para Device ID {onlineExpectedDeviceId}.");
                    PostEvent("smartPreparationFinished", new
                    {
                        success = true,
                        module,
                        accessMode = "online",
                        stage = "linked",
                        message = $"Dispositivo vinculado com sucesso. O Softcomshop confirmou device_id = {onlineExpectedDeviceId}.",
                        packageName = onlineAutomation.PackageName,
                        url = onlineUrl,
                        deviceId = onlineExpectedDeviceId,
                        smartDeviceId = onlineAutomation.SmartDeviceId,
                        adbAndroidId = android.AndroidId
                    });
                    PostEvent("oauthClients", new { items = refreshedItems, companyId = company.Id, accessMode = "online" });
                    return;
                }

                var onlineCurrentDevice = refreshedClient?.DeviceId ?? string.Empty;
                PostEvent("smartPreparationFinished", new
                {
                    success = false,
                    module,
                    accessMode = "online",
                    stage = "verify",
                    message = string.IsNullOrWhiteSpace(onlineCurrentDevice)
                        ? $"A URL foi enviada ao Smart, mas o Softcomshop ainda nao registrou o Device ID esperado ({onlineExpectedDeviceId})."
                        : $"O cadastro passou a ter device_id {onlineCurrentDevice}, diferente do esperado ({onlineExpectedDeviceId}).",
                    packageName = onlineAutomation.PackageName,
                    onlineAutomation.UiSummary,
                    smartDeviceId = onlineAutomation.SmartDeviceId,
                    adbAndroidId = android.AndroidId,
                    url = onlineUrl
                });
                return;
            }

            var url = ProvisioningService.BuildDeviceUrl(database, company, oauthClient);
            if (ShouldUseSelfHost(payload, module))
            {
                url = BuildSelfHostDeviceUrl(url, selfHostBaseUrl);
                Progress("selfhost-url", $"Usando Selfhost em {new Uri(url).GetLeftPart(UriPartial.Authority)} para gerar o vinculo do {module}.");
            }
            var settings = _settingsService.Load();

            async Task BeforeSubmitAsync(string smartDeviceId)
            {
                // No modo Docker, a VPN fica confinada ao container e o MySQL e publicado apenas
                // em 127.0.0.1:13306. No modo VPN local, preservamos o comportamento legado.
                if (IsDockerMode(accessMode))
                {
                    Progress("docker-db", "Confirmando o DB Bridge isolado antes de verificar vinculos anteriores...");
                    await EnsureDatabaseAccessAsync(environment, "verificar e liberar vinculos anteriores", accessMode);
                }
                else
                {
                    var databaseEnvironment = EnvironmentCatalog.Get(environment);
                    if (!await _vpnService.CanReachAsync(databaseEnvironment.Host, databaseEnvironment.Port))
                    {
                        Progress("vpn-on", "Ativando a VPN para verificar e liberar vinculos anteriores no banco...");
                        var vpnConnect = await _vpnService.ConnectAsync(environment, _shutdown.Token);
                        PostEvent("vpnState", new { connected = true, message = vpnConnect });
                    }
                    else
                    {
                        Progress("vpn-db", "Acesso ao banco confirmado. Mantendo a VPN ativa ate concluir a verificacao dos vinculos anteriores.");
                    }
                }

                Progress("unlink-check", $"Verificando vinculos anteriores do Device ID {smartDeviceId}...");

                var toUnlink = new Dictionary<string, OAuthClientInfo>(StringComparer.Ordinal);

                var selectedCurrent = await _databaseService.GetOAuthClientAsync(
                    environment,
                    database,
                    company.Id,
                    oauthClient.ClientId,
                    _shutdown.Token);

                if (selectedCurrent is not null && selectedCurrent.IsLinked)
                {
                    toUnlink[selectedCurrent.ClientId] = selectedCurrent;
                }

                var conflicts = await _databaseService.GetOAuthClientsByDeviceIdAsync(
                    environment,
                    database,
                    company.Id,
                    smartDeviceId,
                    _shutdown.Token);

                foreach (var conflict in conflicts)
                {
                    toUnlink[conflict.ClientId] = conflict;
                }

                if (toUnlink.Count > 0)
                {
                    var names = string.Join(", ", toUnlink.Values.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase));
                    Progress("unlink", $"Desvinculando cadastro(s) anterior(es): {names}...");

                    foreach (var item in toUnlink.Values)
                    {
                        var changed = await _databaseService.UnlinkOAuthClientAsync(
                            environment,
                            database,
                            company.Id,
                            item.ClientId,
                            _shutdown.Token);

                        if (!changed)
                        {
                            throw new InvalidOperationException(
                                $"O cadastro {item.Name} foi localizado como vinculado, mas nao foi possivel limpar o device_id.");
                        }
                    }

                    var remaining = await _databaseService.GetOAuthClientsByDeviceIdAsync(
                        environment,
                        database,
                        company.Id,
                        smartDeviceId,
                        _shutdown.Token);
                    if (remaining.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "O Device ID ainda esta vinculado em oauth_clients apos a tentativa de desvinculacao: " +
                            string.Join(", ", remaining.Select(x => x.Name)) + ".");
                    }

                    Progress("unlink", "Vinculo anterior removido. O Device ID esta livre para reutilizacao.");
                }
                else
                {
                    Progress("unlink", "Nenhum vinculo anterior encontrado para este Device ID.");
                }

                // Com Docker, nenhuma rota do Windows/Android foi alterada, portanto nao existe
                // etapa de desligar VPN. No modo legado, desligamos a VPN local antes do Smart.
                if (IsDockerMode(accessMode))
                {
                    Progress("docker-network", "Banco verificado pelo Docker isolado. Windows e Android continuam na rede normal.");
                }
                else if (_vpnService.IsOpenVpnRunning())
                {
                    Progress("vpn-off", "Vinculos no banco verificados. Desativando a VPN antes de configurar o Smart...");
                    var vpnDisconnect = await _vpnService.DisconnectAsync(_shutdown.Token);
                    vpnDisconnected = true;
                    PostEvent("vpnState", new { connected = false, message = vpnDisconnect });
                }
                else
                {
                    Progress("vpn-off", "Vinculos no banco verificados. Nenhuma sessao OpenVPN esta ativa; seguindo para a configuracao do Smart.");
                }

                var publicHost = new Uri(url).Host;
                var resolved = false;
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 900 : 700, _shutdown.Token);
                    if (await _adbService.CanResolveHostAsync(serial, publicHost, _shutdown.Token))
                    {
                        resolved = true;
                        break;
                    }
                }

                if (!resolved)
                    throw new InvalidOperationException($"O Android nao consegue resolver {publicHost}. A URL nao foi confirmada para evitar falha no vinculo.");

                Progress("network", $"Android com acesso ao host {publicHost}.");
            }

            var automation = await _smartAutomationService.SubmitDeviceUrlAsync(
                serial,
                url,
                module,
                settings.SmartPackageName,
                clearData,
                Progress,
                BeforeSubmitAsync,
                _shutdown.Token);

            if (!automation.Success)
            {
                PostEvent("smartPreparationFinished", new
                {
                    success = false,
                    module,
                    automation.Stage,
                    message = automation.Message,
                    automation.PackageName,
                    automation.UiSummary,
                    automation.SmartDeviceId,
                    adbAndroidId = android.AndroidId,
                    url
                });
                return;
            }

            if (!string.Equals(settings.SmartPackageName, automation.PackageName, StringComparison.OrdinalIgnoreCase))
            {
                settings.SmartPackageName = automation.PackageName;
                _settingsService.Save(settings);
                SendBootstrap();
            }

            var expectedDeviceId = !string.IsNullOrWhiteSpace(automation.SmartDeviceId)
                ? automation.SmartDeviceId
                : android.AndroidId;

            Progress("settle", "Aguardando o Smart concluir o vinculo fora da VPN...");
            await Task.Delay(5000, _shutdown.Token);

            if (IsDockerMode(accessMode))
            {
                await EnsureDatabaseAccessAsync(environment, "validar o vinculo no banco", accessMode);
                Progress("docker-verify", "Validando o vinculo pelo banco atraves do Docker isolado...");
            }
            else if (vpnDisconnected)
            {
                Progress("vpn-on", "Ativando a VPN somente para validar o vinculo no banco...");
                var vpnReconnect = await _vpnService.ConnectAsync(environment, _shutdown.Token);
                PostEvent("vpnState", new { connected = true, message = vpnReconnect });
            }

            Progress(
                "verify",
                !string.IsNullOrWhiteSpace(automation.SmartDeviceId)
                    ? $"Aguardando o Softcomshop registrar o Device ID exibido pelo Smart ({automation.SmartDeviceId})..."
                    : "Aguardando o Softcomshop registrar o dispositivo...");

            OAuthClientInfo? refreshed = null;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(1000, _shutdown.Token);
                refreshed = await _databaseService.GetOAuthClientAsync(
                    environment,
                    database,
                    company.Id,
                    oauthClient.ClientId,
                    _shutdown.Token);

                if (refreshed is not null &&
                    string.Equals(refreshed.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (refreshed is not null && !string.IsNullOrWhiteSpace(refreshed.DeviceId))
                {
                    break;
                }
            }

            var linked = refreshed is not null &&
                         string.Equals(refreshed.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase);

            if (linked)
            {
                WriteLog("SMART", $"Vinculo confirmado no banco para Device ID {expectedDeviceId}.");
                PostEvent("smartPreparationFinished", new
                {
                    success = true,
                    module,
                    stage = "linked",
                    message = $"Dispositivo vinculado com sucesso. O banco confirmou device_id = {expectedDeviceId}.",
                    accessMode,
                    packageName = automation.PackageName,
                    url,
                    deviceId = expectedDeviceId,
                    smartDeviceId = automation.SmartDeviceId,
                    adbAndroidId = android.AndroidId,
                    next = "Vinculo, reutilizacao de Device ID e selecao do modulo automatizados. Proxima etapa: Configurar Serie."
                });

                var items = await _databaseService.GetOAuthClientsAsync(
                    environment, database, company.Id, _shutdown.Token);
                PostEvent("oauthClients", new { items, companyId = company.Id });
                return;
            }

            var currentDevice = refreshed?.DeviceId ?? string.Empty;
            PostEvent("smartPreparationFinished", new
            {
                success = false,
                module,
                stage = "verify",
                message = string.IsNullOrWhiteSpace(currentDevice)
                    ? $"A automacao informou a URL no Smart, mas o banco ainda nao registrou o Device ID esperado ({expectedDeviceId}). Abra o scrcpy e confira a tela atual."
                    : $"O cadastro passou a ter device_id {currentDevice}, diferente do Device ID esperado ({expectedDeviceId}).",
                packageName = automation.PackageName,
                automation.UiSummary,
                smartDeviceId = automation.SmartDeviceId,
                adbAndroidId = android.AndroidId,
                url
            });
        }
        finally
        {
            // O Provisioner usa a VPN apenas enquanto precisa acessar o banco.
            // Ao terminar a preparacao (com sucesso ou erro), nao deixa a VPN ativa.
            try
            {
                if (_vpnService.IsOpenVpnRunning())
                {
                    WriteLog("VPN", "Finalizando VPN apos a preparacao. Nenhuma conexao sera mantida ativa.");
                    var disconnectMessage = await _vpnService.DisconnectAsync(CancellationToken.None);
                    PostEvent("vpnState", new { connected = false, message = disconnectMessage });
                }
                else
                {
                    PostEvent("vpnState", new
                    {
                        connected = false,
                        message = "Preparacao finalizada com a VPN desconectada."
                    });
                }
            }
            catch (Exception vpnEx)
            {
                WriteLog("VPN", "Nao foi possivel confirmar o encerramento da VPN ao final: " + vpnEx.Message, "WARN");
                PostEvent("vpnState", new
                {
                    connected = _vpnService.IsOpenVpnRunning(),
                    message = "Preparacao finalizada. Verifique o status da VPN nos Logs."
                });
            }

            PostBusy("provision", false);
        }
    }

    private void CopyText(JsonElement payload)
    {
        var text = ReadString(payload, "text") ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        Clipboard.SetText(text);
        PostEvent("toast", new { type = "success", message = "Copiado para a area de transferencia." });
    }

    private void OpenClientSite(JsonElement payload)
    {
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Selecione um cliente.");
        var url = EnvironmentCatalog.BuildSiteUrl(database);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void WriteLog(string source, string message, string level = "INFO")
    {
        var entry = _logService.Write(source, message, level);
        if (!_pageReady || _webView.CoreWebView2 is null) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => PostEvent("logEntry", entry)));
            return;
        }

        PostEvent("logEntry", entry);
    }

    private void PostEvent(string type, object payload)
    {
        if (!_pageReady || _webView.CoreWebView2 is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new { type, payload }, JsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void PostBusy(string key, bool active) =>
        PostEvent("busy", new { key, active });

    private void PostError(string message) =>
        PostEvent("toast", new { type = "error", message });

    private static bool ReadBool(JsonElement payload, string propertyName, bool defaultValue)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static bool ShouldUseSelfHost(JsonElement payload, string? module)
    {
        if (IsSelfHostModule(module)) return true;
        if (string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase)) return false;
        return ReadBool(payload, "useSelfHost", false);
    }

    private static bool IsSelfHostModule(string? module) =>
        string.Equals(module, "smart_comanda", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "smart_autopagamento", StringComparison.OrdinalIgnoreCase);

    private static string BuildSelfHostDeviceUrl(string softcomshopUrl, string? requestedBaseUrl)
    {
        if (!Uri.TryCreate(softcomshopUrl, UriKind.Absolute, out var source))
            throw new InvalidOperationException("A URL original do dispositivo Softcomshop e invalida.");

        var baseUrl = string.IsNullOrWhiteSpace(requestedBaseUrl)
            ? DetectSelfHostBaseUrl()
            : requestedBaseUrl.Trim();

        if (!baseUrl.Contains("://", StringComparison.Ordinal))
            baseUrl = "http://" + baseUrl;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var selfHost))
            throw new InvalidOperationException("Informe um endereco Selfhost valido, por exemplo http://192.168.0.10:7711.");

        var builder = new UriBuilder(selfHost)
        {
            Scheme = string.IsNullOrWhiteSpace(selfHost.Scheme) ? Uri.UriSchemeHttp : selfHost.Scheme,
            Port = selfHost.IsDefaultPort ? 7711 : selfHost.Port,
            Path = "/device/add",
            Query = source.Query.TrimStart('?')
        };
        return builder.Uri.ToString();
    }

    private static string DetectSelfHostBaseUrl()
    {
        static bool IsUsable(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => new
                {
                    Interface = n,
                    Properties = n.GetIPProperties()
                })
                .SelectMany(x => x.Properties.UnicastAddresses
                    .Where(u => IsUsable(u.Address))
                    .Select(u => new
                    {
                        Address = u.Address,
                        HasGateway = x.Properties.GatewayAddresses.Any(g =>
                            g.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.Any.Equals(g.Address) &&
                            !IPAddress.None.Equals(g.Address)),
                        IsWifiOrEthernet = x.Interface.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    }))
                .OrderByDescending(x => x.HasGateway)
                .ThenByDescending(x => x.IsWifiOrEthernet)
                .ToArray();

            var selected = candidates.FirstOrDefault()?.Address;
            if (selected is not null) return $"http://{selected}:7711";
        }
        catch
        {
            // Fallback abaixo.
        }

        return "http://127.0.0.1:7711";
    }

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private static long? ReadLong(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
        {
            return value;
        }

        return long.TryParse(element.ToString(), out var parsed) ? parsed : null;
    }

    private static T? ReadObject<T>(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var element))
        {
            return default;
        }

        return element.Deserialize<T>(JsonOptions);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Nunca bloqueia a thread da janela esperando Docker/CLI responder.
        // O encerramento do container e best-effort em segundo plano.
        try { _shutdown.Cancel(); } catch { }
        try { _ = Task.Run(() => _dockerDbBridgeService.StopAsync(CancellationToken.None)); } catch { }
        try { _scrcpyService.Dispose(); } catch { }
        try { _selfHostDeviceService.Dispose(); } catch { }
        try { _shutdown.Dispose(); } catch { }
    }
}
