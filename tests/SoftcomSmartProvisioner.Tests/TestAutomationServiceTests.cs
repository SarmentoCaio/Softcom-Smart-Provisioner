using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class TestAutomationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smart-provisioner-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindsAutomationProjectWhenInstalledBesideProjetos()
    {
        var installed = Path.Combine(_root, "Softcom Smart Provisioner");
        var automation = Path.Combine(_root, "Projetos", "softcom-smart-automation");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(Path.Combine(automation, "tests", "regression", "pdv"));
        File.WriteAllText(Path.Combine(automation, "run_tests.ps1"), "param()");
        File.WriteAllText(Path.Combine(automation, "tests", "regression", "pdv", "pdv.robot"),
            "*** Test Cases ***\nCT01 - Pedido\n    No Operation\n");

        var service = new TestAutomationService(
            commandResolver: _ => null,
            workingDirectory: installed,
            baseDirectory: installed);

        Assert.Equal(automation, service.ResolveProjectRoot());
        var catalog = service.LoadCatalog(Array.Empty<DeviceInfo>(), DeviceCatalogService.Parse(Array.Empty<string>()));
        Assert.Equal(automation, catalog.ProjectRoot);
        Assert.Single(catalog.Suites);
    }

    [Fact]
    public async Task FirstUseClonesDevIntoManagedFolderWithoutBundlingEnvironment()
    {
        var installed = Path.Combine(_root, "installed");
        var managed = Path.Combine(_root, "local", "automation");
        Directory.CreateDirectory(installed);
        string[]? cloneArguments = null;
        var service = new TestAutomationService(
            processRunner: (_, arguments, _, _, _, _, _) =>
            {
                cloneArguments = arguments.ToArray();
                var staging = cloneArguments[^1];
                Directory.CreateDirectory(Path.Combine(staging, ".git"));
                Directory.CreateDirectory(Path.Combine(staging, "tests"));
                File.WriteAllText(Path.Combine(staging, "run_tests.ps1"), "param()");
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            },
            commandResolver: name => name == "git" ? "git.exe" : null,
            workingDirectory: installed,
            baseDirectory: installed,
            managedRoot: managed);

        await service.CloneForFirstUseAsync(CancellationToken.None);

        Assert.NotNull(cloneArguments);
        Assert.Equal("clone", cloneArguments![0]);
        Assert.Contains("dev", cloneArguments);
        Assert.Equal(managed, service.ResolveProjectRoot());
        Assert.False(File.Exists(Path.Combine(managed, ".env")));
        Assert.False(service.LoadCatalog(Array.Empty<DeviceInfo>(),
            DeviceCatalogService.Parse(Array.Empty<string>())).Prerequisites.EnvironmentAvailable);
    }

    [Fact]
    public void EnvironmentImportNeverOverwritesExistingFile()
    {
        CreateAutomationProject();
        var original = File.ReadAllText(Path.Combine(_root, ".env"));
        var sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, ".env");
        File.WriteAllText(source, "STONE_UDID=OTHER");
        var service = CreateService();

        Assert.Throws<InvalidOperationException>(() => service.ImportEnvironment(source));
        Assert.Equal(original, File.ReadAllText(Path.Combine(_root, ".env")));
    }

    [Fact]
    public void EnvironmentImportCopiesExistingConfigurationOnlyLocally()
    {
        CreateAutomationProject();
        File.Delete(Path.Combine(_root, ".env"));
        var sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, ".env");
        File.WriteAllText(source, "STONE_UDID=STONE123\nSMART_LOGIN_EMAIL=qa@example.test\n");
        var service = CreateService();

        service.ImportEnvironment(source);

        Assert.Equal(File.ReadAllText(source), File.ReadAllText(Path.Combine(_root, ".env")));
        Assert.True(service.LoadCatalog(Array.Empty<DeviceInfo>(),
            DeviceCatalogService.Parse(Array.Empty<string>())).Prerequisites.EnvironmentAvailable);
    }

    [Fact]
    public async Task WorktreeGitFileCanUpdateAnArbitraryRemoteBranch()
    {
        CreateAutomationProject();
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: elsewhere");
        var invocations = new List<string[]>();
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _, _) =>
            {
                var values = arguments.ToArray();
                invocations.Add(values);
                var output = values.SequenceEqual(new[] { "rev-parse", "--show-toplevel" }) ? _root :
                    values.SequenceEqual(new[] { "branch", "--show-current" }) ? "dev" : string.Empty;
                var exitCode = values.SequenceEqual(new[] { "show-ref", "--verify", "--quiet", "refs/heads/feature/qa" }) ? 1 : 0;
                return Task.FromResult(new ProcessResult(exitCode, output, string.Empty));
            }, _ => "git.exe");

        await service.UpdateSourceAsync("feature/qa", CancellationToken.None);

        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "fetch", "--prune", "origin", "+refs/heads/*:refs/remotes/origin/*" }));
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "switch", "--track", "-c", "feature/qa", "origin/feature/qa" }));
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "merge", "--ff-only", "origin/feature/qa" }));
    }

    [Fact]
    public async Task DivergedTargetBranchIsRejectedBeforeSwitching()
    {
        CreateAutomationProject();
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var invocations = new List<string[]>();
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _, _) =>
            {
                var values = arguments.ToArray();
                invocations.Add(values);
                var output = values.SequenceEqual(new[] { "rev-parse", "--show-toplevel" }) ? _root :
                    values.SequenceEqual(new[] { "branch", "--show-current" }) ? "dev" : string.Empty;
                var exitCode = values.FirstOrDefault() == "merge-base" ? 1 : 0;
                return Task.FromResult(new ProcessResult(exitCode, output, string.Empty));
            }, _ => "git.exe");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateSourceAsync("master", CancellationToken.None));

        Assert.Contains("divergiu", error.Message);
        Assert.DoesNotContain(invocations, args => args.FirstOrDefault() == "switch");
    }

    [Fact]
    public void ParsesOnlyRealTestCaseHeadings()
    {
        var tests = TestAutomationService.ParseTestCases(new[]
        {
            "*** Settings ***",
            "Library    Example",
            "*** Test Cases ***",
            "CT01 - Pedido comum",
            "    Keyword    value",
            "",
            "CT02 - Cancelamento",
            "    Another Keyword",
            "*** Keywords ***",
            "Helper",
        });

        Assert.Equal(new[] { "CT01 - Pedido comum", "CT02 - Cancelamento" }, tests);
    }

    [Fact]
    public void DiscoversAvailableTagsAndIgnoresAllureMetadata()
    {
        var tags = TestAutomationService.ParseTags(new[]
        {
            "    [Tags]    @allure.label.severity:critical    regression    pdv    orders",
            "    [Tags]    regression    cancel"
        });

        Assert.Equal(new[] { "cancel", "orders", "pdv", "regression" }, tags);
        Assert.DoesNotContain(tags, tag => tag.StartsWith("@allure."));
    }

    [Fact]
    public void MapsConnectedDeviceToAutomationTagThroughUdidVariable()
    {
        CreateAutomationProject();
        var service = CreateService();
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123" });

        var catalog = service.LoadCatalog(new[] { Device("STONE123", "Stone") }, deviceCatalog);

        var device = Assert.Single(catalog.Devices);
        Assert.Equal("stone", device.SuggestedDeviceTag);
        Assert.Equal(new[] { "stone" }, device.DeviceTags);
        Assert.True(device.AutoCampaignSupported);
        Assert.Equal("PDV", Assert.Single(catalog.Suites).Name);
        Assert.Equal(2, catalog.Suites[0].TestCases.Count);
    }

    [Fact]
    public void DiscoversTotemSuiteWithFriendlyName()
    {
        CreateAutomationProject();
        var suiteDirectory = Path.Combine(_root, "tests", "regression", "totem");
        Directory.CreateDirectory(suiteDirectory);
        File.WriteAllText(Path.Combine(suiteDirectory, "totem.robot"),
            "*** Test Cases ***\nCT01 - Totem - Home Ready\n    [Tags]    smoke    totem\n    No Operation\n");

        var suite = TestAutomationService.DiscoverSuites(_root)
            .Single(item => item.Id == "totem/totem.robot");

        Assert.Equal("Autoatendimento (Totem)", suite.Name);
        Assert.Equal(new[] { "CT01 - Totem - Home Ready" }, suite.TestCases);
    }

    [Fact]
    public void MapsK2ToTotemAutomationProfile()
    {
        CreateAutomationProject();
        File.AppendAllText(Path.Combine(_root, ".env"), "\nTOTEM_K2_UDID=KM54257740097\n");
        File.AppendAllText(Path.Combine(_root, "resources", "data", "devices.yaml"),
            "  totem_k2:\n    udid: \"${TOTEM_K2_UDID}\"\n");
        var service = CreateService();
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "TOTEM_K2_UDID=KM54257740097" });

        var device = Assert.Single(service.LoadCatalog(
            new[] { Device("KM54257740097", "Totem - K2") }, deviceCatalog).Devices);

        Assert.Equal("totem_k2", device.SuggestedDeviceTag);
        Assert.Equal(new[] { "totem_k2" }, device.DeviceTags);
        Assert.False(device.RequiresProfileSelection);
    }

    [Fact]
    public void DuplicateAutomationProfilesRequireExplicitSelection()
    {
        CreateAutomationProject(includeAlias: true);
        var service = CreateService();
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123" });

        var device = Assert.Single(service.LoadCatalog(new[] { Device("STONE123", "Stone") }, deviceCatalog).Devices);

        Assert.True(device.RequiresProfileSelection);
        Assert.Equal(string.Empty, device.SuggestedDeviceTag);
        Assert.Equal(new[] { "stone", "stone_alias" }, device.DeviceTags);
    }

    [Fact]
    public async Task RunUsesValidatedRunnerArgumentsWithoutPuttingSerialOnCommandLine()
    {
        CreateAutomationProject();
        IReadOnlyList<string>? capturedArguments = null;
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, progress, _) =>
            {
                capturedArguments = arguments.ToArray();
                progress?.Invoke("> Iniciando execução...");
                return Task.FromResult(new ProcessResult(0, "1 test, 1 passed, 0 failed", string.Empty));
            },
            _ => "available");
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123" });

        var result = await service.RunAsync(
            new TestAutomationRunRequest("STONE123", "stone", "pdv/pdv.robot", "CT01 - Pedido", null),
            new[] { Device("STONE123", "Stone") }, deviceCatalog, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Contains("-Debug", capturedArguments!);
        Assert.Contains("stone", capturedArguments!);
        Assert.Contains("tests/regression/pdv/pdv.robot", capturedArguments!);
        Assert.DoesNotContain("STONE123", capturedArguments!);
    }

    [Fact]
    public async Task DevCampaignRunnerReceivesNonInteractiveReportArguments()
    {
        CreateAutomationProject(campaignRunner: true);
        IReadOnlyList<string>? capturedArguments = null;
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _, _) =>
            {
                capturedArguments = arguments.ToArray();
                return Task.FromResult(new ProcessResult(0, "1 test, 1 passed, 0 failed", string.Empty));
            },
            _ => "available");
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123" });

        var result = await service.RunAsync(
            new TestAutomationRunRequest("STONE123", "stone", "pdv/pdv.robot", "CT01 - Pedido", null),
            new[] { Device("STONE123", "Stone") }, deviceCatalog, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Contains("-Campaign", capturedArguments!);
        Assert.Contains("-SaveReports", capturedArguments!);
        Assert.Contains("all", capturedArguments!);
        Assert.Contains(capturedArguments!, value => value.StartsWith("provisioner-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelCampaignUsesOneRunnerWithDistinctDeviceTags()
    {
        CreateAutomationProject(campaignRunner: true);
        File.AppendAllText(Path.Combine(_root, ".env"), "\nGETNET_P2_UDID=P2SERIAL\n");
        File.AppendAllText(Path.Combine(_root, "resources", "data", "devices.yaml"),
            "  getnet_p2:\n    udid: \"${GETNET_P2_UDID}\"\n");
        IReadOnlyList<string>? capturedArguments = null;
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _, _) =>
            {
                capturedArguments = arguments.ToArray();
                return Task.FromResult(new ProcessResult(0, "2 tests, 2 passed, 0 failed", string.Empty));
            },
            _ => "available");
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123", "GETNET_P2_UDID=P2SERIAL" });

        var result = await service.RunParallelAsync("pdv/pdv.robot",
            [("STONE123", "stone"), ("P2SERIAL", "getnet_p2")],
            [Device("STONE123", "Stone"), Device("P2SERIAL", "Getnet P2")],
            deviceCatalog, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Contains("-DeviceTags", capturedArguments!);
        Assert.Contains("stone,getnet_p2", capturedArguments!);
        Assert.DoesNotContain("-Debug", capturedArguments!);
        Assert.DoesNotContain("STONE123", capturedArguments!);
        Assert.Contains("-Campaign", capturedArguments!);
    }

    [Fact]
    public async Task Android7RunUsesDedicatedLegacyAppiumHome()
    {
        CreateAutomationProject();
        var legacyHome = Path.Combine(_root, ".appium-k2");
        Directory.CreateDirectory(Path.Combine(legacyHome, "node_modules", "appium-uiautomator2-driver"));
        File.WriteAllText(
            Path.Combine(legacyHome, "node_modules", "appium-uiautomator2-driver", "package.json"),
            "{}");
        IReadOnlyDictionary<string, string?>? capturedEnvironment = null;
        var service = new TestAutomationService(
            _root,
            (_, _, _, _, _, _, environment) =>
            {
                capturedEnvironment = environment;
                return Task.FromResult(new ProcessResult(0, "1 test, 1 passed, 0 failed", string.Empty));
            },
            _ => "available");
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123" });
        var android7 = Device("STONE123", "K2") with { AndroidSdk = "25" };

        var result = await service.RunAsync(
            new TestAutomationRunRequest("STONE123", "stone", "pdv/pdv.robot", null, null),
            new[] { android7 }, deviceCatalog, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedEnvironment);
        Assert.Equal(Path.GetFullPath(legacyHome), capturedEnvironment!["APPIUM_HOME"]);
        Assert.StartsWith("http://", capturedEnvironment["APPIUM_SERVER_URL"]);
    }

    [Fact]
    public async Task SourceUpdateRefusesDirtyAutomationWithoutFetchingOrSwitching()
    {
        CreateAutomationProject();
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var invocations = new List<string[]>();
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _, _) =>
            {
                var values = arguments.ToArray();
                invocations.Add(values);
                var output = values.SequenceEqual(new[] { "rev-parse", "--show-toplevel" })
                    ? _root : " M run_tests.ps1";
                return Task.FromResult(new ProcessResult(0, output, string.Empty));
            },
            _ => "available");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateSourceAsync("dev", CancellationToken.None));

        Assert.Contains("alterações locais", error.Message);
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "status", "--porcelain" }));
        Assert.DoesNotContain(invocations, args => args.Contains("fetch"));
    }

    [Fact]
    public async Task SourceUpdateFetchesSwitchesAndFastForwardsSelectedBranchOnly()
    {
        CreateAutomationProject();
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var invocations = new List<string[]>();
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _, _) =>
            {
                var values = arguments.ToArray();
                invocations.Add(values);
                var output = values.SequenceEqual(new[] { "branch", "--show-current" }) ? "master\n" :
                    values.SequenceEqual(new[] { "rev-parse", "--show-toplevel" }) ? _root : string.Empty;
                var exitCode = values.SequenceEqual(new[] { "show-ref", "--verify", "--quiet", "refs/heads/dev" }) ? 1 : 0;
                return Task.FromResult(new ProcessResult(exitCode, output, string.Empty));
            },
            _ => "available");

        await service.UpdateSourceAsync("dev", CancellationToken.None);

        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "fetch", "--prune", "origin", "+refs/heads/*:refs/remotes/origin/*" }));
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "switch", "--track", "-c", "dev", "origin/dev" }));
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "merge", "--ff-only", "origin/dev" }));
        Assert.DoesNotContain(invocations, args => args.Contains("origin/master", StringComparer.Ordinal));
    }

    [Fact]
    public async Task SourceUpdateSupportsSarmentoBranchWithoutMergingDevOrMaster()
    {
        CreateAutomationProject();
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var invocations = new List<string[]>();
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _, _) =>
            {
                var values = arguments.ToArray();
                invocations.Add(values);
                var output = values.SequenceEqual(new[] { "branch", "--show-current" }) ? "dev\n" :
                    values.SequenceEqual(new[] { "rev-parse", "--show-toplevel" }) ? _root : string.Empty;
                var exitCode = values.SequenceEqual(new[] { "show-ref", "--verify", "--quiet", "refs/heads/DEV-Sarmento" }) ? 1 : 0;
                return Task.FromResult(new ProcessResult(exitCode, output, string.Empty));
            },
            _ => "available");

        await service.UpdateSourceAsync("DEV-Sarmento", CancellationToken.None);

        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "fetch", "--prune", "origin", "+refs/heads/*:refs/remotes/origin/*" }));
        Assert.Contains(invocations, args => args.SequenceEqual(
            new[] { "switch", "--track", "-c", "DEV-Sarmento", "origin/DEV-Sarmento" }));
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "merge", "--ff-only", "origin/DEV-Sarmento" }));
        Assert.DoesNotContain(invocations, args => args.Contains("origin/dev", StringComparer.Ordinal));
        Assert.DoesNotContain(invocations, args => args.Contains("origin/master", StringComparer.Ordinal));
    }

    [Fact]
    public async Task CancellationIsReturnedAsCanceledResult()
    {
        CreateAutomationProject();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new TestAutomationService(
            _root,
            (_, _, _, token, _, _, _) => Task.FromCanceled<ProcessResult>(token),
            _ => "available");
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123" });

        var result = await service.RunAsync(
            new TestAutomationRunRequest("STONE123", "stone", "pdv/pdv.robot", null, null),
            new[] { Device("STONE123", "Stone") }, deviceCatalog, cancellation.Token);

        Assert.True(result.Canceled);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UnknownTagIsRejectedBeforeStartingRobot()
    {
        CreateAutomationProject();
        var runnerCalled = false;
        var service = new TestAutomationService(
            _root,
            (_, _, _, _, _, _, _) =>
            {
                runnerCalled = true;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            },
            _ => "available");
        var deviceCatalog = DeviceCatalogService.Parse(new[] { "STONE_UDID=STONE123" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            new TestAutomationRunRequest("STONE123", "stone", "pdv/pdv.robot", null, "smoke"),
            new[] { Device("STONE123", "Stone") }, deviceCatalog, CancellationToken.None));

        Assert.Contains("não existe", error.Message);
        Assert.False(runnerCalled);
    }

    [Fact]
    public void LocalAppiumUrlUsesAnIpv4AddressAndNeverLocalhost()
    {
        var url = TestAutomationService.ResolveLocalAppiumServerUrl();

        Assert.StartsWith("http://", url);
        Assert.EndsWith(":4723", url);
        Assert.DoesNotContain("localhost", url, StringComparison.OrdinalIgnoreCase);
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        Assert.True(System.Net.IPAddress.TryParse(uri!.Host, out _));
    }

    private TestAutomationService CreateService() => new(_root, commandResolver: _ => "available");

    private void CreateAutomationProject(bool includeAlias = false, bool campaignRunner = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "resources", "data"));
        Directory.CreateDirectory(Path.Combine(_root, "tests", "regression", "pdv"));
        File.WriteAllText(Path.Combine(_root, "run_tests.ps1"),
            campaignRunner ? "param([string]$Campaign, [string]$SaveReports)" : "param()");
        File.WriteAllText(Path.Combine(_root, ".env"), "STONE_UDID=STONE123");
        var yaml = "devices:\n  stone:\n    udid: \"${STONE_UDID}\"\n";
        if (includeAlias) yaml += "  stone_alias:\n    udid: \"${STONE_UDID}\"\n";
        File.WriteAllText(Path.Combine(_root, "resources", "data", "devices.yaml"), yaml);
        File.WriteAllText(Path.Combine(_root, "tests", "regression", "pdv", "pdv.robot"),
            "*** Test Cases ***\nCT01 - Pedido\n    [Tags]    regression    pdv\n    No Operation\nCT02 - Cancela\n    [Tags]    regression    cancel\n    No Operation\n");
    }

    private static DeviceInfo Device(string serial, string friendlyName) =>
        new(serial, "device", "L400", "", "", "11", null, null, "android-id", "8.1.0", "modern")
        {
            FriendlyName = friendlyName,
            IsKnownDevice = true,
            Acquirer = friendlyName,
            TerminalModel = string.Empty
        };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
