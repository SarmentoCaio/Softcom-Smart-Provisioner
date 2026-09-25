using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SoftcomSmartProvisioner.Services;

public sealed record SmartAutomationResult(
    bool Success,
    string PackageName,
    string Stage,
    string Message,
    string UiSummary,
    string SmartDeviceId);

public sealed class SmartUiAutomationService
{
    private readonly AdbService _adb;

    private static readonly string[] StartConfigurationLabels =
    {
        "iniciar configuracao",
        "configurar dispositivo",
        "novo dispositivo"
    };

    private static readonly string[] AdvanceLabels =
    {
        "avancar",
        "continuar",
        "proximo"
    };

    private static readonly string[] SubmitLabels =
    {
        "confirmar",
        "vincular",
        "salvar",
        "adicionar",
        "conectar"
    };

    private static readonly IReadOnlyDictionary<string, string> ModuleLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["smart_pdv"] = "Smart PDV",
            ["smart_pre_venda"] = "Smart Pre-Venda",
            ["smart_tef"] = "Smart TEF",
            ["smart_minimercado"] = "Smart Minimercado",
            ["smart_totem"] = "Smart Totem",
            ["smart_comanda"] = "Smart Comanda",
            ["smart_autopagamento"] = "Smart Autopagamento"
        };

    public SmartUiAutomationService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<SmartAutomationResult> SubmitDeviceUrlAsync(
        string serial,
        string url,
        string module,
        string? configuredPackage,
        bool clearData,
        Action<string, string>? progress = null,
        Func<string, Task>? beforeSubmitAsync = null,
        CancellationToken cancellationToken = default,
        string? confirmedSmartDeviceId = null,
        string? provisioningProfile = null)
    {
        progress?.Invoke("package", "Identificando o package do Smart neste dispositivo...");
        var packageName = await ResolvePackageAsync(serial, configuredPackage, cancellationToken);
        progress?.Invoke("package", $"Package validado para este job: {packageName}.");

        // A versao e validada novamente no momento do provisionamento. A lista de Androids
        // tambem exibe essa informacao, mas a leitura aqui evita usar um diagnostico antigo
        // caso o APK tenha sido atualizado depois da ultima atualizacao da tela.
        var smartVersion = await _adb.GetPackageVersionNameAsync(serial, packageName, cancellationToken);
        var smartFlow = AdbService.ClassifySmartFlow(smartVersion);
        var isLegacySmart = string.Equals(smartFlow, "Smart legado (< 8.1)", StringComparison.OrdinalIgnoreCase);
        progress?.Invoke(
            "smart-version",
            string.IsNullOrWhiteSpace(smartVersion)
                ? "Versao do Smart nao identificada. O fluxo sera validado pela interface atual."
                : $"Smart {smartVersion} detectado. Perfil de interface: {smartFlow}.");

        if (clearData)
        {
            progress?.Invoke("clear", $"Limpando os dados locais de {packageName}...");
            var clear = await TryClearPackageAsync(serial, packageName, "Smart", progress, cancellationToken);
            if (!clear.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "clear",
                    clear.Detail,
                    string.Empty, string.Empty);
            }
        }
        else
        {
            progress?.Invoke("restart", "Reiniciando o Softcom Smart sem limpar os dados locais...");
            var stop = await _adb.ForceStopPackageAsync(serial, packageName, cancellationToken);
            if (!stop.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "restart",
                    string.IsNullOrWhiteSpace(stop.CombinedOutput)
                        ? "Nao foi possivel fechar o Softcom Smart antes de reabri-lo."
                        : stop.CombinedOutput.Trim(),
                    string.Empty, string.Empty);
            }

            await Task.Delay(500, cancellationToken);
        }

        // Smart 8.0 / Android 7: o `pm clear` revoga as permissoes runtime.
        // O APK 8.0 mapeado declara READ_EXTERNAL_STORAGE e WRITE_EXTERNAL_STORAGE e,
        // ao abrir a configuracao, solicita o grupo de armazenamento. Concedemos essas
        // permissoes antes do launch para que o dialogo do Android nao interrompa o fluxo.
        if (isLegacySmart)
        {
            progress?.Invoke(
                "legacy-permissions",
                "Garantindo as permissoes de armazenamento exigidas pelo Smart 8.0...");

            var permissionResult = await EnsureLegacyStoragePermissionsAsync(
                serial,
                packageName,
                cancellationToken);

            progress?.Invoke(
                "legacy-permissions",
                permissionResult.Success
                    ? (string.IsNullOrWhiteSpace(permissionResult.Detail)
                        ? "Permissoes de armazenamento do Smart 8.0 concedidas via ADB."
                        : permissionResult.Detail)
                    : "Nao foi possivel confirmar todas as permissoes de armazenamento via ADB. O fluxo continuara e validara uma eventual solicitacao do Android. " + permissionResult.Detail);
        }

        progress?.Invoke("launch", "Abrindo o Softcom Smart no Android selecionado...");
        var launch = await _adb.LaunchPackageAsync(serial, packageName, cancellationToken);
        if (!launch.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "launch",
                string.IsNullOrWhiteSpace(launch.CombinedOutput)
                    ? "Nao foi possivel abrir o Softcom Smart."
                    : launch.CombinedOutput.Trim(),
                string.Empty, string.Empty);
        }

        await Task.Delay(1800, cancellationToken);

        // O onboarding do Smart 8.1+ no K2 e uma interface Compose completamente
        // diferente do fluxo legado 8.0. Nesta ROM Android 7, `uiautomator dump`
        // retira a AuthActivity do primeiro plano e devolve o terminal ao launcher.
        // A decisao precisa ocorrer ANTES da primeira leitura da arvore Android.
        var smart81SdkLevel = !isLegacySmart
            ? await GetAndroidSdkLevelAsync(serial, cancellationToken)
            : 0;
        if (ShouldUseSmart81K2OnboardingFlow(
                smartFlow,
                smart81SdkLevel,
                module,
                provisioningProfile,
                packageName,
                clearData))
        {
            return await SubmitDeviceUrlSmart81K2OnboardingAsync(
                serial,
                url,
                module,
                packageName,
                confirmedSmartDeviceId,
                progress,
                beforeSubmitAsync,
                cancellationToken);
        }

        // Smart 8.0 / Android 7: o mapeamento real do K2_MINI mostrou que
        // `uiautomator dump` falha na LoginActivity com "ERROR: could not get idle state".
        // Portanto NUNCA lemos a arvore de acessibilidade enquanto essa Activity estiver
        // em primeiro plano. Confirmamos a Activity via dumpsys, tocamos a engrenagem
        // usando a posicao proporcional mapeada e somente depois, ja em EmpresaActivity,
        // voltamos a usar UIAutomator. Isso evita fechar/perder o foco do APK legado.
        UiSnapshot snapshot;
        // Em Android 8+ o ANDROID_ID lido pelo shell ADB nao pertence ao mesmo
        // escopo de assinatura do APK. Quando o Provisioner ja confirmou o ID real
        // em uma vinculacao anterior, ele entra aqui como a unica fonte antecipada.
        var smartDeviceId = confirmedSmartDeviceId?.Trim() ?? string.Empty;

        if (isLegacySmart)
        {
            // No Android 7 o foco da janela pode apontar momentaneamente para o launcher
            // enquanto a LoginActivity do Smart ja esta sendo retomada. Nao podemos cair
            // no UIAutomator nesse intervalo, pois o dump nessa tela foi comprovadamente
            // instavel no K2_MINI. Aguarda primeiro uma Activity REAL do Smart 8.0.
            var foregroundActivity = await WaitForLegacySmartActivityAsync(
                serial,
                24,
                cancellationToken);

            progress?.Invoke(
                "legacy-activity",
                string.IsNullOrWhiteSpace(foregroundActivity)
                    ? "Nenhuma Activity do Smart 8.0 foi confirmada apos o launch."
                    : $"Activity inicial estabilizada para o Smart 8.0: {foregroundActivity}.");

            if (IsLegacyLoginActivity(foregroundActivity))
            {
                var largeSelfService = IsLegacy80LargeSelfServiceModule(module);
                var sdkLevel = await GetAndroidSdkLevelAsync(serial, cancellationToken);

                progress?.Invoke(
                    "legacy-settings",
                    sdkLevel > 25 && !largeSelfService
                        ? $"Smart 8.0 na LoginActivity (SDK {sdkLevel}). Localizando a engrenagem pela arvore da tela do celular/GPOS..."
                        : "Smart 8.0 na LoginActivity. Abrindo a engrenagem pelo ponto mapeado, sem UIAutomator nesta tela...");

                var display = await GetAndroidDisplaySizeAsync(serial, cancellationToken);
                if (!display.Success)
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-settings",
                        "A tela de login do Smart 8.0 foi identificada, mas nao foi possivel obter a resolucao do Android para acionar a engrenagem.",
                        foregroundActivity,
                        string.Empty);
                }

                int settingsX;
                int settingsY;
                var settingsSource = "ponto mapeado";

                // O problema de UIAutomator foi comprovado no K2_MINI / Android 7 (SDK 25).
                // Em Android mais novo, especialmente no emulador/celular, a arvore e estavel
                // e e mais seguro localizar o botao real do que escalar uma coordenada de screenshot.
                if (!largeSelfService && sdkLevel > 25)
                {
                    var loginUi = await ReadUiQuickAsync(serial, cancellationToken);
                    var settingsNode = loginUi.Success && IsUiFromPackage(loginUi.Nodes, packageName)
                        ? FindLegacySettingsButton(loginUi.Nodes)
                        : null;

                    if (settingsNode is not null)
                    {
                        settingsX = settingsNode.CenterX;
                        settingsY = settingsNode.CenterY;
                        settingsSource = string.IsNullOrWhiteSpace(settingsNode.ResourceId)
                            ? "arvore da tela"
                            : $"resource-id {settingsNode.ResourceId}";
                    }
                    else
                    {
                        // Alguns GPOS modernos (confirmado no Newland N950 / Android 12)
                        // nao conseguem produzir o XML do UIAutomator na LoginActivity,
                        // embora `dumpsys activity top` exponha a hierarquia nativa completa.
                        // Nesse caso usamos o retangulo real de app:id/btn_config e acumulamos
                        // os offsets dos pais (inclusive a barra de status) antes de tocar.
                        var dumpSettings = await FindViewBoundsFromActivityDumpAsync(
                            serial,
                            "app:id/btn_config",
                            cancellationToken);

                        if (dumpSettings.Success)
                        {
                            settingsX = Math.Clamp(dumpSettings.CenterX, 1, display.Width - 1);
                            settingsY = Math.Clamp(dumpSettings.CenterY, 1, display.Height - 1);
                            settingsSource = "dumpsys activity top (app:id/btn_config)";
                        }
                        else
                        {
                            var settingsXRatio = 258d / 307d;
                            var settingsYRatio = 265d / 672d;
                            settingsX = Math.Clamp((int)Math.Round(display.Width * settingsXRatio), 1, display.Width - 1);
                            settingsY = Math.Clamp((int)Math.Round(display.Height * settingsYRatio), 1, display.Height - 1);
                            settingsSource = loginUi.Success
                                ? "fallback proporcional (engrenagem nao exposta nas hierarquias Android)"
                                : "fallback proporcional (hierarquias Android indisponiveis)";
                        }
                    }
                }
                else
                {
                    // Totem/AutoPagamento e Android 7 preservam o comportamento ja validado.
                    var settingsXRatio = largeSelfService ? (1000d / 1080d) : (258d / 307d);
                    var settingsYRatio = largeSelfService ? (477d / 1920d) : (265d / 672d);
                    settingsX = Math.Clamp((int)Math.Round(display.Width * settingsXRatio), 1, display.Width - 1);
                    settingsY = Math.Clamp((int)Math.Round(display.Height * settingsYRatio), 1, display.Height - 1);
                }

                progress?.Invoke(
                    "legacy-settings-tap",
                    $"LoginActivity confirmada. Acionando a engrenagem em {settingsX},{settingsY} via {settingsSource}.");

                var tapSettings = await _adb.TapAsync(serial, settingsX, settingsY, cancellationToken);
                if (!tapSettings.Success)
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-settings-tap",
                        "Nao foi possivel acionar a engrenagem do Smart 8.0 pelo ADB.",
                        foregroundActivity,
                        string.Empty);
                }

                var configurationActivity = await WaitForLegacyCompanyActivityAsync(
                    serial,
                    packageName,
                    sdkLevel,
                    settingsX,
                    settingsY,
                    progress,
                    cancellationToken);

                if (!IsLegacyCompanyActivity(configurationActivity))
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-settings-open",
                        string.IsNullOrWhiteSpace(configurationActivity)
                            ? "A engrenagem foi acionada, mas a EmpresaActivity do Smart 8.0 nao apareceu."
                            : $"A engrenagem foi acionada, mas a Activity esperada nao abriu. Activity atual: {configurationActivity}.",
                        configurationActivity,
                        string.Empty);
                }

                progress?.Invoke(
                    "legacy-settings-open",
                    "EmpresaActivity aberta. O Smart 8.0 permanecera sem UIAutomator nesta tela para evitar perda de foco no Android 7.");

                // Essa equivalencia foi confirmada somente no Android 7. Em Android 8+
                // o ANDROID_ID e escopado pela assinatura do APK e o shell retorna outro
                // valor; portanto ele nunca pode substituir o ID real do Smart.
                if (sdkLevel <= 25)
                {
                    smartDeviceId = FirstNonEmpty(
                        smartDeviceId,
                        await GetAndroidIdFallbackAsync(serial, cancellationToken));
                }
                progress?.Invoke(
                    "legacy-settings",
                    string.IsNullOrWhiteSpace(smartDeviceId)
                        ? "Tela Configurar Empresas aberta. O Android moderno nao permite usar o ANDROID_ID do shell como Device ID do Smart."
                        : $"Tela Configurar Empresas aberta. Device ID autoritativo disponivel: {smartDeviceId}.");

                // IMPORTANTE: no Android 7 deste Smart 8.0, o uiautomator pode retirar o
                // aplicativo do primeiro plano mesmo na EmpresaActivity. O botao NOVA
                // EMPRESA ja foi mapeado no aparelho de referencia: bounds
                // [540,1771][1059,1835] em 1080x1920, centro aproximado 800,1803.
                // Portanto acionamos diretamente o ponto proporcional, mas somente depois
                // de confirmar por dumpsys que a EmpresaActivity ainda esta em primeiro plano.
                var activityBeforeNewCompany = await GetLegacySmartActivityAsync(serial, cancellationToken);
                if (!IsLegacyCompanyActivity(activityBeforeNewCompany))
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-new-company",
                        $"A tela Configurar Empresas foi aberta, mas deixou de estar em primeiro plano antes de Nova Empresa. Activity atual: {activityBeforeNewCompany}.",
                        activityBeforeNewCompany,
                        smartDeviceId);
                }

                var newCompanyDisplay = await GetAndroidDisplaySizeAsync(serial, cancellationToken);
                if (!newCompanyDisplay.Success)
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-new-company",
                        "A tela Configurar Empresas foi aberta, mas nao foi possivel obter a resolucao do Android para acionar Nova Empresa.",
                        activityBeforeNewCompany,
                        smartDeviceId);
                }

                var largeSelfServiceNewCompany = IsLegacy80LargeSelfServiceModule(module);
                var newCompanyXRatio = largeSelfServiceNewCompany ? (800d / 1080d) : (223d / 304d);
                var newCompanyYRatio = largeSelfServiceNewCompany ? (1803d / 1920d) : (634d / 676d);
                var newCompanyX = Math.Clamp((int)Math.Round(newCompanyDisplay.Width * newCompanyXRatio), 1, newCompanyDisplay.Width - 1);
                var newCompanyY = Math.Clamp((int)Math.Round(newCompanyDisplay.Height * newCompanyYRatio), 1, newCompanyDisplay.Height - 1);
                var newCompanySource = "ponto proporcional mapeado";

                if (!largeSelfServiceNewCompany && sdkLevel > 25)
                {
                    var newCompanyBounds = await FindViewBoundsFromActivityDumpAsync(
                        serial,
                        "app:id/btn_novo",
                        cancellationToken);
                    if (IsActivityPointInsideDisplay(newCompanyBounds, newCompanyDisplay))
                    {
                        newCompanyX = newCompanyBounds.CenterX;
                        newCompanyY = newCompanyBounds.CenterY;
                        newCompanySource = "dumpsys activity top (app:id/btn_novo)";
                    }
                }

                await Task.Delay(450, cancellationToken);
                activityBeforeNewCompany = await GetLegacySmartActivityAsync(serial, cancellationToken);
                if (!IsLegacyCompanyActivity(activityBeforeNewCompany))
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-new-company",
                        $"A EmpresaActivity nao permaneceu estavel ate o clique em Nova Empresa. Activity atual: {activityBeforeNewCompany}.",
                        activityBeforeNewCompany,
                        smartDeviceId);
                }

                progress?.Invoke(
                    "legacy-new-company",
                    $"EmpresaActivity confirmada. Acionando Nova Empresa em {newCompanyX},{newCompanyY} via {newCompanySource}...");

                var tapNewCompany = await _adb.TapAsync(
                    serial,
                    newCompanyX,
                    newCompanyY,
                    cancellationToken);

                if (!tapNewCompany.Success)
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-new-company",
                        "Nao foi possivel acionar Nova Empresa pelo ADB.",
                        tapNewCompany.CombinedOutput.Trim(),
                        smartDeviceId);
                }

                // O mapeamento completo confirmou que NOVA EMPRESA abre
                // EmpresaAddConfigActivity. Usamos a Activity como primeira validacao e
                // somente depois lemos a arvore da tela. Isso evita confundir a lista de
                // empresas com a selecao de modulo do Smart 8.0.
                var addConfigActivity = await WaitForLegacyActivityAsync(
                    serial,
                    IsLegacyCompanyAddConfigActivity,
                    20,
                    cancellationToken);

                if (!IsLegacyCompanyAddConfigActivity(addConfigActivity))
                {
                    return new SmartAutomationResult(
                        false,
                        packageName,
                        "legacy-new-company-open",
                        string.IsNullOrWhiteSpace(addConfigActivity)
                            ? "Nova Empresa foi acionado, mas a EmpresaAddConfigActivity nao apareceu."
                            : $"Nova Empresa foi acionado, mas a Activity esperada nao abriu. Activity atual: {addConfigActivity}.",
                        addConfigActivity,
                        smartDeviceId);
                }

                // Nao lemos a arvore da EmpresaAddConfigActivity aqui. No K2_MINI / Android 7
                // o UIAutomator tambem pode interferir nessa tela e fazer o Smart recuar/fechar.
                // O fluxo especializado decide se pode usar acessibilidade (celular/GPOS) ou se
                // deve seguir apenas por Activity + pontos proporcionais (Totem/AutoPagamento).
                snapshot = new UiSnapshot(false, Array.Empty<UiNode>(), string.Empty);

                progress?.Invoke(
                    "legacy-new-company-open",
                    "Tela Nova Empresa aberta. Iniciando o fluxo mapeado do Smart 8.0: modulo -> Confirmar -> DIGITAR -> Host.");

                return await SubmitDeviceUrlLegacy80Async(
                    serial,
                    url,
                    module,
                    packageName,
                    snapshot,
                    smartDeviceId,
                    progress,
                    beforeSubmitAsync,
                    cancellationToken,
                    provisioningProfile);
            }
            else if (IsLegacySmartPackageActivity(foregroundActivity))
            {
                // O UIAutomator so e permitido depois que dumpsys confirmou que o primeiro
                // plano pertence de fato ao softcom.mobile.smart2 e nao e a LoginActivity.
                progress?.Invoke(
                    "legacy-activity",
                    $"Activity do Smart 8.0 confirmada antes da leitura da interface: {foregroundActivity}.");
                snapshot = await WaitForReadableUiAsync(serial, 10, cancellationToken);
            }
            else
            {
                var mercadoPagoLauncher = foregroundActivity.Contains(
                    "com.mercadopago.smartpos/",
                    StringComparison.OrdinalIgnoreCase);
                var mercadoPagoLogin = foregroundActivity.Contains(
                    "TestUserLoginActivity",
                    StringComparison.OrdinalIgnoreCase);
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy-launch-wait",
                    mercadoPagoLauncher
                        ? mercadoPagoLogin
                            ? "O Softcom Smart foi iniciado, mas o launcher do Mercado Pago retomou o primeiro plano. O terminal esta na tela de login de usuario de teste do Mercado Pago; conclua esse login e tente novamente."
                            : "O Softcom Smart foi iniciado, mas o launcher do Mercado Pago retomou o primeiro plano. Desbloqueie o aplicativo da adquirente e tente novamente."
                        : string.IsNullOrWhiteSpace(foregroundActivity)
                        ? $"O Smart 8.0 foi iniciado, mas nenhuma Activity de {packageName} ficou em primeiro plano. A automacao foi interrompida sem executar UIAutomator na tela de login."
                        : $"O Smart 8.0 foi iniciado, mas a Activity do package nao estabilizou. Primeiro plano observado: {foregroundActivity}. A automacao foi interrompida sem executar UIAutomator.",
                    foregroundActivity,
                    smartDeviceId);
            }
        }
        else
        {
            // Em 8.1+ mantemos exatamente o comportamento ja validado.
            snapshot = await WaitForReadableUiAsync(serial, 10, cancellationToken);
        }

        if (!snapshot.Success)
        {
            return new SmartAutomationResult(false, packageName, "ui", snapshot.Error, string.Empty, smartDeviceId);
        }

        // Em Smart legado, nenhum toque adicional e executado se a arvore atual nao
        // pertencer ao package esperado. A excecao e a LoginActivity acima, validada
        // exclusivamente por dumpsys antes do toque mapeado na engrenagem.
        if (isLegacySmart && !IsUiFromPackage(snapshot.Nodes, packageName))
        {
            var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy-launch-verify",
                string.IsNullOrWhiteSpace(foreground)
                    ? $"A interface atual nao pertence ao package {packageName}. A automacao foi interrompida sem tocar em outro aplicativo."
                    : $"O primeiro plano atual e {foreground}. A automacao foi interrompida sem tocar em outro aplicativo.",
                BuildSummary(snapshot.Nodes),
                smartDeviceId);
        }

        smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(snapshot.Nodes));

        // O Smart 8.1 possui dois caminhos diferentes:
        // 1) onboarding/primeira configuracao: Bem-vindo -> Selecione o modulo -> Configurar modulo.
        //    Esse e o fluxo que ja estava validado na v1.0.2 e deve continuar sendo usado,
        //    inclusive para Smart Comanda e Smart Autopagamento via SelfHost.
        // 2) Smart ja fora do onboarding: Configuracoes -> Nova Empresa -> DIGITAR.
        //
        // A regressao da v1.0.3 ocorria porque qualquer 8.1+ era enviado diretamente para
        // Nova Empresa, mesmo quando a tela real era "Selecione o modulo".
        var isSmart81 = string.Equals(smartFlow, "Smart 8.1+", StringComparison.OrdinalIgnoreCase);
        var moduleLabel = GetModuleLabel(module);
        var isInitialProvisioning = IsInitialProvisioningState(snapshot.Nodes, moduleLabel);

        if (isSmart81 && !isInitialProvisioning)
        {
            return await SubmitDeviceUrlSmart81Async(
                serial,
                url,
                module,
                packageName,
                snapshot,
                smartDeviceId,
                progress,
                beforeSubmitAsync,
                cancellationToken);
        }

        if (isSmart81 && isInitialProvisioning)
        {
            progress?.Invoke(
                "smart81-onboarding",
                $"Smart 8.1+ em configuracao inicial. Mantendo o fluxo validado da v1.0.2 para {moduleLabel}: selecao de modulo -> Avancar -> URL.");
        }

        // A LoginActivity do Smart 8.0 ja foi tratada antes da primeira chamada ao
        // UIAutomator. Se chegamos aqui, estamos em uma tela que pode ser lida normalmente.

        // Tela inicial do Smart: "Bem vindo ao Smart!" -> "Iniciar Configuracao".
        if (ContainsLabel(snapshot.Nodes, "bem vindo ao smart") ||
            FindByLabels(snapshot.Nodes, StartConfigurationLabels) is not null)
        {
            progress?.Invoke("start", "Abrindo a configuracao inicial do Smart...");
            var start = FindByLabels(snapshot.Nodes, StartConfigurationLabels);
            if (start is null)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "start",
                    "A tela inicial do Smart foi identificada, mas o botao Iniciar Configuracao nao foi localizado.",
                    BuildSummary(snapshot.Nodes), string.Empty);
            }

            await _adb.TapAsync(serial, start.CenterX, start.CenterY, cancellationToken);
            await Task.Delay(900, cancellationToken);
            snapshot = await ReadUiAsync(serial, cancellationToken);
            if (!snapshot.Success)
            {
                return new SmartAutomationResult(false, packageName, "ui", snapshot.Error, string.Empty, string.Empty);
            }
            smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(snapshot.Nodes));
        }

        // Tela "Selecione o modulo".
        if (ContainsLabel(snapshot.Nodes, "selecione o modulo"))
        {
            progress?.Invoke("module", $"Selecionando o modulo {moduleLabel}...");

            var moduleCard = FindByLabels(snapshot.Nodes, new[] { moduleLabel });
            if (moduleCard is null)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "module",
                    $"A tela de modulos abriu, mas nao foi possivel localizar {moduleLabel}.",
                    BuildSummary(snapshot.Nodes), string.Empty);
            }

            // Em Compose o card e clicavel, mas o controle visual de selecao fica no lado
            // direito. Tocar nessa regiao evita o comportamento observado de manter o primeiro
            // modulo como padrao em algumas builds.
            var moduleTapX = Math.Max(moduleCard.Left + 1, moduleCard.Right - 55);
            await _adb.TapAsync(serial, moduleTapX, moduleCard.CenterY, cancellationToken);
            await Task.Delay(650, cancellationToken);

            snapshot = await ReadUiAsync(serial, cancellationToken);
            if (!snapshot.Success)
            {
                return new SmartAutomationResult(false, packageName, "ui", snapshot.Error, string.Empty, string.Empty);
            }
            smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(snapshot.Nodes));

            var advance = FindByLabels(snapshot.Nodes, AdvanceLabels);
            if (advance is null)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "module",
                    "O modulo foi selecionado, mas o botao Avancar nao foi localizado.",
                    BuildSummary(snapshot.Nodes), string.Empty);
            }

            progress?.Invoke("advance", "Avancando para a configuracao da URL...");
            await _adb.TapAsync(serial, advance.CenterX, advance.CenterY, cancellationToken);
            await Task.Delay(1000, cancellationToken);
            snapshot = await ReadUiAsync(serial, cancellationToken);
            if (!snapshot.Success)
            {
                return new SmartAutomationResult(false, packageName, "ui", snapshot.Error, string.Empty, string.Empty);
            }
            smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(snapshot.Nodes));

            // Nao continua silenciosamente caso o Smart tenha mantido outro modulo marcado.
            // A tela seguinte deve expor "Configurar <modulo escolhido>" e/ou "Modulo <modulo>".
            if (!IsExpectedModuleConfiguration(snapshot.Nodes, moduleLabel))
            {
                var detected = DetectConfiguredModule(snapshot.Nodes);
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "module",
                    string.IsNullOrWhiteSpace(detected)
                        ? $"O Smart avancou, mas nao confirmou a configuracao do modulo {moduleLabel}."
                        : $"O Smart abriu a configuracao de {detected}, mas o modulo solicitado foi {moduleLabel}.",
                    BuildSummary(snapshot.Nodes),
                    smartDeviceId);
            }
        }

        if (isLegacySmart &&
            (!IsUiFromPackage(snapshot.Nodes, packageName) || !IsLegacySmartConfigurationScreen(snapshot.Nodes)))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy-ui-state",
                "O Smart legado nao esta na tela de configuracao esperada. A automacao foi interrompida antes de qualquer toque adicional.",
                BuildSummary(snapshot.Nodes),
                smartDeviceId);
        }

        // Neste ponto o Smart ja informou o Device ID real usado pelo Softcomshop.
        // O chamador pode usar esse ID para localizar/desvincular um vinculo anterior no banco
        // e somente depois derrubar a VPN, imediatamente antes de enviar a URL.
        if (beforeSubmitAsync is not null)
        {
            smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(snapshot.Nodes));
            if (string.IsNullOrWhiteSpace(smartDeviceId))
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "device-id",
                    "A tela de configuracao abriu, mas o Device ID do Smart nao foi localizado. O vinculo anterior nao pode ser validado com seguranca.",
                    BuildSummary(snapshot.Nodes),
                    string.Empty);
            }

            await beforeSubmitAsync(smartDeviceId);
        }

        // Tela "Configurar Smart <modulo>" com EditText da URL.
        var edit = FindEditable(snapshot.Nodes);
        if (edit is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "input",
                "Nao foi possivel localizar o campo Digite a URL na tela de configuracao do Smart.",
                BuildSummary(snapshot.Nodes), string.Empty);
        }

        progress?.Invoke("input", "Informando automaticamente a URL de vinculo...");
        await _adb.TapAsync(serial, edit.CenterX, edit.CenterY, cancellationToken);
        await Task.Delay(250, cancellationToken);

        // O campo vem vazio no fluxo inicial. Limpar antes evita concatenacao ao reutilizar a tela.
        await _adb.KeyEventAsync(serial, "KEYCODE_MOVE_END", cancellationToken);
        var input = await _adb.InputTextAsync(serial, url, cancellationToken);
        if (!input.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "input",
                string.IsNullOrWhiteSpace(input.CombinedOutput)
                    ? "Nao foi possivel preencher a URL pelo ADB."
                    : input.CombinedOutput.Trim(),
                BuildSummary(snapshot.Nodes), string.Empty);
        }

        // Fecha apenas o teclado realmente visivel. Em alguns K2/Android 7 o ADB consegue
        // preencher o EditText sem abrir o IME; enviar BACK nesse estado fecha o Smart.
        await Task.Delay(350, cancellationToken);
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        await Task.Delay(350, cancellationToken);

        snapshot = await ReadUiAsync(serial, cancellationToken);
        if (!snapshot.Success)
        {
            return new SmartAutomationResult(false, packageName, "ui", snapshot.Error, string.Empty, string.Empty);
        }

        smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(snapshot.Nodes));

        progress?.Invoke("submit", "Confirmando a configuracao no Smart...");
        var submit = FindByLabels(snapshot.Nodes, SubmitLabels);
        if (submit is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "submit",
                "A URL foi preenchida, mas o botao Confirmar nao foi localizado.",
                BuildSummary(snapshot.Nodes), string.Empty);
        }

        await _adb.TapAsync(serial, submit.CenterX, submit.CenterY, cancellationToken);
        progress?.Invoke("sync", "Aguardando o Smart concluir a sincronizacao inicial...");

        UiSnapshot finalSnapshot = new(false, Array.Empty<UiNode>(), string.Empty);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1800 : 900, cancellationToken);
            finalSnapshot = await ReadUiAsync(serial, cancellationToken);
            if (finalSnapshot.Success &&
                (IsSynchronizationSuccess(finalSnapshot.Nodes) || IsSynchronizationFailure(finalSnapshot.Nodes)))
            {
                break;
            }
        }

        if (finalSnapshot.Success && IsSynchronizationSuccess(finalSnapshot.Nodes))
        {
            progress?.Invoke("sync-success", "Sincronizacao concluida. Fechando apenas a confirmacao final...");
            var ok = FindByLabels(finalSnapshot.Nodes, new[] { "ok!!!", "ok" });
            if (ok is not null)
            {
                await _adb.TapAsync(serial, ok.CenterX, ok.CenterY, cancellationToken);
                await Task.Delay(700, cancellationToken);
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "sync-complete",
                $"Modulo {GetModuleLabel(module)} configurado e dados sincronizados com sucesso.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        if (finalSnapshot.Success && IsSynchronizationFailure(finalSnapshot.Nodes))
        {
            progress?.Invoke("sync-restart", "Falha de sincronizacao detectada no Android. A configuracao ja foi salva; fechando e abrindo o Smart novamente...");
            await _adb.ForceStopPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(600, cancellationToken);
            var relaunch = await _adb.LaunchPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(1800, cancellationToken);

            if (!relaunch.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "sync-restart",
                    "A configuracao foi enviada, mas o Smart apresentou falha de sincronizacao e nao foi possivel reabrir o aplicativo.",
                    BuildSummary(finalSnapshot.Nodes),
                    smartDeviceId);
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "sync-restarted",
                "A configuracao foi salva. O Smart apresentou falha de sincronizacao complementar no Android e foi reiniciado automaticamente.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        if (finalSnapshot.Success && IsSynchronizationInProgress(finalSnapshot.Nodes))
        {
            progress?.Invoke("sync-timeout-restart", "A sincronizacao permaneceu em andamento alem do tempo esperado. A configuracao ja foi enviada; reiniciando o Smart para liberar a tela e continuar a validacao do vinculo...");
            await _adb.ForceStopPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(700, cancellationToken);
            var relaunch = await _adb.LaunchPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(1800, cancellationToken);

            if (!relaunch.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "sync-timeout-restart",
                    "A configuracao foi enviada, mas a sincronizacao ficou presa e nao foi possivel reabrir o Smart automaticamente.",
                    BuildSummary(finalSnapshot.Nodes),
                    smartDeviceId);
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "sync-timeout-restarted",
                "A configuracao foi enviada. A sincronizacao permaneceu presa alem do tempo esperado e o Smart foi reiniciado automaticamente para continuar a validacao do vinculo.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        return new SmartAutomationResult(
            true,
            packageName,
            "submitted",
            $"Modulo {GetModuleLabel(module)} selecionado, URL informada e confirmacao acionada no Smart.",
            finalSnapshot.Success ? BuildSummary(finalSnapshot.Nodes) : string.Empty,
            smartDeviceId);
    }

    private async Task<SmartAutomationResult> SubmitDeviceUrlSmart81K2OnboardingAsync(
        string serial,
        string url,
        string module,
        string packageName,
        string? confirmedSmartDeviceId,
        Action<string, string>? progress,
        Func<string, Task>? beforeSubmitAsync,
        CancellationToken cancellationToken)
    {
        progress?.Invoke(
            "smart81-k2",
            "Smart 8.1+ no K2/Android 7 detectado. Usando a rota propria Bem-vindo -> Modulo -> URL, sem UIAutomator.");

        var display = await GetAndroidDisplaySizeAsync(serial, cancellationToken);
        if (!display.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-k2-display",
                display.Message,
                string.Empty,
                string.Empty);
        }

        var activity = await WaitForForegroundActivityAsync(
            serial,
            current => IsSmart81AuthActivity(current, packageName),
            20,
            cancellationToken);
        if (!IsSmart81AuthActivity(activity, packageName))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-k2-launch",
                string.IsNullOrWhiteSpace(activity)
                    ? "O Smart 8.1+ foi aberto, mas a AuthActivity nao estabilizou no K2."
                    : $"O Smart 8.1+ foi aberto, mas outra tela ficou em primeiro plano: {activity}.",
                activity,
                string.Empty);
        }

        var scaler = new ReferenceScaler(display.Width, display.Height, 1080, 1920);
        var (startX, startY) = scaler.Scale(540, 763);
        progress?.Invoke("smart81-k2-start", "Abrindo a configuracao inicial propria do Smart 8.1+...");
        var start = await _adb.TapAsync(serial, startX, startY, cancellationToken);
        if (!start.Success)
        {
            return BuildSmart81K2AdbFailure(packageName, "smart81-k2-start", "Iniciar Configuracao", start, activity);
        }

        await Task.Delay(900, cancellationToken);
        activity = await GetForegroundActivityAsync(serial, cancellationToken);
        if (!IsSmart81AuthActivity(activity, packageName))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-k2-module",
                $"O Smart 8.1+ deixou a AuthActivity antes da selecao do modulo. Activity atual: {activity}.",
                activity,
                string.Empty);
        }

        var moduleReferenceY = GetSmart81K2ModuleReferenceY(module);
        if (moduleReferenceY <= 0)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-k2-module",
                $"O modulo '{module}' ainda nao possui ponto mapeado na selecao do Smart 8.1+ do K2.",
                activity,
                string.Empty);
        }

        var (moduleX, moduleY) = scaler.Scale(1014, moduleReferenceY);
        progress?.Invoke("smart81-k2-module", $"Selecionando {GetModuleLabel(module)} na tela propria do Smart 8.1+...");
        var selectModule = await _adb.TapAsync(serial, moduleX, moduleY, cancellationToken);
        if (!selectModule.Success)
        {
            return BuildSmart81K2AdbFailure(packageName, "smart81-k2-module", GetModuleLabel(module), selectModule, activity);
        }

        await Task.Delay(450, cancellationToken);
        var (advanceX, advanceY) = scaler.Scale(540, 1808);
        progress?.Invoke("smart81-k2-advance", "Avancando para a tela de URL do Smart 8.1+...");
        var advance = await _adb.TapAsync(serial, advanceX, advanceY, cancellationToken);
        if (!advance.Success)
        {
            return BuildSmart81K2AdbFailure(packageName, "smart81-k2-advance", "Avancar", advance, activity);
        }

        await Task.Delay(1000, cancellationToken);
        activity = await GetForegroundActivityAsync(serial, cancellationToken);
        if (!IsSmart81AuthActivity(activity, packageName))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-k2-url",
                $"O Smart 8.1+ nao permaneceu na AuthActivity ao abrir a configuracao de {GetModuleLabel(module)}. Activity atual: {activity}.",
                activity,
                string.Empty);
        }

        // No Android 7 deste K2, o Device ID exibido pelo Smart coincide com o
        // ANDROID_ID do aparelho. Esse fallback ja e usado e validado no fluxo 8.0
        // apenas para SDK <= 25; nao e aplicado a Androids modernos.
        var smartDeviceId = FirstNonEmpty(
            confirmedSmartDeviceId?.Trim() ?? string.Empty,
            await GetAndroidIdFallbackAsync(serial, cancellationToken));
        if (beforeSubmitAsync is not null)
        {
            if (string.IsNullOrWhiteSpace(smartDeviceId))
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "smart81-k2-device-id",
                    "A tela de configuracao do Smart 8.1+ abriu, mas o Device ID do K2 nao pode ser obtido com seguranca.",
                    activity,
                    string.Empty);
            }

            await beforeSubmitAsync(smartDeviceId);
        }

        var (urlX, urlY) = scaler.Scale(540, 640);
        progress?.Invoke("smart81-k2-url", "Informando a URL na tela propria do Smart 8.1+...");
        var focusUrl = await _adb.TapAsync(serial, urlX, urlY, cancellationToken);
        if (!focusUrl.Success)
        {
            return BuildSmart81K2AdbFailure(packageName, "smart81-k2-url", "campo Digite a URL", focusUrl, activity, smartDeviceId);
        }

        await Task.Delay(250, cancellationToken);
        var clear = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clear.Success)
        {
            return BuildSmart81K2AdbFailure(packageName, "smart81-k2-url", "limpeza do campo URL", clear, activity, smartDeviceId);
        }

        var input = await _adb.InputTextAsync(serial, url, cancellationToken);
        if (!input.Success)
        {
            return BuildSmart81K2AdbFailure(packageName, "smart81-k2-url", "preenchimento da URL", input, activity, smartDeviceId);
        }

        await Task.Delay(350, cancellationToken);
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        await Task.Delay(450, cancellationToken);

        activity = await GetForegroundActivityAsync(serial, cancellationToken);
        if (!IsSmart81AuthActivity(activity, packageName))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-k2-confirm",
                $"A URL foi informada, mas o Smart 8.1+ deixou a AuthActivity antes da confirmacao. Activity atual: {activity}.",
                activity,
                smartDeviceId);
        }

        var (confirmX, confirmY) = scaler.Scale(540, 1808);
        progress?.Invoke("smart81-k2-confirm", "Confirmando a URL no fluxo inicial do Smart 8.1+...");
        var confirm = await _adb.TapAsync(serial, confirmX, confirmY, cancellationToken);
        if (!confirm.Success)
        {
            return BuildSmart81K2AdbFailure(packageName, "smart81-k2-confirm", "Confirmar", confirm, activity, smartDeviceId);
        }

        // Nao consulta UIAutomator durante sincronizacao no K2. O chamador valida o
        // vinculo pelo SelfHost/Softcomshop e somente depois fecha o OK por uma unica
        // acao controlada em DismissConfirmedSynchronizationAsync.
        await Task.Delay(1800, cancellationToken);
        progress?.Invoke(
            "smart81-k2-sync-pending",
            "URL confirmada no Smart 8.1+ do K2. Aguardando a confirmacao remota do Device ID, sem ler a arvore Android.");
        return new SmartAutomationResult(
            true,
            packageName,
            "smart81-k2-sync-pending",
            "A configuracao do Smart 8.1+ foi enviada; o vinculo sera confirmado remotamente antes de finalizar.",
            activity,
            smartDeviceId);
    }

    private static SmartAutomationResult BuildSmart81K2AdbFailure(
        string packageName,
        string stage,
        string operation,
        ProcessResult result,
        string activity,
        string smartDeviceId = "") =>
        new(
            false,
            packageName,
            stage,
            string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? $"O ADB nao conseguiu executar: {operation}."
                : result.CombinedOutput.Trim(),
            activity,
            smartDeviceId);

    private async Task<SmartAutomationResult> SubmitDeviceUrlLegacy80Async(
        string serial,
        string url,
        string module,
        string packageName,
        UiSnapshot snapshot,
        string smartDeviceId,
        Action<string, string>? progress,
        Func<string, Task>? beforeSubmitAsync,
        CancellationToken cancellationToken,
        string? provisioningProfile)
    {
        var moduleLabel = GetModuleLabel(module);
        progress?.Invoke(
            "legacy80",
            $"Smart 8.0 detectado. Usando o fluxo mapeado: Nova Empresa -> {moduleLabel} -> Confirmar -> DIGITAR -> Host -> Confirmar 5s -> Confirmar final.");

        var currentActivity = await WaitForLegacyActivityAsync(
            serial,
            IsLegacyCompanyAddConfigActivity,
            12,
            cancellationToken);

        if (!IsLegacyCompanyAddConfigActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-module-screen",
                string.IsNullOrWhiteSpace(currentActivity)
                    ? "A tela Nova Empresa foi aberta, mas a EmpresaAddConfigActivity nao permaneceu em primeiro plano."
                    : $"A tela Nova Empresa foi aberta, mas a Activity atual e {currentActivity}.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                smartDeviceId);
        }

        // Totem e AutoPagamento usam o perfil de tela grande ja validado no K2_MINI.
        if (IsLegacy80LargeSelfServiceModule(module))
        {
            return await SubmitDeviceUrlLegacy80LargeSelfServiceAsync(
                serial,
                url,
                module,
                packageName,
                smartDeviceId,
                progress,
                beforeSubmitAsync,
                cancellationToken);
        }

        // Demais modulos usam o perfil celular/GPOS. As telas reais enviadas pelo
        // usuario mostram que o layout e diferente do Totem ja na LoginActivity.
        // Para evitar a mesma regressao causada por uiautomator dump no Smart 8.0,
        // o caminho movel tambem usa Activity + pontos proporcionais nas etapas criticas.
        return await SubmitDeviceUrlLegacy80MobileAsync(
            serial,
            url,
            module,
            packageName,
            smartDeviceId,
            progress,
            beforeSubmitAsync,
            cancellationToken,
            provisioningProfile);
    }

    private async Task<SmartAutomationResult> SubmitDeviceUrlLegacy80MobileAsync(
        string serial,
        string url,
        string module,
        string packageName,
        string smartDeviceId,
        Action<string, string>? progress,
        Func<string, Task>? beforeSubmitAsync,
        CancellationToken cancellationToken,
        string? provisioningProfile)
    {
        var preserveKeyboardForN950 = ShouldPreserveLegacy80Keyboard(provisioningProfile);
        var moduleLabel = GetModuleLabel(module);
        var display = await GetAndroidDisplaySizeAsync(serial, cancellationToken);
        if (!display.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-display",
                "A tela de selecao do modulo foi aberta, mas nao foi possivel consultar a resolucao do Android.",
                display.Message,
                smartDeviceId);
        }

        var currentActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddConfigActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-module-screen",
                $"A selecao do modulo nao esta mais na EmpresaAddConfigActivity. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        var sdkLevel = await GetAndroidSdkLevelAsync(serial, cancellationToken);

        // Referencia real do celular/GPOS: 304x674.
        // A lista de modulos aparece sempre na mesma ordem no Smart 8.0.x.
        var moduleX = Math.Clamp((int)Math.Round(display.Width * (268d / 304d)), 1, display.Width - 1);
        var moduleY = Math.Clamp((int)Math.Round(display.Height * (GetLegacy80MobileModuleReferenceY(module) / 674d)), 1, display.Height - 1);
        var firstConfirmX = Math.Clamp((int)Math.Round(display.Width * (223d / 304d)), 1, display.Width - 1);
        var firstConfirmY = Math.Clamp((int)Math.Round(display.Height * (636d / 674d)), 1, display.Height - 1);
        var moduleSource = "ponto proporcional mapeado";
        var firstConfirmSource = "ponto proporcional mapeado";

        if (sdkLevel > 25)
        {
            var moduleResourceId = GetLegacy80ModuleResourceId(module);
            if (!string.IsNullOrWhiteSpace(moduleResourceId))
            {
                var moduleBounds = await FindViewBoundsFromActivityDumpAsync(
                    serial,
                    moduleResourceId,
                    cancellationToken);
                if (IsActivityPointInsideDisplay(moduleBounds, display))
                {
                    moduleX = moduleBounds.CenterX;
                    moduleY = moduleBounds.CenterY;
                    moduleSource = $"dumpsys activity top ({moduleResourceId})";
                }
            }

            var firstConfirmBounds = await FindViewBoundsFromActivityDumpAsync(
                serial,
                "app:id/btn_confirmar",
                cancellationToken);
            if (IsActivityPointInsideDisplay(firstConfirmBounds, display))
            {
                firstConfirmX = firstConfirmBounds.CenterX;
                firstConfirmY = firstConfirmBounds.CenterY;
                firstConfirmSource = "dumpsys activity top (app:id/btn_confirmar)";
            }
        }

        if (string.Equals(module, "smart_pdv", StringComparison.OrdinalIgnoreCase))
        {
            // Nova Empresa abre com Smart PDV ja selecionado. Tocar novamente no switch
            // poderia desmarcar o modulo, entao apenas preservamos a selecao padrao.
            progress?.Invoke(
                "legacy80-mobile-module",
                "Smart PDV ja vem selecionado ao abrir Nova Empresa. Mantendo a selecao atual sem tocar no switch.");
        }
        else
        {
            progress?.Invoke(
                "legacy80-mobile-module",
                $"Perfil celular/GPOS: selecionando {moduleLabel} em {moduleX},{moduleY} via {moduleSource}...");

            var moduleTap = await _adb.TapAsync(serial, moduleX, moduleY, cancellationToken);
            if (!moduleTap.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy80-mobile-module",
                    $"Nao foi possivel selecionar {moduleLabel} no Smart 8.0.",
                    moduleTap.CombinedOutput.Trim(),
                    smartDeviceId);
            }

            await Task.Delay(450, cancellationToken);
            currentActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
            if (!IsLegacyCompanyAddConfigActivity(currentActivity))
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy80-mobile-module",
                    $"O toque de selecao do modulo foi executado, mas a EmpresaAddConfigActivity deixou o primeiro plano antes de Confirmar. Activity atual: {currentActivity}.",
                    currentActivity,
                    smartDeviceId);
            }
        }

        progress?.Invoke(
            "legacy80-mobile-module-confirm",
            $"{moduleLabel} selecionado. Acionando Confirmar uma unica vez em {firstConfirmX},{firstConfirmY} via {firstConfirmSource}...");

        var firstConfirmTap = await _adb.TapAsync(serial, firstConfirmX, firstConfirmY, cancellationToken);
        if (!firstConfirmTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-module-confirm",
                "Nao foi possivel acionar o Confirmar da selecao do modulo.",
                firstConfirmTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        // Android moderno reporta a Activity de primeiro plano de forma mais confiavel
        // pelo WindowManager. No Android 7 mantemos o caminho legado porque o launcher
        // pode aparecer temporariamente durante as transicoes.
        var deviceActivity = sdkLevel > 25
            ? await WaitForForegroundActivityAsync(serial, IsLegacyCompanyAddActivity, 30, cancellationToken)
            : await WaitForLegacyActivityAsync(serial, IsLegacyCompanyAddActivity, 30, cancellationToken);

        if (!IsLegacyCompanyAddActivity(deviceActivity) && sdkLevel > 25)
        {
            // Fallback adicional apenas para diagnostico/compatibilidade: se o WindowManager
            // nao refletir a transicao, consulta o ActivityManager antes de desistir.
            deviceActivity = await WaitForLegacyActivityAsync(
                serial,
                IsLegacyCompanyAddActivity,
                8,
                cancellationToken);
        }

        if (!IsLegacyCompanyAddActivity(deviceActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-device-screen",
                string.IsNullOrWhiteSpace(deviceActivity)
                    ? "O modulo foi confirmado, mas a EmpresaAddActivity nao apareceu. O botao DIGITAR nao sera acionado ate essa tela ser confirmada."
                    : $"O modulo foi confirmado, mas a EmpresaAddActivity nao foi confirmada. Activity atual: {deviceActivity}. O botao DIGITAR nao sera acionado ate essa tela ser confirmada.",
                deviceActivity,
                smartDeviceId);
        }

        progress?.Invoke(
            "legacy80-mobile-device-screen",
            $"EmpresaAddActivity confirmada em primeiro plano ({deviceActivity}). Iniciando agora a etapa obrigatoria DIGITAR -> Host...");

        var beforeSubmitCompleted = false;
        if (sdkLevel > 25)
        {
            progress?.Invoke(
                "legacy80-mobile-device-id",
                "Lendo o Device ID exibido pelo proprio Smart antes de alterar ou confirmar o Host...");
            smartDeviceId = await ResolveSmartDeviceIdFromCurrentScreenAsync(
                serial,
                packageName,
                smartDeviceId,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(smartDeviceId))
            {
                progress?.Invoke(
                    "legacy80-mobile-device-id",
                    $"Device ID real do Smart localizado antes do envio: {smartDeviceId}.");
                if (beforeSubmitAsync is not null)
                {
                    await beforeSubmitAsync(smartDeviceId);
                    beforeSubmitCompleted = true;
                }
            }
            else
            {
                progress?.Invoke(
                    "legacy80-mobile-device-id",
                    "O Device ID ainda nao ficou legivel na tela inicial de configuracao; uma ultima leitura sera feita antes de alterar o Host.");
            }
        }

        await Task.Delay(450, cancellationToken);

        // Mapeamento real do Smart 8.0 em celular/GPOS (incluindo Android 17):
        // a EmpresaAddActivity permanece a mesma antes e depois de DIGITAR, e o
        // UIAutomator nao fornece um estado confiavel para confirmar essa transicao.
        // O sinal confiavel aparece somente depois de tocar no Host: o IME passa de
        // oculto para visivel. Portanto esta etapa NAO usa UIAutomator em nenhuma
        // versao do Android. Executamos DIGITAR -> Host por pontos proporcionais e
        // so seguimos quando o teclado realmente estiver visivel.
        // Referencia real da tela Configuracao em celular/GPOS: 304x678.
        // Centro medido do botao DIGITAR na captura real 304x678: ~225,115.
        var typeX = Math.Clamp((int)Math.Round(display.Width * (225d / 304d)), 1, display.Width - 1);
        var typeY = Math.Clamp((int)Math.Round(display.Height * (115d / 678d)), 1, display.Height - 1);
        var hostX = Math.Clamp((int)Math.Round(display.Width * (152d / 304d)), 1, display.Width - 1);
        var hostY = Math.Clamp((int)Math.Round(display.Height * (274d / 678d)), 1, display.Height - 1);
        var confirmX = Math.Clamp((int)Math.Round(display.Width * (224d / 304d)), 1, display.Width - 1);
        var confirmY = Math.Clamp((int)Math.Round(display.Height * (318d / 678d)), 1, display.Height - 1);
        var typeSource = "ponto proporcional mapeado";
        var hostSource = "ponto proporcional mapeado";
        var confirmSource = "ponto proporcional mapeado";

        if (sdkLevel > 25)
        {
            var typeBounds = await FindViewBoundsFromActivityDumpAsync(
                serial,
                "app:id/btn_digitar",
                cancellationToken);
            if (IsActivityPointInsideDisplay(typeBounds, display))
            {
                typeX = typeBounds.CenterX;
                typeY = typeBounds.CenterY;
                typeSource = "dumpsys activity top (app:id/btn_digitar)";
            }
        }

        progress?.Invoke(
            "legacy80-mobile-type",
            $"EmpresaAddActivity aberta. Acionando DIGITAR em {typeX},{typeY} via {typeSource} antes de acessar o Host...");
        var typeTap = await _adb.TapAsync(serial, typeX, typeY, cancellationToken);
        if (!typeTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-type",
                "Nao foi possivel acionar DIGITAR no Smart 8.0.",
                typeTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(650, cancellationToken);
        currentActivity = sdkLevel > 25
            ? await GetForegroundActivityAsync(serial, cancellationToken)
            : await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-type",
                $"O toque em DIGITAR foi enviado, mas a EmpresaAddActivity deixou o primeiro plano. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        if (sdkLevel > 25)
        {
            var hostBounds = await FindViewBoundsFromActivityDumpAsync(
                serial,
                "app:id/text_host",
                cancellationToken);
            if (IsActivityPointInsideDisplay(hostBounds, display))
            {
                hostX = hostBounds.CenterX;
                hostY = hostBounds.CenterY;
                hostSource = "dumpsys activity top (app:id/text_host)";
            }
        }

        progress?.Invoke(
            "legacy80-mobile-host-focus",
            $"DIGITAR acionado. Selecionando o campo Host em {hostX},{hostY} via {hostSource} e validando o modo de edicao antes de alterar o conteudo...");

        var hostTap = await _adb.TapAsync(serial, hostX, hostY, cancellationToken);
        if (!hostTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host-focus",
                "Nao foi possivel selecionar o campo Host depois de acionar DIGITAR.",
                hostTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(400, cancellationToken);
        var editingActive = await _adb.IsSoftKeyboardVisibleAsync(serial, cancellationToken);
        if (!editingActive)
        {
            progress?.Invoke(
                "legacy80-mobile-type-retry",
                "O Host ainda nao entrou em edicao. Repetindo DIGITAR e Host uma unica vez antes de continuar...");

            currentActivity = sdkLevel > 25
                ? await GetForegroundActivityAsync(serial, cancellationToken)
                : await GetLegacySmartActivityAsync(serial, cancellationToken);
            if (!IsLegacyCompanyAddActivity(currentActivity))
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy80-mobile-type-retry",
                    $"Antes da segunda tentativa de DIGITAR, a EmpresaAddActivity deixou o primeiro plano. Activity atual: {currentActivity}.",
                    currentActivity,
                    smartDeviceId);
            }

            var retryTypeTap = await _adb.TapAsync(serial, typeX, typeY, cancellationToken);
            if (!retryTypeTap.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy80-mobile-type-retry",
                    "Nao foi possivel repetir o toque em DIGITAR.",
                    retryTypeTap.CombinedOutput.Trim(),
                    smartDeviceId);
            }

            await Task.Delay(650, cancellationToken);
            var retryHostTap = await _adb.TapAsync(serial, hostX, hostY, cancellationToken);
            if (!retryHostTap.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy80-mobile-host-focus",
                    "DIGITAR foi repetido, mas nao foi possivel selecionar o Host.",
                    retryHostTap.CombinedOutput.Trim(),
                    smartDeviceId);
            }

            await Task.Delay(400, cancellationToken);
            editingActive = await _adb.IsSoftKeyboardVisibleAsync(serial, cancellationToken);
            if (!editingActive)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy80-mobile-host-focus",
                    "O Smart permaneceu na tela de configuracao, mas DIGITAR nao ativou a edicao do Host apos duas tentativas. Nenhum conteudo foi apagado e o fluxo foi interrompido nesta etapa.",
                    currentActivity,
                    smartDeviceId);
            }
        }

        if (sdkLevel <= 25)
        {
            smartDeviceId = FirstNonEmpty(smartDeviceId, await GetAndroidIdFallbackAsync(serial, cancellationToken));
        }
        if (beforeSubmitAsync is not null && !beforeSubmitCompleted)
        {
            smartDeviceId = await ResolveSmartDeviceIdFromCurrentScreenAsync(
                serial,
                packageName,
                smartDeviceId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(smartDeviceId))
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "legacy80-mobile-device-id",
                    "O Device ID real nao ficou legivel antes do envio. O Host nao foi alterado e o procedimento nao sera repetido automaticamente.",
                    currentActivity,
                    string.Empty);
            }

            progress?.Invoke(
                "legacy80-mobile-device-id",
                $"Device ID real confirmado antes do envio: {smartDeviceId}. Procurando o vinculo em todos os dispositivos...");
            await beforeSubmitAsync(smartDeviceId);
            beforeSubmitCompleted = true;
        }

        if (sdkLevel > 25)
        {
            var confirmBounds = await FindViewBoundsFromActivityDumpAsync(
                serial,
                "app:id/btn_confirmar",
                cancellationToken);
            if (IsActivityPointInsideDisplay(confirmBounds, display))
            {
                confirmX = confirmBounds.CenterX;
                confirmY = confirmBounds.CenterY;
                confirmSource = "dumpsys activity top (app:id/btn_confirmar)";
            }
        }

        progress?.Invoke(
            "legacy80-mobile-host",
            "Host em modo de edicao confirmado. Selecionando tudo, apagando o conteudo atual e informando a URL de vinculo...");
        var clear = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clear.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host",
                "Nao foi possivel selecionar tudo e limpar o Host atual.",
                clear.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var clearRemainder = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clearRemainder.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host",
                "A primeira limpeza do Host foi executada, mas a segunda passagem de seguranca falhou.",
                clearRemainder.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var input = await _adb.InputTextAsync(serial, url, cancellationToken);
        if (!input.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host",
                string.IsNullOrWhiteSpace(input.CombinedOutput)
                    ? "Nao foi possivel informar a URL no campo Host."
                    : input.CombinedOutput.Trim(),
                currentActivity,
                smartDeviceId);
        }

        // O fluxo validado do Mercado Pago N950 mantem o teclado aberto. Enviar BACK
        // aqui pode ser interpretado pela Activity como navegacao e voltar para a
        // selecao de modulo enquanto o texto ainda esta sendo renderizado na tela.
        // Nos demais perfis preservamos o fechamento explicito que ja funciona.
        if (!preserveKeyboardForN950 &&
            await _adb.IsSoftKeyboardVisibleAsync(serial, cancellationToken))
        {
            progress?.Invoke(
                "legacy80-mobile-keyboard",
                "URL informada. Fechando somente o teclado deste Android antes do toque prolongado...");
            await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        }
        await Task.Delay(350, cancellationToken);

        currentActivity = sdkLevel > 25
            ? await GetForegroundActivityAsync(serial, cancellationToken)
            : await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-hold-confirm",
                $"A URL foi informada, mas a EmpresaAddActivity deixou o primeiro plano antes do Confirmar. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        // Quando fechamos o teclado, rele o botao no layout ja redimensionado. No
        // N950 o teclado permanece como no fluxo anteriormente validado, portanto
        // mantemos a coordenada obtida nesse mesmo estado antes da digitacao.
        if (sdkLevel > 25 && !preserveKeyboardForN950)
        {
            var currentConfirmBounds = await FindViewBoundsFromActivityDumpAsync(
                serial,
                "app:id/btn_confirmar",
                cancellationToken);
            if (IsActivityPointInsideDisplay(currentConfirmBounds, display))
            {
                confirmX = currentConfirmBounds.CenterX;
                confirmY = currentConfirmBounds.CenterY;
                confirmSource = "dumpsys activity top atualizado (app:id/btn_confirmar)";
            }
        }

        progress?.Invoke(
            "legacy80-mobile-hold-confirm",
            $"Mantendo Confirmar pressionado por 5 segundos em {confirmX},{confirmY} via {confirmSource}...");
        var hold = await _adb.LongPressAsync(serial, confirmX, confirmY, 5000, cancellationToken);
        if (!hold.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-hold-confirm",
                "O ADB nao conseguiu executar o toque prolongado de 5 segundos em Confirmar.",
                hold.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(1400, cancellationToken);
        currentActivity = sdkLevel > 25
            ? await GetForegroundActivityAsync(serial, cancellationToken)
            : await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (IsLegacyCompanyAddConfigActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-hold-confirm",
                "O Android tratou o toque prolongado como clique comum e voltou para a selecao de modulo. O Provisioner nao acionou outro Confirmar nem abriu uma configuracao vazia.",
                currentActivity,
                smartDeviceId);
        }

        if (!IsLegacyCompanyAddActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-review",
                $"O toque prolongado foi executado, mas o Smart nao permaneceu na configuracao nem retornou para a confirmacao do modulo. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        if (sdkLevel > 25)
        {
            var finalConfirmBounds = await FindViewBoundsFromActivityDumpAsync(
                serial,
                "app:id/btn_confirmar",
                cancellationToken);
            if (IsActivityPointInsideDisplay(finalConfirmBounds, display))
            {
                confirmX = finalConfirmBounds.CenterX;
                confirmY = finalConfirmBounds.CenterY;
                confirmSource = "dumpsys activity top atualizado (app:id/btn_confirmar)";
            }
        }

        progress?.Invoke(
            "legacy80-mobile-final-confirm",
            $"Toque prolongado concluido. Acionando o Confirmar final uma unica vez em {confirmX},{confirmY} via {confirmSource}...");
        var finalTap = await _adb.TapAsync(serial, confirmX, confirmY, cancellationToken);
        if (!finalTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-final-confirm",
                "Nao foi possivel acionar o Confirmar final do Smart 8.0.",
                finalTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        return await CompleteLegacy80SynchronizationAsync(
            serial,
            packageName,
            module,
            smartDeviceId,
            progress,
            cancellationToken);
    }

    private async Task<SmartAutomationResult> SubmitDeviceUrlLegacy80MobileModernFinalAsync(
        string serial,
        string url,
        string module,
        string packageName,
        string smartDeviceId,
        Action<string, string>? progress,
        Func<string, Task>? beforeSubmitAsync,
        CancellationToken cancellationToken)
    {
        // 1) Nesta tela DIGITAR precisa ser uma etapa real, nao apenas um toque enviado.
        var snapshot = await WaitForUiStateAsync(
            serial,
            nodes => IsUiFromPackage(nodes, packageName) &&
                     FindByLabelsIncludingText(nodes, new[] { "digitar", "digitar dados manualmente" }) is not null &&
                     ContainsLabel(nodes, "host"),
            12,
            cancellationToken);

        var typeNode = snapshot.Success
            ? FindByLabelsIncludingText(snapshot.Nodes, new[] { "digitar", "digitar dados manualmente" })
            : null;
        if (typeNode is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-type",
                "A tela final de configuracao abriu, mas o botao DIGITAR nao foi localizado. O Host nao sera alterado.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                smartDeviceId);
        }

        progress?.Invoke(
            "legacy80-mobile-type",
            $"Botao DIGITAR localizado pela interface em {typeNode.CenterX},{typeNode.CenterY}. Acionando antes de acessar o Host...");

        var typeTap = await _adb.TapAsync(serial, typeNode.CenterX, typeNode.CenterY, cancellationToken);
        if (!typeTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-type",
                "Nao foi possivel acionar DIGITAR no Smart 8.0.",
                typeTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        // 2) So avanca quando o Host realmente virar um campo editavel.
        var editSnapshot = await WaitForUiStateAsync(
            serial,
            nodes => IsUiFromPackage(nodes, packageName) && FindSmart81HostEditable(nodes) is not null,
            10,
            cancellationToken);

        var hostEdit = editSnapshot.Success ? FindSmart81HostEditable(editSnapshot.Nodes) : null;
        if (hostEdit is null)
        {
            // Uma unica segunda tentativa, novamente pelo botao real da tela.
            var retrySnapshot = await ReadUiQuickAsync(serial, cancellationToken);
            var retryType = retrySnapshot.Success
                ? FindByLabelsIncludingText(retrySnapshot.Nodes, new[] { "digitar", "digitar dados manualmente" })
                : null;

            if (retryType is not null)
            {
                progress?.Invoke(
                    "legacy80-mobile-type-retry",
                    "DIGITAR foi acionado, mas o Host ainda nao ficou editavel. Repetindo o clique em DIGITAR uma unica vez...");
                await _adb.TapAsync(serial, retryType.CenterX, retryType.CenterY, cancellationToken);
                editSnapshot = await WaitForUiStateAsync(
                    serial,
                    nodes => IsUiFromPackage(nodes, packageName) && FindSmart81HostEditable(nodes) is not null,
                    8,
                    cancellationToken);
                hostEdit = editSnapshot.Success ? FindSmart81HostEditable(editSnapshot.Nodes) : null;
            }
        }

        if (hostEdit is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-type",
                "DIGITAR nao ativou o modo de edicao: o campo Host nao apareceu como editavel. A automacao foi interrompida antes de apagar ou informar a URL.",
                editSnapshot.Success ? BuildSummary(editSnapshot.Nodes) : editSnapshot.Error,
                smartDeviceId);
        }

        progress?.Invoke(
            "legacy80-mobile-host-focus",
            $"DIGITAR confirmado. Host editavel localizado em {hostEdit.CenterX},{hostEdit.CenterY}. Selecionando o campo...");

        var hostTap = await _adb.TapAsync(serial, hostEdit.CenterX, hostEdit.CenterY, cancellationToken);
        if (!hostTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host-focus",
                "DIGITAR foi confirmado, mas nao foi possivel focar o campo Host.",
                hostTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(350, cancellationToken);

        smartDeviceId = await ResolveSmartDeviceIdFromCurrentScreenAsync(
            serial, packageName, smartDeviceId, cancellationToken);

        if (beforeSubmitAsync is not null)
        {
            if (string.IsNullOrWhiteSpace(smartDeviceId))
            {
                progress?.Invoke(
                    "legacy80-mobile-device-id",
                    "O Smart 8.0 nao expos o Device ID antes do envio. A confirmacao remota do cadastro selecionado sera usada ao final.");
            }
            else
            {
                await beforeSubmitAsync(smartDeviceId);
            }
        }

        progress?.Invoke(
            "legacy80-mobile-host",
            "Host em modo de edicao confirmado. Selecionando tudo, apagando o valor atual e informando a URL de vinculo...");

        var clear = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clear.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host",
                "Nao foi possivel selecionar tudo e limpar o Host atual.",
                clear.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var clearRemainder = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clearRemainder.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host",
                "A primeira limpeza do Host foi executada, mas a segunda passagem de seguranca falhou.",
                clearRemainder.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var input = await _adb.InputTextAsync(serial, url, cancellationToken);
        if (!input.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-host",
                string.IsNullOrWhiteSpace(input.CombinedOutput)
                    ? "Nao foi possivel informar a URL no campo Host."
                    : input.CombinedOutput.Trim(),
                BuildSummary(editSnapshot.Nodes),
                smartDeviceId);
        }

        await Task.Delay(300, cancellationToken);
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);

        // 3) O primeiro Confirmar tambem e localizado pela tela real.
        var confirmSnapshot = await WaitForUiStateAsync(
            serial,
            nodes => IsUiFromPackage(nodes, packageName) &&
                     FindByLabelsIncludingText(nodes, new[] { "confirmar" }) is not null,
            10,
            cancellationToken);
        var holdConfirm = confirmSnapshot.Success
            ? FindByLabelsIncludingText(confirmSnapshot.Nodes, new[] { "confirmar" })
            : null;
        if (holdConfirm is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-hold-confirm",
                "A URL foi informada, mas o botao Confirmar nao foi localizado para o toque prolongado.",
                confirmSnapshot.Success ? BuildSummary(confirmSnapshot.Nodes) : confirmSnapshot.Error,
                smartDeviceId);
        }

        progress?.Invoke(
            "legacy80-mobile-hold-confirm",
            $"URL informada. Mantendo Confirmar pressionado por 5 segundos em {holdConfirm.CenterX},{holdConfirm.CenterY}...");
        var hold = await _adb.LongPressAsync(
            serial,
            holdConfirm.CenterX,
            holdConfirm.CenterY,
            5000,
            cancellationToken);
        if (!hold.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-hold-confirm",
                "O ADB nao conseguiu executar o toque prolongado de 5 segundos em Confirmar.",
                hold.CombinedOutput.Trim(),
                smartDeviceId);
        }

        // 4) So executa o segundo Confirmar depois que a revisao realmente aparecer.
        var reviewSnapshot = await WaitForUiStateAsync(
            serial,
            nodes => IsUiFromPackage(nodes, packageName) && IsLegacy80ConfirmationDetails(nodes),
            12,
            cancellationToken);
        var finalConfirm = reviewSnapshot.Success
            ? FindByLabelsIncludingText(reviewSnapshot.Nodes, new[] { "confirmar" })
            : null;
        if (finalConfirm is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-review",
                "O Confirmar foi mantido pressionado, mas a tela de revisao com o Confirmar final nao foi localizada.",
                reviewSnapshot.Success ? BuildSummary(reviewSnapshot.Nodes) : reviewSnapshot.Error,
                smartDeviceId);
        }

        progress?.Invoke(
            "legacy80-mobile-final-confirm",
            "Tela de revisao confirmada. Acionando o Confirmar final uma unica vez...");
        var finalTap = await _adb.TapAsync(serial, finalConfirm.CenterX, finalConfirm.CenterY, cancellationToken);
        if (!finalTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-mobile-final-confirm",
                "Nao foi possivel acionar o Confirmar final do Smart 8.0.",
                finalTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        return await CompleteLegacy80SynchronizationAsync(
            serial,
            packageName,
            module,
            smartDeviceId,
            progress,
            cancellationToken);
    }

    private async Task<SmartAutomationResult> SubmitDeviceUrlLegacy80LargeSelfServiceAsync(
        string serial,
        string url,
        string module,
        string packageName,
        string smartDeviceId,
        Action<string, string>? progress,
        Func<string, Task>? beforeSubmitAsync,
        CancellationToken cancellationToken)
    {
        var moduleLabel = GetModuleLabel(module);
        var sdkLevel = await GetAndroidSdkLevelAsync(serial, cancellationToken);
        var display = await GetAndroidDisplaySizeAsync(serial, cancellationToken);
        if (!display.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-display",
                "A tela de selecao do modulo foi aberta, mas nao foi possivel consultar a resolucao do Android.",
                display.Message,
                smartDeviceId);
        }

        var currentActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddConfigActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-module-screen",
                $"A selecao do modulo nao esta mais na EmpresaAddConfigActivity. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        // Mapeamento real do dispositivo de autoatendimento 1080x1920.
        // Os pontos abaixo sao escalados proporcionalmente pela resolucao atual.
        // Nao usamos UIAutomator nesta etapa porque o dump pode fazer o Smart 8.0
        // recuar ate a tela inicial no Android 7.
        var moduleReferenceY = module.Equals("smart_totem", StringComparison.OrdinalIgnoreCase)
            ? 403d
            : 557d; // Smart AutoPagamento
        var moduleX = Math.Clamp((int)Math.Round(display.Width * (1010d / 1080d)), 1, display.Width - 1);
        var moduleY = Math.Clamp((int)Math.Round(display.Height * (moduleReferenceY / 1920d)), 1, display.Height - 1);
        var firstConfirmX = Math.Clamp((int)Math.Round(display.Width * (810d / 1080d)), 1, display.Width - 1);
        var firstConfirmY = Math.Clamp((int)Math.Round(display.Height * (1813d / 1920d)), 1, display.Height - 1);

        progress?.Invoke(
            "legacy80-large-module",
            $"Tela grande detectada para {moduleLabel}. Selecionando o modulo no ponto mapeado {moduleX},{moduleY}, sem UIAutomator...");

        var moduleTap = await _adb.TapAsync(serial, moduleX, moduleY, cancellationToken);
        if (!moduleTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-module",
                $"Nao foi possivel selecionar {moduleLabel} no Smart 8.0.",
                moduleTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(450, cancellationToken);
        currentActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddConfigActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-module",
                $"O toque de selecao do modulo foi executado, mas a EmpresaAddConfigActivity deixou o primeiro plano antes de Confirmar. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        progress?.Invoke(
            "legacy80-large-module-confirm",
            $"{moduleLabel} selecionado. Acionando Confirmar uma unica vez em {firstConfirmX},{firstConfirmY}...");

        var firstConfirmTap = await _adb.TapAsync(serial, firstConfirmX, firstConfirmY, cancellationToken);
        if (!firstConfirmTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-module-confirm",
                "Nao foi possivel acionar o Confirmar da selecao do modulo.",
                firstConfirmTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var deviceActivity = await WaitForLegacyActivityAsync(
            serial,
            IsLegacyCompanyAddActivity,
            20,
            cancellationToken);
        if (!IsLegacyCompanyAddActivity(deviceActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-device-screen",
                string.IsNullOrWhiteSpace(deviceActivity)
                    ? "O modulo foi confirmado, mas a EmpresaAddActivity nao apareceu. A automacao foi interrompida sem voltar ou reiniciar o Smart."
                    : $"O modulo foi confirmado, mas a EmpresaAddActivity nao abriu. Activity atual: {deviceActivity}. A automacao foi interrompida sem voltar ou reiniciar o Smart.",
                deviceActivity,
                smartDeviceId);
        }

        // Tela Configuracao (PDV) do Totem/AutoPagamento 8.0.
        // Mapeamento 1080x1920: DIGITAR ~= 810,226; Host ~= 540,525;
        // Confirmar ~= 810,606. Esses pontos tambem sao proporcionais.
        var typeX = Math.Clamp((int)Math.Round(display.Width * (810d / 1080d)), 1, display.Width - 1);
        var typeY = Math.Clamp((int)Math.Round(display.Height * (226d / 1920d)), 1, display.Height - 1);
        var hostX = Math.Clamp((int)Math.Round(display.Width * (540d / 1080d)), 1, display.Width - 1);
        var hostY = Math.Clamp((int)Math.Round(display.Height * (525d / 1920d)), 1, display.Height - 1);
        var confirmX = Math.Clamp((int)Math.Round(display.Width * (810d / 1080d)), 1, display.Width - 1);
        var confirmY = Math.Clamp((int)Math.Round(display.Height * (606d / 1920d)), 1, display.Height - 1);

        progress?.Invoke(
            "legacy80-large-type",
            $"EmpresaAddActivity aberta. Acionando DIGITAR em {typeX},{typeY}, sem UIAutomator...");
        var typeTap = await _adb.TapAsync(serial, typeX, typeY, cancellationToken);
        if (!typeTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-type",
                "Nao foi possivel acionar DIGITAR no Smart 8.0.",
                typeTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(500, cancellationToken);
        currentActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-host",
                $"DIGITAR foi acionado, mas a EmpresaAddActivity deixou o primeiro plano. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        if (sdkLevel <= 25)
        {
            smartDeviceId = FirstNonEmpty(smartDeviceId, await GetAndroidIdFallbackAsync(serial, cancellationToken));
        }
        else
        {
            smartDeviceId = await ResolveSmartDeviceIdFromCurrentScreenAsync(
                serial, packageName, smartDeviceId, cancellationToken);
        }
        if (beforeSubmitAsync is not null)
        {
            if (string.IsNullOrWhiteSpace(smartDeviceId))
            {
                progress?.Invoke(
                    "legacy80-large-device-id",
                    "O Smart 8.0 nao expos o Device ID antes do envio. A confirmacao remota sera usada ao final.");
            }
            else
            {
                await beforeSubmitAsync(smartDeviceId);
            }
        }

        progress?.Invoke(
            "legacy80-large-host",
            $"Selecionando o campo Host em {hostX},{hostY}, limpando o conteudo e informando a URL de vinculo...");
        var hostTap = await _adb.TapAsync(serial, hostX, hostY, cancellationToken);
        if (!hostTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-host",
                "Nao foi possivel selecionar o campo Host.",
                hostTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(250, cancellationToken);
        var clear = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clear.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-host",
                "Nao foi possivel selecionar tudo e limpar o Host atual.",
                clear.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var clearRemainder = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clearRemainder.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-host",
                "A primeira limpeza do Host foi executada, mas a segunda passagem de seguranca falhou.",
                clearRemainder.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var input = await _adb.InputTextAsync(serial, url, cancellationToken);
        if (!input.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-host",
                string.IsNullOrWhiteSpace(input.CombinedOutput)
                    ? "Nao foi possivel informar a URL no campo Host."
                    : input.CombinedOutput.Trim(),
                currentActivity,
                smartDeviceId);
        }

        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        await Task.Delay(350, cancellationToken);

        currentActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-hold-confirm",
                $"A URL foi informada, mas a EmpresaAddActivity deixou o primeiro plano antes do Confirmar. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        progress?.Invoke(
            "legacy80-large-hold-confirm",
            $"Mantendo Confirmar pressionado por 5 segundos em {confirmX},{confirmY}...");
        var hold = await _adb.LongPressAsync(serial, confirmX, confirmY, 5000, cancellationToken);
        if (!hold.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-hold-confirm",
                "O ADB nao conseguiu executar o toque prolongado de 5 segundos em Confirmar.",
                hold.CombinedOutput.Trim(),
                smartDeviceId);
        }

        await Task.Delay(1400, cancellationToken);
        currentActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
        if (!IsLegacyCompanyAddActivity(currentActivity))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-review",
                $"O toque prolongado foi executado, mas a tela de revisao nao permaneceu na EmpresaAddActivity. Activity atual: {currentActivity}.",
                currentActivity,
                smartDeviceId);
        }

        // O procedimento manual confirmado pelo usuario usa o mesmo Confirmar novamente
        // depois do toque prolongado. Mantemos exatamente esse comportamento, sem ler a UI.
        progress?.Invoke(
            "legacy80-large-final-confirm",
            "Toque prolongado concluido. Acionando o Confirmar final uma unica vez...");
        var finalTap = await _adb.TapAsync(serial, confirmX, confirmY, cancellationToken);
        if (!finalTap.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-large-final-confirm",
                "Nao foi possivel acionar o Confirmar final do Smart 8.0.",
                finalTap.CombinedOutput.Trim(),
                smartDeviceId);
        }

        return await CompleteLegacy80SynchronizationAsync(
            serial,
            packageName,
            module,
            smartDeviceId,
            progress,
            cancellationToken);
    }

    private async Task<SmartAutomationResult> CompleteLegacy80SynchronizationAsync(
        string serial,
        string packageName,
        string module,
        string smartDeviceId,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke("legacy80-sync", "Aguardando a sincronizacao inicial do Smart 8.0...");

        if (IsLegacy80LargeSelfServiceModule(module))
        {
            // No K2/Android 7, ate uma leitura do UIAutomator durante o dialogo de
            // sincronizacao pode fazer o Smart recuar. O AutoPagamento ja seguia pelo
            // caminho seguro quando essa leitura ficava indisponivel; Totem, por
            // conseguir responder ao dump, ainda sofria a regressao. Para os dois
            // modulos de tela grande, nao lemos a arvore: o cadastro remoto confirma
            // o vinculo e somente depois DismissConfirmedSynchronizationAsync toca OK.
            await Task.Delay(1800, cancellationToken);
            progress?.Invoke(
                "legacy80-sync-pending",
                "K2 em sincronizacao. Aguardando a confirmacao remota antes de acionar o OK uma unica vez, sem UIAutomator...");
            return new SmartAutomationResult(
                true,
                packageName,
                "legacy80-sync-pending",
                "A sincronizacao do K2 foi enviada; o vinculo sera confirmado remotamente antes do unico clique em OK.",
                string.Empty,
                smartDeviceId);
        }

        UiSnapshot finalSnapshot = new(false, Array.Empty<UiNode>(), string.Empty);

        var consecutiveUnavailableSnapshots = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1800 : 900, cancellationToken);
            // O N950 pode retornar `could not get idle state` em todas as leituras.
            // Uma captura curta evita transformar a validacao remota em varios minutos.
            finalSnapshot = await ReadUiQuickAsync(serial, cancellationToken);
            if (finalSnapshot.Success &&
                (IsSynchronizationSuccess(finalSnapshot.Nodes) || IsSynchronizationFailure(finalSnapshot.Nodes)))
            {
                break;
            }

            consecutiveUnavailableSnapshots = finalSnapshot.Success
                ? 0
                : consecutiveUnavailableSnapshots + 1;
            if (consecutiveUnavailableSnapshots >= 2)
            {
                // O dialogo de erro 62 da Stone fica disponivel em cerca de 3 segundos,
                // acima do limite da leitura rapida. Faz uma unica leitura estendida
                // antes de desistir; assim o DeviceId pode ser extraido e apenas este
                // job pode liberar o vinculo antigo e repetir o procedimento.
                finalSnapshot = await ReadUiQuickAsync(
                    serial,
                    cancellationToken,
                    dumpTimeoutMilliseconds: 8000);
                break;
            }
        }

        if (finalSnapshot.Success)
        {
            // A mensagem funcional de erro 62 tambem informa o DeviceId real.
            // Conserva esse identificador no resultado para que o chamador possa
            // memoriza-lo por ADB e liberar o vinculo correto na proxima tentativa.
            smartDeviceId = FirstNonEmpty(
                smartDeviceId,
                ExtractSmartDeviceId(finalSnapshot.Nodes));
        }

        if (finalSnapshot.Success && IsSynchronizationSuccess(finalSnapshot.Nodes))
        {
            progress?.Invoke("legacy80-sync-success", "Dados sincronizados com sucesso. Finalizando a confirmacao...");
            if (IsLegacy80LargeSelfServiceModule(module))
            {
                // No K2, o chamador valida primeiro o device_id no SelfHost/Softcomshop
                // e entao fecha o dialogo por uma unica coordenada calibrada. Clicar aqui
                // e novamente apos a validacao atingia a tela de modulo que ficava por
                // baixo do dialogo, aparentando que o Smart havia voltado sozinho.
                progress?.Invoke(
                    "legacy80-sync-ok-pending",
                    "Sincronizacao concluida no K2. Aguardando a confirmacao remota para acionar o OK uma unica vez...");
            }
            else
            {
                var ok = FindSynchronizationOkNode(finalSnapshot.Nodes);
                if (ok is not null)
                {
                    progress?.Invoke("legacy80-sync-ok", $"Acionando OK da sincronizacao em {ok.CenterX},{ok.CenterY}...");
                    await _adb.TapAsync(serial, ok.CenterX, ok.CenterY, cancellationToken);
                    await Task.Delay(700, cancellationToken);
                }
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "legacy80-sync-complete",
                $"Smart 8.0 configurado com {GetModuleLabel(module)} e dados sincronizados com sucesso.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        if (finalSnapshot.Success && IsSynchronizationFailure(finalSnapshot.Nodes))
        {
            var failureSummary = BuildSummary(finalSnapshot.Nodes);
            progress?.Invoke("legacy80-sync-error", "O Smart retornou erro ao registrar ou sincronizar o dispositivo.");
            return new SmartAutomationResult(
                false,
                packageName,
                "legacy80-sync-error",
                ExtractSynchronizationFailureMessage(finalSnapshot.Nodes),
                failureSummary,
                smartDeviceId);
        }

        if (finalSnapshot.Success && IsSynchronizationInProgress(finalSnapshot.Nodes))
        {
            // A sincronizacao do Smart 8.0 pode levar mais tempo em algumas
            // adquirentes. Estar na tela "Sincronizando" nao e falha: o envio ja
            // ocorreu e o chamador ainda fara a verificacao autoritativa pelo
            // cadastro remoto antes de decidir o resultado do job.
            progress?.Invoke(
                "legacy80-sync-pending",
                "O Smart continua sincronizando. Mantendo o job em validacao ate o cadastro remoto confirmar o Device ID...");
            return new SmartAutomationResult(
                true,
                packageName,
                "legacy80-sync-pending",
                "A sincronizacao ainda esta em andamento; o vinculo sera confirmado pelo cadastro remoto antes de finalizar o provisionamento.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        // Quando a arvore e indisponivel, o chamador ainda pode confirmar o device_id
        // pela API administrativa. `submitted` significa somente que o POST foi disparado
        // pelo Smart e ainda exige confirmacao remota.
        return new SmartAutomationResult(
            true,
            packageName,
            "legacy80-submitted",
            "O Confirmar final foi acionado no Smart 8.0. A confirmacao do device_id sera feita pelo cadastro do Softcomshop.",
            finalSnapshot.Success ? BuildSummary(finalSnapshot.Nodes) : finalSnapshot.Error,
            smartDeviceId);
    }

    public async Task<bool> DismissConfirmedSynchronizationAsync(
        string serial,
        string packageName,
        string? module = null,
        Action<string, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Este metodo so deve ser chamado depois que o servidor confirmou o vinculo.
        // No N950 o UIAutomator pode ficar indisponivel justamente no dialogo final,
        // mas o APK 8.0.1 expoe o botao positivo como app:id/dialog_button.
        // Nao usamos coordenada fixa e nao fechamos dialogs enquanto a API ainda nao
        // confirmou o device_id, preservando na tela qualquer erro retornado pelo Smart.
        var display = await GetAndroidDisplaySizeAsync(serial, cancellationToken);
        if (!display.Success)
        {
            return false;
        }

        // O sucesso do Smart 8.1 no K2 e um dialogo Compose dentro da AuthActivity.
        // Ele nao usa o AlertDialog/resource-id do Smart 8.0 e o UIAutomator e instavel
        // nesta ROM. O WindowManager expoe uma segunda janela do package enquanto o
        // modal esta aberto, permitindo validar o estado antes de tocar no OK.
        var installedVersion = await _adb.GetPackageVersionNameAsync(
            serial,
            packageName,
            cancellationToken);
        var sdkLevel = await GetAndroidSdkLevelAsync(serial, cancellationToken);
        var foreground = await GetForegroundActivityAsync(serial, cancellationToken);
        var smart81K2Dialog =
            string.Equals(AdbService.ClassifySmartFlow(installedVersion), "Smart 8.1+", StringComparison.OrdinalIgnoreCase) &&
            sdkLevel is > 0 and <= 25 &&
            IsLegacy80LargeSelfServiceModule(module) &&
            IsSmart81AuthActivity(foreground, packageName);

        if (smart81K2Dialog)
        {
            var dialogReady = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var windows = await _adb.ShellAsync(
                    serial,
                    "dumpsys window windows",
                    cancellationToken,
                    8000);
                if (windows.Success && HasAdditionalWindowForPackage(windows.CombinedOutput, packageName))
                {
                    dialogReady = true;
                    break;
                }

                await Task.Delay(500, cancellationToken);
            }

            if (!dialogReady)
            {
                progress?.Invoke(
                    "smart81-k2-sync-ok-wait",
                    "O vinculo foi confirmado, mas o modal final do Smart 8.1+ ainda nao foi exposto pelo WindowManager. Nenhum toque foi enviado fora de hora.");
                return false;
            }

            var (okX, okY) = GetSmart81K2SynchronizationOkPoint(display.Width, display.Height);
            progress?.Invoke(
                "smart81-k2-sync-ok",
                $"Atualizacao concluida no Smart 8.1+. Acionando OK em {okX},{okY}, sem UIAutomator...");
            var tap = await _adb.TapAsync(serial, okX, okY, cancellationToken);
            if (!tap.Success)
            {
                return false;
            }

            await Task.Delay(900, cancellationToken);
            var afterTap = await _adb.ShellAsync(
                serial,
                "dumpsys window windows",
                cancellationToken,
                8000);
            return afterTap.Success &&
                   !HasAdditionalWindowForPackage(afterTap.CombinedOutput, packageName);
        }

        // No K2 (Android 7), consultar o UIAutomator enquanto o dialogo final esta
        // visivel pode fazer o Smart recuar para a selecao de modulo antes do toque.
        // Como este metodo so e chamado depois da confirmacao autoritativa do vinculo,
        // Totem/AutoPagamento podem fechar o unico OK pelo ponto calibrado, sem ler a
        // arvore de acessibilidade instavel.
        if (IsLegacy80LargeSelfServiceModule(module))
        {
            var foregroundActivity = await GetForegroundActivityAsync(serial, cancellationToken);
            if (string.IsNullOrWhiteSpace(foregroundActivity) ||
                !foregroundActivity.Contains(packageName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsLegacyLoginActivity(foregroundActivity))
            {
                progress?.Invoke(
                    "legacy80-sync-finished",
                    "O Smart ja encerrou a confirmacao de sincronizacao e retornou para a tela de Login.");
                return true;
            }

            var dialogReady = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var windows = await _adb.ShellAsync(
                    serial,
                    "dumpsys window windows",
                    cancellationToken,
                    8000);
                if (windows.Success &&
                    HasAdditionalWindowForActivity(windows.CombinedOutput, foregroundActivity))
                {
                    dialogReady = true;
                    break;
                }

                await Task.Delay(500, cancellationToken);
            }

            if (!dialogReady)
            {
                progress?.Invoke(
                    "legacy80-sync-ok-wait",
                    "O vinculo foi confirmado, mas o dialogo final do K2 ainda nao abriu. Nenhum toque foi enviado fora de hora.");
                return false;
            }

            var (okX, okY) = GetLegacy80LargeSynchronizationOkPoint(display.Width, display.Height);
            progress?.Invoke(
                "legacy80-sync-ok-large",
                $"Dialogo final do K2 confirmado. Acionando o OK em {okX},{okY}, sem UIAutomator...");
            var tap = await _adb.TapAsync(serial, okX, okY, cancellationToken);
            if (!tap.Success)
            {
                return false;
            }

            await Task.Delay(900, cancellationToken);
            var afterTap = await _adb.ShellAsync(
                serial,
                "dumpsys window windows",
                cancellationToken,
                8000);
            return afterTap.Success &&
                   !HasAdditionalWindowForActivity(afterTap.CombinedOutput, foregroundActivity);
        }

        // dialog_button pertence ao custom_dialog.xml do Smart 8.0.1. button1 cobre
        // o AlertDialog padrao usado por outras compilacoes sem afetar o formulario,
        // pois ambos sao procurados somente apos a confirmacao remota do vinculo.
        var dialogResourceIds = new[] { "app:id/dialog_button", "android:id/button1" };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var foregroundActivity = await GetForegroundActivityAsync(serial, cancellationToken);
            if (IsLegacyLoginActivity(foregroundActivity))
            {
                progress?.Invoke(
                    "legacy80-sync-finished",
                    "O Smart ja encerrou a confirmacao de sincronizacao e retornou para a tela de Login.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(foregroundActivity) ||
                !foregroundActivity.Contains(packageName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var resourceId in dialogResourceIds)
            {
                var bounds = await FindViewBoundsFromActivityDumpAsync(serial, resourceId, cancellationToken);
                if (!IsActivityPointInsideDisplay(bounds, display))
                {
                    continue;
                }

                var tap = await _adb.TapAsync(serial, bounds.CenterX, bounds.CenterY, cancellationToken);
                if (!tap.Success)
                {
                    continue;
                }

                progress?.Invoke(
                    "legacy80-sync-ok",
                    $"Vinculo confirmado pela API. Acionando OK da sincronizacao via {resourceId} em {bounds.CenterX},{bounds.CenterY}...");
                await Task.Delay(700, cancellationToken);

                var afterTap = await _adb.ShellAsync(
                    serial,
                    "dumpsys activity top",
                    cancellationToken,
                    12000);
                if (!ContainsSynchronizationDialogResource(afterTap.CombinedOutput))
                {
                    return true;
                }
            }

            // No N950 o botao pode aparecer no dump da Activity, mas o retangulo
            // informado pelo Android nao corresponder a janela modal. Como o vinculo
            // ja foi confirmado pelo servidor, e seguro mover o foco dentro desse
            // dialogo conhecido e acionar o unico botao com ENTER. O fallback jamais
            // e executado apenas pela Activity: exige o resource-id do dialogo final.
            var dialogDump = await _adb.ShellAsync(
                serial,
                "dumpsys activity top",
                cancellationToken,
                12000);
            if (ContainsSynchronizationDialogResource(dialogDump.CombinedOutput))
            {
                progress?.Invoke(
                    "legacy80-sync-ok-fallback",
                    "OK ainda visivel apos a confirmacao remota. Focando e acionando o botao final do dialogo...");

                var focus = await _adb.KeyEventAsync(serial, "KEYCODE_TAB", cancellationToken);
                if (focus.Success)
                {
                    await Task.Delay(180, cancellationToken);
                    var enter = await _adb.KeyEventAsync(serial, "KEYCODE_ENTER", cancellationToken);
                    if (enter.Success)
                    {
                        await Task.Delay(700, cancellationToken);
                        var afterEnter = await _adb.ShellAsync(
                            serial,
                            "dumpsys activity top",
                            cancellationToken,
                            12000);
                        if (!ContainsSynchronizationDialogResource(afterEnter.CombinedOutput))
                        {
                            return true;
                        }
                    }
                }
            }

            if (attempt < 2)
            {
                await Task.Delay(450, cancellationToken);
            }
        }

        // Se a compilacao nao expuser um dos IDs conhecidos no ActivityManager,
        // tentamos por ultimo a arvore de acessibilidade. Essa ordem evita primeiro
        // o UIAutomator instavel do N950 e ainda preserva outras variantes do Smart.
        var snapshot = await ReadUiQuickAsync(serial, cancellationToken);
        if (!snapshot.Success || IsSynchronizationFailure(snapshot.Nodes) ||
            !IsSynchronizationSuccess(snapshot.Nodes))
        {
            return false;
        }

        var ok = FindSynchronizationOkNode(snapshot.Nodes);
        if (ok is null)
        {
            return false;
        }

        var uiTap = await _adb.TapAsync(serial, ok.CenterX, ok.CenterY, cancellationToken);
        if (!uiTap.Success)
        {
            return false;
        }

        progress?.Invoke(
            "legacy80-sync-ok",
            $"Vinculo confirmado. Acionando OK da sincronizacao em {ok.CenterX},{ok.CenterY}...");
        await Task.Delay(700, cancellationToken);
        return true;
    }

    private static bool ContainsSynchronizationDialogResource(string? activityDump) =>
        !string.IsNullOrWhiteSpace(activityDump) &&
        (activityDump.Contains("app:id/dialog_button", StringComparison.OrdinalIgnoreCase) ||
         activityDump.Contains("android:id/button1", StringComparison.OrdinalIgnoreCase));

    private static (int X, int Y) GetLegacy80LargeSynchronizationOkPoint(int width, int height) =>
        (
            Math.Clamp((int)Math.Round(width * (832d / 1080d)), 1, Math.Max(1, width - 1)),
            Math.Clamp((int)Math.Round(height * (1020d / 1920d)), 1, Math.Max(1, height - 1))
        );

    private static (int X, int Y) GetSmart81K2SynchronizationOkPoint(int width, int height) =>
        (
            Math.Clamp((int)Math.Round(width * (540d / 1080d)), 1, Math.Max(1, width - 1)),
            Math.Clamp((int)Math.Round(height * (1089d / 1920d)), 1, Math.Max(1, height - 1))
        );

    private static bool HasAdditionalWindowForPackage(string? windowDump, string? packageName)
    {
        if (string.IsNullOrWhiteSpace(windowDump) || string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var count = windowDump
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line =>
                line.Contains("Window #", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(packageName, StringComparison.OrdinalIgnoreCase));
        return count >= 2;
    }

    private static bool HasAdditionalWindowForActivity(string? windowDump, string? activity)
    {
        if (string.IsNullOrWhiteSpace(windowDump) || string.IsNullOrWhiteSpace(activity))
        {
            return false;
        }

        var count = windowDump
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(line =>
                line.Contains("Window #", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(activity, StringComparison.OrdinalIgnoreCase));
        return count >= 2;
    }

    private async Task<SmartAutomationResult> SubmitDeviceUrlSmart81Async(
        string serial,
        string url,
        string module,
        string packageName,
        UiSnapshot snapshot,
        string smartDeviceId,
        Action<string, string>? progress,
        Func<string, Task>? beforeSubmitAsync,
        CancellationToken cancellationToken)
    {
        progress?.Invoke("smart81", "Smart 8.1+ detectado. Usando o fluxo Configuracoes -> Nova Empresa -> DIGITAR.");

        var expectedCompany = ExtractCompanyNameFromDeviceUrl(url);
        if (string.IsNullOrWhiteSpace(expectedCompany))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-company",
                "A URL /device/add nao possui empresa_name. O Smart 8.1+ precisa desse dado para selecionar a empresa com seguranca.",
                BuildSummary(snapshot.Nodes),
                string.Empty);
        }

        // 1) Abre Configuracoes. Nao existe fallback por coordenada: se a engrenagem nao
        // estiver exposta na arvore de acessibilidade, interrompemos em vez de clicar as cegas.
        if (!ContainsLabel(snapshot.Nodes, "nova empresa"))
        {
            var settings = FindByLabels(snapshot.Nodes, new[]
            {
                "configuracoes",
                "configuracao",
                "ajustes",
                "settings",
                "engrenagem"
            });
            if (settings is null)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "smart81-settings",
                    "Smart 8.1+ detectado, mas o botao de Configuracoes/engrenagem nao foi localizado.",
                    BuildSummary(snapshot.Nodes),
                    string.Empty);
            }

            progress?.Invoke("smart81-settings", "Abrindo Configuracoes do Smart 8.1+...");
            await _adb.TapAsync(serial, settings.CenterX, settings.CenterY, cancellationToken);
            snapshot = await WaitForUiStateAsync(
                serial,
                nodes => ContainsLabel(nodes, "nova empresa"),
                12,
                cancellationToken);

            if (!snapshot.Success || !ContainsLabel(snapshot.Nodes, "nova empresa"))
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "smart81-new-company",
                    "As Configuracoes foram abertas, mas a opcao Nova Empresa nao apareceu.",
                    snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                    string.Empty);
            }
        }

        // 2) Nova Empresa.
        var newCompany = FindByLabels(snapshot.Nodes, new[] { "nova empresa" });
        if (newCompany is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-new-company",
                "A tela de Configuracoes foi identificada, mas Nova Empresa nao esta acionavel.",
                BuildSummary(snapshot.Nodes),
                string.Empty);
        }

        progress?.Invoke("smart81-new-company", "Abrindo Nova Empresa...");
        await _adb.TapAsync(serial, newCompany.CenterX, newCompany.CenterY, cancellationToken);

        snapshot = await WaitForUiStateAsync(
            serial,
            nodes => FindByLabels(nodes, new[] { expectedCompany }) is not null,
            15,
            cancellationToken);
        var companyNode = snapshot.Success
            ? FindByLabels(snapshot.Nodes, new[] { expectedCompany })
            : null;
        if (companyNode is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-company",
                $"A lista de empresas abriu, mas a empresa '{expectedCompany}' nao foi localizada. Nenhuma outra empresa foi selecionada automaticamente.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                string.Empty);
        }

        // 3) Seleciona a empresa exata obtida da propria URL de vinculo.
        progress?.Invoke("smart81-company", $"Selecionando a empresa {expectedCompany}...");
        await _adb.TapAsync(serial, companyNode.CenterX, companyNode.CenterY, cancellationToken);

        snapshot = await WaitForUiStateAsync(
            serial,
            nodes => FindByLabels(nodes, new[] { "confirmar" }) is not null,
            10,
            cancellationToken);
        var firstConfirm = snapshot.Success
            ? FindByLabels(snapshot.Nodes, new[] { "confirmar" })
            : null;
        if (firstConfirm is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-company-confirm",
                "A empresa foi selecionada, mas o primeiro botao Confirmar nao foi localizado.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                string.Empty);
        }

        progress?.Invoke("smart81-company-confirm", "Confirmando a empresa selecionada...");
        await _adb.TapAsync(serial, firstConfirm.CenterX, firstConfirm.CenterY, cancellationToken);

        // 4) O novo fluxo exige entrar explicitamente em DIGITAR antes de informar Host.
        snapshot = await WaitForUiStateAsync(
            serial,
            nodes => FindByLabels(nodes, new[] { "digitar", "digitar dados manualmente" }) is not null,
            12,
            cancellationToken);
        var typeManually = snapshot.Success
            ? FindByLabels(snapshot.Nodes, new[] { "digitar", "digitar dados manualmente" })
            : null;
        if (typeManually is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-type",
                "A empresa foi confirmada, mas a opcao DIGITAR nao foi localizada.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                string.Empty);
        }

        progress?.Invoke("smart81-type", "Abrindo a configuracao manual pelo botao DIGITAR...");
        await _adb.TapAsync(serial, typeManually.CenterX, typeManually.CenterY, cancellationToken);

        snapshot = await WaitForUiStateAsync(
            serial,
            nodes => FindSmart81HostEditable(nodes) is not null,
            12,
            cancellationToken);
        var hostEdit = snapshot.Success ? FindSmart81HostEditable(snapshot.Nodes) : null;
        if (hostEdit is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-host",
                "A configuracao manual abriu, mas o campo Host nao foi localizado.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                string.Empty);
        }

        // Em Android moderno, nunca substituimos o ID do APK pelo ANDROID_ID do shell.
        // O valor antecipado vem de uma vinculacao remota confirmada anteriormente;
        // a arvore do proprio Smart continua tendo prioridade quando exibir o Device ID.
        smartDeviceId = FirstNonEmpty(ExtractSmartDeviceId(snapshot.Nodes), smartDeviceId);
        if (string.IsNullOrWhiteSpace(smartDeviceId) &&
            await GetAndroidSdkLevelAsync(serial, cancellationToken) <= 25)
        {
            smartDeviceId = await GetAndroidIdFallbackAsync(serial, cancellationToken);
        }

        if (beforeSubmitAsync is not null)
        {
            if (string.IsNullOrWhiteSpace(smartDeviceId))
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "smart81-device-id",
                    "O Smart 8.1+ nao exibiu Device ID e o android_id tambem nao pôde ser obtido. O vinculo anterior nao pode ser validado com seguranca.",
                    BuildSummary(snapshot.Nodes),
                    string.Empty);
            }

            await beforeSubmitAsync(smartDeviceId);
        }

        // 5) Limpa o Host existente e cola exatamente a URL /device/add recebida.
        progress?.Invoke("smart81-host", "Informando a URL /device/add no campo Host...");
        await _adb.TapAsync(serial, hostEdit.CenterX, hostEdit.CenterY, cancellationToken);
        await Task.Delay(250, cancellationToken);
        var clear = await _adb.ClearFocusedTextAsync(serial, 128, cancellationToken);
        if (!clear.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-host",
                "Nao foi possivel limpar o campo Host antes de informar a URL.",
                clear.CombinedOutput.Trim(),
                smartDeviceId);
        }

        var input = await _adb.InputTextAsync(serial, url, cancellationToken);
        if (!input.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-host",
                string.IsNullOrWhiteSpace(input.CombinedOutput)
                    ? "Nao foi possivel preencher o Host pelo ADB."
                    : input.CombinedOutput.Trim(),
                BuildSummary(snapshot.Nodes),
                smartDeviceId);
        }

        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        snapshot = await WaitForUiStateAsync(
            serial,
            nodes => FindByLabels(nodes, new[] { "confirmar" }) is not null,
            10,
            cancellationToken);
        var holdConfirm = snapshot.Success
            ? FindByLabels(snapshot.Nodes, new[] { "confirmar" })
            : null;
        if (holdConfirm is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-hold-confirm",
                "A URL foi preenchida, mas o botao Confirmar que exige toque prolongado nao foi localizado.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                smartDeviceId);
        }

        // 6) Primeiro Confirmar da configuracao manual: toque prolongado de 5 segundos.
        progress?.Invoke("smart81-hold-confirm", "Mantendo Confirmar pressionado por 5 segundos...");
        var hold = await _adb.LongPressAsync(
            serial,
            holdConfirm.CenterX,
            holdConfirm.CenterY,
            5000,
            cancellationToken);
        if (!hold.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-hold-confirm",
                "O ADB nao conseguiu executar o toque prolongado de 5 segundos em Confirmar.",
                hold.CombinedOutput.Trim(),
                smartDeviceId);
        }

        // 7) So confirma definitivamente depois que a tela de revisao expuser os quatro dados
        // esperados. Se essa etapa nao aparecer, a automacao para aqui.
        snapshot = await WaitForUiStateAsync(
            serial,
            IsSmart81ConfirmationDetails,
            15,
            cancellationToken);
        if (!snapshot.Success || !IsSmart81ConfirmationDetails(snapshot.Nodes))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-review",
                "O toque prolongado foi executado, mas a tela de revisao com Empresa, Dispositivo, Client ID e Host nao apareceu.",
                snapshot.Success ? BuildSummary(snapshot.Nodes) : snapshot.Error,
                smartDeviceId);
        }

        smartDeviceId = FirstNonEmpty(ExtractSmartDeviceId(snapshot.Nodes), smartDeviceId);
        var finalConfirm = FindByLabels(snapshot.Nodes, new[] { "confirmar" });
        if (finalConfirm is null)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "smart81-final-confirm",
                "A tela de revisao foi validada, mas o Confirmar final nao foi localizado.",
                BuildSummary(snapshot.Nodes),
                smartDeviceId);
        }

        progress?.Invoke("smart81-final-confirm", "Revisao validada. Confirmando o vinculo no Smart 8.1+...");
        await _adb.TapAsync(serial, finalConfirm.CenterX, finalConfirm.CenterY, cancellationToken);
        return await CompleteSmart81SynchronizationAsync(
            serial,
            packageName,
            module,
            smartDeviceId,
            progress,
            cancellationToken);
    }

    private async Task<SmartAutomationResult> CompleteSmart81SynchronizationAsync(
        string serial,
        string packageName,
        string module,
        string smartDeviceId,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke("smart81-sync", "Aguardando a sincronizacao inicial do Smart 8.1+...");
        UiSnapshot finalSnapshot = new(false, Array.Empty<UiNode>(), string.Empty);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1800 : 900, cancellationToken);
            finalSnapshot = await ReadUiAsync(serial, cancellationToken);
            if (finalSnapshot.Success &&
                (IsSynchronizationSuccess(finalSnapshot.Nodes) || IsSynchronizationFailure(finalSnapshot.Nodes)))
            {
                break;
            }
        }

        if (finalSnapshot.Success && IsSynchronizationSuccess(finalSnapshot.Nodes))
        {
            progress?.Invoke("smart81-sync-success", "Dados sincronizados com sucesso. Fechando apenas a confirmacao final...");
            var ok = FindByLabels(finalSnapshot.Nodes, new[] { "ok!!!", "ok" });
            if (ok is not null)
            {
                await _adb.TapAsync(serial, ok.CenterX, ok.CenterY, cancellationToken);
                await Task.Delay(700, cancellationToken);
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "smart81-sync-complete",
                $"Smart 8.1+ configurado e {GetModuleLabel(module)} pronto apos a sincronizacao inicial.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        if (finalSnapshot.Success && IsSynchronizationFailure(finalSnapshot.Nodes))
        {
            progress?.Invoke("smart81-sync-restart", "Falha de sincronizacao detectada. O vinculo ja foi confirmado; reiniciando o Smart...");
            await _adb.ForceStopPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(600, cancellationToken);
            var relaunch = await _adb.LaunchPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(1800, cancellationToken);

            if (!relaunch.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "smart81-sync-restart",
                    "O vinculo foi confirmado, mas houve falha de sincronizacao e o Smart nao pôde ser reaberto automaticamente.",
                    BuildSummary(finalSnapshot.Nodes),
                    smartDeviceId);
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "smart81-sync-restarted",
                "O vinculo foi confirmado. O Smart apresentou falha de sincronizacao complementar e foi reiniciado automaticamente.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        if (finalSnapshot.Success && IsSynchronizationInProgress(finalSnapshot.Nodes))
        {
            progress?.Invoke("smart81-sync-timeout", "A sincronizacao permaneceu em andamento alem do tempo esperado. Reiniciando o Smart para liberar a tela...");
            await _adb.ForceStopPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(700, cancellationToken);
            var relaunch = await _adb.LaunchPackageAsync(serial, packageName, cancellationToken);
            await Task.Delay(1800, cancellationToken);

            if (!relaunch.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "smart81-sync-timeout",
                    "O vinculo foi confirmado, mas a sincronizacao ficou presa e o Smart nao pôde ser reaberto automaticamente.",
                    BuildSummary(finalSnapshot.Nodes),
                    smartDeviceId);
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "smart81-sync-timeout-restarted",
                "O vinculo foi confirmado. A sincronizacao ficou em andamento alem do esperado e o Smart foi reiniciado automaticamente.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        // A confirmacao final ja foi acionada. Permitimos que o chamador faça a verificacao
        // autoritativa pelo Softcomshop/device_id mesmo se a arvore Android nao expuser o modal.
        return new SmartAutomationResult(
            true,
            packageName,
            "smart81-submitted",
            "O Confirmar final foi acionado no Smart 8.1+. A confirmacao do device_id sera feita pelo cadastro do Softcomshop.",
            finalSnapshot.Success ? BuildSummary(finalSnapshot.Nodes) : finalSnapshot.Error,
            smartDeviceId);
    }

    private async Task<UiSnapshot> WaitForReadableUiAsync(
        string serial,
        int attempts,
        CancellationToken cancellationToken)
    {
        attempts = Math.Clamp(attempts, 1, 20);
        UiSnapshot last = new(false, Array.Empty<UiNode>(), "Nao foi possivel ler os componentes da tela Android.");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(650, cancellationToken);
            last = await ReadUiAsync(serial, cancellationToken);
            if (last.Success && last.Nodes.Count > 0)
            {
                return last;
            }
        }

        return last.Success && last.Nodes.Count == 0
            ? new UiSnapshot(false, Array.Empty<UiNode>(), "A arvore da tela Android permaneceu vazia apos varias tentativas.")
            : last;
    }

    private async Task<UiSnapshot> WaitForUiStateAsync(
        string serial,
        Func<IReadOnlyList<UiNode>, bool> predicate,
        int attempts,
        CancellationToken cancellationToken)
    {
        attempts = Math.Clamp(attempts, 1, 30);
        UiSnapshot last = new(false, Array.Empty<UiNode>(), "A tela esperada nao foi localizada.");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(attempt == 0 ? 500 : 650, cancellationToken);
            last = await ReadUiAsync(serial, cancellationToken);
            if (last.Success && predicate(last.Nodes))
            {
                return last;
            }
        }
        return last;
    }

    private async Task<string> GetAndroidIdFallbackAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var result = await _adb.ShellAsync(
            serial,
            "settings get secure android_id",
            cancellationToken,
            6000);
        if (!result.Success)
        {
            return string.Empty;
        }

        var value = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value;
    }

    private async Task<string> ResolveSmartDeviceIdFromCurrentScreenAsync(
        string serial,
        string packageName,
        string current,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(current)) return current.Trim();

        // Em Android moderno, settings secure android_id pertence ao shell e pode ser
        // diferente do identificador enxergado pelo APK. Consultamos somente as telas do
        // próprio package/job e o dump da Activity, sem ler arquivos de outro aplicativo.
        var ui = await ReadUiQuickAsync(
            serial,
            cancellationToken,
            dumpTimeoutMilliseconds: 8000);
        if (ui.Success && IsUiFromPackage(ui.Nodes, packageName))
        {
            var fromUi = ExtractSmartDeviceId(ui.Nodes);
            if (!string.IsNullOrWhiteSpace(fromUi)) return fromUi;
        }

        var activity = await _adb.ShellAsync(serial, "dumpsys activity top", cancellationToken, 10000);
        if (activity.Success)
        {
            var output = activity.StandardOutput ?? string.Empty;
            if (output.Contains(packageName, StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(
                    output,
                    @"Device\s*ID\s*[:=]?\s*([A-Za-z0-9._-]{6,})",
                    RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static UiNode? FindSmart81HostEditable(IEnumerable<UiNode> nodes) =>
        FindEditableByHints(nodes, new[] { "host" }) ??
        FindEditableBelowLabels(nodes, new[] { "host" });

    private static bool IsSmart81ConfirmationDetails(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "empresa") &&
        ContainsLabel(nodes, "dispositivo") &&
        (ContainsLabel(nodes, "client id") ||
         ContainsLabel(nodes, "client_id") ||
         ContainsLabel(nodes, "clientid")) &&
        ContainsLabel(nodes, "host");

    private static bool IsLegacy80ConfirmationDetails(IEnumerable<UiNode> nodes)
    {
        var list = nodes.ToArray();
        if (FindByLabelsIncludingText(list, new[] { "confirmar" }) is null)
        {
            return false;
        }

        // O 8.0 permanece na EmpresaAddActivity durante DIGITAR, Host e revisao.
        // Consideramos revisao apenas quando o Host deixa de ser editavel e a tela
        // ainda apresenta dados de configuracao suficientes para um segundo Confirmar.
        if (FindSmart81HostEditable(list) is not null)
        {
            return false;
        }

        var hasConfigurationData =
            ContainsLabel(list, "host") ||
            ContainsLabel(list, "empresa") ||
            ContainsLabel(list, "dispositivo") ||
            ContainsLabel(list, "client id") ||
            ContainsLabel(list, "client_id") ||
            ContainsLabel(list, "clientid");

        return hasConfigurationData;
    }

    private static string ExtractCompanyNameFromDeviceUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var expectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "empresa_name",
            "empresa_nome",
            "company_name",
            "empresa_fantasia",
            "empresa_razao_social"
        };

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawKey = separator >= 0 ? part[..separator] : part;
            var rawValue = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            var key = Uri.UnescapeDataString(rawKey.Replace("+", " "));
            if (!expectedKeys.Contains(key))
            {
                continue;
            }

            return Uri.UnescapeDataString(rawValue.Replace("+", " ")).Trim();
        }

        return string.Empty;
    }

    private async Task<(bool Success, string Detail)> TryClearPackageAsync(
        string serial,
        string packageName,
        string appLabel,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        var clear = await _adb.ClearPackageAsync(serial, packageName, cancellationToken);
        if (clear.Success && clear.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            return (true, string.Empty);
        }

        var firstDetail = string.IsNullOrWhiteSpace(clear.CombinedOutput)
            ? $"O Android nao confirmou a limpeza dos dados do {appLabel}."
            : clear.CombinedOutput.Trim();

        // Android 17 / alguns emuladores apresentam uma falha interna do PackageManager
        // ao executar `pm clear`, normalmente envolvendo INotificationManager.clearData.
        // Tentamos novamente informando explicitamente o usuario Android antes de
        // considerar a limpeza indisponivel.
        if (firstDetail.Contains("INotificationManager.clearData", StringComparison.OrdinalIgnoreCase) ||
            firstDetail.Contains("clearApplicationUserData", StringComparison.OrdinalIgnoreCase) ||
            firstDetail.Contains("NullPointerException", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Invoke(
                "clear-retry",
                "O Android retornou uma falha interna ao limpar os dados. Tentando novamente para o usuario 0...");

            var retry = await _adb.ShellAsync(
                serial,
                $"pm clear --user 0 {packageName}",
                cancellationToken,
                30000);

            if (retry.Success && retry.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Invoke("clear-retry", "Dados locais limpos na segunda tentativa.");
                return (true, string.Empty);
            }

            // Quando o erro e do proprio Android/emulador, nao bloqueamos todo o
            // provisionamento. Reiniciamos o APK sem limpar os dados e deixamos a
            // automacao validar a tela encontrada. Se o Smart ja estiver configurado,
            // a etapa seguinte indicara de forma clara que e necessario zerar o APK
            // manualmente ou usar outro emulador.
            progress?.Invoke(
                "clear-warning",
                "O emulador nao permitiu limpar os dados do Smart. O Provisioner vai fechar e abrir o aplicativo sem zerar os dados e continuar a verificacao.");

            var stop = await _adb.ForceStopPackageAsync(serial, packageName, cancellationToken);
            if (stop.Success)
            {
                await Task.Delay(500, cancellationToken);
                return (true, string.Empty);
            }

            var retryDetail = string.IsNullOrWhiteSpace(retry.CombinedOutput)
                ? firstDetail
                : retry.CombinedOutput.Trim();
            return (false, retryDetail);
        }

        return (false, firstDetail);
    }

    public async Task<SmartAutomationResult> SubmitSmartTefAsync(
        string serial,
        string deviceName,
        string cnpj,
        string empresaId,
        string token,
        bool clearData,
        string? configuredPackage = null,
        string? provisioningProfile = null,
        Action<string, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke("package", "Identificando o package do Smart TEF neste dispositivo...");
        var packageName = await ResolveTefPackageAsync(serial, configuredPackage, cancellationToken);
        progress?.Invoke("package", $"Package TEF validado para este job: {packageName}.");
        var smartVersion = await _adb.GetPackageVersionNameAsync(serial, packageName, cancellationToken);
        var smartFlow = AdbService.ClassifySmartFlow(smartVersion);
        progress?.Invoke(
            "smart-version",
            string.IsNullOrWhiteSpace(smartVersion)
                ? "Versao do Smart TEF nao identificada; a rota sera validada pela Activity atual."
                : $"Smart TEF {smartVersion} detectado. Perfil de interface: {smartFlow}.");

        if (clearData)
        {
            progress?.Invoke("clear", $"Limpando os dados locais de {packageName}...");
            var clear = await TryClearPackageAsync(serial, packageName, "Smart TEF", progress, cancellationToken);
            if (!clear.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "clear",
                    clear.Detail,
                    string.Empty,
                    string.Empty);
            }
        }
        else
        {
            progress?.Invoke("restart", "Reiniciando o Smart TEF sem limpar os dados locais...");
            var stop = await _adb.ForceStopPackageAsync(serial, packageName, cancellationToken);
            if (!stop.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "restart",
                    string.IsNullOrWhiteSpace(stop.CombinedOutput)
                        ? "Nao foi possivel fechar o Smart TEF antes de reabri-lo."
                        : stop.CombinedOutput.Trim(),
                    string.Empty,
                    string.Empty);
            }

            await Task.Delay(500, cancellationToken);
        }

        // O fluxo por LoginActivity -> EmpresaActivity -> TefSetupActivity pertence
        // somente ao Smart legado. O Smart 8.1 usa AuthActivity e onboarding Compose:
        // Bem-vindo -> selecao Smart TEF -> Avancar -> configuracao. O package pode ser
        // o mesmo nas duas versoes, portanto ele sozinho nunca decide a rota.
        var useActivityNavigation = ShouldUseActivityBasedSmartTefNavigation(packageName, smartFlow);
        if (useActivityNavigation)
        {
            progress?.Invoke(
                "tef-permissions",
                "Preparando as permissoes declaradas pelo Smart TEF antes de abrir o aplicativo...");
            var permissions = await EnsureLegacyStoragePermissionsAsync(serial, packageName, cancellationToken);
            if (!permissions.Success)
            {
                progress?.Invoke(
                    "tef-permissions",
                    "Nem todas as permissoes puderam ser confirmadas por ADB; a navegacao continuara e validara a Activity real. " + permissions.Detail);
            }
        }

        progress?.Invoke("launch", "Abrindo o Smart TEF no Android selecionado...");
        var launch = await _adb.LaunchPackageAsync(serial, packageName, cancellationToken);
        if (!launch.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "launch",
                string.IsNullOrWhiteSpace(launch.CombinedOutput)
                    ? "Nao foi possivel abrir o Smart TEF."
                    : launch.CombinedOutput.Trim(),
                string.Empty,
                string.Empty);
        }

        await Task.Delay(1500, cancellationToken);

        if (useActivityNavigation)
        {
            var navigation = await NavigateToSmartTefSetupByActivityAsync(
                serial,
                packageName,
                progress,
                cancellationToken);
            if (!navigation.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    navigation.Stage,
                    navigation.Message,
                    navigation.Summary,
                    string.Empty);
            }

            progress?.Invoke(
                "tef-direct",
                "Tela Configurar Smart TEF confirmada pela Activity. Preenchendo sem depender do UIAutomator...");
            var activityDirectResult = await FillSmartTefKnownLayoutAsync(
                serial,
                packageName,
                deviceName,
                cnpj,
                empresaId,
                token,
                provisioningProfile,
                progress,
                cancellationToken);
            if (!activityDirectResult.Success)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    activityDirectResult.Stage,
                    activityDirectResult.Message,
                    activityDirectResult.Summary,
                    string.Empty);
            }

            var finalActivity = await WaitForLegacyActivityAsync(
                serial,
                activity => IsLegacyLoginActivity(activity) || IsSmartTefMainActivity(activity),
                12,
                cancellationToken);
            var completed = IsLegacyLoginActivity(finalActivity) || IsSmartTefMainActivity(finalActivity);
            return new SmartAutomationResult(
                true,
                packageName,
                completed ? "tef-complete" : "tef-submitted",
                completed
                    ? "Smart TEF configurado com sucesso. A tela operacional foi localizada."
                    : "Os dados do Smart TEF foram enviados e a conclusao foi acionada. A Activity de login ainda nao foi confirmada automaticamente.",
                string.IsNullOrWhiteSpace(finalActivity) ? activityDirectResult.Summary : finalActivity,
                string.Empty);
        }

        var snapshot = await ReadUiAsync(serial, cancellationToken);
        if (!snapshot.Success)
        {
            return new SmartAutomationResult(false, packageName, "ui", snapshot.Error, string.Empty, string.Empty);
        }

        var smartDeviceId = ExtractSmartDeviceId(snapshot.Nodes);

        if (IsSmartTefLoginScreen(snapshot.Nodes))
        {
            return new SmartAutomationResult(
                true,
                packageName,
                "tef-already-configured",
                "O Smart TEF ja esta configurado e a tela de login foi localizada.",
                BuildSummary(snapshot.Nodes),
                smartDeviceId);
        }

        // Estado 1: tela inicial do RedeFlex.
        if (ContainsLabel(snapshot.Nodes, "bem vindo ao smart") ||
            FindByLabels(snapshot.Nodes, StartConfigurationLabels) is not null)
        {
            progress?.Invoke("tef-start", "Clicando em Iniciar Configuracao...");
            var startButton = FindByLabels(snapshot.Nodes, StartConfigurationLabels);
            if (startButton is null)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "tef-start",
                    "A tela inicial do Smart TEF foi identificada, mas o botao Iniciar Configuracao nao foi localizado.",
                    BuildSummary(snapshot.Nodes),
                    smartDeviceId);
            }

            await _adb.TapAsync(serial, startButton.CenterX, startButton.CenterY, cancellationToken);
            await Task.Delay(900, cancellationToken);
            snapshot = await ReadUiAsync(serial, cancellationToken);
            if (!snapshot.Success)
            {
                return new SmartAutomationResult(false, packageName, "tef-start", snapshot.Error, string.Empty, smartDeviceId);
            }
            smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(snapshot.Nodes));
        }

        // Estado 2: selecao de modulo.
        if (ContainsLabel(snapshot.Nodes, "selecione o modulo"))
        {
            const string tefModuleLabel = "Smart TEF";
            progress?.Invoke("tef-module", "Selecionando o modulo Smart TEF...");

            var moduleCard = FindByLabels(snapshot.Nodes, new[] { tefModuleLabel });
            if (moduleCard is null)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "tef-module",
                    "A tela de modulos abriu, mas nao foi possivel localizar Smart TEF.",
                    BuildSummary(snapshot.Nodes),
                    smartDeviceId);
            }

            // O botao Avancar ja esta presente na mesma arvore usada para localizar
            // o modulo. Guardamos o ponto antes do toque no Smart TEF para nao depender
            // de um segundo uiautomator dump. Em alguns POS fisicos, o Compose deixa de
            // expor a raiz imediatamente depois que o modulo e marcado.
            var advance = FindByLabels(snapshot.Nodes, AdvanceLabels);
            if (advance is null)
            {
                return new SmartAutomationResult(
                    false,
                    packageName,
                    "tef-module",
                    "A tela de modulos foi localizada, mas o botao Avancar nao esta acessivel.",
                    BuildSummary(snapshot.Nodes),
                    smartDeviceId);
            }

            // Toca no controle de selecao, no lado direito do card.
            var moduleTapX = Math.Max(moduleCard.Left + 1, moduleCard.Right - 55);
            await _adb.TapAsync(serial, moduleTapX, moduleCard.CenterY, cancellationToken);
            await Task.Delay(650, cancellationToken);

            progress?.Invoke("tef-advance", "Avancando para Configurar Smart TEF...");
            await _adb.TapAsync(serial, advance.CenterX, advance.CenterY, cancellationToken);

            // IMPORTANTE: nao esperamos mais o UIAutomator reconhecer esta tela.
            // Em alguns aparelhos fisicos o Compose ja mostra Nome do dispositivo,
            // mas `uiautomator dump` retorna "null root node" por bastante tempo.
            // O fluxo do RedeFlex e conhecido e estavel; depois de Avancar usamos
            // coordenadas relativas a resolucao real do Android para preencher a tela.
            await Task.Delay(1800, cancellationToken);
        }
        else if (!IsSmartTefConfigurationScreen(snapshot.Nodes))
        {
            return new SmartAutomationResult(
                false,
                packageName,
                "tef-state",
                "O Smart TEF nao esta na tela inicial, na selecao de modulo nem na configuracao esperada. Tela detectada: " + BuildSummary(snapshot.Nodes),
                BuildSummary(snapshot.Nodes),
                smartDeviceId);
        }

        progress?.Invoke("tef-direct", "Tela Configurar Smart TEF aberta. Preenchendo os campos diretamente, sem aguardar o UIAutomator...");

        var directResult = await FillSmartTefKnownLayoutAsync(
            serial,
            packageName,
            deviceName,
            cnpj,
            empresaId,
            token,
            provisioningProfile,
            progress,
            cancellationToken);

        if (!directResult.Success)
        {
            return new SmartAutomationResult(
                false,
                packageName,
                directResult.Stage,
                directResult.Message,
                directResult.Summary,
                smartDeviceId);
        }

        progress?.Invoke("tef-finish", "Configuracao enviada. Verificando a tela final do Smart TEF...");

        // A validacao final e best-effort. Nao deixa o usuario esperando minutos caso
        // o UiTestAutomationBridge do aparelho fisico continue sem disponibilizar root.
        await Task.Delay(1300, cancellationToken);
        var finalSnapshot = await ReadUiQuickAsync(serial, cancellationToken);
        if (finalSnapshot.Success)
        {
            smartDeviceId = FirstNonEmpty(smartDeviceId, ExtractSmartDeviceId(finalSnapshot.Nodes));

            if (IsSmartTefLoginScreen(finalSnapshot.Nodes))
            {
                return new SmartAutomationResult(
                    true,
                    packageName,
                    "tef-complete",
                    "Smart TEF configurado com sucesso. A tela de login foi localizada.",
                    BuildSummary(finalSnapshot.Nodes),
                    smartDeviceId);
            }

            return new SmartAutomationResult(
                true,
                packageName,
                "tef-submitted",
                "Os dados do Smart TEF foram preenchidos, Confirmar Configuracao e Concluir foram acionados. A tela de login ainda nao foi reconhecida automaticamente.",
                BuildSummary(finalSnapshot.Nodes),
                smartDeviceId);
        }

        // Se o UIAutomator falhar, o fluxo nao e marcado como defeito: a automacao ja
        // preencheu os campos e acionou Confirmar Configuracao. O log informa que a
        // validacao visual nao ficou disponivel no aparelho.
        return new SmartAutomationResult(
            true,
            packageName,
            "tef-submitted-no-ui",
            "Os dados do Smart TEF foram preenchidos, Confirmar Configuracao e Concluir foram acionados. O Android nao disponibilizou a arvore de acessibilidade para validar a tela final.",
            finalSnapshot.Error,
            smartDeviceId);
    }

    private async Task<TefDirectResult> NavigateToSmartTefSetupByActivityAsync(
        string serial,
        string packageName,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        var activity = await WaitForLegacySmartActivityAsync(serial, 18, cancellationToken);
        if (!string.IsNullOrWhiteSpace(activity) &&
            !activity.StartsWith(packageName + "/", StringComparison.OrdinalIgnoreCase))
        {
            return new TefDirectResult(
                false,
                "tef-activity",
                $"O package do Smart TEF foi aberto, mas outra Activity permaneceu em primeiro plano: {activity}.",
                activity);
        }

        if (IsSmartTefSetupActivity(activity))
        {
            return new TefDirectResult(true, "tef-setup", string.Empty, activity);
        }

        if (IsLegacyLoginActivity(activity))
        {
            progress?.Invoke(
                "tef-settings",
                "LoginActivity confirmada. Abrindo Configuracoes pelo controle oficial do Smart...");
            var settingsTap = await TapActivityViewAsync(
                serial,
                "app:id/btn_config",
                600,
                723,
                cancellationToken);
            if (!settingsTap.Success)
            {
                return new TefDirectResult(false, "tef-settings", settingsTap.Message, settingsTap.Summary);
            }

            activity = await WaitForLegacyActivityAsync(
                serial,
                IsLegacyCompanyActivity,
                30,
                cancellationToken);
        }

        if (IsLegacyCompanyActivity(activity))
        {
            progress?.Invoke(
                "tef-new-company",
                "EmpresaActivity confirmada. Abrindo Nova Empresa pelo controle oficial do Smart...");
            var newCompanyTap = await TapActivityViewAsync(
                serial,
                "app:id/btn_novo",
                532,
                1256,
                cancellationToken);
            if (!newCompanyTap.Success)
            {
                return new TefDirectResult(false, "tef-new-company", newCompanyTap.Message, newCompanyTap.Summary);
            }

            activity = await WaitForLegacyActivityAsync(
                serial,
                IsLegacyCompanyAddConfigActivity,
                30,
                cancellationToken);
        }

        if (IsLegacyCompanyAddConfigActivity(activity))
        {
            progress?.Invoke(
                "tef-module",
                "Selecionando o modo Smart TEF na EmpresaAddConfigActivity...");
            var moduleTap = await TapActivityViewAsync(
                serial,
                "app:id/swt_config_modo_tef",
                625,
                1165,
                cancellationToken);
            if (!moduleTap.Success)
            {
                return new TefDirectResult(false, "tef-module", moduleTap.Message, moduleTap.Summary);
            }

            await Task.Delay(350, cancellationToken);
            var confirmTap = await TapActivityViewAsync(
                serial,
                "app:id/btn_confirmar",
                536,
                1256,
                cancellationToken);
            if (!confirmTap.Success)
            {
                return new TefDirectResult(false, "tef-module-confirm", confirmTap.Message, confirmTap.Summary);
            }

            activity = await WaitForLegacyActivityAsync(
                serial,
                IsSmartTefSetupActivity,
                30,
                cancellationToken);
        }

        if (!IsSmartTefSetupActivity(activity))
        {
            return new TefDirectResult(
                false,
                "tef-setup",
                "O fluxo oficial foi acionado, mas a TefSetupActivity nao ficou em primeiro plano. Activity atual: " +
                (string.IsNullOrWhiteSpace(activity) ? "nao identificada" : activity) + ".",
                activity);
        }

        progress?.Invoke("tef-setup", "TefSetupActivity confirmada. A tela esta pronta para preenchimento.");
        return new TefDirectResult(true, "tef-setup", string.Empty, activity);
    }

    private async Task<FieldFillResult> TapActivityViewAsync(
        string serial,
        string resourceId,
        int fallbackX,
        int fallbackY,
        CancellationToken cancellationToken)
    {
        var bounds = await FindViewBoundsFromActivityDumpAsync(serial, resourceId, cancellationToken);
        var x = bounds.Success ? bounds.CenterX : fallbackX;
        var y = bounds.Success ? bounds.CenterY : fallbackY;
        var source = bounds.Success
            ? $"dumpsys activity top ({resourceId})"
            : "coordenada de compatibilidade do package Smart";
        var tap = await _adb.TapAsync(serial, x, y, cancellationToken);
        if (!tap.Success)
        {
            return new FieldFillResult(
                false,
                $"Nao foi possivel acionar {resourceId} em {x},{y}.",
                tap.CombinedOutput.Trim());
        }

        return new FieldFillResult(true, string.Empty, $"{resourceId} em {x},{y} via {source}");
    }

    private async Task<TefDirectResult> FillSmartTefKnownLayoutAsync(
        string serial,
        string packageName,
        string deviceName,
        string cnpj,
        string empresaId,
        string token,
        string? provisioningProfile,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        var sizeResult = await GetAndroidDisplaySizeAsync(serial, cancellationToken);
        if (!sizeResult.Success)
        {
            return new TefDirectResult(false, "tef-display", sizeResult.Message, string.Empty);
        }

        // O XML de referencia foi capturado em 1080x2400. Entretanto, o POS L400
        // (720x1600) nao renderiza o Compose com uma escala puramente proporcional:
        // os campos do card ficam mais abaixo. Por isso o L400 tem um perfil proprio,
        // levantado a partir da tela real enviada durante os testes.
        var layout = GetSmartTefLayout(sizeResult.Width, sizeResult.Height, provisioningProfile);

        progress?.Invoke(
            "tef-layout",
            $"Layout TEF: {layout.Name} ({sizeResult.Width}x{sizeResult.Height}). Campo Nome do dispositivo em {layout.NameX},{layout.NameY}.");

        // Antes de qualquer toque por coordenada, confirma que o package escolhido
        // em primeiro plano. Se algo mudou, interrompe sem enviar BACK ou novos toques.
        var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
        if (!string.Equals(foreground, packageName, StringComparison.OrdinalIgnoreCase))
        {
            return new TefDirectResult(
                false,
                "tef-foreground",
                $"O Smart TEF nao esta em primeiro plano. Aplicativo atual: {foreground}.",
                string.Empty);
        }

        if (!await IsExpectedSmartTefActivityAsync(serial, packageName, layout, cancellationToken))
        {
            return new TefDirectResult(
                false,
                "tef-activity",
                "A TefSetupActivity deixou de ficar em primeiro plano antes do preenchimento.",
                string.Empty);
        }

        progress?.Invoke("tef-name", $"Informando o nome do dispositivo: {deviceName}...");
        var name = await TapAndTypeTefFieldAsync(
            serial,
            layout.NameX,
            layout.NameY,
            deviceName,
            "Nome do dispositivo",
            layout.UseUiFieldBounds,
            cancellationToken);
        if (!name.Success)
        {
            return new TefDirectResult(false, "tef-name", "Nao foi possivel preencher Nome do dispositivo. " + name.Message, name.Summary);
        }

        // Uma leitura rapida serve apenas como confirmacao quando o aparelho expuser
        // a arvore. No L400 o UiTestAutomationBridge pode continuar retornando null;
        // nesse caso nao bloqueamos o fluxo, pois o preenchimento e feito pelo ADB.
        var nameSnapshot = await ReadUiQuickAsync(serial, cancellationToken);
        if (nameSnapshot.Success && !ContainsLabel(nameSnapshot.Nodes, deviceName))
        {
            // Se a arvore estiver disponivel e o valor nao apareceu, tenta pequenos
            // deslocamentos em torno do ponto calibrado. Isso cobre alteracoes de fonte
            // ou densidade sem sair clicando pela tela inteira.
            var retried = false;
            var retryOffsets = layout.UseUiFieldBounds
                ? new[] { 0 }
                : new[] { -34, 34, -58, 58 };
            foreach (var offsetY in retryOffsets)
            {
                var y = Math.Clamp(layout.NameY + offsetY, 1, sizeResult.Height - 1);
                var retry = await TapAndTypeTefFieldAsync(
                    serial,
                    layout.NameX,
                    y,
                    deviceName,
                    "Nome do dispositivo",
                    layout.UseUiFieldBounds,
                    cancellationToken);
                if (!retry.Success)
                {
                    continue;
                }

                var retrySnapshot = await ReadUiQuickAsync(serial, cancellationToken);
                if (!retrySnapshot.Success || ContainsLabel(retrySnapshot.Nodes, deviceName))
                {
                    retried = true;
                    break;
                }
            }

            if (!retried)
            {
                return new TefDirectResult(
                    false,
                    "tef-name",
                    "A tela Configurar Smart TEF foi aberta, mas o valor nao apareceu no campo Nome do dispositivo.",
                    $"layout={layout.Name}; display={sizeResult.Width}x{sizeResult.Height}; ponto={layout.NameX},{layout.NameY}");
            }
        }

        // Fecha o teclado somente se ele estiver realmente aberto. O ponto da opcao
        // manual foi calibrado com o teclado fechado para manter o layout previsivel.
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        await Task.Delay(250, cancellationToken);

        progress?.Invoke("tef-manual", "Abrindo Digitar dados manualmente...");
        if (!await IsExpectedSmartTefActivityAsync(serial, packageName, layout, cancellationToken))
        {
            return new TefDirectResult(false, "tef-activity", "A tela de configuracao TEF foi fechada antes de abrir os dados manuais.", string.Empty);
        }
        var manualPoint = await ResolveSmartTefActionPointAsync(
            serial,
            new[] { "digitar dados manualmente" },
            layout.ManualX,
            layout.ManualY,
            layout.UseUiFieldBounds,
            cancellationToken);
        var manualTap = await _adb.TapAsync(serial, manualPoint.X, manualPoint.Y, cancellationToken);
        if (!manualTap.Success)
        {
            return new TefDirectResult(false, "tef-manual", "Nao foi possivel acionar Digitar dados manualmente.", manualTap.CombinedOutput.Trim());
        }
        await Task.Delay(750, cancellationToken);

        if (!await NormalizeSmartTefManualScrollAsync(serial, layout, cancellationToken))
        {
            return new TefDirectResult(
                false,
                "tef-scroll",
                "A secao manual foi aberta, mas nao foi possivel posicionar os campos do Smart TEF.",
                $"layout={layout.Name}");
        }

        // Depois que a secao e expandida, usamos os limites reais publicados pelo
        // Android. Os pontos calibrados de cada perfil ficam como fallback quando um
        // POS nao disponibiliza temporariamente a arvore de acessibilidade.
        progress?.Invoke("tef-cnpj", "Informando CNPJ do Smart TEF...");
        await NormalizeSmartTefManualScrollAsync(serial, layout, cancellationToken);
        if (!await IsExpectedSmartTefActivityAsync(serial, packageName, layout, cancellationToken))
        {
            return new TefDirectResult(false, "tef-activity", "A tela de configuracao TEF foi fechada antes do preenchimento do CNPJ.", string.Empty);
        }
        var cnpjFill = await TapAndTypeTefFieldAsync(
            serial,
            layout.CnpjX,
            layout.CnpjY,
            cnpj,
            "CNPJ",
            layout.UseUiFieldBounds,
            cancellationToken);
        if (!cnpjFill.Success)
        {
            return new TefDirectResult(false, "tef-cnpj", "Nao foi possivel preencher o CNPJ. " + cnpjFill.Message, cnpjFill.Summary);
        }
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);

        progress?.Invoke("tef-company", "Informando Empresa ID do Smart TEF...");
        await NormalizeSmartTefManualScrollAsync(serial, layout, cancellationToken);
        if (!await IsExpectedSmartTefActivityAsync(serial, packageName, layout, cancellationToken))
        {
            return new TefDirectResult(false, "tef-activity", "A tela de configuracao TEF foi fechada antes do preenchimento da Empresa ID.", string.Empty);
        }
        var companyFill = await TapAndTypeTefFieldAsync(
            serial,
            layout.CompanyX,
            layout.CompanyY,
            empresaId,
            "Empresa ID",
            layout.UseUiFieldBounds,
            cancellationToken);
        if (!companyFill.Success)
        {
            return new TefDirectResult(false, "tef-company", "Nao foi possivel preencher Empresa ID. " + companyFill.Message, companyFill.Summary);
        }
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);

        progress?.Invoke("tef-token", "Informando token do Smart TEF...");
        await NormalizeSmartTefManualScrollAsync(serial, layout, cancellationToken);
        if (!await IsExpectedSmartTefActivityAsync(serial, packageName, layout, cancellationToken))
        {
            return new TefDirectResult(false, "tef-activity", "A tela de configuracao TEF foi fechada antes do preenchimento do Token.", string.Empty);
        }
        var tokenFill = await TapAndTypeTefFieldAsync(
            serial,
            layout.TokenX,
            layout.TokenY,
            token,
            "Token",
            layout.UseUiFieldBounds,
            cancellationToken);
        if (!tokenFill.Success)
        {
            return new TefDirectResult(false, "tef-token", "Nao foi possivel preencher o Token. " + tokenFill.Message, tokenFill.Summary);
        }

        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        await Task.Delay(300, cancellationToken);

        progress?.Invoke("tef-submit", "Confirmando a configuracao do Smart TEF...");
        if (!await IsExpectedSmartTefActivityAsync(serial, packageName, layout, cancellationToken))
        {
            return new TefDirectResult(false, "tef-activity", "A tela de configuracao TEF foi fechada antes da confirmacao.", string.Empty);
        }
        var confirmTap = await _adb.TapAsync(serial, layout.ConfirmX, layout.ConfirmY, cancellationToken);
        if (!confirmTap.Success)
        {
            return new TefDirectResult(false, "tef-submit", "Nao foi possivel acionar Confirmar Configuracao.", confirmTap.CombinedOutput.Trim());
        }

        progress?.Invoke("tef-validating", "Aguardando a validacao das chaves do Smart TEF...");
        var concludeResult = await WaitAndConcludeSmartTefAsync(
            serial,
            packageName,
            layout,
            progress,
            cancellationToken);
        if (!concludeResult.Success)
        {
            return concludeResult;
        }

        return new TefDirectResult(
            true,
            "tef-concluded",
            string.Empty,
            $"layout={layout.Name}; display={sizeResult.Width}x{sizeResult.Height}; nome={layout.NameX},{layout.NameY}; manual={layout.ManualX},{layout.ManualY}; concluir={layout.ConcludeX},{layout.ConcludeY}");
    }

    private async Task<TefDirectResult> WaitAndConcludeSmartTefAsync(
        string serial,
        string packageName,
        SmartTefLayout layout,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        // Depois de Confirmar Configuracao o RedeFlex mostra primeiro o modal
        // "Validando configuracao" e, quando as chaves sao aceitas, o modal
        // "Chaves verificadas com sucesso!" com o botao Concluir.
        // Em alguns POS o Compose nao disponibiliza root node ao UIAutomator;
        // por isso tentamos a arvore quando ela existir e usamos o ponto calibrado
        // de Concluir como fallback sem enviar KEYCODE_BACK.
        var coordinateAttempts = 0;
        UiSnapshot lastSnapshot = new(false, Array.Empty<UiNode>(), string.Empty);

        // No Positivo L400 ja conhecemos a posicao exata do botao Concluir e o
        // UiTestAutomationBridge costuma ficar indisponivel justamente durante os
        // modais de validacao. Nesse perfil, nao esperamos varios dumps: aguardamos
        // a validacao e fazemos toques periodicos somente na area do Concluir. Enquanto
        // o modal "Validando configuracao" estiver aberto, o toque nao executa acao;
        // assim que o modal de sucesso aparecer, o proximo toque conclui o fluxo.
        if (layout.UseCoordinateConclusion)
        {
            await Task.Delay(2600, cancellationToken);
            for (var attempt = 1; attempt <= 7; attempt++)
            {
                if (layout.RequiresActivityGuard)
                {
                    var beforeTapActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
                    if (IsSmartTefMainActivity(beforeTapActivity))
                    {
                        return new TefDirectResult(true, "tef-concluded", string.Empty, beforeTapActivity);
                    }
                    if (!IsExpectedSmartTefActivity(beforeTapActivity, packageName))
                    {
                        return new TefDirectResult(
                            false,
                            "tef-conclude",
                            "A tela Smart TEF foi fechada antes da conclusao. Activity atual: " +
                            (string.IsNullOrWhiteSpace(beforeTapActivity) ? "nao identificada" : beforeTapActivity) + ".",
                            beforeTapActivity);
                    }
                }

                progress?.Invoke("tef-conclude", $"Aguardando validacao das chaves e Concluir... tentativa {attempt}/7.");
                var concludeX = layout.ConcludeX;
                var concludeY = layout.ConcludeY;
                var check = await ReadUiQuickAsync(serial, cancellationToken);
                if (check.Success)
                {
                    if (IsSmartTefLoginScreen(check.Nodes))
                    {
                        return new TefDirectResult(true, "tef-concluded", string.Empty, BuildSummary(check.Nodes));
                    }

                    if (IsSmartTefValidationFailure(check.Nodes))
                    {
                        return new TefDirectResult(
                            false,
                            "tef-validation",
                            "O Smart TEF retornou falha durante a validacao das chaves.",
                            BuildSummary(check.Nodes));
                    }

                    var conclude = FindByLabels(check.Nodes, new[] { "concluir" });
                    if (conclude is not null)
                    {
                        concludeX = conclude.CenterX;
                        concludeY = conclude.CenterY;
                        progress?.Invoke(
                            "tef-conclude",
                            "Chaves verificadas com sucesso. Acionando o botao Concluir localizado na tela...");
                    }
                    else if (IsSmartTefKeySuccess(check.Nodes))
                    {
                        progress?.Invoke(
                            "tef-conclude",
                            "Chaves verificadas com sucesso. Acionando Concluir pelo ponto calibrado do dispositivo...");
                    }
                    else
                    {
                        await Task.Delay(900, cancellationToken);
                        continue;
                    }
                }

                var tap = await _adb.TapAsync(serial, concludeX, concludeY, cancellationToken);
                if (!tap.Success)
                {
                    return new TefDirectResult(false, "tef-conclude", "Nao foi possivel tocar no botao Concluir do Smart TEF.", tap.CombinedOutput.Trim());
                }

                await Task.Delay(attempt == 7 ? 900 : 1200, cancellationToken);
                if (layout.RequiresActivityGuard)
                {
                    var afterTapActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
                    if (IsSmartTefMainActivity(afterTapActivity))
                    {
                        return new TefDirectResult(true, "tef-concluded", string.Empty, afterTapActivity);
                    }
                    if (!IsExpectedSmartTefActivity(afterTapActivity, packageName))
                    {
                        return new TefDirectResult(
                            false,
                            "tef-conclude",
                            "A configuracao nao chegou a tela operacional do Smart TEF. Activity atual: " +
                            (string.IsNullOrWhiteSpace(afterTapActivity) ? "nao identificada" : afterTapActivity) + ".",
                            afterTapActivity);
                    }
                }

                var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
                if (!string.Equals(foreground, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    return new TefDirectResult(
                        false,
                        "tef-conclude",
                        $"O Smart TEF deixou de ficar em primeiro plano durante a conclusao. Aplicativo atual: {foreground}.",
                        string.Empty);
                }

                // Se o clique ocorreu no botao real, uma nova iteracao confirma a
                // Activity final. Nao declaramos sucesso apenas porque o comando ADB
                // terminou sem erro.
            }

            return layout.RequiresActivityGuard
                ? new TefDirectResult(
                    false,
                    "tef-conclude",
                    "A validacao permaneceu na TefSetupActivity e nao confirmou a abertura da tela operacional.",
                    layout.Name)
                : new TefDirectResult(
                    true,
                    "tef-concluded-l400",
                    string.Empty,
                    $"Concluir acionado no perfil {layout.Name} em {layout.ConcludeX},{layout.ConcludeY}. A validacao visual final sera feita em seguida quando o Android disponibilizar a arvore.");
        }

        for (var attempt = 0; attempt < 18; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1600 : 900, cancellationToken);
            lastSnapshot = await ReadUiQuickAsync(serial, cancellationToken);

            if (lastSnapshot.Success)
            {
                if (IsSmartTefLoginScreen(lastSnapshot.Nodes))
                {
                    return new TefDirectResult(
                        true,
                        "tef-concluded",
                        string.Empty,
                        "A tela de login foi localizada sem necessidade de acionar Concluir novamente.");
                }

                var conclude = FindByLabels(lastSnapshot.Nodes, new[] { "concluir" });
                if (conclude is not null || IsSmartTefKeySuccess(lastSnapshot.Nodes))
                {
                    progress?.Invoke("tef-conclude", "Chaves verificadas com sucesso. Acionando Concluir...");
                    var tap = conclude is not null
                        ? await _adb.TapAsync(serial, conclude.CenterX, conclude.CenterY, cancellationToken)
                        : await _adb.TapAsync(serial, layout.ConcludeX, layout.ConcludeY, cancellationToken);

                    if (!tap.Success)
                    {
                        return new TefDirectResult(false, "tef-conclude", "As chaves foram validadas, mas nao foi possivel acionar Concluir.", tap.CombinedOutput.Trim());
                    }

                    await Task.Delay(900, cancellationToken);
                    return new TefDirectResult(true, "tef-concluded", string.Empty, BuildSummary(lastSnapshot.Nodes));
                }

                if (IsSmartTefValidationFailure(lastSnapshot.Nodes))
                {
                    return new TefDirectResult(
                        false,
                        "tef-validation",
                        "O Smart TEF retornou falha durante a validacao das chaves.",
                        BuildSummary(lastSnapshot.Nodes));
                }
            }

            // O L400 pode manter o UiTestAutomationBridge sem root mesmo com o modal
            // de sucesso visivel. A partir de alguns segundos tentamos o ponto exato do
            // botao Concluir. Se o modal ainda estiver em "Validando configuracao", o
            // toque cai sobre a sobreposicao e nao navega para tras nem altera campos.
            if (attempt >= 4 && attempt % 2 == 0 && coordinateAttempts < 5)
            {
                coordinateAttempts++;
                progress?.Invoke("tef-conclude", $"Aguardando Concluir no Smart TEF... tentativa {coordinateAttempts}/5.");
                var tap = await _adb.TapAsync(serial, layout.ConcludeX, layout.ConcludeY, cancellationToken);
                if (!tap.Success)
                {
                    return new TefDirectResult(false, "tef-conclude", "Nao foi possivel tocar no ponto de Concluir do Smart TEF.", tap.CombinedOutput.Trim());
                }

                await Task.Delay(700, cancellationToken);
                var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
                if (!string.Equals(foreground, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    return new TefDirectResult(
                        false,
                        "tef-conclude",
                        $"O Smart TEF deixou de ficar em primeiro plano durante a conclusao. Aplicativo atual: {foreground}.",
                        string.Empty);
                }

                var afterTap = await ReadUiQuickAsync(serial, cancellationToken);
                if (afterTap.Success && IsSmartTefLoginScreen(afterTap.Nodes))
                {
                    return new TefDirectResult(true, "tef-concluded", string.Empty, BuildSummary(afterTap.Nodes));
                }
            }
        }

        // Se a arvore nunca ficou disponivel, o ultimo toque calibrado pode ter
        // concluido corretamente. Nao declaramos falha falsa; o chamador ainda fara
        // uma validacao final best-effort da tela de login.
        if (!lastSnapshot.Success && coordinateAttempts > 0)
        {
            return new TefDirectResult(
                true,
                "tef-concluded-no-ui",
                string.Empty,
                $"Concluir acionado por coordenada apos a validacao. UIAutomator indisponivel: {lastSnapshot.Error}");
        }

        return new TefDirectResult(
            false,
            "tef-conclude",
            "A configuracao foi enviada, mas o Provisioner nao conseguiu confirmar nem acionar a tela Chaves verificadas com sucesso / Concluir.",
            lastSnapshot.Success ? BuildSummary(lastSnapshot.Nodes) : lastSnapshot.Error);
    }

    private static bool IsSmartTefKeySuccess(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "chaves verificadas com sucesso") ||
        ContainsLabel(nodes, "verificadas com sucesso");

    private static bool IsSmartTefValidationFailure(IEnumerable<UiNode> nodes)
    {
        var summary = BuildSummary(nodes).ToLowerInvariant();
        return summary.Contains("falha na configuracao") ||
               summary.Contains("falha na validacao") ||
               summary.Contains("erro ao validar") ||
               summary.Contains("chaves invalidas") ||
               summary.Contains("dados invalidos");
    }

    private async Task<FieldFillResult> TapAndTypeTefFieldAsync(
        string serial,
        int x,
        int y,
        string value,
        string fieldName,
        bool useUiFieldBounds,
        CancellationToken cancellationToken)
    {
        if (useUiFieldBounds)
        {
            var point = await ResolveSmartTefFieldPointAsync(
                serial,
                fieldName,
                x,
                y,
                cancellationToken);
            x = point.X;
            y = point.Y;
        }

        var tap = await _adb.TapAsync(serial, x, y, cancellationToken);
        if (!tap.Success)
        {
            return new FieldFillResult(false, "Nao foi possivel tocar no campo no Android.", tap.CombinedOutput.Trim());
        }

        await Task.Delay(220, cancellationToken);

        // Nao usamos mais "teclado visivel" como criterio para decidir se o campo foi
        // encontrado. Em maquinetas/POS o IME pode nao ser exibido apesar de o EditText
        // estar focado. Essa verificacao era o motivo de o Provisioner parar exatamente
        // em Nome do dispositivo.
        _ = await _adb.ClearFocusedTextAsync(serial, 64, cancellationToken);

        var input = await _adb.InputTextAsync(serial, value ?? string.Empty, cancellationToken);
        if (!input.Success)
        {
            return new FieldFillResult(
                false,
                string.IsNullOrWhiteSpace(input.CombinedOutput)
                    ? "O ADB nao confirmou o preenchimento."
                    : input.CombinedOutput.Trim(),
                $"campo={fieldName}; ponto={x},{y}");
        }

        await Task.Delay(220, cancellationToken);
        return new FieldFillResult(true, string.Empty, $"campo={fieldName}; ponto={x},{y}");
    }

    private async Task<(int X, int Y)> ResolveSmartTefFieldPointAsync(
        string serial,
        string fieldName,
        int fallbackX,
        int fallbackY,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadUiQuickAsync(serial, cancellationToken);
        if (!snapshot.Success)
        {
            return (fallbackX, fallbackY);
        }

        var labels = fieldName switch
        {
            "Nome do dispositivo" => new[] { "nome do dispositivo" },
            "CNPJ" => new[] { "cnpj" },
            "Empresa ID" => new[] { "empresa id" },
            "Token" => new[] { "token" },
            _ => new[] { fieldName }
        };
        var field = FindEditableBelowLabels(snapshot.Nodes, labels);
        if (field is null ||
            field.Right <= field.Left ||
            field.Bottom - field.Top < 24)
        {
            return (fallbackX, fallbackY);
        }

        return (field.CenterX, field.CenterY);
    }

    private async Task<(int X, int Y)> ResolveSmartTefActionPointAsync(
        string serial,
        IReadOnlyList<string> labels,
        int fallbackX,
        int fallbackY,
        bool useUiBounds,
        CancellationToken cancellationToken)
    {
        if (!useUiBounds)
        {
            return (fallbackX, fallbackY);
        }

        var snapshot = await ReadUiQuickAsync(serial, cancellationToken);
        if (!snapshot.Success)
        {
            return (fallbackX, fallbackY);
        }

        var action = FindByLabelsIncludingText(snapshot.Nodes, labels);
        if (action is null || action.Right <= action.Left || action.Bottom <= action.Top)
        {
            return (fallbackX, fallbackY);
        }

        return (action.CenterX, action.CenterY);
    }

    private static SmartTefLayout GetSmartTefLayout(int width, int height, string? provisioningProfile = null)
    {
        // O Mercado Pago N950 possui barra inferior propria e area util de 1320 px
        // apesar da resolucao fisica 720x1440. Estes pontos foram medidos na tela real
        // do TefSetupActivity. O botao de confirmar fica fixo acima da navegacao.
        if (IsMercadoPagoN950Profile(provisioningProfile) &&
            Math.Abs(width - 720) <= 24 && Math.Abs(height - 1440) <= 40)
        {
            return new SmartTefLayout(
                "Mercado Pago N950 720x1440",
                360, 534,
                360, 912,
                360, 695,
                360, 860,
                360, 1024,
                360, 1248,
                360, 930,
                true,
                360, 1080,
                360, 620,
                450,
                true,
                true,
                true);
        }

        // Perfil do Positivo L400 observado no teste real. O layout do Compose neste
        // aparelho nao coincide com a simples escala 1080x2400 -> 720x1600.
        if (Math.Abs(width - 720) <= 24 && Math.Abs(height - 1600) <= 40)
        {
            return new SmartTefLayout(
                "Positivo L400 720x1600",
                360, 527,
                360, 912,
                360, 873,
                360, 1037,
                360, 1201,
                360, 1432,
                360, 1066,
                true,
                360, 1280, 360, 800, 450,
                true,
                true,
                true);
        }

        // Layout de referencia extraido do XML do Smart TEF em 1080x2400.
        var scaler = new ReferenceScaler(width, height, 1080, 2400);
        var name = scaler.Scale(540, 642);
        var manual = scaler.Scale(540, 1129);
        var cnpj = scaler.Scale(540, 1315);
        var company = scaler.Scale(540, 1511);
        var token = scaler.Scale(540, 1707);
        var confirm = scaler.Scale(540, 2244);
        var conclude = scaler.Scale(540, 1692);

        return new SmartTefLayout(
            "Escala XML 1080x2400",
            name.X, name.Y,
            manual.X, manual.Y,
            cnpj.X, cnpj.Y,
            company.X, company.Y,
            token.X, token.Y,
            confirm.X, confirm.Y,
            conclude.X, conclude.Y,
            false,
            0, 0, 0, 0, 0,
            false,
            false,
            false);
    }

    private async Task<bool> NormalizeSmartTefManualScrollAsync(
        string serial,
        SmartTefLayout layout,
        CancellationToken cancellationToken)
    {
        if (!layout.RequiresManualScroll)
        {
            return true;
        }

        var swipe = await _adb.ShellAsync(
            serial,
            $"input swipe {layout.ScrollStartX} {layout.ScrollStartY} {layout.ScrollEndX} {layout.ScrollEndY} {layout.ScrollDurationMilliseconds}",
            cancellationToken,
            8000);
        if (!swipe.Success)
        {
            return false;
        }

        await Task.Delay(300, cancellationToken);
        return true;
    }

    private async Task<bool> IsExpectedSmartTefActivityAsync(
        string serial,
        string packageName,
        SmartTefLayout layout,
        CancellationToken cancellationToken)
    {
        if (!layout.RequiresActivityGuard)
        {
            return true;
        }

        var activity = await GetLegacySmartActivityAsync(serial, cancellationToken);
        return IsExpectedSmartTefActivity(activity, packageName);
    }

    private async Task<string> GetForegroundActivityAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var window = await _adb.ShellAsync(serial, "dumpsys window windows", cancellationToken, 10000);
        if (window.Success)
        {
            foreach (var pattern in new[]
            {
                @"mCurrentFocus\[[^\]]*\]=Window\{[^}]*\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)",
                @"mCurrentFocus=.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)",
                @"mFocusedApp=.*?ActivityRecord\{[^}]*\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)"
            })
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    window.StandardOutput ?? string.Empty,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }
        }

        var activities = await _adb.ShellAsync(serial, "dumpsys activity activities", cancellationToken, 10000);
        if (activities.Success)
        {
            foreach (var pattern in new[]
            {
                @"mResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)",
                @"ResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)",
                @"\* Hist #0: ActivityRecord\{[^}]*\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)"
            })
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    activities.StandardOutput ?? string.Empty,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }
        }

        return string.Empty;
    }

    private async Task<string> WaitForLegacySmartActivityAsync(
        string serial,
        int attempts,
        CancellationToken cancellationToken)
    {
        attempts = Math.Clamp(attempts, 1, 40);
        var last = string.Empty;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(350, cancellationToken);
            }

            last = await GetLegacySmartActivityAsync(serial, cancellationToken);
            if (IsLegacySmartPackageActivity(last))
            {
                return last;
            }
        }

        return last;
    }

    private async Task<string> WaitForLegacyActivityAsync(
        string serial,
        Func<string, bool> predicate,
        int attempts,
        CancellationToken cancellationToken)
    {
        attempts = Math.Clamp(attempts, 1, 40);
        var last = string.Empty;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(350, cancellationToken);
            }

            last = await GetLegacySmartActivityAsync(serial, cancellationToken);
            if (predicate(last))
            {
                return last;
            }
        }

        return last;
    }

    private async Task<string> GetLegacySmartActivityAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        // Nao usa a primeira linha ACTIVITY de `dumpsys activity top`: em terminais
        // kiosk (Mercado Pago/N950) esse dump mantem a task PARADA do Smart antes da
        // task HOME realmente resumida. Isso fazia a automacao tocar no launcher da
        // adquirente e parecer que o Smart abria e fechava.
        //
        // No Android 7 observado, dumpsys window pode reportar o launcher durante uma
        // transicao; por isso a Activity explicitamente RESUMIDA do ActivityManager
        // continua sendo a fonte primaria.
        var activities = await _adb.ShellAsync(serial, "dumpsys activity activities", cancellationToken, 10000);
        if (activities.Success)
        {
            foreach (var pattern in new[]
            {
                @"mResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)",
                @"ResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)"
            })
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    activities.StandardOutput ?? string.Empty,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }
        }

        return await GetForegroundActivityAsync(serial, cancellationToken);
    }

    private async Task<string> WaitForForegroundActivityAsync(
        string serial,
        Func<string, bool> predicate,
        int attempts,
        CancellationToken cancellationToken)
    {
        attempts = Math.Clamp(attempts, 1, 30);
        var last = string.Empty;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(350, cancellationToken);
            }

            last = await GetForegroundActivityAsync(serial, cancellationToken);
            if (predicate(last))
            {
                return last;
            }
        }

        return last;
    }

    private async Task<LegacyPermissionResult> EnsureLegacyStoragePermissionsAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken)
    {
        var packageInfo = await _adb.ShellAsync(
            serial,
            $"dumpsys package {packageName}",
            cancellationToken,
            12000);
        var packageOutput = packageInfo.CombinedOutput ?? string.Empty;
        var permissions = new[]
        {
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.WRITE_EXTERNAL_STORAGE"
        }
        .Where(permission => packageOutput.Contains(permission, StringComparison.OrdinalIgnoreCase))
        .ToArray();

        if (packageInfo.Success && permissions.Length == 0)
        {
            return new LegacyPermissionResult(
                true,
                "O package deste Smart 8.0 nao solicita permissoes legadas de armazenamento; nenhuma concessao ADB foi necessaria.");
        }

        var failures = new List<string>();
        foreach (var permission in permissions.Length > 0
                     ? permissions
                     : new[] { "android.permission.READ_EXTERNAL_STORAGE", "android.permission.WRITE_EXTERNAL_STORAGE" })
        {
            var grant = await _adb.ShellAsync(
                serial,
                $"pm grant {packageName} {permission}",
                cancellationToken,
                10000);

            if (!grant.Success)
            {
                var detail = string.IsNullOrWhiteSpace(grant.CombinedOutput)
                    ? "sem retorno do ADB"
                    : grant.CombinedOutput.Trim();
                failures.Add($"{permission}: {detail}");
            }
        }

        if (failures.Count == 0)
        {
            return new LegacyPermissionResult(true, string.Empty);
        }

        return new LegacyPermissionResult(false, string.Join(" | ", failures));
    }

    private async Task<string> WaitForLegacyCompanyActivityAsync(
        string serial,
        string packageName,
        int sdkLevel,
        int settingsX,
        int settingsY,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        var last = string.Empty;
        var permissionWasHandled = false;
        var settingsRetriedAfterPermission = false;
        var settingsRetriedWhileStillOnLogin = false;

        for (var attempt = 0; attempt < 24; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(350, cancellationToken);
            }

            // Primeiro verifica a Activity REAL em primeiro plano. GetLegacySmartActivityAsync
            // filtra pelo package do Smart e, com uma janela de permissao por cima, pode continuar
            // devolvendo LoginActivity e esconder o PermissionController.
            var actualForeground = await GetForegroundActivityAsync(serial, cancellationToken);
            if (IsLegacyCompanyActivity(actualForeground))
            {
                return actualForeground;
            }

            var permissionDetected = IsRuntimePermissionActivity(actualForeground);

            // Em Android moderno o dialogo de permissao pode ser uma janela sobre a LoginActivity
            // sem que dumpsys activity troque de Activity. Nessa plataforma o UIAutomator da tela
            // de login e estavel, entao podemos procurar explicitamente o botao Allow/Permitir.
            UiSnapshot? permissionUi = null;
            UiNode? allowButton = null;
            if (sdkLevel > 25 && !permissionDetected)
            {
                permissionUi = await ReadUiQuickAsync(serial, cancellationToken);
                if (permissionUi.Success)
                {
                    allowButton = FindByLabels(permissionUi.Nodes, new[]
                    {
                        "permitir",
                        "allow",
                        "enquanto usa o app",
                        "while using the app",
                        "permitir acesso",
                        "allow access"
                    });
                    permissionDetected = allowButton is not null;
                }
            }

            if (permissionDetected)
            {
                last = actualForeground;
                progress?.Invoke(
                    "legacy-permission-dialog",
                    "O Android exibiu uma permissao sobre o Smart. Aceitando a solicitacao antes de abrir Configuracoes novamente...");

                var grant = await EnsureLegacyStoragePermissionsAsync(serial, packageName, cancellationToken);
                if (!grant.Success)
                {
                    progress?.Invoke(
                        "legacy-permission-dialog",
                        "O pm grant nao resolveu completamente a solicitacao. Acionando o botao Permitir/Allow da janela do Android...");
                }

                if (allowButton is null)
                {
                    permissionUi ??= await ReadUiQuickAsync(serial, cancellationToken);
                    if (permissionUi.Success)
                    {
                        allowButton = FindByLabels(permissionUi.Nodes, new[]
                        {
                            "permitir",
                            "allow",
                            "enquanto usa o app",
                            "while using the app",
                            "permitir acesso",
                            "allow access"
                        });
                    }
                }

                if (allowButton is not null)
                {
                    progress?.Invoke(
                        "legacy-permission-dialog",
                        $"Acionando a permissao do Android em {allowButton.CenterX},{allowButton.CenterY}...");
                    await _adb.TapAsync(serial, allowButton.CenterX, allowButton.CenterY, cancellationToken);
                }

                permissionWasHandled = true;
                await Task.Delay(800, cancellationToken);
                continue;
            }

            var smartActivity = await GetLegacySmartActivityAsync(serial, cancellationToken);
            if (IsLegacyCompanyActivity(smartActivity))
            {
                return smartActivity;
            }

            // O primeiro toque na engrenagem do Smart 8.0 pode servir apenas para disparar
            // a permissao de fotos/midia. Depois que o usuario/automacao aceita, o app volta
            // para LoginActivity e exige um SEGUNDO toque na engrenagem. Fazemos isso uma unica
            // vez e somente apos confirmar que a permissao foi tratada.
            if (permissionWasHandled &&
                !settingsRetriedAfterPermission &&
                (IsLegacyLoginActivity(actualForeground) || IsLegacyLoginActivity(smartActivity)))
            {
                progress?.Invoke(
                    "legacy-settings-retap",
                    $"Permissao concluida e Smart retornou para LoginActivity. Acionando Configuracoes novamente em {settingsX},{settingsY}...");

                var retryTap = await _adb.TapAsync(serial, settingsX, settingsY, cancellationToken);
                if (!retryTap.Success)
                {
                    return actualForeground;
                }

                settingsRetriedAfterPermission = true;
                await Task.Delay(800, cancellationToken);
                continue;
            }

            // Alguns terminais nao disponibilizam a hierarquia no primeiro instante
            // apos o launch. Nessa situacao o toque inicial pode ter usado apenas o
            // ponto proporcional e ficar acima do botao (caso observado na Stone
            // L400). Se o Smart continuar inequivocamente na LoginActivity, relê o
            // retangulo real e repete o toque uma unica vez.
            if (!permissionWasHandled &&
                !settingsRetriedWhileStillOnLogin &&
                attempt >= 3 &&
                (IsLegacyLoginActivity(actualForeground) || IsLegacyLoginActivity(smartActivity)))
            {
                var currentSettingsBounds = await FindViewBoundsFromActivityDumpAsync(
                    serial,
                    "app:id/btn_config",
                    cancellationToken);
                var retryX = currentSettingsBounds.Success ? currentSettingsBounds.CenterX : settingsX;
                var retryY = currentSettingsBounds.Success ? currentSettingsBounds.CenterY : settingsY;
                var retrySource = currentSettingsBounds.Success
                    ? "dumpsys activity top (app:id/btn_config)"
                    : "ponto calculado anteriormente";

                progress?.Invoke(
                    "legacy-settings-retap",
                    $"Smart ainda na LoginActivity. Repetindo Configuracoes uma unica vez em {retryX},{retryY} via {retrySource}...");

                var retryTap = await _adb.TapAsync(serial, retryX, retryY, cancellationToken);
                if (!retryTap.Success)
                {
                    return actualForeground;
                }

                settingsRetriedWhileStillOnLogin = true;
                await Task.Delay(800, cancellationToken);
                continue;
            }

            last = !string.IsNullOrWhiteSpace(actualForeground)
                ? actualForeground
                : smartActivity;
        }

        return last;
    }

    private static bool IsRuntimePermissionActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        (activity.Contains("packageinstaller", StringComparison.OrdinalIgnoreCase) ||
         activity.Contains("permissioncontroller", StringComparison.OrdinalIgnoreCase) ||
         activity.Contains("grantpermissions", StringComparison.OrdinalIgnoreCase));

    private sealed record LegacyPermissionResult(bool Success, string Detail);

    private static bool IsLegacySmartPackageActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        activity.Contains('/', StringComparison.Ordinal) &&
        activity[..activity.IndexOf('/', StringComparison.Ordinal)]
            .StartsWith("softcom.mobile.smart", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyLoginActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        IsLegacySmartPackageActivity(activity) &&
        activity.EndsWith("softcom.mobile.smart.views.activities.login.LoginActivity", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyCompanyActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        IsLegacySmartPackageActivity(activity) &&
        activity.EndsWith("softcom.mobile.smart.views.activities.empresa.EmpresaActivity", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyCompanyAddConfigActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        IsLegacySmartPackageActivity(activity) &&
        activity.EndsWith("softcom.mobile.smart.views.activities.EmpresaAddConfigActivity", StringComparison.OrdinalIgnoreCase);

    private static bool IsSmartTefSetupActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        IsLegacySmartPackageActivity(activity) &&
        activity.EndsWith("softcom.mobile.smart.tef.ui.TefSetupActivity", StringComparison.OrdinalIgnoreCase);

    private static bool IsSmartTefMainActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        IsLegacySmartPackageActivity(activity) &&
        activity.EndsWith("softcom.mobile.smart.tef.ui.TefActivity", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyCompanyAddActivity(string activity) =>
        !string.IsNullOrWhiteSpace(activity) &&
        IsLegacySmartPackageActivity(activity) &&
        activity.EndsWith("softcom.mobile.smart.views.activities.device.EmpresaAddActivity", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacy80LargeSelfServiceModule(string? module) =>
        string.Equals(module, "smart_totem", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "smart_autopagamento", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseSmart81K2OnboardingFlow(
        string? smartFlow,
        int sdkLevel,
        string? module,
        string? provisioningProfile,
        string? packageName,
        bool clearData)
    {
        if (!clearData ||
            !string.Equals(smartFlow, "Smart 8.1+", StringComparison.OrdinalIgnoreCase) ||
            sdkLevel > 25 ||
            !IsLegacy80LargeSelfServiceModule(module))
        {
            return false;
        }

        // O catalogo e a fonte primaria. O package .redeflex e mantido como fallback
        // porque algumas instalacoes antigas do catalogo ainda nao possuem TOTEM_K2_UDID.
        return string.Equals(provisioningProfile?.Trim(), "totemk2", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(packageName) &&
                packageName.Contains("redeflex", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSmart81AuthActivity(string? activity, string packageName) =>
        !string.IsNullOrWhiteSpace(activity) &&
        activity.Contains(packageName + "/", StringComparison.OrdinalIgnoreCase) &&
        activity.EndsWith(
            "softcom.mobile.smart.views.activities.loginnew.AuthActivity",
            StringComparison.OrdinalIgnoreCase);

    private static int GetSmart81K2ModuleReferenceY(string? module) =>
        (module ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "smart_pdv" => 239,
            "smart_comanda" => 335,
            "smart_pre_venda" => 431,
            "smart_tef" => 527,
            "smart_minimercado" => 623,
            "smart_totem" => 719,
            "smart_autopagamento" => 815,
            _ => 0
        };

    private static bool ShouldPreserveLegacy80Keyboard(string? provisioningProfile) =>
        IsMercadoPagoN950Profile(provisioningProfile);

    private static bool IsMercadoPagoN950Profile(string? provisioningProfile) =>
        string.Equals(
            provisioningProfile?.Trim(),
            "mercadopagon950",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsActivityBasedSmartTefPackage(string? packageName) =>
        !string.IsNullOrWhiteSpace(packageName) &&
        packageName.Trim().StartsWith("softcom.mobile.smart", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseActivityBasedSmartTefNavigation(
        string? packageName,
        string? smartFlow) =>
        IsActivityBasedSmartTefPackage(packageName) &&
        !string.Equals(smartFlow, "Smart 8.1+", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedSmartTefActivity(string? activity, string packageName) =>
        !string.IsNullOrWhiteSpace(activity) &&
        (IsSmartTefSetupActivity(activity) || IsSmart81AuthActivity(activity, packageName));

    private static string GetLegacy80ModuleResourceId(string module) =>
        (module ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "smart_pdv" => "app:id/swt_config_modo_pdv",
            "smart_comanda" => "app:id/swt_config_modo_comanda",
            "smart_pre_venda" => "app:id/swt_config_modo_pre_venda",
            "smart_totem" => "app:id/swt_config_modo_autoatendimento",
            "smart_minimercado" => "app:id/swt_config_modo_selfcheckout",
            "smart_autopagamento" => "app:id/swt_config_modo_autopag",
            _ => string.Empty
        };

    private static double GetLegacy80MobileModuleReferenceY(string module) =>
        (module ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "smart_pdv" => 91d,
            "smart_comanda" => 135d,
            "smart_pre_venda" => 178d,
            "smart_totem" => 221d,
            "smart_minimercado" => 264d,
            "smart_autopagamento" => 311d,
            "smart_tef" => 404d,
            _ => 91d
        };

    private async Task<int> GetAndroidSdkLevelAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var result = await _adb.ShellAsync(
            serial,
            "getprop ro.build.version.sdk",
            cancellationToken,
            8000);

        if (!result.Success)
        {
            return 0;
        }

        return int.TryParse((result.StandardOutput ?? string.Empty).Trim(), out var sdk)
            ? sdk
            : 0;
    }

    private async Task<DisplaySizeResult> GetAndroidDisplaySizeAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var result = await _adb.ShellAsync(serial, "wm size", cancellationToken, 8000);
        if (!result.Success)
        {
            return new DisplaySizeResult(false, 0, 0, "Nao foi possivel consultar a resolucao do Android.");
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(
            result.StandardOutput ?? string.Empty,
            @"(\d+)x(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (matches.Count == 0 ||
            !int.TryParse(matches[matches.Count - 1].Groups[1].Value, out var width) ||
            !int.TryParse(matches[matches.Count - 1].Groups[2].Value, out var height) ||
            width <= 0 || height <= 0)
        {
            return new DisplaySizeResult(false, 0, 0, "O Android retornou uma resolucao invalida: " + (result.StandardOutput?.Trim() ?? string.Empty));
        }

        return new DisplaySizeResult(true, width, height, string.Empty);
    }

    private async Task<ActivityViewBoundsResult> FindViewBoundsFromActivityDumpAsync(
        string serial,
        string resourceId,
        CancellationToken cancellationToken)
    {
        ActivityViewBoundsResult? lastFailure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(180, cancellationToken);
            }

            var result = await _adb.ShellAsync(
                serial,
                "dumpsys activity top",
                cancellationToken,
                12000);

            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                lastFailure = new ActivityViewBoundsResult(
                    false,
                    0,
                    0,
                    0,
                    0,
                    "O Android nao disponibilizou a hierarquia da Activity atual.");
                continue;
            }

            var parsed = ParseActivityViewBounds(result.StandardOutput, resourceId);
            if (parsed.Success)
            {
                return parsed;
            }

            lastFailure = parsed;
        }

        return lastFailure ?? new ActivityViewBoundsResult(
            false,
            0,
            0,
            0,
            0,
            $"O resource-id {resourceId} nao foi localizado na Activity atual.");
    }

    private static ActivityViewBoundsResult ParseActivityViewBounds(string dump, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(dump) || string.IsNullOrWhiteSpace(resourceId))
        {
            return new ActivityViewBoundsResult(false, 0, 0, 0, 0, "Hierarquia ou resource-id nao informado.");
        }

        // O formato de View.toString() usado pelo dumpsys informa as coordenadas
        // relativas ao pai: `classe{estado left,top-right,bottom ... resource-id}`.
        // A pilha por indentacao converte o retangulo para coordenadas fisicas da tela.
        var parents = new Stack<ActivityDumpView>();
        var inViewHierarchy = false;
        ActivityViewBoundsResult? lastMatch = null;
        ActivityViewBoundsResult? directMatch = null;
        var viewPattern = new Regex(
            @"^(?<indent>\s*)(?<type>[A-Za-z0-9_.$]+)\{.*\s(?<left>-?\d+),(?<top>-?\d+)-(?<right>-?\d+),(?<bottom>-?\d+)(?:\s[^}]*)?\}\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (var rawLine in dump.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Contains("View Hierarchy:", StringComparison.OrdinalIgnoreCase))
            {
                inViewHierarchy = true;
                parents.Clear();
                continue;
            }

            var match = viewPattern.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["left"].Value, out var left) ||
                !int.TryParse(match.Groups["top"].Value, out var top) ||
                !int.TryParse(match.Groups["right"].Value, out var right) ||
                !int.TryParse(match.Groups["bottom"].Value, out var bottom))
            {
                continue;
            }

            // Mantem uma alternativa baseada no proprio retangulo da View. Ela cobre
            // fabricantes cujo dumpsys omite o cabecalho "View Hierarchy" ou corta
            // parte dos pais, sem voltar silenciosamente para coordenadas de outro
            // aparelho.
            if (rawLine.Contains(resourceId, StringComparison.OrdinalIgnoreCase) &&
                right > left && bottom > top)
            {
                directMatch = new ActivityViewBoundsResult(
                    true,
                    left,
                    top,
                    right,
                    bottom,
                    "Retangulo relativo retornado sem todos os pais da hierarquia.");
            }

            if (!inViewHierarchy)
            {
                continue;
            }

            var indent = match.Groups["indent"].Value.Length;
            while (parents.Count > 0 && parents.Peek().Indent >= indent)
            {
                parents.Pop();
            }

            var parentLeft = parents.Count > 0 ? parents.Peek().AbsoluteLeft : 0;
            var parentTop = parents.Count > 0 ? parents.Peek().AbsoluteTop : 0;
            var current = new ActivityDumpView(
                indent,
                parentLeft + left,
                parentTop + top,
                parentLeft + right,
                parentTop + bottom);

            if (rawLine.Contains(resourceId, StringComparison.OrdinalIgnoreCase))
            {
                if (current.AbsoluteRight <= current.AbsoluteLeft ||
                    current.AbsoluteBottom <= current.AbsoluteTop)
                {
                    continue;
                }

                // `dumpsys activity top` pode incluir tasks antigas antes da Activity
                // retomada. Conservamos a ultima ocorrencia valida, que corresponde ao
                // topo exibido nas versoes Android observadas.
                lastMatch = new ActivityViewBoundsResult(
                    true,
                    current.AbsoluteLeft,
                    current.AbsoluteTop,
                    current.AbsoluteRight,
                    current.AbsoluteBottom,
                    string.Empty);
            }

            parents.Push(current);
        }

        return lastMatch ?? directMatch ?? new ActivityViewBoundsResult(
            false,
            0,
            0,
            0,
            0,
            $"O resource-id {resourceId} nao foi localizado na Activity atual.");
    }

    private static bool IsActivityPointInsideDisplay(
        ActivityViewBoundsResult bounds,
        DisplaySizeResult display) =>
        bounds.Success &&
        bounds.CenterX > 0 && bounds.CenterX < display.Width &&
        bounds.CenterY > 0 && bounds.CenterY < display.Height;

    private async Task<UiSnapshot> ReadUiQuickAsync(
        string serial,
        CancellationToken cancellationToken,
        int dumpTimeoutMilliseconds = 2500)
    {
        var result = await _adb.DumpUiHierarchyAsync(
            serial,
            cancellationToken,
            maxAttempts: 1,
            dumpTimeoutMilliseconds: dumpTimeoutMilliseconds);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new UiSnapshot(
                false,
                Array.Empty<UiNode>(),
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? "O Android nao disponibilizou a arvore de acessibilidade."
                    : result.CombinedOutput.Trim());
        }

        try
        {
            var xmlStart = result.StandardOutput.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
            var xml = xmlStart >= 0 ? result.StandardOutput[xmlStart..] : result.StandardOutput;
            var document = XDocument.Parse(xml);
            var nodes = document
                .Descendants("node")
                .Select(ParseNode)
                .Where(x => x is not null)
                .Cast<UiNode>()
                .ToArray();
            return new UiSnapshot(true, nodes, string.Empty);
        }
        catch (Exception ex)
        {
            return new UiSnapshot(false, Array.Empty<UiNode>(), "Falha ao interpretar a tela Android: " + ex.Message);
        }
    }

    private async Task<UiSnapshot> WaitForUiAsync(
        string serial,
        Func<IEnumerable<UiNode>, bool> predicate,
        int maxAttempts,
        int delayMilliseconds,
        CancellationToken cancellationToken,
        bool stopOnlyWhenPredicateChangesFromInitial = false)
    {
        UiSnapshot last = new(false, Array.Empty<UiNode>(), "A tela Android nao respondeu.");
        bool? initialState = null;

        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(Math.Max(100, delayMilliseconds), cancellationToken);
            }

            last = await ReadUiAsync(serial, cancellationToken);
            if (!last.Success)
            {
                continue;
            }

            var currentState = predicate(last.Nodes);
            initialState ??= currentState;

            if (!stopOnlyWhenPredicateChangesFromInitial && currentState)
            {
                return last;
            }

            if (stopOnlyWhenPredicateChangesFromInitial && currentState != initialState.Value)
            {
                return last;
            }

            if (stopOnlyWhenPredicateChangesFromInitial && IsSmartTefLoginScreen(last.Nodes))
            {
                return last;
            }
        }

        return last;
    }

    private static bool IsSmartTefConfigurationScreen(IEnumerable<UiNode> nodes)
    {
        var list = nodes.ToArray();

        // Estados conhecidos da tela de configuracao do RedeFlex:
        // - recolhida: Nome do dispositivo + Digitar dados manualmente
        // - expandida: CNPJ + Empresa ID + Token
        // O titulo pode nao aparecer em todos os dumps de Compose em aparelhos fisicos.
        var hasTefContext = ContainsLabel(list, "smart tef") || ContainsLabel(list, "modulo smart tef");
        var hasName = ContainsLabel(list, "nome do dispositivo");
        var hasManual = ContainsLabel(list, "digitar dados manualmente");
        var hasExpanded = ContainsLabel(list, "cnpj") &&
                          ContainsLabel(list, "empresa id") &&
                          ContainsLabel(list, "token");
        var hasEditable = list.Any(x => x.Enabled && x.ClassName.Contains("EditText", StringComparison.OrdinalIgnoreCase));

        return (hasName && hasEditable) ||
               (hasManual && hasEditable) ||
               (hasTefContext && (hasName || hasManual || hasExpanded)) ||
               hasExpanded;
    }

    private static bool HasExpandedTefFields(IEnumerable<UiNode> nodes)
    {
        var list = nodes.ToArray();
        return ContainsLabel(list, "cnpj") &&
               ContainsLabel(list, "empresa id") &&
               ContainsLabel(list, "token") &&
               list.Count(x => x.Enabled && x.ClassName.Contains("EditText", StringComparison.OrdinalIgnoreCase)) >= 3;
    }

    private static bool IsSmartTefLoginScreen(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "seja bem vindo") &&
        ContainsLabel(nodes, "faca login para acessar o smart") &&
        ContainsLabel(nodes, "senha") &&
        ContainsLabel(nodes, "login");

    private async Task<string> ResolveTefPackageAsync(
        string serial,
        string? configuredPackage,
        CancellationToken cancellationToken)
    {
        const string tefPackage = "softcom.mobile.smart2.redeflex";
        if (await _adb.IsPackageInstalledAsync(serial, tefPackage, cancellationToken))
        {
            return tefPackage;
        }

        // Algumas adquirentes, como a Cielo DX8000 validada, entregam o modulo
        // Smart TEF dentro do APK principal (softcom.mobile.smart). Nesse caso o
        // package detectado para aquele UDID e a fonte correta.
        if (!string.IsNullOrWhiteSpace(configuredPackage) &&
            configuredPackage.StartsWith("softcom.mobile.smart", StringComparison.OrdinalIgnoreCase) &&
            await _adb.IsPackageInstalledAsync(serial, configuredPackage, cancellationToken))
        {
            return configuredPackage.Trim();
        }

        var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
        if (!string.IsNullOrWhiteSpace(foreground) &&
            foreground.StartsWith("softcom.mobile.smart", StringComparison.OrdinalIgnoreCase) &&
            await _adb.IsPackageInstalledAsync(serial, foreground, cancellationToken))
        {
            return foreground;
        }

        var detection = await _adb.DetectSmartPackageAsync(serial, cancellationToken);
        var fallback = detection.Candidates.FirstOrDefault(x =>
            x.StartsWith("softcom.mobile.smart", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(fallback) &&
            await _adb.IsPackageInstalledAsync(serial, fallback, cancellationToken))
        {
            return fallback;
        }

        throw new InvalidOperationException(
            "Nenhum package Softcom Smart com suporte ao modulo TEF foi localizado neste Android.");
    }

    private async Task<FieldFillResult> FillFieldAsync(
        string serial,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> hints,
        string value,
        CancellationToken cancellationToken,
        bool fallbackToFirstEditable = false)
    {
        var snapshot = await ReadUiAsync(serial, cancellationToken);
        if (!snapshot.Success)
        {
            return new FieldFillResult(false, snapshot.Error, string.Empty);
        }

        var field = FindEditableByHints(snapshot.Nodes, hints) ??
                    FindEditableBelowLabels(snapshot.Nodes, labels) ??
                    (fallbackToFirstEditable ? FindEditable(snapshot.Nodes) : null);
        if (field is null)
        {
            return new FieldFillResult(
                false,
                $"Nao foi possivel localizar o campo {string.Join(" / ", labels)} na tela do Smart TEF.",
                BuildSummary(snapshot.Nodes));
        }

        await _adb.TapAsync(serial, field.CenterX, field.CenterY, cancellationToken);
        await Task.Delay(220, cancellationToken);

        // Se o campo ja contem exatamente o valor desejado, nao tenta limpar nem redigitar.
        // Isso evita falhas desnecessarias ao reaproveitar uma configuracao parcial.
        if (string.Equals(field.Text?.Trim(), value?.Trim(), StringComparison.Ordinal))
        {
            await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
            await Task.Delay(180, cancellationToken);
            return new FieldFillResult(true, string.Empty, BuildSummary(snapshot.Nodes));
        }

        // No Compose, quando o campo esta vazio o hint aparece como TextView filho,
        // mas o EditText continua com Text vazio. Nao tentamos limpar nesse caso.
        // Quando ha valor anterior, usamos keyevents simples em lotes, evitando o
        // comando `while` que falhou em alguns Androids/aparelhos fisicos.
        if (!string.IsNullOrWhiteSpace(field.Text))
        {
            var clear = await _adb.ClearFocusedTextAsync(
                serial,
                Math.Max(64, Math.Min(128, field.Text.Length + 16)),
                cancellationToken);
            if (!clear.Success)
            {
                var detail = string.IsNullOrWhiteSpace(clear.CombinedOutput)
                    ? "O Android nao confirmou a limpeza do campo."
                    : clear.CombinedOutput.Trim();
                return new FieldFillResult(
                    false,
                    "Nao foi possivel limpar o campo antes do preenchimento. " + detail,
                    BuildSummary(snapshot.Nodes));
            }
        }

        var input = await _adb.InputTextAsync(serial, value ?? string.Empty, cancellationToken);
        if (!input.Success)
        {
            return new FieldFillResult(
                false,
                string.IsNullOrWhiteSpace(input.CombinedOutput)
                    ? "Nao foi possivel preencher o campo pelo ADB."
                    : input.CombinedOutput.Trim(),
                BuildSummary(snapshot.Nodes));
        }

        await Task.Delay(180, cancellationToken);
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        await Task.Delay(250, cancellationToken);
        return new FieldFillResult(true, string.Empty, BuildSummary(snapshot.Nodes));
    }

    private async Task<string> ResolvePackageAsync(
        string serial,
        string? configuredPackage,
        CancellationToken cancellationToken)
    {
        var configuredPackageInstalled = !string.IsNullOrWhiteSpace(configuredPackage) &&
                                         await _adb.IsPackageInstalledAsync(
                                             serial,
                                             configuredPackage,
                                             cancellationToken);

        var detection = await _adb.DetectSmartPackageAsync(serial, cancellationToken);
        return SelectProvisioningPackage(configuredPackage, configuredPackageInstalled, detection);
    }

    private static string SelectProvisioningPackage(
        string? configuredPackage,
        bool configuredPackageInstalled,
        SmartPackageDetection detection)
    {
        // O mesmo identificador .redeflex e usado tanto pelo fluxo TEF quanto por builds
        // normais do Smart 8.1 em alguns terminais (por exemplo, o K2). A decisao do fluxo
        // continua sendo feita pelo modulo selecionado; o sufixo do package nao o torna TEF.
        if (configuredPackageInstalled && !string.IsNullOrWhiteSpace(configuredPackage))
            return configuredPackage.Trim();

        if (!string.IsNullOrWhiteSpace(detection.PackageName))
            return detection.PackageName.Trim();

        var candidates = detection.Candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(AdbService.SmartPackageScore)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                "Nenhum package Android contendo 'softcom' ou 'smart' foi localizado. Configure o package do Smart em Configuracoes.");
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        throw new InvalidOperationException(
            "Foram encontrados varios packages possiveis para o Smart. Configure o package correto em Configuracoes. Encontrados: " +
            string.Join(", ", candidates));
    }

    private async Task<UiSnapshot> ReadUiAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await _adb.DumpUiHierarchyAsync(serial, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new UiSnapshot(
                false,
                Array.Empty<UiNode>(),
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? "Nao foi possivel ler os componentes da tela Android."
                    : result.CombinedOutput.Trim());
        }

        try
        {
            var xmlStart = result.StandardOutput.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
            var xml = xmlStart >= 0 ? result.StandardOutput[xmlStart..] : result.StandardOutput;
            var document = XDocument.Parse(xml);
            var nodes = document
                .Descendants("node")
                .Select(ParseNode)
                .Where(x => x is not null)
                .Cast<UiNode>()
                .ToArray();

            return new UiSnapshot(true, nodes, string.Empty);
        }
        catch (Exception ex)
        {
            return new UiSnapshot(false, Array.Empty<UiNode>(), "Falha ao interpretar a tela Android: " + ex.Message);
        }
    }

    private static UiNode? ParseNode(XElement element)
    {
        var bounds = element.Attribute("bounds")?.Value ?? string.Empty;
        if (!TryParseBounds(bounds, out var left, out var top, out var right, out var bottom))
        {
            return null;
        }

        var searchText = string.Join(" ", element
            .DescendantsAndSelf("node")
            .SelectMany(x => new[]
            {
                x.Attribute("text")?.Value ?? string.Empty,
                x.Attribute("content-desc")?.Value ?? string.Empty
            })
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        return new UiNode(
            element.Attribute("text")?.Value ?? string.Empty,
            element.Attribute("content-desc")?.Value ?? string.Empty,
            element.Attribute("resource-id")?.Value ?? string.Empty,
            element.Attribute("class")?.Value ?? string.Empty,
            element.Attribute("package")?.Value ?? string.Empty,
            searchText,
            element.Attribute("clickable")?.Value == "true",
            element.Attribute("enabled")?.Value != "false",
            left,
            top,
            right,
            bottom);
    }

    private static bool IsUiFromPackage(IEnumerable<UiNode> nodes, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return false;
        return nodes.Any(x =>
            !string.IsNullOrWhiteSpace(x.PackageName) &&
            x.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLegacySmartLoginScreen(IEnumerable<UiNode> nodes)
    {
        var list = nodes.ToArray();
        var hasLogin = FindByLabels(list, new[] { "login" }) is not null || ContainsLabel(list, "login");
        var hasCompany = ContainsLabel(list, "empresa");
        var hasPassword = ContainsLabel(list, "senha");
        var hasUser = ContainsLabel(list, "email ou nome") || ContainsLabel(list, "email") || ContainsLabel(list, "nome");

        return hasLogin && hasCompany && hasPassword && hasUser;
    }

    private static bool IsLegacySmartConfigurationScreen(IEnumerable<UiNode> nodes)
    {
        var list = nodes.ToArray();
        if (IsLegacySmartLoginScreen(list))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ExtractSmartDeviceId(list)) ||
               FindEditable(list) is not null ||
               ContainsLabel(list, "digite a url") ||
               ContainsLabel(list, "configurar smart") ||
               ContainsLabel(list, "device id");
    }

    private static UiNode? FindLegacySettingsButton(IEnumerable<UiNode> nodes)
    {
        var list = nodes.Where(x => x.Enabled).ToArray();

        var labeled = FindByLabels(
            list,
            new[] { "configuracoes", "configuracao", "ajustes", "settings", "engrenagem" });
        if (labeled is not null)
        {
            return labeled;
        }

        var byResource = list
            .Where(x => x.Clickable ||
                        x.ClassName.Contains("Button", StringComparison.OrdinalIgnoreCase) ||
                        x.ClassName.Contains("Image", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x =>
            {
                var resource = Normalize(x.ResourceId);
                return resource.Contains("config", StringComparison.OrdinalIgnoreCase) ||
                       resource.Contains("setting", StringComparison.OrdinalIgnoreCase) ||
                       resource.Contains("gear", StringComparison.OrdinalIgnoreCase);
            });
        if (byResource is not null)
        {
            return byResource;
        }

        // Na interface antiga o botao de configuracao fica imediatamente a direita do LOGIN.
        // Esse criterio usa a geometria da propria arvore Android e evita coordenada fixa.
        var login = FindByLabels(list, new[] { "login" });
        if (login is null)
        {
            return null;
        }

        var rowTolerance = Math.Max(70, login.Bottom - login.Top);
        return list
            .Where(x => x.Clickable)
            .Where(x => x.Left >= login.Right - 6)
            .Where(x => Math.Abs(x.CenterY - login.CenterY) <= rowTolerance)
            .OrderBy(x => Math.Abs(x.CenterY - login.CenterY))
            .ThenBy(x => Math.Abs(x.Left - login.Right))
            .FirstOrDefault();
    }

    private static UiNode? FindEditableByHints(IEnumerable<UiNode> nodes, IEnumerable<string> hints)
    {
        var normalizedHints = hints.Select(Normalize).Where(x => x.Length > 0).ToArray();
        if (normalizedHints.Length == 0)
        {
            return null;
        }

        return nodes
            .Where(x => x.Enabled && x.ClassName.Contains("EditText", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x =>
            {
                var value = Normalize(x.SearchText);
                return normalizedHints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static UiNode? FindEditableBelowLabels(IEnumerable<UiNode> nodes, IEnumerable<string> labels)
    {
        var list = nodes.ToArray();
        var normalizedLabels = labels.Select(Normalize).Where(x => x.Length > 0).ToArray();
        var label = list
            .Where(x => !x.ClassName.Contains("EditText", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x =>
            {
                // SearchText inclui todos os descendentes. Em telas Compose isso faz o
                // container raiz conter simultaneamente "CNPJ", "Empresa ID" e "Token".
                // Se o container for tratado como label, a busca geometrica escolhe o
                // ultimo EditText (Token) para todos os campos. Aqui a correspondencia
                // precisa considerar somente o texto/descricao do proprio no.
                var value = Normalize($"{x.Text} {x.ContentDescription}");
                return normalizedLabels.Any(item => value.Contains(item, StringComparison.OrdinalIgnoreCase));
            });

        if (label is null)
        {
            return null;
        }

        return list
            .Where(x => x.Enabled && x.ClassName.Contains("EditText", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Top >= label.Top && x.Top - label.Bottom < 220)
            .OrderBy(x => Math.Abs(x.Top - label.Bottom))
            .FirstOrDefault();
    }

    private static UiNode? FindEditable(IEnumerable<UiNode> nodes) =>
        nodes.FirstOrDefault(x =>
            x.Enabled &&
            x.ClassName.Contains("EditText", StringComparison.OrdinalIgnoreCase));

    private static UiNode? FindByResourceId(IEnumerable<UiNode> nodes, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        return nodes
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ResourceId))
            .FirstOrDefault(x =>
                x.ResourceId.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
    }

    private static UiNode? FindByLabels(IEnumerable<UiNode> nodes, IEnumerable<string> labels)
    {
        var normalizedLabels = labels.Select(Normalize).Where(x => x.Length > 0).ToArray();
        return nodes
            .Where(x => x.Enabled && (x.Clickable || x.ClassName.Contains("Button", StringComparison.OrdinalIgnoreCase)))
            .Select(x => new { Node = x, Value = Normalize(x.SearchText) })
            .Where(x => normalizedLabels.Any(label => x.Value.Contains(label, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => (x.Node.Right - x.Node.Left) * (x.Node.Bottom - x.Node.Top))
            .Select(x => x.Node)
            .FirstOrDefault();
    }

    private static UiNode? FindByLabelsIncludingText(IEnumerable<UiNode> nodes, IEnumerable<string> labels)
    {
        var list = nodes.ToArray();
        var actionable = FindByLabels(list, labels);
        if (actionable is not null)
        {
            return actionable;
        }

        var normalizedLabels = labels.Select(Normalize).Where(x => x.Length > 0).ToArray();
        if (normalizedLabels.Length == 0)
        {
            return null;
        }

        return list
            .Where(x => x.Enabled)
            .Select(x => new { Node = x, Value = Normalize(x.SearchText) })
            .Where(x => normalizedLabels.Any(label => x.Value.Contains(label, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => (x.Node.Right - x.Node.Left) * (x.Node.Bottom - x.Node.Top))
            .Select(x => x.Node)
            .FirstOrDefault();
    }

    private static UiNode? FindSynchronizationOkNode(IEnumerable<UiNode> nodes)
    {
        var list = nodes.ToArray();
        var exactOk = list
            .Where(x => x.Enabled)
            .Where(x =>
            {
                var value = Normalize(x.SearchText);
                return value == "ok" || value == "ok!!!";
            })
            .OrderByDescending(x => x.Clickable || x.ClassName.Contains("Button", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => (x.Right - x.Left) * (x.Bottom - x.Top))
            .FirstOrDefault();

        if (exactOk is not null)
        {
            return exactOk;
        }

        var success = list
            .Where(x => x.Enabled)
            .FirstOrDefault(x =>
            {
                var value = Normalize(x.SearchText);
                return value.Contains("dados sincronizados com sucesso", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("dados foram sincronizados com sucesso", StringComparison.OrdinalIgnoreCase);
            });

        if (success is null)
        {
            return null;
        }

        // Alguns dialogs antigos expoem o texto de OK em um filho nao clicavel.
        // Nesse caso, usamos o primeiro controle acionavel logo abaixo da mensagem
        // de sucesso, evitando escolher botoes da tela que ficam acima do dialogo.
        return list
            .Where(x => x.Enabled &&
                        (x.Clickable || x.ClassName.Contains("Button", StringComparison.OrdinalIgnoreCase)) &&
                        x.CenterY > success.CenterY)
            .OrderBy(x => x.CenterY - success.CenterY)
            .ThenBy(x => Math.Abs(x.CenterX - success.CenterX))
            .FirstOrDefault();
    }

    private static bool ContainsLabel(IEnumerable<UiNode> nodes, string label)
    {
        var normalized = Normalize(label);
        return nodes.Any(x => Normalize(x.SearchText).Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSynchronizationSuccess(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "atualizacao concluida") ||
        ContainsLabel(nodes, "dados foram sincronizados com sucesso") ||
        ContainsLabel(nodes, "dados sincronizados com sucesso");

    private static bool IsSynchronizationFailure(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "falha na sincronizacao") ||
        ContainsLabel(nodes, "unable to resolve host") ||
        ContainsLabel(nodes, "no address associated with hostname") ||
        ContainsLabel(nodes, "nao foi possivel sincronizar") ||
        ContainsLabel(nodes, "erro de sincronizacao") ||
        ContainsLabel(nodes, "dispositivo ja esta em uso") ||
        ContainsLabel(nodes, "dispositivo esta em uso") ||
        ContainsLabel(nodes, "device ja esta em uso") ||
        ContainsLabel(nodes, "device esta em uso") ||
        ContainsLabel(nodes, "dispositivo em uso") ||
        ContainsLabel(nodes, "encontra-se em uso") ||
        ContainsLabel(nodes, "erro ao vincular") ||
        ContainsLabel(nodes, "falha ao vincular") ||
        ContainsLabel(nodes, "vinculo recusado");

    private static string ExtractSynchronizationFailureMessage(IEnumerable<UiNode> nodes)
    {
        var visibleMessages = nodes
            .SelectMany(x => new[] { x.Text, x.ContentDescription })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(x =>
            {
                var value = Normalize(x);
                return value.Contains("erro", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("falha", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("nao foi possivel", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("em uso", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("no address", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("recus", StringComparison.OrdinalIgnoreCase);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return visibleMessages.Length == 0
            ? "O Smart retornou erro ao registrar ou sincronizar o dispositivo. Confira a mensagem mantida na tela do Android."
            : "O Smart recusou o registro do dispositivo: " + string.Join(" | ", visibleMessages);
    }

    private static bool IsSynchronizationInProgress(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "sincronizando seus dados") ||
        ContainsLabel(nodes, "aguarde a sincronizacao") ||
        ContainsLabel(nodes, "sincronizando cadeias de certificados") ||
        ContainsLabel(nodes, "sincronizando");

    private static bool IsInitialProvisioningState(IEnumerable<UiNode> nodes, string moduleLabel)
    {
        // Estado inicial apos limpar os dados do APK. A deteccao e feita pela tela,
        // nao apenas pela versao, para permitir retomar uma configuracao interrompida.
        return ContainsLabel(nodes, "bem vindo ao smart") ||
               FindByLabels(nodes, StartConfigurationLabels) is not null ||
               ContainsLabel(nodes, "selecione o modulo") ||
               IsExpectedModuleConfiguration(nodes, moduleLabel);
    }

    private static bool IsExpectedModuleConfiguration(IEnumerable<UiNode> nodes, string moduleLabel)
    {
        var expected = Normalize(moduleLabel);
        return nodes.Any(x =>
        {
            var text = Normalize(x.SearchText);
            return text.Contains("configurar " + expected, StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("modulo " + expected, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string DetectConfiguredModule(IEnumerable<UiNode> nodes)
    {
        foreach (var node in nodes)
        {
            var text = node.Text?.Trim() ?? string.Empty;
            if (text.StartsWith("Configurar ", StringComparison.OrdinalIgnoreCase))
            {
                return text["Configurar ".Length..].Trim();
            }

            if (text.StartsWith("Módulo ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Modulo ", StringComparison.OrdinalIgnoreCase))
            {
                var separator = text.IndexOf(' ');
                return separator >= 0 ? text[(separator + 1)..].Trim() : text;
            }
        }

        return string.Empty;
    }

    private static string ExtractSmartDeviceId(IEnumerable<UiNode> nodes)
    {
        foreach (var value in nodes.SelectMany(x => new[] { x.Text, x.SearchText }))
        {
            var deviceId = ExtractSmartDeviceIdFromText(value);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                return deviceId;
            }
        }

        return string.Empty;
    }

    private static string ExtractSmartDeviceIdFromText(string? value)
    {
        var match = Regex.Match(
            value ?? string.Empty,
            @"Device\s*ID\s*:\s*([A-Za-z0-9._-]+)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string FirstNonEmpty(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) ? candidate : current;

    private static string GetModuleLabel(string module) =>
        ModuleLabels.TryGetValue(module ?? string.Empty, out var label) ? label : "Smart PDV";

    private static bool TryParseBounds(
        string value,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        left = top = right = bottom = 0;
        var cleaned = value.Replace("][", ",", StringComparison.Ordinal)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);
        var parts = cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 4 &&
               int.TryParse(parts[0], out left) &&
               int.TryParse(parts[1], out top) &&
               int.TryParse(parts[2], out right) &&
               int.TryParse(parts[3], out bottom);
    }

    private static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).Trim();
    }

    private static string BuildSummary(IEnumerable<UiNode> nodes)
    {
        var values = nodes
            .SelectMany(x => new[] { x.Text, x.ContentDescription })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        return values.Length == 0 ? "Nenhum texto acessivel encontrado na tela." : string.Join(" | ", values);
    }

    private sealed record FieldFillResult(bool Success, string Message, string Summary);

    private sealed record TefDirectResult(bool Success, string Stage, string Message, string Summary);

    private sealed record SmartTefLayout(
        string Name,
        int NameX, int NameY,
        int ManualX, int ManualY,
        int CnpjX, int CnpjY,
        int CompanyX, int CompanyY,
        int TokenX, int TokenY,
        int ConfirmX, int ConfirmY,
        int ConcludeX, int ConcludeY,
        bool RequiresManualScroll,
        int ScrollStartX, int ScrollStartY,
        int ScrollEndX, int ScrollEndY,
        int ScrollDurationMilliseconds,
        bool UseCoordinateConclusion,
        bool RequiresActivityGuard = false,
        bool UseUiFieldBounds = false);

    private sealed record DisplaySizeResult(bool Success, int Width, int Height, string Message);

    private sealed record ActivityDumpView(
        int Indent,
        int AbsoluteLeft,
        int AbsoluteTop,
        int AbsoluteRight,
        int AbsoluteBottom);

    private sealed record ActivityViewBoundsResult(
        bool Success,
        int Left,
        int Top,
        int Right,
        int Bottom,
        string Message)
    {
        public int CenterX => Left + Math.Max(1, Right - Left) / 2;
        public int CenterY => Top + Math.Max(1, Bottom - Top) / 2;
    }

    private sealed class ReferenceScaler
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _referenceWidth;
        private readonly int _referenceHeight;

        public ReferenceScaler(int width, int height, int referenceWidth, int referenceHeight)
        {
            _width = width;
            _height = height;
            _referenceWidth = referenceWidth;
            _referenceHeight = referenceHeight;
        }

        public int Width => _width;
        public int Height => _height;

        public (int X, int Y) Scale(int referenceX, int referenceY)
        {
            var x = (int)Math.Round(referenceX * (_width / (double)_referenceWidth));
            var y = (int)Math.Round(referenceY * (_height / (double)_referenceHeight));
            return (Math.Clamp(x, 1, Math.Max(1, _width - 1)), Math.Clamp(y, 1, Math.Max(1, _height - 1)));
        }
    }

    private sealed record UiSnapshot(bool Success, IReadOnlyList<UiNode> Nodes, string Error);

    private sealed record UiNode(
        string Text,
        string ContentDescription,
        string ResourceId,
        string ClassName,
        string PackageName,
        string SearchText,
        bool Clickable,
        bool Enabled,
        int Left,
        int Top,
        int Right,
        int Bottom)
    {
        public int CenterX => Left + Math.Max(1, Right - Left) / 2;
        public int CenterY => Top + Math.Max(1, Bottom - Top) / 2;
    }
}
