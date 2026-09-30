using System.Text.Json.Nodes;
using System.Xml.Linq;
using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SmartPrinterConfigurationServiceTests
{
    private static readonly SmartPrinterProfile P3 =
        new("smart_comanda", "getnetp3", "P3", "GETNET", "BLUETOOTH");

    [Fact]
    public void MergePreservesUnrelatedPreferenceKeys()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><map><boolean name=\"outra_chave\" value=\"true\" /></map>";

        var merged = SmartPrinterConfigurationService.MergePreferences(xml, P3);
        var document = XDocument.Parse(merged);

        Assert.NotNull(document.Root?.Elements("boolean")
            .SingleOrDefault(x => (string?)x.Attribute("name") == "outra_chave"));
        Assert.True(SmartPrinterConfigurationService.ValidatePreferences(merged, P3));
    }

    [Fact]
    public void MergeKeepsExistingDevicesAndLeavesOnlyOnePreferred()
    {
        const string xml = "<map><string name=\"listadispositivospareados\">" +
                           "[{&quot;id&quot;:9,&quot;descricao&quot;:&quot;Antiga&quot;,&quot;tipo&quot;:&quot;USB&quot;,&quot;driver&quot;:&quot;OLD&quot;,&quot;endereco&quot;:&quot;x&quot;,&quot;preferencial&quot;:true}]" +
                           "</string></map>";

        var merged = SmartPrinterConfigurationService.MergePreferences(xml, P3);
        var value = XDocument.Parse(merged).Root!.Elements("string").Single().Value;
        var devices = JsonNode.Parse(value)!.AsArray();

        Assert.Equal(2, devices.Count);
        Assert.Single(devices, x => x?["preferencial"]?.GetValue<bool>() == true);
        Assert.Equal("P3", devices.Single(x => x?["preferencial"]?.GetValue<bool>() == true)!["descricao"]!.GetValue<string>());
        Assert.True(SmartPrinterConfigurationService.ValidatePreferences(merged, P3));
    }

    [Fact]
    public void MergeUpdatesMappedPrinterWithoutCreatingDuplicate()
    {
        const string xml = "<map><string name=\"listadispositivospareados\">" +
                           "[{&quot;id&quot;:7,&quot;descricao&quot;:&quot;P3&quot;,&quot;tipo&quot;:&quot;USB&quot;,&quot;driver&quot;:&quot;GETNET&quot;,&quot;endereco&quot;:&quot;old&quot;,&quot;preferencial&quot;:false}]" +
                           "</string></map>";

        var merged = SmartPrinterConfigurationService.MergePreferences(xml, P3);
        var value = XDocument.Parse(merged).Root!.Elements("string").Single().Value;
        var devices = JsonNode.Parse(value)!.AsArray();

        Assert.Single(devices);
        Assert.Equal("BLUETOOTH", devices[0]!["tipo"]!.GetValue<string>());
        Assert.Equal(string.Empty, devices[0]!["endereco"]!.GetValue<string>());
        Assert.True(devices[0]!["preferencial"]!.GetValue<bool>());
    }

    [Fact]
    public void ValidationRejectsDifferentPreferredPrinter()
    {
        const string xml = "<map><string name=\"listadispositivospareados\">" +
                           "[{&quot;id&quot;:1,&quot;descricao&quot;:&quot;K2&quot;,&quot;tipo&quot;:&quot;BLUETOOTH&quot;,&quot;driver&quot;:&quot;K2_MINI&quot;,&quot;endereco&quot;:&quot;&quot;,&quot;preferencial&quot;:true}]" +
                           "</string></map>";

        Assert.False(SmartPrinterConfigurationService.ValidatePreferences(xml, P3));
    }

    [Fact]
    public async Task ApplyUsesRequestedSerialAndPackageAndValidatesWrittenContent()
    {
        var adb = new FakeAdbPackageFiles();
        var service = new SmartPrinterConfigurationService(adb);
        var progress = new List<string>();

        var result = await service.ApplyAsync(
            "P31824AA70363",
            "softcom.mobile.smart2",
            P3,
            (_, message) => progress.Add(message),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.All(adb.Calls, call =>
        {
            Assert.Equal("P31824AA70363", call.Serial);
            Assert.Equal("softcom.mobile.smart2", call.PackageName);
        });
        Assert.True(SmartPrinterConfigurationService.ValidatePreferences(adb.StoredContent, P3));
        Assert.DoesNotContain(progress, x => x.Contains("listadispositivospareados", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsFailureDoesNotReturnSuccessAndReopensSmart()
    {
        var adb = new FakeAdbPackageFiles { RunAsResult = new ProcessResult(1, "", "not debuggable") };
        var service = new SmartPrinterConfigurationService(adb);

        var result = await service.ApplyAsync(
            "P31824AA70363",
            "softcom.mobile.smart2",
            P3,
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("printer-run-as", result.Stage);
        Assert.Contains(adb.Calls, x => x.Operation == "launch");
        Assert.DoesNotContain("not debuggable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DivergentReloadIsReportedAsFailure()
    {
        var adb = new FakeAdbPackageFiles { ReturnDivergentReload = true };
        var service = new SmartPrinterConfigurationService(adb);

        var result = await service.ApplyAsync(
            "KM54257740097",
            "softcom.mobile.smart2",
            new SmartPrinterProfile("smart_totem", "totemk2", "K2", "K2_MINI", "BLUETOOTH"),
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("printer-validate", result.Stage);
        Assert.Contains(adb.Calls, x => x.Operation == "launch");
    }

    private sealed class FakeAdbPackageFiles : IAdbPackageFileService
    {
        public List<(string Operation, string Serial, string PackageName)> Calls { get; } = [];
        public ProcessResult RunAsResult { get; init; } = new(0, "uid=10000", "");
        public bool ReturnDivergentReload { get; init; }
        public string StoredContent { get; private set; } = string.Empty;
        private int _reads;

        public Task<ProcessResult> ForceStopPackageAsync(string serial, string packageName, CancellationToken cancellationToken = default)
        {
            Calls.Add(("stop", serial, packageName));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public Task<ProcessResult> LaunchPackageAsync(string serial, string packageName, CancellationToken cancellationToken = default)
        {
            Calls.Add(("launch", serial, packageName));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public Task<ProcessResult> RunAsAsync(string serial, string packageName, IReadOnlyList<string> commandArguments, CancellationToken cancellationToken = default, int timeoutMilliseconds = 30000)
        {
            Calls.Add(("run-as", serial, packageName));
            return Task.FromResult(RunAsResult);
        }

        public Task<ProcessResult> ReadRunAsTextFileAsync(string serial, string packageName, string relativePath, CancellationToken cancellationToken = default, int timeoutMilliseconds = 30000)
        {
            Calls.Add(("read", serial, packageName));
            _reads++;
            if (_reads == 1)
                return Task.FromResult(new ProcessResult(1, "", "No such file"));
            if (ReturnDivergentReload)
                return Task.FromResult(new ProcessResult(0, "<map />", ""));
            return Task.FromResult(new ProcessResult(0, StoredContent, ""));
        }

        public Task<ProcessResult> WriteRunAsTextFileAsync(string serial, string packageName, string relativePath, string content, CancellationToken cancellationToken = default, int timeoutMilliseconds = 30000)
        {
            Calls.Add(("write", serial, packageName));
            StoredContent = content;
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
