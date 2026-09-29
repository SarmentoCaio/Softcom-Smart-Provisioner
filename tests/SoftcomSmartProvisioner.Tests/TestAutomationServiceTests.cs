using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class TestAutomationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smart-provisioner-tests-" + Guid.NewGuid().ToString("N"));

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
        Assert.Equal("PDV", Assert.Single(catalog.Suites).Name);
        Assert.Equal(2, catalog.Suites[0].TestCases.Count);
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
            (_, arguments, _, _, _, progress) =>
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
            (_, arguments, _, _, _, _) =>
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
    public async Task SourceUpdateRefusesDirtyAutomationWithoutFetchingOrSwitching()
    {
        CreateAutomationProject();
        var invocations = new List<string[]>();
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _) =>
            {
                var values = arguments.ToArray();
                invocations.Add(values);
                return Task.FromResult(new ProcessResult(0, " M run_tests.ps1", string.Empty));
            },
            _ => "available");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateSourceAsync("dev", CancellationToken.None));

        Assert.Contains("alterações locais", error.Message);
        Assert.Single(invocations);
        Assert.Equal(new[] { "status", "--porcelain" }, invocations[0]);
    }

    [Fact]
    public async Task SourceUpdateFetchesSwitchesAndFastForwardsSelectedBranchOnly()
    {
        CreateAutomationProject();
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var invocations = new List<string[]>();
        var service = new TestAutomationService(
            _root,
            (_, arguments, _, _, _, _) =>
            {
                var values = arguments.ToArray();
                invocations.Add(values);
                var output = values.SequenceEqual(new[] { "branch", "--show-current" }) ? "master\n" : string.Empty;
                var exitCode = values.SequenceEqual(new[] { "show-ref", "--verify", "--quiet", "refs/heads/dev" }) ? 1 : 0;
                return Task.FromResult(new ProcessResult(exitCode, output, string.Empty));
            },
            _ => "available");

        await service.UpdateSourceAsync("dev", CancellationToken.None);

        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "fetch", "origin", "dev" }));
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "switch", "--track", "-c", "dev", "origin/dev" }));
        Assert.Contains(invocations, args => args.SequenceEqual(new[] { "merge", "--ff-only", "origin/dev" }));
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
            (_, _, _, token, _, _) => Task.FromCanceled<ProcessResult>(token),
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
            (_, _, _, _, _, _) =>
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
