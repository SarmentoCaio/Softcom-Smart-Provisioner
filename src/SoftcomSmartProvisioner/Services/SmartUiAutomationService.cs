using System.Globalization;
using System.Text;
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
        CancellationToken cancellationToken = default)
    {
        // Todos os modulos deste fluxo (PDV, Pre-Venda, Minimercado, Totem,
        // Comanda e Autopagamento) usam o package padrao do Smart. O Smart TEF
        // possui um fluxo separado e nao passa por este metodo.
        //
        // Antes a preparacao tentava descobrir/validar o package por varios
        // comandos ADB (pm path, foreground package, pm list packages). Em alguns
        // emuladores isso podia ficar preso exatamente na etapa
        // "Identificando o aplicativo Softcom Smart...", sem chegar ao pm clear.
        // Agora usamos diretamente o package configurado (quando valido) ou o
        // package padrao conhecido. Se o package estiver incorreto/ausente, a
        // proxima operacao ADB retorna o erro normalmente em vez de travar aqui.
        progress?.Invoke("package", "Preparando o aplicativo Softcom Smart...");
        const string standardSmartPackage = "softcom.mobile.smart2";
        var packageName = !string.IsNullOrWhiteSpace(configuredPackage) &&
                          !configuredPackage.Contains("redeflex", StringComparison.OrdinalIgnoreCase)
            ? configuredPackage.Trim()
            : standardSmartPackage;

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

        var snapshot = await ReadUiAsync(serial, cancellationToken);
        if (!snapshot.Success)
        {
            return new SmartAutomationResult(false, packageName, "ui", snapshot.Error, string.Empty, string.Empty);
        }

        var smartDeviceId = ExtractSmartDeviceId(snapshot.Nodes);

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
            var moduleLabel = GetModuleLabel(module);
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

        // Fecha o teclado para garantir que o botao Confirmar fique acessivel.
        await Task.Delay(350, cancellationToken);
        await _adb.KeyEventAsync(serial, "KEYCODE_BACK", cancellationToken);
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
        Action<string, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke("package", "Identificando o aplicativo Smart TEF (RedeFlex)...");
        var packageName = await ResolveTefPackageAsync(serial, cancellationToken);

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
            deviceName,
            cnpj,
            empresaId,
            token,
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

    private async Task<TefDirectResult> FillSmartTefKnownLayoutAsync(
        string serial,
        string deviceName,
        string cnpj,
        string empresaId,
        string token,
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
        var layout = GetSmartTefLayout(sizeResult.Width, sizeResult.Height);

        progress?.Invoke(
            "tef-layout",
            $"Layout TEF: {layout.Name} ({sizeResult.Width}x{sizeResult.Height}). Campo Nome do dispositivo em {layout.NameX},{layout.NameY}.");

        // Antes de qualquer toque por coordenada, confirma que o RedeFlex continua
        // em primeiro plano. Se algo mudou, interrompe sem enviar BACK ou novos toques.
        var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
        if (!string.Equals(foreground, "softcom.mobile.smart2.redeflex", StringComparison.OrdinalIgnoreCase))
        {
            return new TefDirectResult(
                false,
                "tef-foreground",
                $"O Smart TEF nao esta em primeiro plano. Aplicativo atual: {foreground}.",
                string.Empty);
        }

        progress?.Invoke("tef-name", $"Informando o nome do dispositivo: {deviceName}...");
        var name = await TapAndTypeTefFieldAsync(
            serial,
            layout.NameX,
            layout.NameY,
            deviceName,
            "Nome do dispositivo",
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
            foreach (var offsetY in new[] { -34, 34, -58, 58 })
            {
                var y = Math.Clamp(layout.NameY + offsetY, 1, sizeResult.Height - 1);
                var retry = await TapAndTypeTefFieldAsync(
                    serial,
                    layout.NameX,
                    y,
                    deviceName,
                    "Nome do dispositivo",
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
        var manualTap = await _adb.TapAsync(serial, layout.ManualX, layout.ManualY, cancellationToken);
        if (!manualTap.Success)
        {
            return new TefDirectResult(false, "tef-manual", "Nao foi possivel acionar Digitar dados manualmente.", manualTap.CombinedOutput.Trim());
        }
        await Task.Delay(750, cancellationToken);

        // Depois que a secao e expandida, usamos pontos conhecidos dos campos. Nao
        // dependemos de UIAutomator nem da abertura do teclado para considerar o campo
        // encontrado: em POS fisicos o TextField recebe os eventos ADB mesmo quando o
        // UiTestAutomationBridge nao publica root node.
        progress?.Invoke("tef-cnpj", "Informando CNPJ do Smart TEF...");
        var cnpjFill = await TapAndTypeTefFieldAsync(
            serial,
            layout.CnpjX,
            layout.CnpjY,
            cnpj,
            "CNPJ",
            cancellationToken);
        if (!cnpjFill.Success)
        {
            return new TefDirectResult(false, "tef-cnpj", "Nao foi possivel preencher o CNPJ. " + cnpjFill.Message, cnpjFill.Summary);
        }
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);

        progress?.Invoke("tef-company", "Informando Empresa ID do Smart TEF...");
        var companyFill = await TapAndTypeTefFieldAsync(
            serial,
            layout.CompanyX,
            layout.CompanyY,
            empresaId,
            "Empresa ID",
            cancellationToken);
        if (!companyFill.Success)
        {
            return new TefDirectResult(false, "tef-company", "Nao foi possivel preencher Empresa ID. " + companyFill.Message, companyFill.Summary);
        }
        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);

        progress?.Invoke("tef-token", "Informando token do Smart TEF...");
        var tokenFill = await TapAndTypeTefFieldAsync(
            serial,
            layout.TokenX,
            layout.TokenY,
            token,
            "Token",
            cancellationToken);
        if (!tokenFill.Success)
        {
            return new TefDirectResult(false, "tef-token", "Nao foi possivel preencher o Token. " + tokenFill.Message, tokenFill.Summary);
        }

        await _adb.HideSoftKeyboardIfVisibleAsync(serial, cancellationToken);
        await Task.Delay(300, cancellationToken);

        progress?.Invoke("tef-submit", "Confirmando a configuracao do Smart TEF...");
        var confirmTap = await _adb.TapAsync(serial, layout.ConfirmX, layout.ConfirmY, cancellationToken);
        if (!confirmTap.Success)
        {
            return new TefDirectResult(false, "tef-submit", "Nao foi possivel acionar Confirmar Configuracao.", confirmTap.CombinedOutput.Trim());
        }

        progress?.Invoke("tef-validating", "Aguardando a validacao das chaves do Smart TEF...");
        var concludeResult = await WaitAndConcludeSmartTefAsync(
            serial,
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
        if (layout.Name.StartsWith("Positivo L400", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(2600, cancellationToken);
            for (var attempt = 1; attempt <= 7; attempt++)
            {
                progress?.Invoke("tef-conclude", $"Aguardando validacao das chaves e Concluir... tentativa {attempt}/7.");
                var tap = await _adb.TapAsync(serial, layout.ConcludeX, layout.ConcludeY, cancellationToken);
                if (!tap.Success)
                {
                    return new TefDirectResult(false, "tef-conclude", "Nao foi possivel tocar no botao Concluir do Smart TEF.", tap.CombinedOutput.Trim());
                }

                await Task.Delay(attempt == 7 ? 900 : 1200, cancellationToken);
                var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
                if (!string.Equals(foreground, "softcom.mobile.smart2.redeflex", StringComparison.OrdinalIgnoreCase))
                {
                    return new TefDirectResult(
                        false,
                        "tef-conclude",
                        $"O Smart TEF deixou de ficar em primeiro plano durante a conclusao. Aplicativo atual: {foreground}.",
                        string.Empty);
                }

                // Faz no maximo duas leituras auxiliares para nao repetir o problema
                // de ficar minutos preso no UIAutomator do L400.
                if (attempt is 3 or 7)
                {
                    var check = await ReadUiQuickAsync(serial, cancellationToken);
                    if (check.Success)
                    {
                        if (IsSmartTefLoginScreen(check.Nodes))
                        {
                            return new TefDirectResult(true, "tef-concluded", string.Empty, BuildSummary(check.Nodes));
                        }
                        if (IsSmartTefValidationFailure(check.Nodes))
                        {
                            return new TefDirectResult(false, "tef-validation", "O Smart TEF retornou falha durante a validacao das chaves.", BuildSummary(check.Nodes));
                        }
                    }
                }
            }

            return new TefDirectResult(
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
                if (!string.Equals(foreground, "softcom.mobile.smart2.redeflex", StringComparison.OrdinalIgnoreCase))
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
        CancellationToken cancellationToken)
    {
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

    private static SmartTefLayout GetSmartTefLayout(int width, int height)
    {
        // Perfil do Positivo L400 observado no teste real. O layout do Compose neste
        // aparelho nao coincide com a simples escala 1080x2400 -> 720x1600.
        if (Math.Abs(width - 720) <= 24 && Math.Abs(height - 1600) <= 40)
        {
            return new SmartTefLayout(
                "Positivo L400 720x1600",
                360, 497,
                360, 862,
                360, 1006,
                360, 1154,
                360, 1303,
                360, 1496,
                360, 1128);
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
            conclude.X, conclude.Y);
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

    private async Task<UiSnapshot> ReadUiQuickAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await _adb.DumpUiHierarchyAsync(serial, cancellationToken, maxAttempts: 1, dumpTimeoutMilliseconds: 2500);
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
        CancellationToken cancellationToken)
    {
        const string tefPackage = "softcom.mobile.smart2.redeflex";
        if (await _adb.IsPackageInstalledAsync(serial, tefPackage, cancellationToken))
        {
            return tefPackage;
        }

        var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
        if (!string.IsNullOrWhiteSpace(foreground) &&
            foreground.Contains("redeflex", StringComparison.OrdinalIgnoreCase) &&
            await _adb.IsPackageInstalledAsync(serial, foreground, cancellationToken))
        {
            return foreground;
        }

        throw new InvalidOperationException(
            "O package do Smart TEF nao foi localizado. Esperado: softcom.mobile.smart2.redeflex.");
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
            await _adb.KeyEventAsync(serial, "KEYCODE_BACK", cancellationToken);
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
        await _adb.KeyEventAsync(serial, "KEYCODE_BACK", cancellationToken);
        await Task.Delay(250, cancellationToken);
        return new FieldFillResult(true, string.Empty, BuildSummary(snapshot.Nodes));
    }

    private async Task<string> ResolvePackageAsync(
        string serial,
        string? configuredPackage,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredPackage) &&
            !configuredPackage.Contains("redeflex", StringComparison.OrdinalIgnoreCase) &&
            await _adb.IsPackageInstalledAsync(serial, configuredPackage, cancellationToken))
        {
            return configuredPackage.Trim();
        }

        var foreground = await _adb.GetForegroundPackageAsync(serial, cancellationToken);
        if (!string.IsNullOrWhiteSpace(foreground) &&
            (foreground.Contains("softcom", StringComparison.OrdinalIgnoreCase) ||
             foreground.Contains("smart", StringComparison.OrdinalIgnoreCase)) &&
            await _adb.IsPackageInstalledAsync(serial, foreground, cancellationToken))
        {
            return foreground;
        }

        // Package confirmado no Smart padrao atual. Evita confundir com a variante RedeFlex.
        const string standardSmart = "softcom.mobile.smart2";
        if (await _adb.IsPackageInstalledAsync(serial, standardSmart, cancellationToken))
        {
            return standardSmart;
        }

        var candidates = await _adb.FindLikelySmartPackagesAsync(serial, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Nenhum package Android contendo 'softcom' ou 'smart' foi localizado. Configure o package do Smart em Configuracoes.");
        }

        if (candidates.Count == 1)
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
            element.Attribute("class")?.Value ?? string.Empty,
            searchText,
            element.Attribute("clickable")?.Value == "true",
            element.Attribute("enabled")?.Value != "false",
            left,
            top,
            right,
            bottom);
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
                var value = Normalize(x.SearchText);
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

    private static bool ContainsLabel(IEnumerable<UiNode> nodes, string label)
    {
        var normalized = Normalize(label);
        return nodes.Any(x => Normalize(x.SearchText).Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSynchronizationSuccess(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "atualizacao concluida") ||
        ContainsLabel(nodes, "dados foram sincronizados com sucesso");

    private static bool IsSynchronizationFailure(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "falha na sincronizacao") ||
        ContainsLabel(nodes, "unable to resolve host") ||
        ContainsLabel(nodes, "no address associated with hostname");

    private static bool IsSynchronizationInProgress(IEnumerable<UiNode> nodes) =>
        ContainsLabel(nodes, "sincronizando seus dados") ||
        ContainsLabel(nodes, "aguarde a sincronizacao") ||
        ContainsLabel(nodes, "sincronizando cadeias de certificados") ||
        ContainsLabel(nodes, "sincronizando");

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
            var match = System.Text.RegularExpressions.Regex.Match(
                value ?? string.Empty,
                @"Device\s*ID\s*:\s*([A-Za-z0-9._-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        return string.Empty;
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
        int ConcludeX, int ConcludeY);

    private sealed record DisplaySizeResult(bool Success, int Width, int Height, string Message);

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
        string ClassName,
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
