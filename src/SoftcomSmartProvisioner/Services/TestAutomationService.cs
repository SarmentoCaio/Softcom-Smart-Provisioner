using System.Diagnostics;
using System.Text.RegularExpressions;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed partial class TestAutomationService
{
    private const int TestTimeoutMilliseconds = 2 * 60 * 60 * 1000;
    private const int GitTimeoutMilliseconds = 60 * 1000;
    private static readonly string[] SupportedChannels = ["master", "dev", "DEV-Sarmento"];
    private readonly string? _explicitRoot;
    private readonly Func<string, IEnumerable<string>, string, CancellationToken, int, Action<string>?, IReadOnlyDictionary<string, string?>?, Task<ProcessResult>> _processRunner;
    private readonly Func<string, string?> _commandResolver;

    public TestAutomationService(
        string? explicitRoot = null,
        Func<string, IEnumerable<string>, string, CancellationToken, int, Action<string>?, IReadOnlyDictionary<string, string?>?, Task<ProcessResult>>? processRunner = null,
        Func<string, string?>? commandResolver = null)
    {
        _explicitRoot = explicitRoot;
        _processRunner = processRunner ?? ProcessRunner.RunTextStreamingAsync;
        _commandResolver = commandResolver ?? FindCommand;
    }

    public TestAutomationCatalog LoadCatalog(
        IReadOnlyList<DeviceInfo> connectedDevices,
        DeviceCatalogSnapshot deviceCatalog)
    {
        var root = ResolveProjectRoot();
        if (root is null)
        {
            return new TestAutomationCatalog(
                null,
                new TestAutomationSourceInfo(false, string.Empty, string.Empty, false, SupportedChannels),
                new TestAutomationPrerequisites(false, false, false, false, _commandResolver("uv") is not null,
                    _commandResolver("appium") is not null),
                Array.Empty<TestAutomationSuite>(),
                Array.Empty<TestAutomationDevice>(),
                null,
                new[] { "Projeto softcom-smart-automation não localizado." });
        }

        var runnerPath = Path.Combine(root, "run_tests.ps1");
        var environmentPath = Path.Combine(root, ".env");
        var devicesPath = Path.Combine(root, "resources", "data", "devices.yaml");
        var warnings = new List<string>();
        var mappings = File.Exists(devicesPath)
            ? ParseDeviceMappings(File.ReadLines(devicesPath))
            : Array.Empty<DeviceTagMapping>();
        if (!File.Exists(environmentPath)) warnings.Add("Arquivo .env da automação não localizado.");
        if (!File.Exists(devicesPath)) warnings.Add("Catálogo resources/data/devices.yaml não localizado.");

        var suites = DiscoverSuites(root);
        if (suites.Count == 0) warnings.Add("Nenhuma suíte Robot Framework foi localizada.");

        var devices = connectedDevices
            .Select(device => BuildDevice(device, deviceCatalog, mappings))
            .OrderByDescending(device => device.IsOnline)
            .ThenBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var reportPath = ResolveLatestReportPath(root);
        return new TestAutomationCatalog(
            root,
            ReadSourceInfo(root),
            new TestAutomationPrerequisites(
                true,
                File.Exists(runnerPath),
                File.Exists(environmentPath),
                File.Exists(devicesPath),
                _commandResolver("uv") is not null,
                _commandResolver("appium") is not null),
            suites,
            devices,
            reportPath,
            warnings);
    }

    public async Task UpdateSourceAsync(
        string? requestedChannel,
        CancellationToken cancellationToken,
        Action<TestAutomationProgress>? onProgress = null)
    {
        var channel = NormalizeChannel(requestedChannel);
        var root = ResolveProjectRoot()
            ?? throw new InvalidOperationException("Projeto softcom-smart-automation não localizado.");
        var git = _commandResolver("git")
            ?? throw new InvalidOperationException("Git não está disponível no PATH.");

        var status = await RunGitAsync(git, root, ["status", "--porcelain"], cancellationToken, onProgress);
        EnsureGitSuccess(status, "Não foi possível verificar o estado local do Automation.");
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
            throw new InvalidOperationException(
                "O Automation possui alterações locais. Preserve ou finalize essas alterações antes de trocar de branch.");

        onProgress?.Invoke(new TestAutomationProgress(
            DateTimeOffset.Now, "fonte", $"Buscando origin/{channel}...", "INFO"));
        var fetch = await RunGitAsync(git, root, ["fetch", "origin", channel], cancellationToken, onProgress);
        EnsureGitSuccess(fetch, $"Não foi possível buscar origin/{channel}.");

        var current = await RunGitAsync(git, root, ["branch", "--show-current"], cancellationToken, onProgress);
        EnsureGitSuccess(current, "Não foi possível identificar a branch atual do Automation.");
        if (!string.Equals(current.StandardOutput.Trim(), channel, StringComparison.OrdinalIgnoreCase))
        {
            var localBranch = await RunGitAsync(
                git, root, ["show-ref", "--verify", "--quiet", $"refs/heads/{channel}"], cancellationToken, onProgress);
            var switchArguments = localBranch.Success
                ? new[] { "switch", channel }
                : new[] { "switch", "--track", "-c", channel, $"origin/{channel}" };
            var branchSwitch = await RunGitAsync(git, root, switchArguments, cancellationToken, onProgress);
            EnsureGitSuccess(branchSwitch, $"Não foi possível selecionar a branch {channel}.");
        }

        // Atualiza somente a branch selecionada. Nunca integra DEV em master.
        var fastForward = await RunGitAsync(
            git, root, ["merge", "--ff-only", $"origin/{channel}"], cancellationToken, onProgress);
        EnsureGitSuccess(fastForward, $"A branch local {channel} divergiu de origin/{channel}; atualização automática cancelada.");
        onProgress?.Invoke(new TestAutomationProgress(
            DateTimeOffset.Now, "fonte", $"Automation atualizado em {channel}.", "INFO"));
    }

    public async Task<TestAutomationRunResult> RunAsync(
        TestAutomationRunRequest request,
        IReadOnlyList<DeviceInfo> connectedDevices,
        DeviceCatalogSnapshot deviceCatalog,
        CancellationToken cancellationToken,
        Action<TestAutomationProgress>? onProgress = null)
    {
        var catalog = LoadCatalog(connectedDevices, deviceCatalog);
        var root = catalog.ProjectRoot
            ?? throw new InvalidOperationException("Projeto softcom-smart-automation não localizado.");
        if (!catalog.Prerequisites.RunnerAvailable)
            throw new InvalidOperationException("Runner run_tests.ps1 não localizado.");
        if (!catalog.Prerequisites.EnvironmentAvailable)
            throw new InvalidOperationException("Arquivo .env da automação não localizado.");
        if (!catalog.Prerequisites.UvAvailable)
            throw new InvalidOperationException("O comando uv não está disponível no PATH.");

        var connectedDevice = connectedDevices.FirstOrDefault(x =>
            string.Equals(x.Serial, request.Serial, StringComparison.OrdinalIgnoreCase));
        var device = catalog.Devices.FirstOrDefault(x =>
            string.Equals(x.Serial, request.Serial, StringComparison.OrdinalIgnoreCase));
        if (connectedDevice is null || device is null || !device.IsOnline)
            throw new InvalidOperationException("O Android selecionado não está conectado.");

        var deviceTag = NormalizeDeviceTag(request.DeviceTag);
        if (!device.DeviceTags.Contains(deviceTag, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("O perfil selecionado não corresponde ao UDID conectado.");

        var suite = catalog.Suites.FirstOrDefault(x =>
            string.Equals(x.Id, request.SuiteId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Suíte de testes inválida.");
        var testCase = request.TestCase?.Trim() ?? string.Empty;
        if (testCase.Length > 0 && !suite.TestCases.Contains(testCase, StringComparer.Ordinal))
            throw new InvalidOperationException("Caso de teste inválido para a suíte selecionada.");

        var includeTag = NormalizeIncludeTag(request.IncludeTag);
        if (includeTag.Length > 0 && !suite.Tags.Contains(includeTag, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"A tag '{includeTag}' não existe na suíte {suite.Name}. Selecione uma das tags disponíveis.");
        var runnerPath = Path.Combine(root, "run_tests.ps1");
        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", runnerPath,
            "-Debug",
            "-DeviceTag", deviceTag,
            "-Suite", suite.RelativePath
        };
        if (testCase.Length > 0) arguments.AddRange(new[] { "-Test", testCase });
        if (includeTag.Length > 0) arguments.AddRange(new[] { "-Include", includeTag });
        string? campaignName = null;
        if (SupportsCampaignRunner(runnerPath))
        {
            campaignName = $"provisioner-{DateTime.Now:yyyyMMdd-HHmmss}-{deviceTag}";
            arguments.AddRange(new[] { "-Campaign", campaignName, "-SaveReports", "all" });
        }
        arguments.Add("-NoAllureOpen");

        var appiumServerUrl = ResolveLocalAppiumServerUrl();
        var childEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["APPIUM_SERVER_URL"] = appiumServerUrl
        };
        var legacyAppiumHome = ResolveLegacyAppiumHome(root, connectedDevice);
        if (legacyAppiumHome is not null)
            childEnvironment["APPIUM_HOME"] = legacyAppiumHome;

        var stopwatch = Stopwatch.StartNew();
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = ReportHeartbeatAsync(stopwatch, onProgress, heartbeatCancellation.Token);
        try
        {
            onProgress?.Invoke(new TestAutomationProgress(
                DateTimeOffset.Now, "preparando", $"Preparando {suite.Name} para {device.FriendlyName}. Appium: {appiumServerUrl}", "INFO"));
            if (legacyAppiumHome is not null)
            {
                onProgress?.Invoke(new TestAutomationProgress(
                    DateTimeOffset.Now,
                    "appium",
                    "Android 7 detectado. Usando o ambiente Appium compativel com o K2.",
                    "INFO"));
            }
            var result = await _processRunner(
                ResolvePowerShell(), arguments, root, cancellationToken, TestTimeoutMilliseconds,
                line => ForwardSafeProgress(line, onProgress), childEnvironment);
            stopwatch.Stop();
            var reportPath = campaignName is null
                ? ResolveLatestReportPath(root)
                : ExistingFileOrNull(Path.Combine(root, "results", "campaigns", campaignName, "index.html"));
            var success = result.Success;
            return new TestAutomationRunResult(
                success,
                false,
                result.ExitCode,
                request.Serial,
                deviceTag,
                suite.Name,
                testCase,
                stopwatch.ElapsedMilliseconds,
                success ? "Testes concluídos com sucesso." : "A execução terminou com falhas.",
                File.Exists(reportPath) ? reportPath : null,
                BuildSafeSummary(result.CombinedOutput));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new TestAutomationRunResult(
                false, true, -1, request.Serial, deviceTag, suite.Name, testCase,
                stopwatch.ElapsedMilliseconds, "Execução cancelada pelo usuário.", null, string.Empty);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
        }
    }

    public string? GetExistingReportPath()
    {
        var root = ResolveProjectRoot();
        if (root is null) return null;
        return ResolveLatestReportPath(root);
    }

    public string? ResolveProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SOFTCOM_SMART_AUTOMATION_ROOT");
        var candidates = new List<string?> { _explicitRoot, configured };
        AddRootCandidates(candidates, Directory.GetCurrentDirectory());
        AddRootCandidates(candidates, AppContext.BaseDirectory);
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(IsAutomationRoot);
    }

    public static IReadOnlyList<TestAutomationSuite> DiscoverSuites(string root)
    {
        var testsRoot = Path.Combine(Path.GetFullPath(root), "tests", "regression");
        if (!Directory.Exists(testsRoot)) return Array.Empty<TestAutomationSuite>();

        return Directory.EnumerateFiles(testsRoot, "*.robot", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var id = Path.GetRelativePath(testsRoot, path).Replace('\\', '/');
                var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? Path.GetFileNameWithoutExtension(path);
                var lines = File.ReadAllLines(path);
                return new TestAutomationSuite(
                    id,
                    DisplaySuiteName(folder),
                    relative,
                    ParseTestCases(lines),
                    ParseTags(lines));
            })
            .ToArray();
    }

    public static IReadOnlyList<string> ParseTestCases(IEnumerable<string> lines)
    {
        var testCases = new List<string>();
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Equals("*** Test Cases ***", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }
            if (inSection && line.TrimStart().StartsWith("***", StringComparison.Ordinal)) break;
            if (!inSection || line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            testCases.Add(line.Trim());
        }
        return testCases;
    }

    public static IReadOnlyList<string> ParseTags(IEnumerable<string> lines) =>
        lines
            .Select(line => TagsLine().Match(line))
            .Where(match => match.Success)
            .SelectMany(match => TagSeparator().Split(match.Groups[1].Value.Trim()))
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0 && !tag.StartsWith("@allure.", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<DeviceTagMapping> ParseDeviceMappings(IEnumerable<string> lines)
    {
        var mappings = new List<DeviceTagMapping>();
        string? currentTag = null;
        foreach (var line in lines)
        {
            var tagMatch = DeviceTagLine().Match(line);
            if (tagMatch.Success)
            {
                currentTag = tagMatch.Groups[1].Value;
                continue;
            }
            if (currentTag is null) continue;
            var udidMatch = UdidVariableLine().Match(line);
            if (udidMatch.Success)
                mappings.Add(new DeviceTagMapping(currentTag, udidMatch.Groups[1].Value));
        }
        return mappings;
    }

    private static TestAutomationDevice BuildDevice(
        DeviceInfo device,
        DeviceCatalogSnapshot catalog,
        IReadOnlyList<DeviceTagMapping> mappings)
    {
        var entries = catalog.Entries
            .Where(x => string.Equals(x.Serial, device.Serial, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length > 1 && !string.IsNullOrWhiteSpace(device.TerminalModel))
        {
            var modelEntries = entries.Where(x => Normalize(x.TerminalModel) == Normalize(device.TerminalModel)).ToArray();
            if (modelEntries.Length == 1) entries = modelEntries;
        }

        var variables = entries.Select(x => x.VariableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = mappings
            .Where(x => variables.Contains(x.UdidVariable))
            .Select(x => x.DeviceTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var suggested = tags.Length == 1 ? tags[0] : string.Empty;
        return new TestAutomationDevice(
            device.Serial,
            device.FriendlyName,
            device.AndroidVersion,
            device.SmartVersion,
            device.IsOnline,
            tags,
            suggested,
            tags.Length != 1);
    }

    private static void AddRootCandidates(ICollection<string?> candidates, string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        for (var level = 0; directory is not null && level < 9; level++, directory = directory.Parent)
        {
            candidates.Add(directory.FullName);
            candidates.Add(Path.Combine(directory.FullName, "softcom-smart-automation"));
        }
    }

    private static bool IsAutomationRoot(string path) =>
        File.Exists(Path.Combine(path, "run_tests.ps1")) &&
        Directory.Exists(Path.Combine(path, "tests"));

    private async Task<ProcessResult> RunGitAsync(
        string git,
        string root,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        Action<TestAutomationProgress>? onProgress)
    {
        return await _processRunner(
            git, arguments, root, cancellationToken, GitTimeoutMilliseconds,
            line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                onProgress?.Invoke(new TestAutomationProgress(
                    DateTimeOffset.Now, "fonte", SensitiveDataSanitizer.Clean(line.Trim()), "INFO"));
            }, null);
    }

    private static string? ResolveLegacyAppiumHome(string automationRoot, DeviceInfo device)
    {
        if (!int.TryParse(device.AndroidSdk, out var sdk) || sdk >= 26) return null;

        var configured = Environment.GetEnvironmentVariable("SOFTCOM_LEGACY_APPIUM_HOME")?.Trim();
        var candidates = new[]
        {
            configured,
            Path.Combine(Directory.GetParent(automationRoot)?.FullName ?? automationRoot, ".appium-k2"),
            Path.Combine(automationRoot, ".appium-k2")
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (File.Exists(Path.Combine(fullPath, "node_modules", "appium-uiautomator2-driver", "package.json")))
                return fullPath;
        }

        throw new InvalidOperationException(
            "O Android 7 exige o ambiente Appium legado. Configure SOFTCOM_LEGACY_APPIUM_HOME " +
            "ou instale o driver compativel na pasta .appium-k2 ao lado do projeto Automation.");
    }

    private static void EnsureGitSuccess(ProcessResult result, string message)
    {
        if (result.Success) return;
        var details = SensitiveDataSanitizer.Clean(
            string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? message : $"{message} {details}");
    }

    private TestAutomationSourceInfo ReadSourceInfo(string root)
    {
        var git = _commandResolver("git");
        if (git is null || !Directory.Exists(Path.Combine(root, ".git")))
            return new TestAutomationSourceInfo(false, string.Empty, string.Empty, false, SupportedChannels);

        var branch = RunGitReadOnly(git, root, ["branch", "--show-current"]);
        var commit = RunGitReadOnly(git, root, ["rev-parse", "--short", "HEAD"]);
        var status = RunGitReadOnly(git, root, ["status", "--porcelain"]);
        return new TestAutomationSourceInfo(
            true,
            branch.Trim(),
            commit.Trim(),
            !string.IsNullOrWhiteSpace(status),
            SupportedChannels);
    }

    private static string RunGitReadOnly(string git, string root, IEnumerable<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = git,
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return string.Empty;
            }
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeChannel(string? value)
    {
        var channel = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return channel switch
        {
            "master" => "master",
            "dev" => "dev",
            "dev-sarmento" => "DEV-Sarmento",
            _ => throw new InvalidOperationException(
                "Canal do Automation inválido. Escolha master, dev ou DEV-Sarmento.")
        };
    }

    private static bool SupportsCampaignRunner(string runnerPath)
    {
        try
        {
            var content = File.ReadAllText(runnerPath);
            return content.Contains("$Campaign", StringComparison.Ordinal) &&
                   content.Contains("$SaveReports", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveLatestReportPath(string root)
    {
        var legacy = ExistingFileOrNull(Path.Combine(root, "allure-report", "index.html"));
        var campaignsRoot = Path.Combine(root, "results", "campaigns");
        if (!Directory.Exists(campaignsRoot)) return legacy;
        var campaignReport = Directory.EnumerateFiles(campaignsRoot, "index.html", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
        return campaignReport ?? legacy;
    }

    private static string? ExistingFileOrNull(string path) => File.Exists(path) ? path : null;

    private static string DisplaySuiteName(string value) => value.ToLowerInvariant() switch
    {
        "pdv" => "PDV",
        "commands" => "Comanda",
        "minimarket" => "Minimercado",
        "totem" => "Autoatendimento (Totem)",
        _ => value
    };

    private static string NormalizeDeviceTag(string? value)
    {
        var tag = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SafeDeviceTag().IsMatch(tag)) throw new InvalidOperationException("Perfil de dispositivo inválido.");
        return tag;
    }

    private static string NormalizeIncludeTag(string? value)
    {
        var tag = value?.Trim() ?? string.Empty;
        if (tag.Length > 80 || tag.Any(char.IsControl))
            throw new InvalidOperationException("Tag de filtro inválida.");
        return tag;
    }

    private static string BuildSafeSummary(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => RobotSummaryLine().IsMatch(line) ||
                           line.StartsWith("Debug finalizado", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith("CAMPAIGN ERROR:", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(12)
            .Select(SensitiveDataSanitizer.Clean)
            .ToArray();
        return string.Join(Environment.NewLine, lines);
    }

    private static void ForwardSafeProgress(string rawLine, Action<TestAutomationProgress>? onProgress)
    {
        if (onProgress is null) return;
        var line = rawLine.Trim();
        if (line.Length == 0 || !IsUsefulProgressLine(line)) return;

        var clean = SensitiveDataSanitizer.Clean(line);
        var level = line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("| FAIL |", StringComparison.OrdinalIgnoreCase)
            ? "ERROR"
            : line.StartsWith("WARN", StringComparison.OrdinalIgnoreCase) || line.Contains("| SKIP |", StringComparison.OrdinalIgnoreCase)
                ? "WARN"
                : "INFO";
        var stage = line.Contains("Appium", StringComparison.OrdinalIgnoreCase)
            ? "appium"
            : line.Contains("| PASS |", StringComparison.OrdinalIgnoreCase) || line.Contains("| FAIL |", StringComparison.OrdinalIgnoreCase) || line.Contains("| SKIP |", StringComparison.OrdinalIgnoreCase)
                ? "teste"
                : line.Contains("Report", StringComparison.OrdinalIgnoreCase) || line.Contains("Allure", StringComparison.OrdinalIgnoreCase)
                    ? "relatório"
                    : "execução";
        onProgress(new TestAutomationProgress(DateTimeOffset.Now, stage, clean, level));
    }

    private static async Task ReportHeartbeatAsync(
        Stopwatch stopwatch,
        Action<TestAutomationProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        if (onProgress is null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var elapsed = (int)stopwatch.Elapsed.TotalSeconds;
            onProgress(new TestAutomationProgress(
                DateTimeOffset.Now,
                "execução",
                $"Teste em andamento há {elapsed}s...",
                "INFO"));
        }
    }

    private static bool IsUsefulProgressLine(string line) =>
        line.StartsWith('>') ||
        line.StartsWith("OK:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("CAMPAIGN ERROR:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Campanha:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Lote:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Resumo da campanha:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("DeviceTag:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Suite:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Test:", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("| PASS |", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("| FAIL |", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("| SKIP |", StringComparison.OrdinalIgnoreCase) ||
        RobotSummaryLine().IsMatch(line);

    private static string ResolvePowerShell()
    {
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "powershell.exe";
    }

    public static string ResolveLocalAppiumServerUrl()
    {
        var candidates = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is not System.Net.NetworkInformation.NetworkInterfaceType.Loopback and
                              not System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(address => new
                {
                    address.Address,
                    HasGateway = adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !gateway.Address.Equals(System.Net.IPAddress.Any))
                }))
            .Where(item => !System.Net.IPAddress.IsLoopback(item.Address) &&
                           !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .OrderByDescending(item => item.HasGateway)
            .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
        var address = candidates.FirstOrDefault()?.Address.ToString() ?? "127.0.0.1";
        return $"http://{address}:4723";
    }

    private static string? FindCommand(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            foreach (var name in new[] { command }.Concat(extensions.Select(extension => command + extension.ToLowerInvariant())))
            {
                var candidate = Path.Combine(cleanDirectory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    [GeneratedRegex("^\\s{2}([A-Za-z0-9_]+):\\s*$")]
    private static partial Regex DeviceTagLine();

    [GeneratedRegex("^\\s{4}udid:\\s*[\"']?\\$\\{([A-Za-z_][A-Za-z0-9_]*)\\}[\"']?\\s*$")]
    private static partial Regex UdidVariableLine();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex SafeDeviceTag();

    [GeneratedRegex("(?i)(\\d+\\s+tests?,\\s+\\d+\\s+passed,\\s+\\d+\\s+failed|all tests passed|tests? failed)")]
    private static partial Regex RobotSummaryLine();

    [GeneratedRegex("^\\s*\\[Tags\\]\\s{2,}(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TagsLine();

    [GeneratedRegex("(?:\\t+|\\s{2,})")]
    private static partial Regex TagSeparator();
}

public sealed record DeviceTagMapping(string DeviceTag, string UdidVariable);
