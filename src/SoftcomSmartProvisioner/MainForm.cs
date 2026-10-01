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
    private readonly SmartPrinterProfileService _smartPrinterProfileService = new();
    private readonly SmartPrinterConfigurationService _smartPrinterConfigurationService;
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
    private readonly SelfHostBridgeService _selfHostBridgeService = new();
    private readonly SelfHostBackupService _selfHostBackupService = new();
    private readonly SelfHostServiceManager _selfHostServiceManager = new();
    private readonly DeviceCatalogService _deviceCatalogService;
    private readonly TestAutomationService _testAutomationService = new();
    private readonly MultiDeviceProvisioningService _multiDeviceProvisioningService = new();
    private readonly AsyncLocal<ProvisioningExecutionContext?> _provisioningContext = new();
    private readonly SemaphoreSlim _sharedInfrastructureGate = new(1, 1);
    private readonly object _settingsMutationGate = new();
    private readonly object _testAutomationGate = new();
    private CancellationTokenSource? _testAutomationRun;
    private bool _testAutomationSourceUpdating;
    private DeviceCatalogSnapshot _deviceCatalog = new(null, Array.Empty<DeviceCatalogEntry>(), Array.Empty<string>(),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

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
        _deviceCatalogService = new DeviceCatalogService();
        _deviceCatalog = _deviceCatalogService.Load();
        WriteLog("CATALOGO", _deviceCatalog.SourcePath is null
            ? "Arquivo .env de UDIDs não localizado; dispositivos serão marcados como não identificados."
            : $"Catálogo de UDIDs carregado com {_deviceCatalog.Entries.Count} mapeamento(s); somente variáveis *_UDID foram lidas.");
        if (_deviceCatalog.DuplicateSerials.Count > 0)
            WriteLog("CATALOGO", $"{_deviceCatalog.DuplicateSerials.Count} UDID(s) duplicado(s) serão marcados para validação.", "WARN");
        _multiDeviceProvisioningService.JobChanged += snapshot => PostEvent("provisionJob", snapshot);
        _adbService = new AdbService(toolsDirectory);
        _smartPrinterConfigurationService = new SmartPrinterConfigurationService(_adbService);
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
        WindowState = FormWindowState.Maximized;
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

                case "resolveDockerClient":
                    await ResolveDockerClientAsync(request.Payload);
                    break;

                case "loadCompanies":
                    await LoadCompaniesAsync(request.Payload);
                    break;

                case "connectOnline":
                    await ConnectOnlineAsync(request.Payload);
                    break;

                case "removeOnlineClient":
                    RemoveOnlineClient(request.Payload);
                    break;

                case "loadOauthClients":
                    await LoadOauthClientsAsync(request.Payload);
                    break;

                case "createOauthClient":
                    await CreateOauthClientAsync(request.Payload);
                    break;

                case "readSelfHostConfiguration":
                    await ReadSelfHostConfigurationAsync();
                    break;

                case "loadSelfHostRootDevices":
                    await LoadSelfHostRootDevicesAsync(request.Payload);
                    break;

                case "createSelfHostRootDevice":
                    await CreateSelfHostRootDeviceAsync(request.Payload);
                    break;

                case "previewSelfHostConfiguration":
                    await PreviewSelfHostConfigurationAsync(request.Payload);
                    break;

                case "configureSelfHost":
                    await ConfigureSelfHostAsync(request.Payload);
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

                case "saveSmartTefSettings":
                    SaveSmartTefSettings(request.Payload, notify: true);
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

                case "cancelProvisioningJob":
                    if (ReadString(request.Payload, "serial") is { } cancelSerial)
                        _multiDeviceProvisioningService.Cancel(cancelSerial);
                    break;

                case "cancelAllProvisioning":
                    _multiDeviceProvisioningService.CancelAll();
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

                case "loadTestAutomation":
                    await LoadTestAutomationAsync(request.Payload);
                    break;

                case "updateTestAutomationSource":
                    await UpdateTestAutomationSourceAsync(request.Payload);
                    break;

                case "runTestAutomation":
                    await RunTestAutomationAsync(request.Payload);
                    break;

                case "cancelTestAutomation":
                    CancelTestAutomation();
                    break;

                case "openTestReport":
                    OpenTestReport();
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
        var selfHostInstallation = _selfHostDeviceService.DetectInstallation();
        PostEvent("bootstrap", new
        {
            app = new
            {
                name = "Softcom Smart Provisioner",
                version = AppVersion,
                architecture = ".NET 8 + WebView2",
                phase = "1.0.4.1"
            },
            environments = EnvironmentCatalog.Environments.Values,
            selfHost = new
            {
                defaultBaseUrl = DetectSelfHostBaseUrl(),
                port = 7711,
                installed = selfHostInstallation.Installed,
                version = selfHostInstallation.Version,
                generation = selfHostInstallation.Generation,
                installPath = selfHostInstallation.InstallPath,
                configDescription = selfHostInstallation.ConfigDescription,
                legacyConfig = selfHostInstallation.HasLegacyConfig,
                modernConfig = selfHostInstallation.HasModernConfig,
                note = selfHostInstallation.Installed
                    ? $"{selfHostInstallation.Generation} detectado."
                    : "SelfHost nao localizado."
            },
            settings,
            smartTef = new
            {
                hasSavedToken = !string.IsNullOrWhiteSpace(_secretStore.Get(SecretStore.SmartTefToken))
            },
            smartPrinterProfiles = _smartPrinterProfileService.GetAll(),
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

            _androidDevices = devices
                .Select(x =>
                {
                    var identity = DeviceCatalogService.Identify(x.Serial, _deviceCatalog, x.Model);
                    return x with
                    {
                        ConfirmedSmartDeviceId = GetConfirmedSmartDeviceId(x.Serial, x.AndroidId),
                        Acquirer = identity.Acquirer,
                        TerminalModel = identity.TerminalModel,
                        FriendlyName = identity.FriendlyName,
                        IsKnownDevice = identity.IsKnownDevice,
                        IsAmbiguousIdentity = identity.IsAmbiguous,
                        IdentificationStatus = identity.Status,
                        ProvisioningProfile = identity.ProvisioningProfile
                    };
                })
                .ToArray();
            foreach (var device in _androidDevices.Where(x => x.IsOnline))
            {
                var smartInfo = string.IsNullOrWhiteSpace(device.SmartVersion)
                    ? "Smart nao detectado"
                    : $"Smart {device.SmartVersion} ({device.SmartFlow})";
                var packageInfo = string.IsNullOrWhiteSpace(device.SmartPackage) ? "nao detectado" : device.SmartPackage;
                WriteLog("ADB", $"{device.FriendlyName} / {device.Serial}: modelo Android {device.Model}; Android {device.AndroidVersion} (SDK {device.AndroidSdk}); package {packageInfo}; {smartInfo}.");
            }
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

                RememberOnlineClient(normalizedDatabase);

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

            RememberOnlineClient(normalizedDatabase);

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

    private void RemoveOnlineClient(JsonElement payload)
    {
        var database = ReadString(payload, "database")
            ?? throw new InvalidOperationException("Informe o cliente salvo que deseja remover.");
        var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
        if (string.IsNullOrWhiteSpace(normalizedDatabase))
        {
            throw new InvalidOperationException("Cliente salvo invalido.");
        }

        var settings = _settingsService.Load();
        settings.RecentOnlineClients = (settings.RecentOnlineClients ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(EnvironmentCatalog.NormalizeDatabaseName)
            .Where(x => !x.Equals(normalizedDatabase, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        if (!string.IsNullOrWhiteSpace(settings.LastOnlineClient) &&
            EnvironmentCatalog.NormalizeDatabaseName(settings.LastOnlineClient)
                .Equals(normalizedDatabase, StringComparison.OrdinalIgnoreCase))
        {
            settings.LastOnlineClient = string.Empty;
        }

        if (string.Equals(settings.AccessMode, "online", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.LastDatabase) &&
            EnvironmentCatalog.NormalizeDatabaseName(settings.LastDatabase)
                .Equals(normalizedDatabase, StringComparison.OrdinalIgnoreCase))
        {
            settings.LastDatabase = string.Empty;
        }

        _settingsService.Save(settings);
        var displayName = EnvironmentCatalog.DatabaseDisplayName(normalizedDatabase);
        WriteLog("CONFIG", $"Cliente Online salvo removido da lista local: {displayName}.");
        PostEvent("onlineClientRemoved", new
        {
            removed = displayName,
            items = settings.RecentOnlineClients.Select(EnvironmentCatalog.DatabaseDisplayName).ToArray(),
            current = string.IsNullOrWhiteSpace(settings.LastOnlineClient)
                ? string.Empty
                : EnvironmentCatalog.DatabaseDisplayName(settings.LastOnlineClient)
        });
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
            var listScope = "default";
            if (ShouldUseSelfHost(payload, module))
            {
                var selfHostInstallation = RequireSelfHostInstallation();
                await EnsureSelfHostTargetsClientAsync(normalizedDatabase, selfHostInstallation);
                items = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                listScope = IsModernSelfHost(selfHostInstallation) ? "selfhost41" : "selfhost40";
                WriteLog(
                    "SELFHOST",
                    IsModernSelfHost(selfHostInstallation)
                        ? $"{items.Count} dispositivo(s) SELFHOST_ carregado(s) pela API administrativa do SelfHost {selfHostInstallation.Version}."
                        : $"{items.Count} dispositivo(s) carregado(s) pelo fluxo legado do SelfHost {selfHostInstallation.Version}.");
            }
            else if (IsOnlineMode(accessMode))
            {
                items = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "listar dispositivos",
                    service => service.GetOAuthClientsAsync(normalizedDatabase, companyId, _shutdown.Token));
                listScope = "softcomshop";
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
            PostEvent("oauthClients", new { items, accessMode, listScope });
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
                var nfceSeries = ReadString(payload, "series")?.Trim() ?? string.Empty;
                var nfceInitialNumberText = ReadString(payload, "initialNumber")?.Trim() ?? string.Empty;
                var nfeSeries = ReadString(payload, "nfeSeries")?.Trim() ?? string.Empty;
                var nfeInitialNumberText = ReadString(payload, "nfeInitialNumber")?.Trim() ?? string.Empty;
                var selfHostInstallation = RequireSelfHostInstallation();
                await EnsureSelfHostTargetsClientAsync(normalizedDatabase, selfHostInstallation);
                var modernSelfHost = IsModernSelfHost(selfHostInstallation);

                var useNfce = !modernSelfHost || !string.IsNullOrWhiteSpace(nfceSeries);
                var effectiveSeries = useNfce ? nfceSeries : nfeSeries;
                var effectiveInitialNumber = useNfce ? nfceInitialNumberText : nfeInitialNumberText;
                var documentLabel = useNfce ? "NFC-e" : "NF-e";
                if (string.IsNullOrWhiteSpace(effectiveSeries))
                    throw new InvalidOperationException(modernSelfHost
                        ? "Informe a série NFC-e ou, como alternativa, a série NF-e."
                        : "Informe a série NFC-e do dispositivo SelfHost.");
                if (!int.TryParse(effectiveSeries, out var seriesValue) || seriesValue < 0)
                    throw new InvalidOperationException($"Informe uma série {documentLabel} válida.");
                if (!int.TryParse(effectiveInitialNumber, out var initialNumber) || initialNumber < 1)
                    throw new InvalidOperationException($"Informe uma numeração inicial {documentLabel} válida.");

                if (!string.IsNullOrWhiteSpace(nfceSeries))
                {
                    if (!int.TryParse(nfceSeries, out var parsedNfceSeries) || parsedNfceSeries < 0)
                        throw new InvalidOperationException("Informe uma série NFC-e válida.");
                    if (!int.TryParse(nfceInitialNumberText, out var parsedNfceNumber) || parsedNfceNumber < 1)
                        throw new InvalidOperationException("Informe uma numeração inicial NFC-e válida.");
                }

                item = await _selfHostDeviceService.CreateDeviceAsync(
                    name, effectiveSeries, effectiveInitialNumber, _shutdown.Token);

                // O endpoint administrativo oficial do SelfHost recebe uma única série.
                // No 4.0 ela é a NFC-e; no 4.1+ a NF-e funciona apenas como fallback
                // quando a NFC-e não foi informada. Não há uma segunda gravação fiscal.

                items = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                var listedItem = items.FirstOrDefault(x =>
                    (!string.IsNullOrWhiteSpace(item.ClientId) && x.ClientId == item.ClientId) ||
                    x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (listedItem is not null) item = listedItem;

                WriteLog(
                    "SELFHOST",
                    $"Dispositivo {item.Name} criado no SelfHost {selfHostInstallation.Version} com {documentLabel} série {seriesValue}, numeração inicial {initialNumber}; lista administrativa atualizada uma vez.");
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
        catch (Exception ex) when (useSelfHost)
        {
            WriteLog("SELFHOST", $"Falha ao criar dispositivo: {ex.Message}", "ERROR");
            PostEvent("selfHostCreateError", new { message = ex.Message });
        }
        finally
        {
            PostBusy("createDevice", false);
        }
    }

    private async Task ReadSelfHostConfigurationAsync()
    {
        PostBusy("selfHostConfiguration", true);
        try
        {
            var installation = RequireSelfHostInstallation();
            var result = await _selfHostBridgeService.ReadAsync(installation.InstallPath, _shutdown.Token);
            WriteLog(
                "SELFHOST",
                $"Configuração oficial lida do {installation.Generation} {installation.Version}; estado: {(result.Configuration?.IsComplete == true ? "configurado" : "incompleto")}.");
            PostEvent("selfHostConfigurationRead", result);
        }
        catch (Exception ex)
        {
            WriteLog("SELFHOST", "Falha ao ler configuração: " + SensitiveDataSanitizer.Clean(ex.Message), "ERROR");
            PostEvent("selfHostConfigurationError", new { stage = "read", message = SensitiveDataSanitizer.Clean(ex.Message) });
        }
        finally
        {
            PostBusy("selfHostConfiguration", false);
        }
    }

    private async Task LoadSelfHostRootDevicesAsync(JsonElement payload)
    {
        PostBusy("selfHostRootDevices", true);
        try
        {
            _ = RequireSelfHostInstallation();
            var context = ReadSelfHostShopContext(payload);
            var items = await LoadEligibleRootDevicesAsync(context.Database, context.CompanyId);
            WriteLog("SELFHOST", $"{items.Count} dispositivo(s) raiz elegível(is) localizado(s) para {context.CompanyId}. Dispositivos SELFHOST_ foram excluídos.");
            PostEvent("selfHostRootDevices", new { items });
        }
        catch (Exception ex)
        {
            WriteLog("SELFHOST", "Falha ao listar dispositivos raiz: " + SensitiveDataSanitizer.Clean(ex.Message), "ERROR");
            PostEvent("selfHostConfigurationError", new { stage = "rootDevices", message = SensitiveDataSanitizer.Clean(ex.Message) });
        }
        finally
        {
            PostBusy("selfHostRootDevices", false);
        }
    }

    private async Task CreateSelfHostRootDeviceAsync(JsonElement payload)
    {
        PostBusy("selfHostRootCreate", true);
        try
        {
            _ = RequireSelfHostInstallation();
            var context = ReadSelfHostShopContext(payload);
            var name = ReadString(payload, "name")?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Informe o nome do dispositivo raiz.");
            if (name.StartsWith("SELFHOST_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O prefixo SELFHOST_ é reservado aos dispositivos filhos administrados pelo SelfHost.");

            var created = await ExecuteOnlineAsync(
                context.Database,
                "criar dispositivo raiz do SelfHost",
                service => service.CreateOAuthClientAsync(context.Database, context.CompanyId, name, _shutdown.Token));
            var items = await LoadEligibleRootDevicesAsync(context.Database, context.CompanyId);
            var selected = items.FirstOrDefault(x => x.ClientId == created.ClientId)
                ?? items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                throw new InvalidOperationException("O Softcomshop aceitou o cadastro, mas o dispositivo raiz não apareceu na atualização da lista.");

            WriteLog("SELFHOST", $"Dispositivo raiz {selected.Name} criado pelo fluxo oficial /softauth/device/salvar; ainda não vinculado ao SelfHost.");
            PostEvent("selfHostRootDeviceCreated", new { item = selected, items });
        }
        catch (Exception ex)
        {
            WriteLog("SELFHOST", "Falha ao criar dispositivo raiz: " + SensitiveDataSanitizer.Clean(ex.Message), "ERROR");
            PostEvent("selfHostConfigurationError", new { stage = "createRoot", message = SensitiveDataSanitizer.Clean(ex.Message) });
        }
        finally
        {
            PostBusy("selfHostRootCreate", false);
        }
    }

    private async Task PreviewSelfHostConfigurationAsync(JsonElement payload)
    {
        PostBusy("selfHostConfiguration", true);
        try
        {
            var installation = RequireSelfHostInstallation();
            var desired = await BuildSelfHostDesiredConfigurationAsync(payload);
            var result = await _selfHostBridgeService.PreviewAsync(installation.InstallPath, desired, _shutdown.Token);
            WriteLog("SELFHOST", $"Preview calculado sem gravação: {result.Changes?.Count ?? 0} alteração(ões), restart {(result.RestartRequired ? "necessário" : "dispensado")}.");
            PostEvent("selfHostConfigurationPreview", result);
        }
        catch (Exception ex)
        {
            WriteLog("SELFHOST", "Falha no preview: " + SensitiveDataSanitizer.Clean(ex.Message), "ERROR");
            PostEvent("selfHostConfigurationError", new { stage = "preview", message = SensitiveDataSanitizer.Clean(ex.Message) });
        }
        finally
        {
            PostBusy("selfHostConfiguration", false);
        }
    }

    private async Task ConfigureSelfHostAsync(JsonElement payload)
    {
        PostBusy("selfHostConfiguration", true);
        SelfHostDeviceService.SelfHostInstallationInfo? installation = null;
        SelfHostBridgeResult? before = null;
        SelfHostBridgeResult? configured = null;
        var serviceMayBeStopped = false;
        var desiredPort = 7711;
        string? backupPath = null;
        try
        {
            installation = RequireSelfHostInstallation();
            _selfHostBackupService.EnsureConfigurationToolsClosed();
            var desired = await BuildSelfHostDesiredConfigurationAsync(payload);
            desiredPort = desired.PortaHttp;
            before = await _selfHostBridgeService.ReadAsync(installation.InstallPath, _shutdown.Token);
            SelfHostServiceManager.EnsurePortAvailable(desiredPort, before.Configuration?.PortaHttp);

            PostEvent("selfHostConfigurationProgress", new { stage = "stopping", message = "Parando o SelfHost para criar um backup consistente..." });
            serviceMayBeStopped = true;
            await _selfHostServiceManager.StopAsync(installation.InstallPath, cancellationToken: _shutdown.Token);
            backupPath = _selfHostBackupService.CreateStoppedBackup(installation.InstallPath, installation.Generation);
            WriteLog("SELFHOST", $"Ponto de restauração preparado antes da configuração em {backupPath}.");

            desired = await BuildSelfHostDesiredConfigurationAsync(payload, unlinkRootDevice: true);
            PostEvent("selfHostConfigurationProgress", new { stage = "configuring", message = "Vinculando o dispositivo raiz e gravando pela API oficial..." });
            configured = await _selfHostBridgeService.ConfigureAsync(installation.InstallPath, desired, _shutdown.Token);

            PostEvent("selfHostConfigurationProgress", new { stage = "restarting", message = "Reiniciando o SelfHost e aguardando a porta local..." });
            await _selfHostServiceManager.StartAsync(installation.InstallPath, desiredPort, cancellationToken: _shutdown.Token);
            serviceMayBeStopped = false;

            PostEvent("selfHostConfigurationProgress", new { stage = "validating", message = "Validando healthchecks, autenticação e empresa..." });
            var validation = await _selfHostBridgeService.ValidateAsync(
                installation.InstallPath,
                $"http://127.0.0.1:{desiredPort}",
                _shutdown.Token);
            configured = configured with { Validation = validation.Validation };

            WriteLog("SELFHOST", $"SelfHost {installation.Version} configurado e validado pela API local; backup: {backupPath}.");
            PostEvent("selfHostConfigurationConfigured", new { result = configured, backupPath });
        }
        catch (Exception ex)
        {
            var message = SensitiveDataSanitizer.Clean(ex.Message);
            WriteLog("SELFHOST", "Falha ao configurar: " + message, "ERROR");
            PostEvent("selfHostConfigurationError", new { stage = configured is null ? "configure" : "validate", message, backupPath });
        }
        finally
        {
            if (serviceMayBeStopped && installation is not null)
            {
                try
                {
                    var recoveryPort = configured is null ? before?.Configuration?.PortaHttp ?? 7711 : desiredPort;
                    await _selfHostServiceManager.StartAsync(installation.InstallPath, recoveryPort, cancellationToken: _shutdown.Token);
                }
                catch (Exception restartError)
                {
                    var restartMessage = SensitiveDataSanitizer.Clean(restartError.Message);
                    WriteLog("SELFHOST", "Falha ao restaurar o serviço após erro: " + restartMessage, "ERROR");
                    PostEvent("selfHostConfigurationError", new { stage = "restart", message = restartMessage, backupPath });
                }
            }
            PostBusy("selfHostConfiguration", false);
        }
    }

    private async Task EnsureSelfHostTargetsClientAsync(
        string database,
        SelfHostDeviceService.SelfHostInstallationInfo installation)
    {
        var current = await _selfHostBridgeService.ReadAsync(installation.InstallPath, _shutdown.Token);
        if (SelfHostClientMatcher.IsDesktop(current.Configuration))
            throw new InvalidOperationException("O SelfHost está configurado para Softshop Desktop e não pode listar dispositivos do Softcomshop Web.");
        if (!SelfHostClientMatcher.Matches(current.Configuration, database))
            throw new InvalidOperationException(SelfHostClientMatcher.DescribeMismatch(current.Configuration, database));
    }

    private async Task<SelfHostDesiredConfiguration> BuildSelfHostDesiredConfigurationAsync(
        JsonElement payload,
        bool unlinkRootDevice = false)
    {
        var backend = ReadString(payload, "backend")?.Trim().ToLowerInvariant() ?? "softcomshop";
        if (backend is not ("softcomshop" or "softshop"))
            throw new InvalidOperationException("Escolha Softcomshop Web ou Softshop Desktop.");
        var portText = ReadString(payload, "port")?.Trim() ?? "7711";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("A porta HTTP deve ser um inteiro entre 1 e 65535.");

        string? deviceUrl = null;
        if (backend == "softcomshop")
        {
            var context = ReadSelfHostShopContext(payload);
            var clientId = ReadString(payload, "rootClientId")?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Selecione o dispositivo raiz do SelfHost.");
            var items = await LoadEligibleRootDevicesAsync(context.Database, context.CompanyId);
            var root = items.FirstOrDefault(x => x.ClientId.Equals(clientId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("O dispositivo raiz selecionado não pertence mais à empresa escolhida.");

            if (unlinkRootDevice && root.IsLinked)
            {
                PostEvent("selfHostConfigurationProgress", new
                {
                    stage = "unlinkingRoot",
                    message = $"Desvinculando {root.Name} do SelfHost anterior..."
                });
                var unlinked = await ExecuteOnlineAsync(
                    context.Database,
                    "desvincular dispositivo raiz do SelfHost",
                    service => service.UnlinkOAuthClientAsync(
                        context.Database,
                        context.CompanyId,
                        root.ClientId,
                        _shutdown.Token));
                if (!unlinked)
                    throw new InvalidOperationException($"O Softcomshop não confirmou a desvinculação do dispositivo raiz {root.Name}.");

                var refreshed = await LoadEligibleRootDevicesAsync(context.Database, context.CompanyId);
                root = refreshed.FirstOrDefault(x => x.ClientId.Equals(clientId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("O dispositivo raiz desapareceu da empresa após a desvinculação.");
                if (root.IsLinked)
                    throw new InvalidOperationException($"O dispositivo raiz {root.Name} ainda aparece vinculado no Softcomshop.");

                PostEvent("selfHostConfigurationProgress", new
                {
                    stage = "rootUnlinked",
                    message = $"Dispositivo raiz {root.Name} desvinculado e confirmado. Preparando o novo vínculo..."
                });
            }

            deviceUrl = await ExecuteOnlineAsync(
                context.Database,
                "obter URL oficial do dispositivo raiz",
                service => service.GetDeviceUrlAsync(context.Database, root.ClientId, _shutdown.Token));
        }

        var configureTableDatabase = backend == "softcomshop" && ReadBool(payload, "configureTableDatabase", false);
        return new SelfHostDesiredConfiguration(
            backend,
            port,
            ReadBool(payload, "smartEnabled", true),
            deviceUrl,
            backend == "softshop" ? ReadString(payload, "sqlServer")?.Trim() : null,
            backend == "softshop" ? ReadString(payload, "sqlPort")?.Trim() : null,
            backend == "softshop" ? ReadString(payload, "sqlUser")?.Trim() : null,
            backend == "softshop" ? ReadString(payload, "sqlPassword") : null,
            backend == "softshop" ? ReadString(payload, "sqlDatabase")?.Trim() : null,
            configureTableDatabase,
            configureTableDatabase ? ReadString(payload, "mysqlServer")?.Trim() : null,
            configureTableDatabase ? ReadString(payload, "mysqlPort")?.Trim() : null,
            configureTableDatabase ? ReadString(payload, "mysqlUser")?.Trim() : null,
            configureTableDatabase ? ReadString(payload, "mysqlPassword") : null,
            configureTableDatabase ? ReadString(payload, "mysqlDatabase")?.Trim() : null);
    }

    private async Task<IReadOnlyList<OAuthClientInfo>> LoadEligibleRootDevicesAsync(string database, long companyId)
    {
        var items = await ExecuteOnlineAsync(
            database,
            "listar dispositivos raiz do SelfHost",
            service => service.GetOAuthClientsAsync(database, companyId, _shutdown.Token));
        return items
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId) &&
                        !x.Name.StartsWith("SELFHOST_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private SelfHostShopContext ReadSelfHostShopContext(JsonElement payload)
    {
        var database = EnvironmentCatalog.NormalizeDatabaseName(
            ReadString(payload, "database") ?? throw new InvalidOperationException("Selecione o cliente Softcomshop."));
        var companyId = ReadLong(payload, "companyId")
            ?? throw new InvalidOperationException("Selecione a empresa do dispositivo raiz.");
        return new SelfHostShopContext(database, companyId);
    }

    private sealed record SelfHostShopContext(string Database, long CompanyId);

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
        catch (Exception ex)
        {
            WriteLog("SERIE", $"Falha ao salvar serie {documentType}: {ex.Message}", "ERROR");
            PostEvent("fiscalSeriesError", new { clientId, documentType, message = ex.Message });
        }
        finally
        {
            PostBusy("series", false);
        }
    }

    private void RememberOnlineClient(string database)
    {
        var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
        if (string.IsNullOrWhiteSpace(normalizedDatabase)) return;

        var settings = _settingsService.Load();
        settings.AccessMode = "online";
        settings.LastOnlineClient = normalizedDatabase;
        settings.LastDatabase = normalizedDatabase;

        var recent = (settings.RecentOnlineClients ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(EnvironmentCatalog.NormalizeDatabaseName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        recent.RemoveAll(x => x.Equals(normalizedDatabase, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, normalizedDatabase);
        settings.RecentOnlineClients = recent.Take(30).ToList();
        _settingsService.Save(settings);

        PostEvent("onlineClients", new
        {
            items = settings.RecentOnlineClients.Select(EnvironmentCatalog.DatabaseDisplayName).ToArray(),
            current = EnvironmentCatalog.DatabaseDisplayName(normalizedDatabase)
        });
    }

    private string GetConfirmedSmartDeviceId(string serial, string adbAndroidId)
    {
        lock (_settingsMutationGate)
        {
            var settings = _settingsService.Load();
            settings.ConfirmedSmartDeviceIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in BuildSmartDeviceIdentityKeys(serial, adbAndroidId))
            {
                if (settings.ConfirmedSmartDeviceIds.TryGetValue(key, out var deviceId) &&
                    IsSafeDeviceId(deviceId)) return deviceId.Trim();
            }
        }

        return string.Empty;
    }

    private void RememberConfirmedSmartDeviceId(string serial, string adbAndroidId, string deviceId)
    {
        if (!IsSafeDeviceId(deviceId)) return;

        var confirmedDeviceId = deviceId.Trim();
        lock (_settingsMutationGate)
        {
            var settings = _settingsService.Load();
            settings.ConfirmedSmartDeviceIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in BuildSmartDeviceIdentityKeys(serial, adbAndroidId))
                settings.ConfirmedSmartDeviceIds[key] = confirmedDeviceId;
            _settingsService.Save(settings);

            // Mantem a leitura usada por EvaluateLink coerente imediatamente apos o
            // vinculo, sem depender de uma nova atualizacao ADB da tela.
            _androidDevices = _androidDevices
                .Select(x => string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase)
                    ? x with { ConfirmedSmartDeviceId = confirmedDeviceId }
                    : x)
                .ToArray();
        }
    }

    private static IEnumerable<string> BuildSmartDeviceIdentityKeys(string serial, string adbAndroidId)
    {
        if (!string.IsNullOrWhiteSpace(serial)) yield return "serial:" + serial.Trim();
        if (!string.IsNullOrWhiteSpace(adbAndroidId)) yield return "adb:" + adbAndroidId.Trim();
    }

    private static bool IsSafeDeviceId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.');

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
        var serial = ReadString(payload, "serial")?.Trim();
        var settings = _settingsService.Load();
        settings.SmartPackageNamesBySerial ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(serial)) settings.SmartPackageNamesBySerial[serial] = packageName;
        else settings.SmartPackageName = packageName;
        _settingsService.Save(settings);
        SendBootstrap();
        PostEvent("toast", new { type = "success", message = "Package name salvo." });
    }

    private async Task LoadTestAutomationAsync(JsonElement payload)
    {
        PostBusy("testAutomationCatalog", true);
        try
        {
            if (ReadBool(payload, "refreshDevices", false))
                await RefreshAndroidAsync();

            var catalog = _testAutomationService.LoadCatalog(_androidDevices, _deviceCatalog);
            SaveTestAutomationChannel(catalog.Source.CurrentBranch);
            PostEvent("testAutomationCatalog", catalog);
        }
        finally
        {
            PostBusy("testAutomationCatalog", false);
        }
    }

    private async Task UpdateTestAutomationSourceAsync(JsonElement payload)
    {
        var branch = ReadString(payload, "branch");
        lock (_testAutomationGate)
        {
            if (_testAutomationRun is not null || _testAutomationSourceUpdating)
                throw new InvalidOperationException("Cancele a execução atual antes de trocar a fonte dos testes.");
            _testAutomationSourceUpdating = true;
        }

        PostBusy("testAutomationSource", true);
        PostEvent("testAutomationSourceUpdating", new { branch });
        try
        {
            await _testAutomationService.UpdateSourceAsync(
                branch,
                _shutdown.Token,
                progress => PostEvent("testAutomationSourceProgress", progress));
            var catalog = _testAutomationService.LoadCatalog(_androidDevices, _deviceCatalog);
            SaveTestAutomationChannel(catalog.Source.CurrentBranch);
            PostEvent("testAutomationCatalog", catalog);
            PostEvent("toast", new
            {
                type = "success",
                message = $"Automation atualizado pela branch {catalog.Source.CurrentBranch}."
            });
            WriteLog("TESTES", $"Automation atualizado: {catalog.Source.CurrentBranch} {catalog.Source.Commit}.");
        }
        catch (Exception ex)
        {
            var message = SensitiveDataSanitizer.Clean(ex.Message);
            PostEvent("testAutomationError", new { message });
            WriteLog("TESTES", message, "ERROR");
        }
        finally
        {
            lock (_testAutomationGate) _testAutomationSourceUpdating = false;
            PostBusy("testAutomationSource", false);
        }
    }

    private void SaveTestAutomationChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return;
        var settings = _settingsService.Load();
        settings.TestAutomationChannel = channel;
        _settingsService.Save(settings);
    }

    private async Task RunTestAutomationAsync(JsonElement payload)
    {
        var request = payload.Deserialize<TestAutomationRunRequest>(JsonOptions)
            ?? throw new InvalidOperationException("Informe o dispositivo e a suíte de testes.");

        CancellationTokenSource runCancellation;
        lock (_testAutomationGate)
        {
            if (_testAutomationSourceUpdating)
                throw new InvalidOperationException("Aguarde a atualização da fonte dos testes.");
            if (_testAutomationRun is not null)
                throw new InvalidOperationException("Já existe uma execução de testes em andamento.");
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _testAutomationRun = runCancellation;
        }

        PostBusy("testAutomationRun", true);
        PostEvent("testAutomationStarted", new
        {
            request.Serial,
            request.DeviceTag,
            request.SuiteId,
            testCase = request.TestCase ?? string.Empty
        });
        WriteLog("TESTES", $"Execução iniciada em {request.Serial}, perfil {request.DeviceTag}.");

        try
        {
            var result = await _testAutomationService.RunAsync(
                request,
                _androidDevices,
                _deviceCatalog,
                runCancellation.Token,
                progress => PostEvent("testAutomationProgress", progress));
            PostEvent("testAutomationFinished", result);
            WriteLog("TESTES", result.Success
                ? $"Execução concluída com sucesso em {request.Serial}."
                : result.Canceled
                    ? $"Execução cancelada em {request.Serial}."
                    : $"Execução finalizada com falhas em {request.Serial}.",
                result.Success ? "INFO" : result.Canceled ? "WARN" : "ERROR");
        }
        catch (Exception ex)
        {
            var message = SensitiveDataSanitizer.Clean(ex.Message);
            PostEvent("testAutomationError", new { message });
            WriteLog("TESTES", message, "ERROR");
        }
        finally
        {
            lock (_testAutomationGate)
            {
                if (ReferenceEquals(_testAutomationRun, runCancellation))
                    _testAutomationRun = null;
            }
            runCancellation.Dispose();
            PostBusy("testAutomationRun", false);
        }
    }

    private void CancelTestAutomation()
    {
        lock (_testAutomationGate)
        {
            if (_testAutomationRun is null) return;
            _testAutomationRun.Cancel();
        }
        PostEvent("testAutomationCanceling", new { message = "Cancelando a execução e os processos iniciados por ela..." });
    }

    private void OpenTestReport()
    {
        var reportPath = _testAutomationService.GetExistingReportPath();
        if (reportPath is null)
            throw new InvalidOperationException("Nenhum relatório Allure foi gerado ainda.");

        Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true });
    }

    private void SaveSmartTefSettings(JsonElement payload, bool notify)
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
        if (string.IsNullOrWhiteSpace(token) &&
            string.IsNullOrWhiteSpace(_secretStore.Get(SecretStore.SmartTefToken)))
        {
            throw new InvalidOperationException("Informe o Token do Smart TEF antes de salvar.");
        }

        lock (_settingsMutationGate)
        {
            var settings = _settingsService.Load();
            settings.SmartTefDeviceName = deviceName;
            settings.SmartTefCnpj = cnpj;
            settings.SmartTefEmpresaId = empresaId;
            settings.SaveSmartTefConfiguration = true;
            _settingsService.Save(settings);
            if (!string.IsNullOrWhiteSpace(token))
                _secretStore.Set(SecretStore.SmartTefToken, token);
        }

        if (!notify) return;

        SendBootstrap();
        PostEvent("smartTefSettingsSaved", new { hasSavedToken = true });
        PostEvent("toast", new
        {
            type = "success",
            message = "Configuracao do Smart TEF salva neste usuario do Windows."
        });
    }

    private async Task DetectSmartPackagesAsync(JsonElement payload)
    {
        var serial = ReadString(payload, "serial")
            ?? throw new InvalidOperationException("Selecione um Android.");

        PostBusy("packages", true);
        try
        {
            var detection = await _adbService.DetectSmartPackageAsync(serial, _shutdown.Token);
            var items = detection.Candidates;
            var foregroundPackage = await _adbService.GetForegroundPackageAsync(serial, _shutdown.Token);
            if (!string.IsNullOrWhiteSpace(detection.PackageName))
            {
                var settings = _settingsService.Load();
                settings.SmartPackageNamesBySerial ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                settings.SmartPackageNamesBySerial[serial] = detection.PackageName;
                _settingsService.Save(settings);
                SendBootstrap();
            }

            PostEvent("smartPackages", new { items, serial, foregroundPackage, selectedPackage = detection.PackageName, version = detection.VersionName });
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
        var android = _androidDevices.FirstOrDefault(x => x.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase));
        var packageName = android is null ? string.Empty : ResolveDevicePackage(android, settings);

        if (string.IsNullOrWhiteSpace(packageName))
        {
            packageName = (await _adbService.DetectSmartPackageAsync(serial, _shutdown.Token)).PackageName;
        }

        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new InvalidOperationException(
                "Nao foi possivel identificar automaticamente o package do Smart. Abra o Smart no Android e tente novamente.");
        }

        settings.SmartPackageNamesBySerial ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        settings.SmartPackageNamesBySerial[serial] = packageName;
        _settingsService.Save(settings);

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
            var selfHostInstallation = RequireSelfHostInstallation();
            await EnsureSelfHostTargetsClientAsync(database, selfHostInstallation);
            string selfHostUrl;
            if (IsModernSelfHost(selfHostInstallation))
            {
                var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
                var softcomshopUrl = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "obter URL do dispositivo SelfHost 4.1+",
                    service => service.GetDeviceUrlAsync(normalizedDatabase, oauthClient.ClientId, _shutdown.Token));
                selfHostUrl = BuildSelfHostDeviceUrl(softcomshopUrl, selfHostBaseUrl);
                WriteLog("SELFHOST", $"URL /device/add do SelfHost {selfHostInstallation.Version} montada a partir do vínculo real do Softcomshop para {oauthClient.Name}.");
            }
            else
            {
                selfHostUrl = _selfHostDeviceService.BuildUrl(oauthClient, company, selfHostBaseUrl);
                WriteLog("SELFHOST", $"URL gerada pelo fluxo legado do SelfHost {selfHostInstallation.Version} para {oauthClient.Name}.");
            }

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
                var selfHostInstallation = RequireSelfHostInstallation();
                await EnsureSelfHostTargetsClientAsync(database, selfHostInstallation);
                if (IsModernSelfHost(selfHostInstallation))
                {
                    var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
                    var softcomshopUrl = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        "validar URL do dispositivo SelfHost 4.1+",
                        service => service.GetDeviceUrlAsync(normalizedDatabase, oauthClient.ClientId, _shutdown.Token));
                    url = BuildSelfHostDeviceUrl(softcomshopUrl, selfHostBaseUrl);
                }
                else
                {
                    url = _selfHostDeviceService.BuildUrl(oauthClient, company, selfHostBaseUrl);
                }
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
        try
        {
            var serials = ReadStringArray(payload, "serials");
            if (serials.Count == 0 && ReadString(payload, "serial") is { } singleSerial)
                serials = new[] { singleSerial };
            if (serials.Count == 0) throw new InvalidOperationException("Selecione ao menos um Android.");

            var devices = serials
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(serial => _androidDevices.FirstOrDefault(x => x.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x is not null && x.IsOnline)
                .Cast<DeviceInfo>()
                .ToArray();
            if (devices.Length != serials.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new InvalidOperationException("Um ou mais Androids selecionados não estão mais online. Atualize a lista.");

            // Congela o payload e o cadastro de cada Android antes de iniciar qualquer
            // operacao assÃ­ncrona. Assim, atualizacoes da lista durante um job nao podem
            // fazer outro job herdar o ultimo cadastro selecionado na interface.
            var requests = devices.Select(device =>
            {
                var individualPayload = WithSerial(payload, device.Serial, devices.Length > 1);
                var module = ReadString(individualPayload, "module") ?? "smart_pdv";
                var target = string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ReadObject<OAuthClientInfo>(individualPayload, "oauthClient");
                if (!string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase) && target is null)
                    throw new InvalidOperationException($"Selecione o cadastro que sera vinculado ao Android {device.FriendlyName} ({device.Serial}).");
                return new ProvisioningRequest(
                    new ProvisioningJob(device, target?.Name ?? string.Empty),
                    individualPayload,
                    target?.ClientId ?? string.Empty);
            }).ToArray();

            var duplicateTargets = requests
                .Where(x => !string.IsNullOrWhiteSpace(x.TargetClientId))
                .GroupBy(x => x.TargetClientId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1);
            if (duplicateTargets is not null)
                throw new InvalidOperationException("Cada Android deve usar um cadastro diferente. Revise a selecao multidispositivo.");

            var requestBySerial = requests.ToDictionary(x => x.Job.Serial, StringComparer.OrdinalIgnoreCase);
            var jobs = requests.Select(x => x.Job).ToArray();
            PostEvent("provisioningStarted", new { count = jobs.Length, maxParallelism = MultiDeviceProvisioningService.DefaultMaxParallelism });
            var results = await _multiDeviceProvisioningService.RunAsync(
                jobs,
                async (job, token) =>
                {
                    var context = new ProvisioningExecutionContext(job, token);
                    _provisioningContext.Value = context;
                    var individualPayload = requestBySerial[job.Serial].Payload;
                    var accessMode = ReadAccessMode(individualPayload);
                    var jobModule = ReadString(individualPayload, "module") ?? "smart_pdv";
                    // A API administrativa do SelfHost e todas as chamadas ADB sao
                    // independentes por client_id/serial. Somente VPN/DB Bridge locais
                    // continuam serializados, pois alteram infraestrutura compartilhada.
                    var usesSharedInfrastructure = !ShouldUseSelfHost(individualPayload, jobModule) &&
                                                   !IsOnlineMode(accessMode);
                    var infrastructureAcquired = false;
                    try
                    {
                        context.Job.Progress("target", string.IsNullOrWhiteSpace(context.Job.TargetDeviceName)
                            ? "Destino individual confirmado."
                            : $"Cadastro reservado para este Android: {context.Job.TargetDeviceName}.");
                        _multiDeviceProvisioningService.NotifyChanged(context.Job);
                        if (usesSharedInfrastructure)
                        {
                            await _sharedInfrastructureGate.WaitAsync(token);
                            infrastructureAcquired = true;
                        }
                        await PrepareSmartDeviceAsync(individualPayload);
                        return context.Outcome ?? new ProvisioningJobOutcome(false, "unknown", "O fluxo terminou sem resultado individual.");
                    }
                    finally
                    {
                        if (infrastructureAcquired) _sharedInfrastructureGate.Release();
                        _provisioningContext.Value = null;
                    }
                },
                _shutdown.Token);

            PostEvent("provisioningFinished", new
            {
                success = results.Count(x => x.Status == ProvisioningJobStatus.Succeeded),
                failed = results.Count(x => x.Status == ProvisioningJobStatus.Failed),
                canceled = results.Count(x => x.Status == ProvisioningJobStatus.Canceled),
                items = results
            });
        }
        finally
        {
            PostBusy("provision", false);
        }
    }

    private async Task ResolveDockerClientAsync(JsonElement payload)
    {
        var database = EnvironmentCatalog.NormalizeDatabaseName(
            ReadString(payload, "database") ?? throw new InvalidOperationException("Informe o cliente."));
        var matches = new List<string>();
        var unavailable = new List<string>();

        PostBusy("databases", true);
        try
        {
            foreach (var environmentKey in EnvironmentCatalog.Environments.Keys)
            {
                try
                {
                    WriteLog("DOCKER", $"Procurando {EnvironmentCatalog.DatabaseDisplayName(database)} em {EnvironmentCatalog.Get(environmentKey).Label}.");
                    await EnsureDatabaseAccessAsync(environmentKey, "localizar o cliente nos ambientes AWS", "docker");
                    var databases = await _databaseService.GetDatabasesAsync(environmentKey, _shutdown.Token);
                    if (databases.Contains(database, StringComparer.OrdinalIgnoreCase))
                        matches.Add(environmentKey);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    unavailable.Add(environmentKey);
                    WriteLog("DOCKER", $"Nao foi possivel consultar {EnvironmentCatalog.Get(environmentKey).Label}: {SanitizeSensitiveText(ex.Message)}", "WARN");
                }
            }

            if (matches.Count == 0)
            {
                var suffix = unavailable.Count == EnvironmentCatalog.Environments.Count
                    ? " Nenhum dos dois ambientes pôde ser consultado; verifique Docker, VPN e credenciais."
                    : string.Empty;
                throw new InvalidOperationException(
                    $"O cliente {EnvironmentCatalog.DatabaseDisplayName(database)} nao foi localizado em AWS 1 nem AWS 2.{suffix}");
            }

            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"O cliente {EnvironmentCatalog.DatabaseDisplayName(database)} existe em mais de um ambiente AWS. [VALIDAR] Informe qual cadastro deve ser utilizado.");

            // A busca termina com o bridge apontando para o ultimo ambiente consultado.
            // Reativa o ambiente vencedor antes de carregar empresa/dispositivos.
            var selectedEnvironment = matches[0];
            await EnsureDatabaseAccessAsync(selectedEnvironment, "usar o cliente localizado", "docker");
            PostEvent("databaseResolved", new { database, environment = selectedEnvironment });
        }
        finally
        {
            PostBusy("databases", false);
        }
    }

    private async Task PrepareSmartDeviceAsync(JsonElement payload)
    {
        // Mantem o fluxo legado intacto, mas faz todas as operacoes desta execucao
        // obedecerem tambem ao cancelamento do job individual.
        using var _shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            this._shutdown.Token,
            _provisioningContext.Value?.CancellationToken ?? CancellationToken.None);
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
                WriteLog("SMART", $"[{serial}][{stage}] {message}");
                PostEvent("provisionProgress", new { stage, message });
            }

            var automationProfile = string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase)
                ? "Smart TEF"
                : string.Equals(android.ProvisioningProfile, "mercadopagon950", StringComparison.OrdinalIgnoreCase)
                    ? "Mercado Pago N950 (perfil dedicado)"
                    : IsLegacy80LargeSelfServiceModule(module)
                        ? "Smart 8.0 tela grande (Totem/AutoPagamento)"
                        : "fluxo padrao/mobile";
            Progress(
                "profile",
                $"Modulo recebido: {module}. Perfil solicitado: {automationProfile}. Android: {android.FriendlyName} ({android.Model}), SDK {android.AndroidSdk}.");

            // Smart TEF possui fluxo proprio e nao usa URL/oauth_clients nesta etapa.
            // O XML confirmado do app RedeFlex expoe Nome do dispositivo, CNPJ, Empresa ID e Token.
            if (string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase))
            {
                var deviceName = ReadString(payload, "tefDeviceName")?.Trim();
                var cnpj = ReadString(payload, "tefCnpj")?.Trim();
                var empresaId = ReadString(payload, "tefEmpresaId")?.Trim();
                var token = ReadString(payload, "tefToken")?.Trim();

                if (string.IsNullOrWhiteSpace(token) && ReadBool(payload, "useSavedTefToken", false))
                    token = _secretStore.Get(SecretStore.SmartTefToken)?.Trim();

                if (string.IsNullOrWhiteSpace(deviceName))
                    throw new InvalidOperationException("Informe o Nome do dispositivo do Smart TEF.");
                if (string.IsNullOrWhiteSpace(cnpj))
                    throw new InvalidOperationException("Informe o CNPJ do Smart TEF.");
                if (string.IsNullOrWhiteSpace(empresaId))
                    throw new InvalidOperationException("Informe o Empresa ID do Smart TEF.");
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Informe o Token do Smart TEF.");

                if (ReadBool(payload, "saveTefConfiguration", false))
                    SaveSmartTefSettings(payload, notify: false);

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
                    ResolveDevicePackage(android, _settingsService.Load()),
                    android.ProvisioningProfile,
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
            var useSelfHost = ShouldUseSelfHost(payload, module);
            var selfHostInstallation = useSelfHost ? RequireSelfHostInstallation() : null;
            if (selfHostInstallation is not null)
                await EnsureSelfHostTargetsClientAsync(database, selfHostInstallation);
            var modernSelfHost = selfHostInstallation is not null && IsModernSelfHost(selfHostInstallation);
            var confirmedSmartDeviceId = GetConfirmedSmartDeviceId(serial, android.AndroidId);

            if (useSelfHost && !modernSelfHost)
            {
                if (!oauthClient.Name.StartsWith("SELFHOST_", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"O cadastro {oauthClient.Name} nao e um dispositivo filho valido do SelfHost. Atualize a lista e selecione ou crie um cadastro com prefixo SELFHOST_.");
                }

                // Quando o vínculo usa SelfHost, ele precisa ser liberado no conjunto de
                // dispositivos do próprio SelfHost antes de enviar a URL ao Smart. Não abortamos
                // mais apenas porque o cadastro selecionado já possui device_id.
                Progress(
                    "selfhost-preflight",
                    $"Atualizando o estado de {oauthClient.Name} e verificando vinculos antes de abrir o Smart...");
                var preflightDevices = await _selfHostDeviceService.ListAllDevicesAsync(_shutdown.Token);
                var selectedCurrent = preflightDevices.FirstOrDefault(x =>
                    string.Equals(x.ClientId, oauthClient.ClientId, StringComparison.Ordinal));
                if (selectedCurrent?.IsLinked == true)
                {
                    Progress(
                        "selfhost-unlink-selected",
                        $"Desvinculando {selectedCurrent.Name} do vinculo atual antes de iniciar este Android...");
                    var selectedUnlinked = await _selfHostDeviceService.UnlinkDeviceAsync(oauthClient.ClientId, _shutdown.Token);
                    if (!selectedUnlinked)
                        throw new InvalidOperationException($"O SelfHost não confirmou a desvinculação de {oauthClient.Name}.");
                    Progress(
                        "selfhost-unlink-selected",
                        "Cadastro SelfHost selecionado confirmado como desvinculado pela API.");
                    preflightDevices = await _selfHostDeviceService.ListAllDevicesAsync(_shutdown.Token);
                }

                // Quando esse Android ja foi observado em uma execucao anterior, o ID
                // real do APK permite liberar o cadastro antigo antes mesmo de abrir o
                // Smart. O ANDROID_ID do shell nao e usado nessa comparacao.
                if (!string.IsNullOrWhiteSpace(confirmedSmartDeviceId))
                {
                    Progress(
                        "selfhost-preflight",
                        $"Verificando se o Device ID real {confirmedSmartDeviceId} esta vinculado a outro cadastro SelfHost...");
                    var preflightConflicts = preflightDevices
                        .Where(x => x.IsLinked &&
                                    string.Equals(x.DeviceId, confirmedSmartDeviceId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    foreach (var conflict in preflightConflicts)
                    {
                        Progress(
                            "selfhost-unlink-conflict",
                            $"Device ID real localizado em {conflict.Name}. Desvinculando antes de abrir o Smart...");
                        if (!await _selfHostDeviceService.UnlinkDeviceAsync(conflict.ClientId, _shutdown.Token))
                        {
                            throw new InvalidOperationException(
                                $"Nao foi possivel liberar o Device ID real do cadastro SelfHost {conflict.Name}.");
                        }
                    }

                    if (preflightConflicts.Length > 0)
                    {
                        Progress(
                            "selfhost-unlink-conflict",
                            "Vinculo anterior confirmado como removido antes de abrir o Smart.");
                    }
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
                    var currentDevices = await _selfHostDeviceService.ListAllDevicesAsync(_shutdown.Token);
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

                Task<SmartAutomationResult> RunSelfHostAutomationAsync(string? knownDeviceId) =>
                    _smartAutomationService.SubmitDeviceUrlAsync(
                        serial,
                        selfHostUrl,
                        module,
                        ResolveDevicePackage(android, selfHostSettings),
                        clearData,
                        Progress,
                        BeforeSubmitSelfHostAsync,
                        _shutdown.Token,
                        knownDeviceId,
                        android.ProvisioningProfile);

                var selfHostAutomation = await RunSelfHostAutomationAsync(confirmedSmartDeviceId);

                if (!selfHostAutomation.Success)
                {
                    if (IsSafeDeviceId(selfHostAutomation.SmartDeviceId))
                    {
                        RememberConfirmedSmartDeviceId(
                            serial,
                            android.AndroidId,
                            selfHostAutomation.SmartDeviceId);
                    }
                    PostEvent("smartPreparationFinished", new { success = false, module, accessMode = "selfhost", selfHostAutomation.Stage, message = selfHostAutomation.Message, selfHostAutomation.PackageName, selfHostAutomation.UiSummary, selfHostAutomation.SmartDeviceId, adbAndroidId = android.AndroidId, url = selfHostUrl });
                    return;
                }

                var selfHostExpectedDeviceId = !string.IsNullOrWhiteSpace(selfHostAutomation.SmartDeviceId) ? selfHostAutomation.SmartDeviceId : android.AndroidId;
                Progress("selfhost-verify", $"Aguardando o SelfHost registrar o Device ID {selfHostExpectedDeviceId}...");
                var selfHostRefreshed = await _selfHostDeviceService.WaitForDeviceLinkAsync(
                    oauthClient.ClientId,
                    40,
                    TimeSpan.FromMilliseconds(750),
                    _shutdown.Token);
                var selfHostLinked = selfHostRefreshed is not null && string.Equals(selfHostRefreshed.DeviceId, selfHostExpectedDeviceId, StringComparison.OrdinalIgnoreCase);
                var smartUiFinalized = false;
                if (selfHostLinked)
                {
                    // Fecha o OK assim que a consulta direta confirmar o device_id.
                    // A listagem completa e paginada pode levar varios segundos e deixava
                    // o emulador parado no dialogo mesmo com o vinculo ja concluido.
                    smartUiFinalized = await _smartAutomationService.DismissConfirmedSynchronizationAsync(
                        serial,
                        selfHostAutomation.PackageName,
                        module,
                        Progress,
                        _shutdown.Token);
                }
                SmartPrinterConfigurationResult? selfHostPrinter = null;
                if (selfHostLinked)
                {
                    selfHostPrinter = await ApplyInternalSmartConfigurationAsync(
                        payload,
                        android,
                        selfHostAutomation.PackageName,
                        module,
                        Progress,
                        _shutdown.Token);
                    if (selfHostPrinter is { Success: false })
                    {
                        PostEvent("smartPreparationFinished", new
                        {
                            success = false,
                            module,
                            accessMode = "selfhost",
                            stage = selfHostPrinter.Stage,
                            message = selfHostPrinter.Message,
                            packageName = selfHostAutomation.PackageName,
                            smartDeviceId = selfHostExpectedDeviceId,
                            adbAndroidId = android.AndroidId,
                            url = selfHostUrl,
                            linkConfirmed = true
                        });
                        return;
                    }
                }
                var selfHostItems = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                var selfHostFailure = selfHostRefreshed is null || string.IsNullOrWhiteSpace(selfHostRefreshed.DeviceId)
                    ? $"A URL foi confirmada no Smart, mas o SelfHost nao registrou um Device ID em {oauthClient.Name}."
                    : $"O SelfHost registrou outro Device ID em {oauthClient.Name}; esperado {selfHostExpectedDeviceId}.";
                PostEvent("oauthClients", new { items = selfHostItems, companyId = company.Id, accessMode = "selfhost" });
                PostEvent("smartPreparationFinished", new
                {
                    success = selfHostLinked, module, accessMode = "selfhost", stage = selfHostLinked ? "linked" : "verify",
                    message = selfHostLinked
                        ? smartUiFinalized
                            ? $"Dispositivo SelfHost vinculado com sucesso. device_id = {selfHostExpectedDeviceId}; confirmacao final encerrada no Smart.{BuildPrinterCompletionSuffix(selfHostPrinter)}"
                            : $"Dispositivo SelfHost vinculado com sucesso. device_id = {selfHostExpectedDeviceId}; o Android nao expos o botao OK para fechamento automatico.{BuildPrinterCompletionSuffix(selfHostPrinter)}"
                        : selfHostFailure,
                    packageName = selfHostAutomation.PackageName, smartDeviceId = selfHostAutomation.SmartDeviceId, adbAndroidId = android.AndroidId, url = selfHostUrl
                });
                return;
            }

            if (IsOnlineMode(accessMode) || modernSelfHost)
            {
                var normalizedDatabase = EnvironmentCatalog.NormalizeDatabaseName(database);
                var effectiveAccessMode = modernSelfHost ? "selfhost" : "online";
                var previouslyLinkedDeviceId = oauthClient.DeviceId?.Trim() ?? string.Empty;
                var authoritativeSmartDeviceId = confirmedSmartDeviceId;

                async Task<IReadOnlyList<DeviceRegistryEntry>> LoadManagedDevicesAsync(string operation)
                {
                    var softcomshopDevices = await ExecuteOnlineAsync(
                        normalizedDatabase,
                        modernSelfHost ? $"{operation} no Softcomshop" : operation,
                        service => service.GetOAuthClientsAsync(
                            normalizedDatabase,
                            company.Id,
                            _shutdown.Token));

                    if (!modernSelfHost)
                    {
                        Progress(
                            "registry-scan",
                            $"Vinculos verificados em todos os {softcomshopDevices.Count} dispositivos do Softcomshop selecionado.");
                        return softcomshopDevices
                            .Select(x => new DeviceRegistryEntry(x, DeviceRegistrySource.Softcomshop))
                            .ToArray();
                    }

                    // A listagem administrativa percorre todas as paginas e, em uma
                    // oscilacao do Softcomshop, uma requisicao GET pode atingir o timeout.
                    // Repetimos somente essa operacao de leitura; POST de criacao ou
                    // desvinculacao nunca e repetido automaticamente.
                    IReadOnlyList<OAuthClientInfo>? selfHostDevices = null;
                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            // Esta consulta nunca pode usar ListDevicesAsync: ela filtra
                            // somente SELFHOST_ e esconderia vinculos mantidos em um
                            // dispositivo comum, como ocorreu no Getnet P3.
                            selfHostDevices = await _selfHostDeviceService.ListAllDevicesAsync(_shutdown.Token);
                            break;
                        }
                        catch (TaskCanceledException) when (!_shutdown.IsCancellationRequested && attempt < 2)
                        {
                            Progress(
                                "selfhost-api-retry",
                                $"A API administrativa demorou durante '{operation}'. Repetindo a consulta de leitura...");
                            await Task.Delay(750, _shutdown.Token);
                        }
                        catch (TaskCanceledException) when (!_shutdown.IsCancellationRequested)
                        {
                            throw new InvalidOperationException(
                                $"A API administrativa do SelfHost nao respondeu a tempo durante '{operation}', mesmo apos uma nova tentativa. O Smart nao foi confirmado; tente novamente.");
                        }
                    }

                    if (selfHostDevices is null)
                        throw new InvalidOperationException("Nao foi possivel consultar os dispositivos do SelfHost.");

                    Progress(
                        "registry-scan",
                        $"Vinculos verificados em todos os dispositivos das duas fontes: Softcomshop = {softcomshopDevices.Count}; API administrativa SelfHost = {selfHostDevices.Count}.");

                    return selfHostDevices
                        .Select(x => new DeviceRegistryEntry(x, DeviceRegistrySource.SelfHost))
                        .Concat(softcomshopDevices.Select(x =>
                            new DeviceRegistryEntry(x, DeviceRegistrySource.Softcomshop)))
                        .ToArray();
                }

                Task<bool> UnlinkManagedDeviceAsync(DeviceRegistryEntry entry) =>
                    entry.Source == DeviceRegistrySource.SelfHost
                        ? _selfHostDeviceService.UnlinkDeviceAsync(entry.Device.ClientId, _shutdown.Token)
                        : ExecuteOnlineAsync(
                            normalizedDatabase,
                            $"desvincular {entry.Device.Name}",
                            service => service.UnlinkOAuthClientAsync(
                                normalizedDatabase,
                                company.Id,
                                entry.Device.ClientId,
                                _shutdown.Token));

                static OAuthClientInfo[] GetVisibleItems(
                    IReadOnlyList<DeviceRegistryEntry> entries,
                    bool useModernSelfHost) =>
                    entries
                        .Where(x => useModernSelfHost
                            ? x.Source == DeviceRegistrySource.SelfHost &&
                              x.Device.Name.StartsWith("SELFHOST_", StringComparison.OrdinalIgnoreCase)
                            : x.Source == DeviceRegistrySource.Softcomshop)
                        .Select(x => x.Device)
                        .GroupBy(x => x.ClientId, StringComparer.Ordinal)
                        .Select(x => x.First())
                        .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();

                // Pre-flight remoto: acontece antes de limpar, abrir ou tocar no Smart.
                // Alem de consultar novamente o cadastro selecionado, usa apenas um Device ID
                // previamente confirmado por resposta remota. O ANDROID_ID do shell nao entra
                // nesta decisao em Android moderno.
                Progress(
                    modernSelfHost ? "selfhost-preflight" : "online-preflight",
                    "Verificando vinculos existentes antes de abrir o Smart...");
                var initialManagedDevices = await LoadManagedDevicesAsync("verificacao inicial de vinculos");
                var selectedRemote = initialManagedDevices.FirstOrDefault(x =>
                    string.Equals(x.Device.ClientId, oauthClient.ClientId, StringComparison.Ordinal) &&
                    (!modernSelfHost || x.Source == DeviceRegistrySource.SelfHost))?.Device;

                if (string.IsNullOrWhiteSpace(authoritativeSmartDeviceId) &&
                    selectedRemote is not null && selectedRemote.IsLinked)
                {
                    authoritativeSmartDeviceId = selectedRemote.DeviceId.Trim();
                }

                var initialConflicts = DeviceLinkConflictResolver.FindConflicts(
                    initialManagedDevices,
                    oauthClient.ClientId,
                    authoritativeSmartDeviceId);

                foreach (var conflict in initialConflicts)
                {
                    var source = DeviceLinkConflictResolver.SourceName(conflict.Source);
                    Progress("online-unlink-selected", $"Desvinculando {conflict.Device.Name} pelo {source} antes de iniciar a automacao...");
                    var selectedUnlinked = await UnlinkManagedDeviceAsync(conflict);

                    if (!selectedUnlinked)
                    {
                        throw new InvalidOperationException($"O {source} nao confirmou a desvinculacao de {conflict.Device.Name}.");
                    }
                }

                if (initialConflicts.Count > 0)
                {
                    var afterSelectedUnlink = await LoadManagedDevicesAsync("confirmar desvinculacao do cadastro selecionado");
                    var stillLinkedSelected = DeviceLinkConflictResolver.FindConflicts(
                        afterSelectedUnlink,
                        oauthClient.ClientId,
                        authoritativeSmartDeviceId).FirstOrDefault();
                    if (stillLinkedSelected is not null)
                    {
                        var source = DeviceLinkConflictResolver.SourceName(stillLinkedSelected.Source);
                        throw new InvalidOperationException(
                            $"O vinculo de {stillLinkedSelected.Device.Name} ainda aparece no {source} antes de abrir o Smart.");
                    }

                    Progress("online-unlink-selected", modernSelfHost
                        ? "Softcomshop e SelfHost confirmaram a remocao dos vinculos antes de abrir o Smart."
                        : "Vinculos da lista atual confirmados como removidos antes de abrir o Smart.");
                    PostEvent("oauthClients", new { items = GetVisibleItems(afterSelectedUnlink, modernSelfHost), companyId = company.Id, accessMode = effectiveAccessMode });
                }

                var onlineUrl = await ExecuteOnlineAsync(
                    normalizedDatabase,
                    "obter URL para preparar o Smart",
                    service => service.GetDeviceUrlAsync(normalizedDatabase, oauthClient.ClientId, _shutdown.Token));
                if (modernSelfHost)
                {
                    onlineUrl = BuildSelfHostDeviceUrl(onlineUrl, selfHostBaseUrl);
                    Progress(
                        "selfhost-url",
                        $"SelfHost {selfHostInstallation!.Version}: usando exatamente /device/add com os parametros do vinculo real do Softcomshop em {new Uri(onlineUrl).GetLeftPart(UriPartial.Authority)}.");
                }
                var onlineSettings = _settingsService.Load();

                async Task BeforeSubmitOnlineAsync(string smartDeviceId)
                {
                    Progress(
                        "online-unlink-check",
                        modernSelfHost
                            ? "Validacao final antes do ultimo Confirmar: conferindo se o cadastro selecionado e os vinculos anteriores estao realmente desvinculados..."
                            : $"Verificando o Device ID {smartDeviceId} nos dispositivos do Softcomshop selecionado...");
                    var items = await LoadManagedDevicesAsync("verificar vinculos anteriores");
                    var toUnlink = DeviceLinkConflictResolver.FindConflicts(
                        items,
                        modernSelfHost ? oauthClient.ClientId : null,
                        smartDeviceId,
                        modernSelfHost ? previouslyLinkedDeviceId : null);

                    if (toUnlink.Count > 0)
                    {
                        Progress(
                            "online-unlink",
                            "Vinculos encontrados: " + string.Join(", ", toUnlink.Select(x =>
                                $"{x.Device.Name} ({DeviceLinkConflictResolver.SourceName(x.Source)})")) + ".");
                        foreach (var item in toUnlink)
                        {
                            var changed = await UnlinkManagedDeviceAsync(item);
                            if (!changed)
                            {
                                var source = DeviceLinkConflictResolver.SourceName(item.Source);
                                throw new InvalidOperationException($"O {source} nao confirmou a desvinculacao de {item.Device.Name}.");
                            }
                        }
                    }
                    else
                    {
                        Progress("online-unlink", modernSelfHost
                            ? "Nenhum vinculo anterior foi encontrado no Softcomshop nem no SelfHost."
                            : "Nenhum vinculo anterior foi encontrado no Softcomshop selecionado.");
                    }

                    var refreshed = await LoadManagedDevicesAsync("confirmar desvinculacao");
                    var remaining = DeviceLinkConflictResolver.FindConflicts(
                        refreshed,
                        modernSelfHost ? oauthClient.ClientId : null,
                        smartDeviceId,
                        modernSelfHost ? previouslyLinkedDeviceId : null);
                    if (remaining.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "O ultimo Confirmar foi bloqueado porque ainda existe vinculo em: " +
                            string.Join(", ", remaining.Select(x =>
                                $"{x.Device.Name} ({DeviceLinkConflictResolver.SourceName(x.Source)})")) + ".");
                    }

                    if (modernSelfHost)
                    {
                        Progress(
                            "selfhost-unlink-confirmed",
                            "Softcomshop e SelfHost foram relidos e estao sem o vinculo anterior. O ultimo Confirmar esta liberado.");
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
                        modernSelfHost ? "selfhost-ready" : "online-ready",
                        modernSelfHost
                            ? $"Softcomshop preparado e Android com acesso ao SelfHost em {new Uri(onlineUrl).GetLeftPart(UriPartial.Authority)}."
                            : "Softcomshop preparado e Android com acesso publico. Nenhuma VPN/banco foi utilizado.");
                }

                var onlineAutomation = await _smartAutomationService.SubmitDeviceUrlAsync(
                    serial,
                    onlineUrl,
                    module,
                    ResolveDevicePackage(android, onlineSettings),
                    clearData,
                    Progress,
                    BeforeSubmitOnlineAsync,
                    _shutdown.Token,
                    authoritativeSmartDeviceId,
                    android.ProvisioningProfile);

                if (!onlineAutomation.Success)
                {
                    if (IsSafeDeviceId(onlineAutomation.SmartDeviceId))
                    {
                        RememberConfirmedSmartDeviceId(
                            serial,
                            android.AndroidId,
                            onlineAutomation.SmartDeviceId);
                    }
                    WriteLog(
                        "SMART",
                        $"[{serial}][{onlineAutomation.Stage}] {SanitizeSensitiveText(onlineAutomation.Message)} " +
                        $"Detalhe seguro: {SanitizeSensitiveText(onlineAutomation.UiSummary)}",
                        "ERROR");
                    PostEvent("smartPreparationFinished", new
                    {
                        success = false,
                        module,
                        accessMode = effectiveAccessMode,
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

                onlineSettings.SmartPackageNamesBySerial ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!onlineSettings.SmartPackageNamesBySerial.TryGetValue(serial, out var savedPackage) ||
                    !string.Equals(savedPackage, onlineAutomation.PackageName, StringComparison.OrdinalIgnoreCase))
                {
                    RememberSmartPackage(serial, onlineAutomation.PackageName);
                }

                Progress(
                    modernSelfHost ? "selfhost-verify" : "online-verify",
                    modernSelfHost
                        ? "Confirmando pela API administrativa qual Device ID o SelfHost registrou no cadastro selecionado..."
                        : "Aguardando o Softcomshop registrar no cadastro selecionado o Device ID real informado pelo Smart...");
                OAuthClientInfo? refreshedClient = null;
                IReadOnlyList<OAuthClientInfo> refreshedItems = Array.Empty<OAuthClientInfo>();
                var smartUiFinalized = false;
                if (modernSelfHost)
                {
                    // A consulta por client_id usa uma unica autenticacao e termina assim
                    // que a API retorna qualquer device_id. Erros HTTP/funcionais sao
                    // propagados imediatamente, sem aguardar a listagem paginada inteira.
                    refreshedClient = await _selfHostDeviceService.WaitForDeviceLinkAsync(
                        oauthClient.ClientId,
                        40,
                        TimeSpan.FromMilliseconds(750),
                        _shutdown.Token);

                    // Fecha a confirmacao assim que a consulta direta por client_id
                    // retornar o device_id. Nao aguardamos a listagem paginada inteira
                    // enquanto o Smart permanece parado no dialogo de sucesso.
                    if (refreshedClient is not null && !string.IsNullOrWhiteSpace(refreshedClient.DeviceId))
                    {
                        smartUiFinalized = await _smartAutomationService.DismissConfirmedSynchronizationAsync(
                            serial,
                            onlineAutomation.PackageName,
                            module,
                            Progress,
                            _shutdown.Token);
                    }

                    refreshedItems = await _selfHostDeviceService.ListDevicesAsync(_shutdown.Token);
                }
                else
                {
                    for (var attempt = 0; attempt < 30; attempt++)
                    {
                        await Task.Delay(1000, _shutdown.Token);
                        refreshedItems = GetVisibleItems(
                            await LoadManagedDevicesAsync("validar vinculo"),
                            useModernSelfHost: false);
                        refreshedClient = refreshedItems.FirstOrDefault(x =>
                            string.Equals(x.ClientId, oauthClient.ClientId, StringComparison.Ordinal));
                        if (refreshedClient is not null && !string.IsNullOrWhiteSpace(refreshedClient.DeviceId))
                        {
                            break;
                        }
                    }
                }

                var confirmedDeviceId = refreshedClient?.DeviceId?.Trim() ?? string.Empty;

                // Android 8+ restringe ANDROID_ID por assinatura do APK, usuario e
                // dispositivo. Assim, o valor obtido pelo shell ADB pode ser diferente
                // daquele usado internamente pelo Smart (caso confirmado no N950).
                // Para SelfHost 4.1+, o cadastro filho consultado por client_id e a
                // autoridade: qualquer device_id nao vazio surgido apos a submissao
                // confirma o vinculo desse cadastro. No modo Online, a consulta do cadastro
                // selecionado tambem e autoritativa depois que o pre-flight comprovou que ele
                // estava sem device_id antes do envio.
                var onlineLinked = !string.IsNullOrWhiteSpace(confirmedDeviceId);
                if (onlineLinked)
                {
                    var linkedDeviceId = confirmedDeviceId;
                    RememberConfirmedSmartDeviceId(serial, android.AndroidId, linkedDeviceId);

                    if (!smartUiFinalized)
                    {
                        // CompleteLegacy80SynchronizationAsync ja fecha o dialogo quando
                        // o UIAutomator consegue le-lo. No N950 ele pode estar indisponivel;
                        // apos a API confirmar o vinculo, tentamos o resource-id real do OK.
                        smartUiFinalized = await _smartAutomationService.DismissConfirmedSynchronizationAsync(
                            serial,
                            onlineAutomation.PackageName,
                            module,
                            Progress,
                            _shutdown.Token);
                    }

                    var onlinePrinter = await ApplyInternalSmartConfigurationAsync(
                        payload,
                        android,
                        onlineAutomation.PackageName,
                        module,
                        Progress,
                        _shutdown.Token);
                    if (onlinePrinter is { Success: false })
                    {
                        PostEvent("smartPreparationFinished", new
                        {
                            success = false,
                            module,
                            accessMode = effectiveAccessMode,
                            stage = onlinePrinter.Stage,
                            message = onlinePrinter.Message,
                            packageName = onlineAutomation.PackageName,
                            url = onlineUrl,
                            deviceId = linkedDeviceId,
                            smartDeviceId = linkedDeviceId,
                            adbAndroidId = android.AndroidId,
                            linkConfirmed = true
                        });
                        return;
                    }

                    Progress(
                        modernSelfHost ? "selfhost-complete" : "online-complete",
                        modernSelfHost
                            ? smartUiFinalized
                                ? $"Procedimento concluido: desvinculacao validada, novo device_id {linkedDeviceId} confirmado e tela final do Smart encerrada."
                                : $"Vinculo {linkedDeviceId} confirmado, mas o Android nao expos o controle da tela final para fechamento automatico."
                            : smartUiFinalized
                                ? $"Procedimento concluido para Device ID {linkedDeviceId}; tela final do Smart encerrada."
                                : $"Vinculo {linkedDeviceId} confirmado, mas o Android nao expos o controle da tela final para fechamento automatico.");

                    WriteLog(
                        modernSelfHost ? "SELFHOST" : "ONLINE",
                        modernSelfHost
                            ? $"Vinculo confirmado pela API administrativa para Device ID {linkedDeviceId}."
                            : $"Vinculo confirmado pelo Softcomshop para Device ID {linkedDeviceId}.");
                    PostEvent("smartPreparationFinished", new
                    {
                        success = true,
                        module,
                        accessMode = effectiveAccessMode,
                        stage = "linked",
                        message = !smartUiFinalized
                            ? $"Dispositivo vinculado com sucesso e device_id = {linkedDeviceId} confirmado. A tela final do Smart nao expos o botao para fechamento automatico; se ela ainda estiver aberta, toque em OK.{BuildPrinterCompletionSuffix(onlinePrinter)}"
                            : $"Dispositivo vinculado com sucesso. O Softcomshop confirmou device_id = {linkedDeviceId} e o procedimento foi finalizado.{BuildPrinterCompletionSuffix(onlinePrinter)}",
                        packageName = onlineAutomation.PackageName,
                        url = onlineUrl,
                        deviceId = linkedDeviceId,
                        smartDeviceId = modernSelfHost ? linkedDeviceId : onlineAutomation.SmartDeviceId,
                        adbAndroidId = android.AndroidId
                    });
                    PostEvent("oauthClients", new
                    {
                        items = refreshedItems,
                        companyId = company.Id,
                        accessMode = effectiveAccessMode,
                        preservePreparationResult = true
                    });
                    return;
                }

                var onlineCurrentDevice = refreshedClient?.DeviceId ?? string.Empty;
                PostEvent("smartPreparationFinished", new
                {
                    success = false,
                    module,
                    accessMode = effectiveAccessMode,
                    stage = "verify",
                    message = string.IsNullOrWhiteSpace(onlineCurrentDevice)
                        ? modernSelfHost
                            ? "O Smart enviou a solicitacao, mas o SelfHost nao registrou nenhum device_id no cadastro selecionado. Confira a mensagem mantida na tela do GPOS."
                            : "A URL foi enviada ao Smart, mas o Softcomshop nao registrou nenhum device_id no cadastro selecionado. Confira o erro apresentado pelo Smart."
                        : $"O cadastro selecionado foi vinculado ao device_id {onlineCurrentDevice}.",
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
                _shutdown.Token,
                confirmedSmartDeviceId: null,
                provisioningProfile: android.ProvisioningProfile);

            if (!automation.Success)
            {
                WriteLog(
                    "SMART",
                    $"[{serial}][{automation.Stage}] {SanitizeSensitiveText(automation.Message)} " +
                    $"Detalhe seguro: {SanitizeSensitiveText(automation.UiSummary)}",
                    "ERROR");
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
                RememberSmartPackage(serial, automation.PackageName);
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
                var databasePrinter = await ApplyInternalSmartConfigurationAsync(
                    payload,
                    android,
                    automation.PackageName,
                    module,
                    Progress,
                    _shutdown.Token);
                if (databasePrinter is { Success: false })
                {
                    PostEvent("smartPreparationFinished", new
                    {
                        success = false,
                        module,
                        stage = databasePrinter.Stage,
                        message = databasePrinter.Message,
                        accessMode,
                        packageName = automation.PackageName,
                        url,
                        deviceId = expectedDeviceId,
                        smartDeviceId = automation.SmartDeviceId,
                        adbAndroidId = android.AndroidId,
                        linkConfirmed = true
                    });
                    return;
                }
                PostEvent("smartPreparationFinished", new
                {
                    success = true,
                    module,
                    stage = "linked",
                    message = $"Dispositivo vinculado com sucesso. O banco confirmou device_id = {expectedDeviceId}.{BuildPrinterCompletionSuffix(databasePrinter)}",
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
        catch (OperationCanceledException) when (this._shutdown.IsCancellationRequested)
        {
            // Encerramento normal da aplicacao.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = SanitizeSensitiveText(ex.Message);
            var failedModule = ReadString(payload, "module") ?? "smart_pdv";
            var failedAccessMode = ShouldUseSelfHost(payload, failedModule)
                ? "selfhost"
                : ReadString(payload, "accessMode") ?? string.Empty;
            WriteLog("SMART", "Falha ao vincular dispositivo: " + message, "ERROR");
            PostEvent("smartPreparationFinished", new
            {
                success = false,
                module = failedModule,
                accessMode = failedAccessMode,
                stage = "prepare-error",
                message
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

        var context = _provisioningContext.Value;
        // Listagens administrativas emitidas no meio de jobs paralelos nao podem
        // substituir os dropdowns globais e dar a impressao de que todos os Androids
        // usam o cadastro do ultimo job. Cada job ja possui um snapshot imutavel.
        if (context is not null && type == "oauthClients") return;
        if (context is not null && type is "provisionProgress" or "smartPreparationFinished")
        {
            var element = JsonSerializer.SerializeToElement(payload, JsonOptions);
            var stage = element.TryGetProperty("stage", out var stageElement) ? stageElement.GetString() ?? "processando" : "processando";
            var message = element.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
            if (type == "provisionProgress")
            {
                context.Job.Progress(stage, message);
                _multiDeviceProvisioningService.NotifyChanged(context.Job);
            }
            else
            {
                var success = element.TryGetProperty("success", out var successElement) && successElement.ValueKind == JsonValueKind.True;
                context.Outcome = new ProvisioningJobOutcome(success, stage, message);
            }

            var enriched = element.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            enriched["serial"] = JsonSerializer.SerializeToElement(context.Job.Serial);
            enriched["friendlyName"] = JsonSerializer.SerializeToElement(context.Job.FriendlyName);
            if (type == "smartPreparationFinished" && !enriched.ContainsKey("module"))
                enriched["module"] = JsonSerializer.SerializeToElement("smart_pdv");
            payload = enriched;
        }

        if (InvokeRequired)
        {
            var capturedPayload = payload;
            BeginInvoke(new Action(() => PostEvent(type, capturedPayload)));
            return;
        }

        var json = JsonSerializer.Serialize(new { type, payload }, JsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void PostBusy(string key, bool active) =>
        PostEvent("busy", new { key, active });

    private void PostError(string message) =>
        PostEvent("toast", new { type = "error", message });

    private static string SanitizeSensitiveText(string? value)
    {
        var message = string.IsNullOrWhiteSpace(value)
            ? "Ocorreu uma falha sem mensagem detalhada."
            : value.Trim();
        message = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?i)(client_secret|access_token|authorization|cookie)\s*[:=]\s*[^\s,;]+",
            "$1=[oculto]");
        message = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?i)bearer\s+[A-Za-z0-9._~+/=-]+",
            "Bearer [oculto]");
        return message.Length <= 500 ? message : message[..500];
    }

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

    private async Task<SmartPrinterConfigurationResult?> ApplyInternalSmartConfigurationAsync(
        JsonElement payload,
        DeviceInfo android,
        string packageName,
        string module,
        Action<string, string> progress,
        CancellationToken cancellationToken)
    {
        var preset = ReadString(payload, "configurationPreset")?.Trim();
        if (!string.Equals(preset, "module-default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var profile = _smartPrinterProfileService.Resolve(module, android.ProvisioningProfile);
        if (profile is null)
        {
            return SmartPrinterConfigurationResult.Failed(
                "printer-profile",
                $"O vinculo foi confirmado, mas ainda nao existe perfil de impressora para o modulo {module} no dispositivo {android.FriendlyName}.");
        }

        if (string.IsNullOrWhiteSpace(packageName))
        {
            return SmartPrinterConfigurationResult.Failed(
                "printer-package",
                "O vinculo foi confirmado, mas o package real do Smart nao estava disponivel para aplicar a impressora.",
                profile);
        }

        return await _smartPrinterConfigurationService.ApplyAsync(
            android.Serial,
            packageName,
            profile,
            progress,
            cancellationToken);
    }

    private static string BuildPrinterCompletionSuffix(SmartPrinterConfigurationResult? result) =>
        result is { Success: true, Profile: not null }
            ? $" Impressora {result.Profile.DisplayName} aplicada."
            : string.Empty;

    private SelfHostDeviceService.SelfHostInstallationInfo RequireSelfHostInstallation()
    {
        var installation = _selfHostDeviceService.DetectInstallation();
        if (!installation.Installed)
        {
            throw new InvalidOperationException("SelfHost instalado nao foi localizado em Program Files.");
        }

        if (installation.Generation is "Versao nao identificada" or "Versao nao reconhecida")
        {
            throw new InvalidOperationException(
                $"SelfHost localizado em {installation.InstallPath}, mas a versao instalada nao pôde ser classificada com seguranca.");
        }

        return installation;
    }

    private static bool IsModernSelfHost(SelfHostDeviceService.SelfHostInstallationInfo installation) =>
        string.Equals(installation.Generation, "SelfHost 4.1+", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseSelfHost(JsonElement payload, string? module)
    {
        if (IsSelfHostModule(module)) return true;
        if (string.Equals(module, "smart_tef", StringComparison.OrdinalIgnoreCase)) return false;
        return ReadBool(payload, "useSelfHost", false);
    }

    private static bool IsSelfHostModule(string? module) =>
        string.Equals(module, "smart_comanda", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "smart_autopagamento", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacy80LargeSelfServiceModule(string? module) =>
        string.Equals(module, "smart_totem", StringComparison.OrdinalIgnoreCase) ||
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
            throw new InvalidOperationException(
                "Informe um endereco SelfHost valido, por exemplo http://192.168.0.10:7711 ou https://host/relayId.");

        // O pre-request do SelfHost deriva /authentication/token a partir da propria URL
        // /device/add. Por isso um prefixo de relay precisa ser preservado integralmente:
        // /{relayId}/device/add -> /{relayId}/authentication/token.
        var basePath = selfHost.AbsolutePath.TrimEnd('/');
        var devicePath = basePath.EndsWith("/device/add", StringComparison.OrdinalIgnoreCase)
            ? basePath
            : string.IsNullOrWhiteSpace(basePath)
                ? "/device/add"
                : basePath + "/device/add";

        var builder = new UriBuilder(selfHost)
        {
            Scheme = string.IsNullOrWhiteSpace(selfHost.Scheme) ? Uri.UriSchemeHttp : selfHost.Scheme,
            Path = devicePath,
            Query = source.Query.TrimStart('?')
        };

        // Para o SelfHost local, http sem porta explicita continua usando 7711.
        // HTTPS/relay preserva a porta padrao do proprio endereco, normalmente 443.
        if (selfHost.IsDefaultPort && selfHost.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            builder.Port = 7711;
        }

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

    private static IReadOnlyList<string> ReadStringArray(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return element.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static JsonElement WithSerial(JsonElement payload, string serial, bool requireMappedClient = false)
    {
        var values = payload.ValueKind == JsonValueKind.Object
            ? payload.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        values.Remove("serials");
        values["serial"] = JsonSerializer.SerializeToElement(serial);
        var mapped = false;
        if (payload.TryGetProperty("oauthClientsBySerial", out var mappings) &&
            mappings.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in mappings.EnumerateObject())
            {
                if (!property.Name.Equals(serial, StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.Object) continue;
                values["oauthClient"] = property.Value.Clone();
                mapped = true;
                break;
            }
        }
        values.Remove("oauthClientsBySerial");
        if (requireMappedClient && !mapped)
            values.Remove("oauthClient");
        return JsonSerializer.SerializeToElement(values, JsonOptions);
    }

    private sealed record ProvisioningRequest(
        ProvisioningJob Job,
        JsonElement Payload,
        string TargetClientId);

    private sealed class ProvisioningExecutionContext(ProvisioningJob job, CancellationToken cancellationToken)
    {
        public ProvisioningJob Job { get; } = job;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public ProvisioningJobOutcome? Outcome { get; set; }
    }

    private static string ResolveDevicePackage(DeviceInfo device, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(device.SmartPackage)) return device.SmartPackage;
        settings.SmartPackageNamesBySerial ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings.SmartPackageNamesBySerial.TryGetValue(device.Serial, out var perDevice) &&
            !string.IsNullOrWhiteSpace(perDevice)) return perDevice;
        return settings.SmartPackageName;
    }

    private void RememberSmartPackage(string serial, string packageName)
    {
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(packageName)) return;
        lock (_settingsMutationGate)
        {
            var latest = _settingsService.Load();
            latest.SmartPackageNamesBySerial ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            latest.SmartPackageNamesBySerial[serial] = packageName.Trim();
            _settingsService.Save(latest);
        }
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
        lock (_testAutomationGate)
        {
            try { _testAutomationRun?.Cancel(); } catch { }
        }
        try { _ = Task.Run(() => _dockerDbBridgeService.StopAsync(CancellationToken.None)); } catch { }
        try { _scrcpyService.Dispose(); } catch { }
        try { _selfHostDeviceService.Dispose(); } catch { }
        try { _multiDeviceProvisioningService.Dispose(); } catch { }
        try { _sharedInfrastructureGate.Dispose(); } catch { }
        try { _shutdown.Dispose(); } catch { }
    }
}
