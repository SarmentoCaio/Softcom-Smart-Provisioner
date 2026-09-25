namespace SoftcomSmartProvisioner.SelfHostBridge;

public static class BridgeApplication
{
    public static BridgeResponse Execute(
        BridgeCommand command,
        Func<string, IConfigurationGateway>? gatewayFactory = null)
    {
        var action = (command.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("read" or "preview" or "configure" or "validate"))
            throw new BridgeValidationException("unknown_action", "Ação desconhecida. Use read, preview, configure ou validate.");

        using var gateway = (gatewayFactory ?? (root => new OfficialConfigurationGateway(root)))(command.InstallRoot);
        var current = gateway.Load();
        if (action == "read") return BuildRead(gateway, current);
        if (action == "validate")
        {
            return new BridgeResponse
            {
                Success = true,
                Version = gateway.Version,
                Generation = gateway.Generation,
                Configuration = ConfigurationEngine.Sanitize(current),
                Validation = SelfHostValidator.Validate(current, command.LocalBaseUrl, gateway.Generation),
                PreservedFieldCount = current.PropertyNames.Count
            };
        }

        var desired = command.Desired
            ?? throw new BridgeValidationException("desired_missing", "Informe a configuração desejada.");
        ConfigurationEngine.ValidateDesired(current, desired);
        var isDesktop = string.Equals(desired.Backend?.Trim(), "softshop", StringComparison.OrdinalIgnoreCase);
        if (!isDesktop && (desired.RootDevice is null || string.IsNullOrWhiteSpace(desired.RootDevice.DeviceUrl)))
            throw new BridgeValidationException("root_device_missing", "Selecione o dispositivo raiz do SelfHost.");
        var root = isDesktop ? null : RootDeviceUrl.Parse(desired.RootDevice?.DeviceUrl);
        if (action == "preview")
        {
            var changes = ConfigurationEngine.Preview(current, desired, root);
            return new BridgeResponse
            {
                Success = true,
                Version = gateway.Version,
                Generation = gateway.Generation,
                Configuration = ConfigurationEngine.Sanitize(current),
                Changes = changes,
                RestartRequired = ConfigurationEngine.RequiresRestart(changes),
                PreservedFieldCount = Math.Max(0, current.PropertyNames.Count - 11)
            };
        }

        // Valida todos os campos antes de executar o vínculo remoto, evitando um
        // dispositivo raiz registrado no Softcomshop quando a configuração é inválida.
        _ = ConfigurationEngine.BuildUpdates(desired, root, null, preview: true);
        // CONFIGURE representa um novo vínculo confirmado pelo usuário. Mesmo quando
        // o client_id já está salvo localmente, a credencial anterior pode pertencer a
        // outro vínculo removido no Softcomshop. Sempre registramos novamente pelo
        // método oficial e persistimos a nova credencial retornada.
        var secret = isDesktop ? null : gateway.RegisterRootDevice(root!);
        var applied = ConfigurationEngine.Apply(current, desired, root, secret!);
        gateway.Save(current);
        var reloaded = gateway.Load();
        ValidatePersisted(reloaded, desired, root, secret!);
        return new BridgeResponse
        {
            Success = true,
            Version = gateway.Version,
            Generation = gateway.Generation,
            Configuration = ConfigurationEngine.Sanitize(reloaded),
            Changes = applied,
            RestartRequired = ConfigurationEngine.RequiresRestart(applied),
            PreservedFieldCount = Math.Max(0, current.PropertyNames.Count - 12)
        };
    }

    private static BridgeResponse BuildRead(IConfigurationGateway gateway, IConfigurationDocument current) => new()
    {
        Success = true,
        Version = gateway.Version,
        Generation = gateway.Generation,
        Configuration = ConfigurationEngine.Sanitize(current),
        PreservedFieldCount = current.PropertyNames.Count
    };

    public static void ValidatePersisted(
        IConfigurationDocument actual,
        DesiredConfiguration desired,
        RootDeviceUrl? root,
        string? clientSecret)
    {
        var expected = ConfigurationEngine.BuildUpdates(desired, root, clientSecret, preview: false);
        foreach (var pair in expected)
        {
            if (!string.Equals(actual.Get(pair.Key)?.ToString()?.Trim(), pair.Value?.ToString()?.Trim(), StringComparison.Ordinal))
                throw new BridgeValidationException("verification_failed", $"A validação após salvar falhou no campo {pair.Key}.");
        }
    }
}
