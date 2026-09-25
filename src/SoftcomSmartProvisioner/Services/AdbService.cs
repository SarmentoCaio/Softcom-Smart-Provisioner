using System.Diagnostics;
using System.Text.RegularExpressions;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class AdbService
{
    private readonly string _toolsDirectory;
    private readonly string _adbPath;

    public AdbService(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
        _adbPath = Path.Combine(_toolsDirectory, "adb.exe");
    }

    public bool IsAvailable => File.Exists(_adbPath);

    public Task<ProcessResult> StartServerAsync(CancellationToken cancellationToken = default) =>
        RunAsync(new[] { "start-server" }, cancellationToken);

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(new[] { "devices", "-l" }, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.CombinedOutput.Length > 0
                ? result.CombinedOutput
                : "Nao foi possivel consultar os dispositivos ADB.");
        }

        var devices = new List<DeviceInfo>();
        var lines = result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(line => line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase));

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('*'))
            {
                continue;
            }

            var columns = Regex.Split(line, "\\s+");
            if (columns.Length < 2)
            {
                continue;
            }

            var serial = columns[0];
            var state = columns[1];
            var properties = ParseProperties(columns.Skip(2));
            var model = properties.TryGetValue("model", out var modelValue)
                ? modelValue.Replace('_', ' ')
                : FriendlySerial(serial);
            var product = properties.GetValueOrDefault("product", string.Empty).Replace('_', ' ');
            var transport = GetTransport(serial);

            string androidVersion = string.Empty;
            string androidSdk = string.Empty;
            string manufacturer = string.Empty;
            string productModel = model;
            string androidId = string.Empty;
            string smartVersion = string.Empty;
            string smartPackage = string.Empty;
            IReadOnlyList<string> smartPackageCandidates = Array.Empty<string>();
            string currentActivity = string.Empty;
            string resolution = string.Empty;
            string density = string.Empty;
            IReadOnlyDictionary<string, string> permissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string smartFlow = "Nao detectado";
            int? battery = null;
            long? latency = null;

            if (string.Equals(state, "device", StringComparison.OrdinalIgnoreCase))
            {
                var stopwatch = Stopwatch.StartNew();
                var probe = await ShellAsync(serial, "echo smart-provisioner", cancellationToken, 6000);
                stopwatch.Stop();
                if (probe.Success)
                {
                    latency = Math.Max(1, stopwatch.ElapsedMilliseconds);
                }

                var versionResult = await ShellAsync(serial, "getprop ro.build.version.release", cancellationToken, 6000);
                if (versionResult.Success)
                {
                    androidVersion = versionResult.StandardOutput.Trim();
                }

                manufacturer = await ReadShellValueAsync(serial, "getprop ro.product.manufacturer", cancellationToken);
                var reportedModel = await ReadShellValueAsync(serial, "getprop ro.product.model", cancellationToken);
                if (!string.IsNullOrWhiteSpace(reportedModel)) productModel = reportedModel;
                androidSdk = await ReadShellValueAsync(serial, "getprop ro.build.version.sdk", cancellationToken);
                resolution = await ReadShellValueAsync(serial, "wm size", cancellationToken);
                density = await ReadShellValueAsync(serial, "wm density", cancellationToken);
                currentActivity = await GetForegroundActivityAsync(serial, cancellationToken);

                var idResult = await ShellAsync(serial, "settings get secure android_id", cancellationToken, 6000);
                if (idResult.Success)
                {
                    var value = idResult.StandardOutput.Trim();
                    if (!string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        androidId = value;
                    }
                }

                var batteryResult = await ShellAsync(serial, "dumpsys battery", cancellationToken, 6000);
                if (batteryResult.Success)
                {
                    var match = Regex.Match(batteryResult.StandardOutput, @"level:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var parsedBattery))
                    {
                        battery = parsedBattery;
                    }
                }

                // O package varia por adquirente. Primeiro enumera no proprio aparelho e
                // valida cada candidato por dumpsys; nunca reutiliza o package de outro UDID.
                var detectedSmart = await DetectSmartPackageAsync(serial, cancellationToken);
                smartPackage = detectedSmart.PackageName;
                smartVersion = detectedSmart.VersionName;
                smartPackageCandidates = detectedSmart.Candidates;
                smartFlow = ClassifySmartFlow(smartVersion);
                if (!string.IsNullOrWhiteSpace(smartPackage))
                    permissions = await GetRelevantPermissionsAsync(serial, smartPackage, cancellationToken);
            }

            devices.Add(new DeviceInfo(
                serial,
                state,
                model,
                product,
                transport,
                androidVersion,
                battery,
                latency,
                androidId,
                smartVersion,
                smartFlow)
            {
                Manufacturer = manufacturer,
                Model = productModel,
                AndroidSdk = androidSdk,
                Resolution = resolution,
                Density = density,
                SmartPackage = smartPackage,
                SmartPackageCandidates = smartPackageCandidates,
                CurrentActivity = currentActivity,
                RelevantPermissions = permissions
            });
        }

        return devices;
    }

    public Task<ProcessResult> ShellAsync(
        string serial,
        string command,
        CancellationToken cancellationToken = default,
        int timeoutMilliseconds = 30000) =>
        RunAsync(BuildShellArguments(serial, command), cancellationToken, timeoutMilliseconds);

    public static string[] BuildShellArguments(string serial, string command)
    {
        if (string.IsNullOrWhiteSpace(serial)) throw new ArgumentException("O serial ADB é obrigatório.", nameof(serial));
        return new[] { "-s", serial.Trim(), "shell", command };
    }

    public static IReadOnlyList<string> ParseConnectedSerials(string adbDevicesOutput) =>
        (adbDevicesOutput ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Regex.Split(x.Trim(), "\\s+"))
            .Where(x => x.Length >= 2 && !x[0].Equals("List", StringComparison.OrdinalIgnoreCase))
            .Select(x => x[0])
            .ToArray();

    public Task<ProcessResult> ClearPackageAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("Informe o package name do Smart.", nameof(packageName));
        }

        return ShellAsync(serial, $"pm clear {packageName.Trim()}", cancellationToken, 30000);
    }


    public async Task<bool> IsPackageInstalledAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var result = await ShellAsync(serial, $"pm path {packageName.Trim()}", cancellationToken, 10000);
        return result.Success && result.StandardOutput.Contains("package:", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ProcessResult> ForceStopPackageAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("Informe o package name do Smart.", nameof(packageName));
        }

        return ShellAsync(serial, $"am force-stop {packageName.Trim()}", cancellationToken, 10000);
    }

    public async Task<ProcessResult> LaunchPackageAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("Informe o package name do Smart.", nameof(packageName));
        }

        var package = packageName.Trim();

        // Prefere o launcher explicitamente restrito ao package informado. Isso evita
        // depender do comportamento do monkey em Androids antigos e garante que o
        // Provisioner nunca tente iniciar outro APK durante o fluxo do Smart.
        var direct = await ShellAsync(
            serial,
            $"am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p {package}",
            cancellationToken,
            20000);

        var directOutput = direct.CombinedOutput ?? string.Empty;
        var directFailed = !direct.Success ||
                           directOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
                           directOutput.Contains("unable to resolve", StringComparison.OrdinalIgnoreCase) ||
                           directOutput.Contains("Activity not started", StringComparison.OrdinalIgnoreCase);
        if (!directFailed)
        {
            return direct;
        }

        // Fallback para builds Android onde `am start -p` nao resolve o launcher.
        // O `-p` continua limitando o evento exclusivamente ao package solicitado.
        var monkey = await ShellAsync(
            serial,
            $"monkey -p {package} -c android.intent.category.LAUNCHER 1",
            cancellationToken,
            20000);

        if (monkey.Success)
        {
            return monkey;
        }

        return new ProcessResult(
            monkey.ExitCode,
            string.Join(Environment.NewLine, new[] { direct.StandardOutput, monkey.StandardOutput }.Where(x => !string.IsNullOrWhiteSpace(x))),
            string.Join(Environment.NewLine, new[] { direct.StandardError, monkey.StandardError }.Where(x => !string.IsNullOrWhiteSpace(x))));
    }

    public async Task<ProcessResult> DumpUiHierarchyAsync(
        string serial,
        CancellationToken cancellationToken = default,
        int maxAttempts = 3,
        int dumpTimeoutMilliseconds = 18000)
    {
        // Usa um arquivo remoto unico em cada leitura. Em alguns aparelhos fisicos o
        // `uiautomator dump` pode falhar silenciosamente e deixar o XML anterior no
        // caminho fixo. Isso fazia o Provisioner continuar enxergando a tela de
        // selecao de modulo mesmo quando o Smart ja estava em Configurar Smart TEF.
        ProcessResult? last = null;

        for (var attempt = 0; attempt < Math.Max(1, maxAttempts); attempt++)
        {
            var remotePath = $"/sdcard/softcom_smart_ui_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{attempt}.xml";

            // Remove por seguranca qualquer arquivo com o mesmo nome. O comando pode
            // retornar erro em alguns Androids e por isso o resultado e ignorado.
            await ShellAsync(serial, $"rm -f {remotePath}", cancellationToken, 5000);

            var dump = await ShellAsync(
                serial,
                $"uiautomator dump {remotePath}",
                cancellationToken,
                Math.Max(2000, dumpTimeoutMilliseconds));

            // Nao confia apenas no exit code: algumas builds retornam 0 mesmo quando
            // nao conseguem produzir uma nova arvore de acessibilidade.
            var dumpOutput = dump.CombinedOutput ?? string.Empty;
            var dumpLooksInvalid =
                dumpOutput.Contains("null root", StringComparison.OrdinalIgnoreCase) ||
                dumpOutput.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                dumpOutput.Contains("Exception", StringComparison.OrdinalIgnoreCase);

            if (dump.Success && !dumpLooksInvalid)
            {
                var read = await RunAsync(
                    new[] { "-s", serial, "exec-out", "cat", remotePath },
                    cancellationToken,
                    15000);

                await ShellAsync(serial, $"rm -f {remotePath}", cancellationToken, 5000);

                if (read.Success &&
                    !string.IsNullOrWhiteSpace(read.StandardOutput) &&
                    read.StandardOutput.Contains("<hierarchy", StringComparison.OrdinalIgnoreCase))
                {
                    return read;
                }

                last = new ProcessResult(
                    read.ExitCode == 0 ? -1 : read.ExitCode,
                    read.StandardOutput,
                    string.IsNullOrWhiteSpace(read.StandardError)
                        ? "O uiautomator executou, mas nao retornou uma arvore XML valida da tela atual."
                        : read.StandardError);
            }
            else
            {
                await ShellAsync(serial, $"rm -f {remotePath}", cancellationToken, 5000);
                last = dump;
            }

            if (attempt < Math.Max(1, maxAttempts) - 1)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        return last ?? new ProcessResult(
            -1,
            string.Empty,
            "Nao foi possivel capturar a arvore atual da interface Android.");
    }

    public async Task<string> GetForegroundPackageAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await ShellAsync(serial, "dumpsys window", cancellationToken, 12000);
        if (result.Success)
        {
            foreach (var pattern in new[]
            {
                @"mCurrentFocus=.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+)/(?:[A-Za-z0-9._$]+)",
                @"mFocusedApp=.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+)/(?:[A-Za-z0-9._$]+)"
            })
            {
                var match = Regex.Match(result.StandardOutput, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }
        }

        // Android 7 e algumas ROMs nao publicam mCurrentFocus de forma consistente.
        // Nesses casos consultamos a activity retomada, sem inferir o package por nome.
        var activity = await ShellAsync(serial, "dumpsys activity activities", cancellationToken, 12000);
        if (activity.Success)
        {
            foreach (var pattern in new[]
            {
                @"mResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+)/(?:[A-Za-z0-9._$]+)",
                @"ResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+)/(?:[A-Za-z0-9._$]+)",
                @"mFocusedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+)/(?:[A-Za-z0-9._$]+)"
            })
            {
                var match = Regex.Match(activity.StandardOutput, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }
        }

        return string.Empty;
    }

    public async Task<string> GetForegroundActivityAsync(string serial, CancellationToken cancellationToken = default)
    {
        foreach (var command in new[] { "dumpsys window windows", "dumpsys activity activities" })
        {
            var result = await ShellAsync(serial, command, cancellationToken, 12000);
            if (!result.Success) continue;
            foreach (var pattern in new[]
            {
                @"mCurrentFocus=.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)",
                @"mResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)",
                @"ResumedActivity:.*?\s(?:u\d+\s+)?([A-Za-z0-9._]+/[A-Za-z0-9._$]+)"
            })
            {
                var match = Regex.Match(result.StandardOutput, pattern, RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
            }
        }
        return string.Empty;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetRelevantPermissionsAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        var result = await ShellAsync(serial, $"dumpsys package {packageName}", cancellationToken, 12000);
        var output = result.CombinedOutput ?? string.Empty;
        var names = new[]
        {
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.WRITE_EXTERNAL_STORAGE",
            "android.permission.POST_NOTIFICATIONS",
            "android.permission.CAMERA"
        };
        return names.ToDictionary(
            name => name[(name.LastIndexOf('.') + 1)..],
            name => Regex.IsMatch(output, $@"{Regex.Escape(name)}:\s*granted=true", RegexOptions.IgnoreCase) ? "Concedida" : "Não concedida",
            StringComparer.OrdinalIgnoreCase);
    }

    public Task<ProcessResult> TapAsync(
        string serial,
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        ShellAsync(serial, $"input tap {x} {y}", cancellationToken, 10000);

    public Task<ProcessResult> LongPressAsync(
        string serial,
        int x,
        int y,
        int durationMs = 5000,
        CancellationToken cancellationToken = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 15000);
        return ShellAsync(
            serial,
            BuildLongPressCommand(x, y, durationMs),
            cancellationToken,
            durationMs + 10000);
    }

    public static string BuildLongPressCommand(int x, int y, int durationMs = 5000)
    {
        durationMs = Math.Clamp(durationMs, 500, 15000);
        // Alguns firmwares tratam swipe com inicio e fim identicos como um tap
        // comum. Um deslocamento de apenas um pixel continua dentro do touch-slop
        // do botao, mas obriga o Android a manter o gesto durante todo o periodo.
        var endX = x < int.MaxValue ? x + 1 : x - 1;
        return $"input touchscreen swipe {x} {y} {endX} {y} {durationMs}";
    }

    public async Task<string> GetPackageVersionNameAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName) ||
            !Regex.IsMatch(packageName, @"^[A-Za-z0-9._]+$"))
        {
            return string.Empty;
        }

        var result = await ShellAsync(serial, $"dumpsys package {packageName}", cancellationToken, 12000);
        if (!result.Success)
        {
            return string.Empty;
        }

        var match = Regex.Match(
            result.StandardOutput ?? string.Empty,
            @"(?:^|\s)versionName=([^\s]+)",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }


    public static string ClassifySmartFlow(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return "Nao detectado";
        }

        var normalized = versionName.Trim();
        var numeric = Regex.Match(normalized, @"\d+(?:\.\d+){1,3}").Value;
        if (string.IsNullOrWhiteSpace(numeric))
        {
            return "Versao nao reconhecida";
        }

        // Version.TryParse("8.1.0") gera Revision = -1. Comparar esse valor diretamente
        // com 8.1.0.0 faz 8.1.0 ser considerado menor, embora seja a mesma versao
        // funcional para a regra de interface. Normalizamos sempre para quatro partes.
        var components = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (components.Count < 4) components.Add("0");
        if (components.Count > 4) components = components.Take(4).ToList();

        var comparable = string.Join('.', components);
        if (!Version.TryParse(comparable, out var version))
        {
            return "Versao nao reconhecida";
        }

        return version >= new Version(8, 1, 0, 0)
            ? "Smart 8.1+"
            : "Smart legado (< 8.1)";
    }

    public Task<ProcessResult> KeyEventAsync(
        string serial,
        string keyCode,
        CancellationToken cancellationToken = default) =>
        ShellAsync(serial, $"input keyevent {keyCode}", cancellationToken, 10000);

    /// <summary>
    /// Verifica se o teclado virtual esta realmente visivel. Isso e usado antes de
    /// enviar KEYCODE_BACK para evitar que o comando volte telas do Smart quando o
    /// toque nao conseguiu focar um EditText.
    /// </summary>
    public async Task<bool> IsSoftKeyboardVisibleAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await ShellAsync(serial, "dumpsys input_method", cancellationToken, 6000);
        if (!result.Success)
        {
            return false;
        }

        var output = result.CombinedOutput ?? string.Empty;
        return IsSoftKeyboardActuallyVisible(output);
    }

    private static bool IsSoftKeyboardActuallyVisible(string? dumpsysOutput)
    {
        var output = dumpsysOutput ?? string.Empty;

        // Android 11/12 pode manter mInputShown/mShowRequested=true mesmo depois de o
        // IME perder a janela visivel. Enviar BACK nesse estado volta a Activity do
        // Smart em vez de apenas recolher o teclado. Quando o dump expoe o estado da
        // janela, ele e a fonte de verdade; mInputShown fica apenas como compatibilidade
        // para ROMs antigas que nao publicam nenhum dos indicadores visuais.
        // Algumas ROMs Android 7 do K2 mantem mIsInputViewShown=true mesmo com a janela
        // do IME fechada. Quando mWindowVisible/mDecorViewVisible existem, eles tem
        // prioridade e impedem que um BACK seja enviado para a Activity do Smart.
        var publishesRenderedWindowState =
            output.Contains("mWindowVisible=", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("mDecorViewVisible=", StringComparison.OrdinalIgnoreCase);

        if (publishesRenderedWindowState)
        {
            return output.Contains("mWindowVisible=true", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("mDecorViewVisible=true", StringComparison.OrdinalIgnoreCase);
        }

        if (output.Contains("mIsInputViewShown=", StringComparison.OrdinalIgnoreCase))
            return output.Contains("mIsInputViewShown=true", StringComparison.OrdinalIgnoreCase);

        return output.Contains("mInputShown=true", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("inputShown=true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fecha o teclado somente quando ele esta visivel. Nunca envia BACK as cegas.
    /// </summary>
    public async Task<bool> HideSoftKeyboardIfVisibleAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSoftKeyboardVisibleAsync(serial, cancellationToken))
        {
            return false;
        }

        await KeyEventAsync(serial, "KEYCODE_BACK", cancellationToken);
        await Task.Delay(300, cancellationToken);
        return true;
    }

    /// <summary>
    /// Limpa o texto do campo Android atualmente focado sem depender de comandos
    /// de shell compostos (while/variaveis), que variam entre builds do Android.
    /// </summary>
    public async Task<ProcessResult> ClearFocusedTextAsync(
        string serial,
        int maxCharacters = 96,
        CancellationToken cancellationToken = default)
    {
        maxCharacters = Math.Clamp(maxCharacters, 1, 128);

        // Posiciona o cursor no fim do campo. Algumas builds podem nao reconhecer
        // MOVE_END; nesse caso seguimos, pois os DELs ainda sao seguros em campo vazio.
        await KeyEventAsync(serial, "KEYCODE_MOVE_END", cancellationToken);

        // O utilitario Android `input keyevent` aceita varios keycodes na mesma chamada.
        // Isso evita o loop de shell usado anteriormente, que falhava em alguns aparelhos.
        const int batchSize = 24;
        ProcessResult? last = null;
        var remaining = maxCharacters;
        while (remaining > 0)
        {
            var count = Math.Min(batchSize, remaining);
            var deletes = string.Join(" ", Enumerable.Repeat("KEYCODE_DEL", count));
            last = await ShellAsync(serial, $"input keyevent {deletes}", cancellationToken, 10000);
            if (!last.Success)
            {
                // Fallback para builds cujo comando `input` aceita apenas um keycode
                // por chamada. E mais lento, mas so e utilizado quando o lote falha.
                var fallbackCount = Math.Min(maxCharacters, 64);
                ProcessResult? fallback = null;
                for (var i = 0; i < fallbackCount; i++)
                {
                    fallback = await KeyEventAsync(serial, "KEYCODE_DEL", cancellationToken);
                    if (!fallback.Success)
                    {
                        return fallback;
                    }
                }

                return fallback ?? last;
            }
            remaining -= count;
        }

        return last ?? new ProcessResult(0, string.Empty, string.Empty);
    }

    public Task<ProcessResult> InputTextAsync(
        string serial,
        string text,
        CancellationToken cancellationToken = default)
    {
        var escaped = (text ?? string.Empty)
            .Replace(" ", "%s", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);
        return ShellAsync(serial, $"input text '{escaped}'", cancellationToken, 30000);
    }

    public async Task<bool> CanResolveHostAsync(
        string serial,
        string host,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            !Regex.IsMatch(host, @"^[A-Za-z0-9.-]+$"))
        {
            return false;
        }

        var lookup = await ShellAsync(serial, $"getent hosts {host}", cancellationToken, 6000);
        if (lookup.Success && !string.IsNullOrWhiteSpace(lookup.StandardOutput))
        {
            return true;
        }

        var ping = await ShellAsync(serial, $"ping -c 1 -W 2 {host}", cancellationToken, 6000);
        var output = ping.CombinedOutput;
        if (output.Contains("unknown host", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("bad address", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("name or service not known", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Mesmo que ICMP seja bloqueado, a linha PING <host> (<ip>) confirma que o DNS resolveu.
        return output.Contains("PING ", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("bytes from", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("1 received", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> FindLikelySmartPackagesAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await ShellAsync(serial, "pm list packages", cancellationToken, 20000);
        if (!result.Success)
        {
            return Array.Empty<string>();
        }

        return result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(x => x["package:".Length..])
            .Where(x =>
                x.Contains("softcom", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("smart", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();
    }

    public async Task<SmartPackageDetection> DetectSmartPackageAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var candidates = await FindLikelySmartPackagesAsync(serial, cancellationToken);
        var validated = new List<(string Package, string Version)>();
        foreach (var packageName in candidates)
        {
            var version = await GetPackageVersionNameAsync(serial, packageName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(version)) validated.Add((packageName, version));
        }

        if (validated.Count == 0)
            return new SmartPackageDetection(string.Empty, string.Empty, candidates);

        var foreground = await GetForegroundPackageAsync(serial, cancellationToken);
        var selectedPackage = SelectPreferredSmartPackage(
            validated.Select(x => x.Package),
            foreground);
        var selected = validated.First(x => x.Package.Equals(selectedPackage, StringComparison.OrdinalIgnoreCase));

        return new SmartPackageDetection(selected.Package, selected.Version, validated.Select(x => x.Package).ToArray());
    }

    public static int SmartPackageScore(string packageName)
    {
        if (packageName.Equals("softcom.mobile.smart2", StringComparison.OrdinalIgnoreCase)) return 100;
        if (packageName.Contains("mobile.smart", StringComparison.OrdinalIgnoreCase)) return 90;
        if (packageName.Contains("smart2", StringComparison.OrdinalIgnoreCase)) return 80;
        if (packageName.Contains("smart", StringComparison.OrdinalIgnoreCase)) return 60;
        if (packageName.Contains("softcom", StringComparison.OrdinalIgnoreCase)) return 20;
        return 0;
    }

    public static string SelectPreferredSmartPackage(IEnumerable<string> packages, string? foregroundPackage)
    {
        var candidates = packages
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(SmartPackageScore)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return string.Empty;

        var selected = candidates[0];
        var foreground = candidates.FirstOrDefault(x =>
            x.Equals(foregroundPackage, StringComparison.OrdinalIgnoreCase));

        // Estar em primeiro plano nao torna um package o Smart. Adquirentes podem
        // manter launchers próprios com "smart" no nome (ex.: Mercado Pago SmartPOS).
        // O foreground só desempata candidatos com a mesma confiança do melhor package.
        return !string.IsNullOrWhiteSpace(foreground) &&
               SmartPackageScore(foreground) >= SmartPackageScore(selected)
            ? foreground
            : selected;
    }

    private Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 30000)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("adb.exe nao encontrado na pasta tools.", _adbPath);
        }

        return ProcessRunner.RunTextAsync(
            _adbPath,
            arguments,
            _toolsDirectory,
            cancellationToken,
            timeoutMilliseconds);
    }

    private async Task<string> ReadShellValueAsync(string serial, string command, CancellationToken cancellationToken)
    {
        var result = await ShellAsync(serial, command, cancellationToken, 8000);
        return result.Success ? result.StandardOutput.Trim() : string.Empty;
    }

    private static Dictionary<string, string> ParseProperties(IEnumerable<string> columns)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var separator = column.IndexOf(':');
            if (separator <= 0 || separator >= column.Length - 1)
            {
                continue;
            }

            result[column[..separator]] = column[(separator + 1)..];
        }

        return result;
    }

    private static string FriendlySerial(string serial)
    {
        if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
        {
            return $"Emulador {serial[9..]}";
        }

        return serial;
    }

    private static string GetTransport(string serial)
    {
        if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
        {
            return "Emulador";
        }

        return serial.Contains(':') ? "Wi-Fi" : "USB";
    }
}

public sealed record SmartPackageDetection(
    string PackageName,
    string VersionName,
    IReadOnlyList<string> Candidates);
