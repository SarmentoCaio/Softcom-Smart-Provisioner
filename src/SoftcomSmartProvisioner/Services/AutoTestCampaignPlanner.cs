using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed record AutoTestCampaignDevice(
    string Serial,
    string DeviceTag,
    string FriendlyName,
    OAuthClientInfo SelfHostClient,
    OAuthClientInfo FafnirClient);

public sealed record AutoTestCampaignPlan(
    IReadOnlyList<AutoTestCampaignDevice> Devices,
    IReadOnlyList<string> Skipped);

public static class AutoTestCampaignPlanner
{
    // Cadastros observados em jormungandr e fafnir. Um nome ausente nunca e criado
    // ou substituido silenciosamente durante uma campanha destrutiva.
    private static readonly IReadOnlyDictionary<string, (string? SelfHost, string? Fafnir)> Targets =
        new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase)
        {
            ["cielo"] = (null, "device_cielo"),
            ["rede"] = ("SELFHOST_device_rede_l400", "device_redel400"),
            ["rede_n960k"] = (null, "device_rede_n960k"),
            ["getnet_dx8000"] = ("SELFHOST_device_getnet_dx8000", "device_getnetdx8000"),
            ["getnet_p2"] = ("SELFHOST_device_getnet_p2", "device_getnetp2"),
            ["getnet_p3"] = ("SELFHOST_device_getnet_p3", "device_getnetp3"),
            ["stone"] = ("SELFHOST_device_stone", "device_stone"),
            ["pagbank"] = ("SELFHOST_device_pagbank_p2", "device_pagbankp2"),
            ["pagbank_a11"] = (null, "device_pagbank_p2a11"),
            ["pagbank_g780s"] = ("SELFHOST_device_pagbank_G780S", "device_pagbankg780s"),
            ["fiserv"] = (null, "device_fiserv"),
            ["sipag_p2"] = ("SELFHOST_device_sipag_p2", "device_sipagp2"),
            ["sipag_x990"] = ("SELFHOST_device_sipag_x990", "device_sipagx990plus"),
            ["sipag_dx8000"] = ("SELFHOST_device_sipag_dx8000", "device_sipagdx8000"),
            ["safra"] = ("SELFHOST_device_safra", "device_safra"),
            ["mercadopago"] = ("SELFHOST_device_mercadopago", "device_mercadopago"),
            ["clover"] = ("SELFHOST_device_clover_c405", "device_clover_c405")
        };

    public static bool SupportsTag(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) &&
        Targets.TryGetValue(tag, out var target) &&
        target.SelfHost is not null && target.Fafnir is not null;

    public static AutoTestCampaignPlan Build(
        IReadOnlyList<TestAutomationDevice> connectedDevices,
        IReadOnlyList<OAuthClientInfo> selfHostClients,
        IReadOnlyList<OAuthClientInfo> fafnirClients)
    {
        var selected = new List<AutoTestCampaignDevice>();
        var skipped = new List<string>();
        foreach (var device in connectedDevices.Where(x => x.IsOnline))
        {
            var tag = device.SuggestedDeviceTag;
            if (device.RequiresProfileSelection || string.IsNullOrWhiteSpace(tag) ||
                !device.DeviceTags.Contains(tag, StringComparer.OrdinalIgnoreCase) ||
                !Targets.TryGetValue(tag, out var target))
            {
                skipped.Add($"{device.FriendlyName} ({device.Serial}): perfil sem mapeamento seguro para a campanha.");
                continue;
            }
            if (target.SelfHost is null || target.Fafnir is null)
            {
                skipped.Add($"{device.FriendlyName} ({device.Serial}): faltam cadastros para as tres fases.");
                continue;
            }
            var selfHost = FindUnique(selfHostClients, target.SelfHost);
            var fafnir = FindUnique(fafnirClients, target.Fafnir);
            if (selfHost is null || fafnir is null)
            {
                skipped.Add($"{device.FriendlyName} ({device.Serial}): cadastro {string.Join(" e ", new[] { selfHost is null ? target.SelfHost : null, fafnir is null ? target.Fafnir : null }.Where(x => x is not null))} nao encontrado.");
                continue;
            }
            selected.Add(new AutoTestCampaignDevice(device.Serial, tag, device.FriendlyName, selfHost, fafnir));
        }
        return new AutoTestCampaignPlan(selected, skipped);
    }

    private static OAuthClientInfo? FindUnique(IReadOnlyList<OAuthClientInfo> clients, string name)
    {
        var matches = clients.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"Mais de um cadastro ativo chamado {name}; revise o cliente antes da campanha.");
        return matches.FirstOrDefault();
    }
}
