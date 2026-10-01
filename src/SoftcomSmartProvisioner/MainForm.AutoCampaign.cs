using System.Text.Json;
using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner;

public sealed partial class MainForm
{
    private static readonly (string Module, string SuiteId, string Database, bool SelfHost)[] AutoCampaignPhases =
    [
        ("smart_comanda", "commands/commands.robot", "jormungandr", true),
        ("smart_pdv", "pdv/pdv.robot", "fafnir", false),
        ("smart_minimercado", "minimarket/minimarket.robot", "fafnir", false)
    ];

    private async Task RunAutoTestCampaignAsync(JsonElement payload)
    {
        if (_manualProvisioningRunning)
            throw new InvalidOperationException("Aguarde o provisionamento manual em andamento antes de iniciar a campanha.");
        CancellationTokenSource runCancellation;
        lock (_testAutomationGate)
        {
            if (_testAutomationRun is not null || _testAutomationSourceUpdating || _autoTestCampaignRunning)
                throw new InvalidOperationException("Ja existe uma execucao de testes ou atualizacao em andamento.");
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _testAutomationRun = runCancellation;
            _autoTestCampaignRunning = true;
        }

        PostBusy("testAutomationRun", true);
        PostBusy("provision", true);
        PostEvent("autoTestCampaignStarted", new { });
        string? lastReport = null;
        try
        {
            void Progress(string message, string level = "INFO") =>
                PostEvent("testAutomationProgress", new TestAutomationProgress(DateTimeOffset.Now, "auto-campanha", message, level));

            Progress("Validando SelfHost, cadastros e dispositivos antes de alterar os Androids...");
            _deviceCatalog = _deviceCatalogService.Load();
            await RefreshAndroidAsync();
            runCancellation.Token.ThrowIfCancellationRequested();
            var catalog = _testAutomationService.LoadCatalog(_androidDevices, GetTestDeviceCatalog());
            if (!catalog.Prerequisites.ProjectAvailable || !catalog.Prerequisites.RunnerAvailable ||
                !catalog.Prerequisites.EnvironmentAvailable || !catalog.Prerequisites.UvAvailable)
                throw new InvalidOperationException("Prepare o Automation, o .env e o uv antes da campanha.");
            var requestedSerials = ReadStringArray(payload, "serials");
            if (requestedSerials.Count == 0)
                throw new InvalidOperationException("Selecione ao menos uma maquininha para o autoteste.");
            if (requestedSerials.Count > 7 ||
                requestedSerials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedSerials.Count)
                throw new InvalidOperationException("Selecione de 1 a 7 maquininhas diferentes para o autoteste.");
            var selectedDevices = requestedSerials.Select(serial => catalog.Devices.FirstOrDefault(x =>
                string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (selectedDevices.Any(x => x is null || !x.IsOnline || !x.AutoCampaignSupported))
                throw new InvalidOperationException("Uma maquininha selecionada nao esta conectada ou nao possui perfil completo para as tres fases.");
            foreach (var phase in AutoCampaignPhases)
            {
                if (!catalog.Suites.Any(x => string.Equals(x.Id, phase.SuiteId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"A suite {phase.SuiteId} nao existe na fonte de testes atual.");
            }

            var installation = RequireSelfHostInstallation();
            if (!IsModernSelfHost(installation))
                throw new InvalidOperationException("A campanha requer o SelfHost 4.1+ configurado em jormungandr.");
            var selfHost = await _selfHostBridgeService.ReadAsync(installation.InstallPath, runCancellation.Token);
            if (!SelfHostClientMatcher.Matches(selfHost.Configuration, "jormungandr") ||
                selfHost.Configuration?.IsComplete != true ||
                !selfHost.Configuration.SmartEnabled ||
                !string.Equals(selfHost.Configuration.SoftcomShopEmpresa, "MATRIZ", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O SelfHost precisa estar ativo, completo e apontar para jormungandr / MATRIZ antes da campanha.");

            const string jormungandr = "softcoms_softcomshop_jormungandr";
            const string fafnir = "softcoms_softcomshop_fafnir";
            var jormCompanies = await ExecuteOnlineAsync(jormungandr, "validar empresa da campanha",
                service => service.GetCompaniesAsync(jormungandr, runCancellation.Token));
            var fafnirCompanies = await ExecuteOnlineAsync(fafnir, "validar empresa da campanha",
                service => service.GetCompaniesAsync(fafnir, runCancellation.Token));
            var jormCompany = RequireMatriz(jormCompanies, "jormungandr");
            var fafnirCompany = RequireMatriz(fafnirCompanies, "fafnir");
            var jormClients = await ExecuteOnlineAsync(jormungandr, "validar cadastros da campanha",
                service => service.GetOAuthClientsAsync(jormungandr, jormCompany.Id, runCancellation.Token));
            var fafnirClients = await ExecuteOnlineAsync(fafnir, "validar cadastros da campanha",
                service => service.GetOAuthClientsAsync(fafnir, fafnirCompany.Id, runCancellation.Token));
            if (!jormClients.Any(x => string.Equals(x.Name, selfHost.Configuration.SoftcomShopDevice, StringComparison.OrdinalIgnoreCase) && x.IsLinked))
                throw new InvalidOperationException("O dispositivo raiz configurado no SelfHost nao esta vinculado em jormungandr.");
            var selfHostClients = await _selfHostDeviceService.ListDevicesAsync(runCancellation.Token);
            var plan = AutoTestCampaignPlanner.Build(selectedDevices.Select(x => x!).ToArray(), selfHostClients, fafnirClients);
            if (plan.Skipped.Count > 0)
                throw new InvalidOperationException("Cadastros ausentes para a selecao: " + string.Join(" ", plan.Skipped));
            if (plan.Devices.Count != requestedSerials.Count)
                throw new InvalidOperationException("A selecao de maquininhas mudou durante o preflight. Atualize o catalogo e tente novamente.");
            var selectedIds = plan.Devices.SelectMany(x => new[] { x.SelfHostClient.ClientId, x.FafnirClient.ClientId });
            if (selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Devices.Count * 2)
                throw new InvalidOperationException("Dois aparelhos resolveram o mesmo cadastro remoto. Campanha cancelada antes de alterar os Androids.");

            var selfHostBaseUrl = ReadString(payload, "selfHostBaseUrl")?.Trim();
            if (string.IsNullOrWhiteSpace(selfHostBaseUrl))
            {
                var detected = new UriBuilder(DetectSelfHostBaseUrl());
                if (selfHost.Configuration.PortaHttp is > 0 and <= 65535)
                    detected.Port = selfHost.Configuration.PortaHttp.Value;
                selfHostBaseUrl = detected.Uri.ToString();
            }
            if (!selfHostBaseUrl.Contains("://", StringComparison.Ordinal))
                selfHostBaseUrl = "http://" + selfHostBaseUrl;
            if (!Uri.TryCreate(selfHostBaseUrl, UriKind.Absolute, out var selfHostUri) ||
                selfHostUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Informe um endereco HTTP ou HTTPS valido para o SelfHost.");
            if (selfHostUri.IsLoopback || selfHostUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Nao foi possivel detectar um IP de rede para o SelfHost acessivel pelas maquininhas.");

            Progress($"Preflight aprovado: {string.Join(", ", plan.Devices.Select(x => x.DeviceTag))}. SelfHost em jormungandr; PDV/Minimercado em fafnir.");
            foreach (var phase in AutoCampaignPhases)
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                var company = phase.SelfHost ? jormCompany : fafnirCompany;
                Progress($"Fase {phase.Module}: limpando dados, desvinculando e vinculando {plan.Devices.Count} aparelho(s)...");
                var jobs = plan.Devices.Select(x => new ProvisioningJob(
                    _androidDevices.First(d => d.Serial.Equals(x.Serial, StringComparison.OrdinalIgnoreCase)),
                    phase.SelfHost ? x.SelfHostClient.Name : x.FafnirClient.Name)).ToArray();
                var bySerial = plan.Devices.ToDictionary(x => x.Serial, StringComparer.OrdinalIgnoreCase);
                PostEvent("provisioningStarted", new { count = jobs.Length, maxParallelism = MultiDeviceProvisioningService.DefaultMaxParallelism });
                var outcomes = await _multiDeviceProvisioningService.RunAsync(jobs, async (job, token) =>
                {
                    var item = bySerial[job.Serial];
                    var context = new ProvisioningExecutionContext(job, token);
                    _provisioningContext.Value = context;
                    try
                    {
                        var request = JsonSerializer.SerializeToElement(new
                        {
                            accessMode = "online",
                            environment = "aws2",
                            database = phase.Database,
                            company,
                            oauthClient = phase.SelfHost ? item.SelfHostClient : item.FafnirClient,
                            serial = item.Serial,
                            module = phase.Module,
                            useSelfHost = phase.SelfHost,
                            selfHostBaseUrl,
                            clearData = true,
                            configurationPreset = "none"
                        }, JsonOptions);
                        await PrepareSmartDeviceAsync(request);
                        return context.Outcome ?? new ProvisioningJobOutcome(false, "unknown", "O provisionamento terminou sem confirmacao de vinculo.");
                    }
                    finally
                    {
                        _provisioningContext.Value = null;
                    }
                }, runCancellation.Token);
                PostEvent("provisioningFinished", new
                {
                    success = outcomes.Count(x => x.Status == ProvisioningJobStatus.Succeeded),
                    failed = outcomes.Count(x => x.Status == ProvisioningJobStatus.Failed),
                    canceled = outcomes.Count(x => x.Status == ProvisioningJobStatus.Canceled),
                    items = outcomes
                });
                runCancellation.Token.ThrowIfCancellationRequested();
                var failure = outcomes.FirstOrDefault(x => x.Status != ProvisioningJobStatus.Succeeded);
                if (failure is not null)
                    throw new InvalidOperationException($"A fase {phase.Module} parou no provisionamento de {failure.FriendlyName}: {failure.Error}");

                Progress($"Todos os vinculos de {phase.Module} confirmados. Iniciando {phase.SuiteId} em paralelo...");
                var result = await _testAutomationService.RunParallelAsync(
                    phase.SuiteId,
                    plan.Devices.Select(x => (x.Serial, x.DeviceTag)).ToArray(),
                    _androidDevices, GetTestDeviceCatalog(), runCancellation.Token,
                    progress => PostEvent("testAutomationProgress", progress));
                lastReport = result.ReportPath ?? lastReport;
                if (result.Canceled) throw new OperationCanceledException(runCancellation.Token);
                if (!result.Success)
                    throw new InvalidOperationException($"A fase {phase.Module} falhou nos testes. {result.Summary} Relatorio: {result.ReportPath ?? "nao gerado"}.");
                Progress($"Fase {phase.Module} aprovada para todos os aparelhos.");
            }
            PostEvent("autoTestCampaignFinished", new { success = true, message = "Comanda, PDV e Minimercado aprovados em todos os aparelhos selecionados.", reportPath = lastReport });
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            PostEvent("autoTestCampaignFinished", new { success = false, canceled = true, message = "Campanha cancelada. Nenhuma fase seguinte foi iniciada.", reportPath = lastReport });
        }
        catch (Exception ex)
        {
            var message = SensitiveDataSanitizer.Clean(ex.Message);
            WriteLog("AUTO TESTE", message, "ERROR");
            PostEvent("autoTestCampaignFinished", new { success = false, canceled = false, message, reportPath = lastReport });
        }
        finally
        {
            lock (_testAutomationGate)
            {
                if (ReferenceEquals(_testAutomationRun, runCancellation)) _testAutomationRun = null;
                _autoTestCampaignRunning = false;
            }
            runCancellation.Dispose();
            PostBusy("provision", false);
            PostBusy("testAutomationRun", false);
        }
    }

    private static CompanyInfo RequireMatriz(IReadOnlyList<CompanyInfo> companies, string client) =>
        companies.SingleOrDefault(x => string.Equals(x.Name, "MATRIZ", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"A empresa MATRIZ nao foi localizada em {client}.");
}
