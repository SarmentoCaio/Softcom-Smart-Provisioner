using System.Diagnostics;
using System.Text.RegularExpressions;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed partial class TestAutomationService
{
    private const int TestTimeoutMilliseconds = 2 * 60 * 60 * 1000;
    private readonly string? _explicitRoot;
    private readonly Func<string, IEnumerable<string>, string, CancellationToken, int, Action<string>?, Task<ProcessResult>> _processRunner;
    private readonly Func<string, string?> _commandResolver;

    public TestAutomationService(
        string? explicitRoot = null,
        Func<string, IEnumerable<string>, string, CancellationToken, int, Action<string>?, Task<ProcessResult>>? processRunner = null,
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

        var reportPath = Path.Combine(root, "allure-report", "index.html");
        return new TestAutomationCatalog(
            root,
            new TestAutomationPrerequisites(
                true,
                File.Exists(runnerPath),
                File.Exists(environmentPath),
                File.Exists(devicesPath),
                _commandResolver("uv") is not null,
                _commandResolver("appium") is not null),
            suites,
            devices,
            File.Exists(reportPath) ? reportPath : null,
            warnings);
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

        var device = catalog.Devices.FirstOrDefault(x =>
            string.Equals(x.Serial, request.Serial, StringComparison.OrdinalIgnoreCase));
        if (device is null || !device.IsOnline)
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
        arguments.Add("-NoAllureOpen");

        var appiumServerUrl = ResolveLocalAppiumServerUrl();
        var previousAppiumServerUrl = Environment.GetEnvironmentVariable("APPIUM_SERVER_URL", EnvironmentVariableTarget.Process);

        var stopwatch = Stopwatch.StartNew();
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = ReportHeartbeatAsync(stopwatch, onProgress, heartbeatCancellation.Token);
        try
        {
            Environment.SetEnvironmentVariable("APPIUM_SERVER_URL", appiumServerUrl, EnvironmentVariableTarget.Process);
            onProgress?.Invoke(new TestAutomationProgress(
                DateTimeOffset.Now, "preparando", $"Preparando {suite.Name} para {device.FriendlyName}. Appium: {appiumServerUrl}", "INFO"));
            var result = await _processRunner(
                ResolvePowerShell(), arguments, root, cancellationToken, TestTimeoutMilliseconds,
                line => ForwardSafeProgress(line, onProgress));
            stopwatch.Stop();
            var reportPath = Path.Combine(root, "allure-report", "index.html");
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
            Environment.SetEnvironmentVariable("APPIUM_SERVER_URL", previousAppiumServerUrl, EnvironmentVariableTarget.Process);
            heartbeatCancellation.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
        }
    }

    public string? GetExistingReportPath()
    {
        var root = ResolveProjectRoot();
        if (root is null) return null;
        var report = Path.Combine(root, "allure-report", "index.html");
        return File.Exists(report) ? report : null;
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

    private static string DisplaySuiteName(string value) => value.ToLowerInvariant() switch
    {
        "pdv" => "PDV",
        "commands" => "Comanda",
        "minimarket" => "Minimercado",
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
