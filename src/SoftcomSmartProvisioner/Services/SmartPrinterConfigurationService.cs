using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class SmartPrinterConfigurationService(IAdbPackageFileService adbService)
{
    public const string PreferencesPath = "shared_prefs/prefdispositivos.xml";
    public const string PairedDevicesKey = "listadispositivospareados";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task<SmartPrinterConfigurationResult> ApplyAsync(
        string serial,
        string packageName,
        SmartPrinterProfile profile,
        Action<string, string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke("printer-config", $"Aplicando impressora padrao {profile.DisplayName}...");

        async Task<SmartPrinterConfigurationResult> FailAfterStopAsync(string stage, string message)
        {
            var reopened = await adbService.LaunchPackageAsync(serial, packageName, cancellationToken);
            return SmartPrinterConfigurationResult.Failed(
                stage,
                reopened.Success ? message : message + " O Provisioner tambem nao conseguiu reabrir o Smart.",
                profile);
        }

        var stopped = await adbService.ForceStopPackageAsync(serial, packageName, cancellationToken);
        if (!stopped.Success)
        {
            return SmartPrinterConfigurationResult.Failed(
                "printer-stop",
                "O Smart foi vinculado, mas nao foi possivel parar o package antes de configurar a impressora.",
                profile);
        }

        var runAs = await adbService.RunAsAsync(serial, packageName, ["id"], cancellationToken, 10000);
        if (!runAs.Success)
        {
            return await FailAfterStopAsync(
                "printer-run-as",
                "O Smart foi vinculado, mas este APK nao permite aplicar o preset interno com run-as.");
        }

        var current = await adbService.ReadRunAsTextFileAsync(
            serial,
            packageName,
            PreferencesPath,
            cancellationToken,
            10000);

        var existingXml = current.Success
            ? current.StandardOutput
            : string.Empty;
        if (!current.Success && !IsMissingFile(current.CombinedOutput))
        {
            return await FailAfterStopAsync(
                "printer-read",
                "O Smart foi vinculado, mas nao foi possivel ler as preferencias atuais de impressora.");
        }

        string mergedXml;
        try
        {
            mergedXml = MergePreferences(existingXml, profile);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Xml.XmlException)
        {
            return await FailAfterStopAsync(
                "printer-merge",
                "O Smart foi vinculado, mas as preferencias atuais de impressora possuem formato invalido.");
        }

        var written = await adbService.WriteRunAsTextFileAsync(
            serial,
            packageName,
            PreferencesPath,
            mergedXml,
            cancellationToken,
            15000);
        if (!written.Success)
        {
            return await FailAfterStopAsync(
                "printer-write",
                "O Smart foi vinculado, mas nao foi possivel gravar a configuracao interna da impressora.");
        }

        var reloaded = await adbService.ReadRunAsTextFileAsync(
            serial,
            packageName,
            PreferencesPath,
            cancellationToken,
            10000);
        if (!reloaded.Success || !ValidatePreferences(reloaded.StandardOutput, profile))
        {
            return await FailAfterStopAsync(
                "printer-validate",
                "O Smart foi vinculado, mas a releitura nao confirmou a impressora configurada.");
        }

        var launched = await adbService.LaunchPackageAsync(serial, packageName, cancellationToken);
        if (!launched.Success)
        {
            return SmartPrinterConfigurationResult.Failed(
                "printer-launch",
                "A impressora foi gravada e validada, mas nao foi possivel reabrir o Smart.",
                profile);
        }

        progress?.Invoke("printer-config", $"Impressora {profile.DisplayName} gravada e validada no Smart.");
        return SmartPrinterConfigurationResult.Completed(
            $"Impressora padrao {profile.DisplayName} aplicada e validada.",
            profile);
    }

    public static string MergePreferences(string? existingXml, SmartPrinterProfile profile)
    {
        var document = ParseOrCreateDocument(existingXml);
        var root = document.Root
            ?? throw new InvalidOperationException("O XML de preferencias nao possui elemento raiz.");

        var preference = root.Elements("string").FirstOrDefault(x =>
            string.Equals((string?)x.Attribute("name"), PairedDevicesKey, StringComparison.Ordinal));
        if (preference is null)
        {
            preference = new XElement("string", new XAttribute("name", PairedDevicesKey));
            root.Add(preference);
        }

        var devices = ParseDevices(preference.Value);
        foreach (var item in devices.OfType<JsonObject>())
            item["preferencial"] = false;

        var selected = devices
            .OfType<JsonObject>()
            .FirstOrDefault(x =>
                JsonValueEquals(x, "driver", profile.Driver) &&
                JsonValueEquals(x, "descricao", profile.Description));
        if (selected is null)
        {
            selected = new JsonObject();
            devices.Add(selected);
        }

        selected["id"] = profile.Id;
        selected["descricao"] = profile.Description;
        selected["tipo"] = profile.Type;
        selected["driver"] = profile.Driver;
        selected["endereco"] = profile.Address;
        selected["preferencial"] = true;
        preference.Value = devices.ToJsonString(JsonOptions);

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static bool ValidatePreferences(string? xml, SmartPrinterProfile profile)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var value = document.Root?
                .Elements("string")
                .FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), PairedDevicesKey, StringComparison.Ordinal))?
                .Value;
            var devices = ParseDevices(value);
            var preferred = devices.OfType<JsonObject>().Where(IsPreferred).ToArray();
            return preferred.Length == 1 &&
                   JsonValueEquals(preferred[0], "driver", profile.Driver) &&
                   JsonValueEquals(preferred[0], "descricao", profile.Description) &&
                   JsonValueEquals(preferred[0], "tipo", profile.Type);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static XDocument ParseOrCreateDocument(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("map"));

        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        if (!string.Equals(document.Root?.Name.LocalName, "map", StringComparison.Ordinal))
            throw new InvalidOperationException("O XML de preferencias nao possui o elemento map.");
        return document;
    }

    private static JsonArray ParseDevices(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return JsonNode.Parse(value) as JsonArray
            ?? throw new JsonException("A lista de dispositivos pareados nao e um array JSON.");
    }

    private static bool JsonValueEquals(JsonObject item, string property, string expected) =>
        string.Equals(item[property]?.GetValue<string>(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsPreferred(JsonObject item) =>
        item["preferencial"]?.GetValue<bool>() == true;

    private static bool IsMissingFile(string? output) =>
        (output ?? string.Empty).Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
        (output ?? string.Empty).Contains("does not exist", StringComparison.OrdinalIgnoreCase);
}
