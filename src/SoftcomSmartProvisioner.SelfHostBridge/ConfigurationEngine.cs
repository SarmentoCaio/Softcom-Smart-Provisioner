namespace SoftcomSmartProvisioner.SelfHostBridge;

public static class ConfigurationEngine
{
    // Valor exato do EnumMember ERPType.SOFTCOMSHOP no Gerenciador 4.0.0.11.
    public const string SoftcomshopWeb = "Softcomshop (Web)";
    // Valor exato do EnumMember ERPType.SOFTSHOP no Gerenciador 4.1.0.16.
    public const string SoftshopDesktop = "Softshop (Desktop)";

    private static readonly HashSet<string> RestartFields = new(StringComparer.Ordinal)
    {
        "TipoBancoDados", "PortaHTTP", "SoftcomShopUrl", "SoftcomShopUrlBase",
        "SoftcomShopEmpresa", "SoftcomShopDevice", "SoftcomShopDeviceId",
        "SoftcomShopClientId", "SoftcomShopSecretId", "SoftcomShopSituacao", "DevicesEnabled",
        "Servidor", "Porta", "Usuario", "Senha", "BancoDados",
        "MysqlServidor", "MysqlPorta", "MysqlUsuario", "MysqlSenha", "MysqlDatabase"
    };

    public static IReadOnlyDictionary<string, object?> BuildUpdates(
        DesiredConfiguration? desired,
        RootDeviceUrl? root,
        string? clientSecret,
        bool preview)
    {
        if (desired is null)
            throw new BridgeValidationException("desired_missing", "Informe a configuração desejada.");
        if (desired.PortaHttp is < 1 or > 65535)
            throw new BridgeValidationException("invalid_port", "A porta HTTP deve estar entre 1 e 65535.");

        var desktop = IsDesktop(desired);
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["TipoBancoDados"] = desktop ? SoftshopDesktop : SoftcomshopWeb,
            ["DevicesEnabled"] = true
        };
        if (desktop)
        {
            var database = desired.DesktopDatabase
                ?? throw new BridgeValidationException("desktop_database_missing", "Informe a conexão SQL Server do Softshop Desktop.");
            updates["Servidor"] = database.Server?.Trim();
            updates["Porta"] = database.Port?.Trim();
            updates["Usuario"] = database.User?.Trim();
            updates["BancoDados"] = database.Database?.Trim();
            if (!string.IsNullOrWhiteSpace(database.Password)) updates["Senha"] = database.Password;
        }
        else
        {
            if (root is null)
                throw new BridgeValidationException("root_device_missing", "Selecione o dispositivo raiz do SelfHost.");
            updates["SoftcomShopUrl"] = root.FullUrl;
            updates["SoftcomShopUrlBase"] = root.BaseUrl;
            updates["SoftcomShopEmpresa"] = root.CompanyName;
            updates["SoftcomShopDevice"] = root.DeviceName;
            updates["SoftcomShopDeviceId"] = Environment.MachineName;
            updates["SoftcomShopClientId"] = root.ClientId;
            updates["SoftcomShopSituacao"] = "Cadastrado";
        }
        if (desired.PortaHttp.HasValue) updates["PortaHTTP"] = desired.PortaHttp.Value;
        if (desired.SmartEnabled.HasValue) updates["SmartEnabled"] = desired.SmartEnabled.Value;
        if (desired.TableDatabase is not null)
        {
            updates["MysqlServidor"] = desired.TableDatabase.Server?.Trim();
            updates["MysqlPorta"] = desired.TableDatabase.Port?.Trim();
            updates["MysqlUsuario"] = desired.TableDatabase.User?.Trim();
            updates["MysqlDatabase"] = desired.TableDatabase.Database?.Trim();
            if (!string.IsNullOrWhiteSpace(desired.TableDatabase.Password))
                updates["MysqlSenha"] = desired.TableDatabase.Password;
        }
        if (!preview && !desktop)
        {
            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new BridgeValidationException("root_secret_missing", "O Softcomshop não retornou a credencial oficial do dispositivo raiz.");
            updates["SoftcomShopSecretId"] = clientSecret;
        }
        return updates;
    }

    public static IReadOnlyList<ConfigurationChange> Preview(
        IConfigurationDocument current,
        DesiredConfiguration desired,
        RootDeviceUrl? root)
    {
        var changes = BuildChanges(current, BuildUpdates(desired, root, null, preview: true), preview: true);
        if (IsDesktop(desired))
        {
            if (string.IsNullOrWhiteSpace(desired.DesktopDatabase?.Password))
                changes.Add(new ConfigurationChange("Senha", Has(current.Get("Senha")) ? "Presente" : "Ausente", "Preservada"));
        }
        else
        {
            changes.Add(new ConfigurationChange(
                "SoftcomShopSecretId",
                Has(current.Get("SoftcomShopSecretId")) ? "Presente" : "Ausente",
                "Será atualizado"));
        }
        if (desired.TableDatabase is not null && string.IsNullOrWhiteSpace(desired.TableDatabase.Password))
        {
            changes.Add(new ConfigurationChange(
                "MysqlSenha",
                Has(current.Get("MysqlSenha")) ? "Presente" : "Ausente",
                "Preservada"));
        }
        return changes;
    }

    public static IReadOnlyList<ConfigurationChange> Apply(
        IConfigurationDocument current,
        DesiredConfiguration desired,
        RootDeviceUrl? root,
        string? clientSecret)
    {
        var updates = BuildUpdates(desired, root, clientSecret, preview: false);
        var changes = BuildChanges(current, updates, preview: false);
        foreach (var pair in updates) current.Set(pair.Key, pair.Value);
        return changes;
    }

    public static bool RequiresRestart(IEnumerable<ConfigurationChange> changes) =>
        changes.Any(x => RestartFields.Contains(x.Field));

    public static void ValidateDesired(IConfigurationDocument current, DesiredConfiguration desired)
    {
        var backend = desired.Backend?.Trim().ToLowerInvariant();
        if (backend is not ("softcomshop" or "softshop"))
            throw new BridgeValidationException("invalid_backend", "Escolha Softcomshop Web ou Softshop Desktop.");
        if (backend == "softshop")
        {
            var desktop = desired.DesktopDatabase
                ?? throw new BridgeValidationException("desktop_database_missing", "Informe a conexão SQL Server do Softshop Desktop.");
            if (string.IsNullOrWhiteSpace(desktop.Server) ||
                string.IsNullOrWhiteSpace(desktop.User) ||
                string.IsNullOrWhiteSpace(desktop.Database))
                throw new BridgeValidationException("desktop_database_incomplete", "Informe servidor, usuário e banco de dados do Softshop Desktop.");
            if (!string.IsNullOrWhiteSpace(desktop.Port) &&
                (!int.TryParse(desktop.Port, out var desktopPort) || desktopPort is < 1 or > 65535))
                throw new BridgeValidationException("invalid_desktop_database_port", "A porta do SQL Server deve estar entre 1 e 65535.");
            if (string.IsNullOrWhiteSpace(desktop.Password) && !Has(current.Get("Senha")))
                throw new BridgeValidationException("desktop_database_password_missing", "Informe a senha do SQL Server.");
        }
        var database = desired.TableDatabase;
        if (database is null) return;
        if (string.IsNullOrWhiteSpace(database.Server) ||
            string.IsNullOrWhiteSpace(database.Port) ||
            string.IsNullOrWhiteSpace(database.User) ||
            string.IsNullOrWhiteSpace(database.Database))
            throw new BridgeValidationException("table_database_incomplete", "Informe servidor, porta, usuário e banco de mesas.");
        if (!int.TryParse(database.Port, out var port) || port is < 1 or > 65535)
            throw new BridgeValidationException("invalid_table_database_port", "A porta do banco de mesas deve estar entre 1 e 65535.");
        if (string.IsNullOrWhiteSpace(database.Password) && !Has(current.Get("MysqlSenha")))
            throw new BridgeValidationException("table_database_password_missing", "Informe a senha do banco de mesas.");
    }

    public static SanitizedConfiguration Sanitize(IConfigurationDocument document)
    {
        var hasClientId = Has(document.Get("SoftcomShopClientId"));
        var hasSecret = Has(document.Get("SoftcomShopSecretId"));
        var situation = Text(document.Get("SoftcomShopSituacao"));
        var tipoBancoDados = Text(document.Get("TipoBancoDados"));
        var servidor = Text(document.Get("Servidor"));
        var porta = Text(document.Get("Porta"));
        var usuario = Text(document.Get("Usuario"));
        var bancoDados = Text(document.Get("BancoDados"));
        var hasDatabasePassword = Has(document.Get("Senha"));
        var desktopComplete = bancoDados is not null && hasDatabasePassword;
        var mysqlServer = Text(document.Get("MysqlServidor"));
        var mysqlPort = Text(document.Get("MysqlPorta"));
        var mysqlUser = Text(document.Get("MysqlUsuario"));
        var mysqlDatabase = Text(document.Get("MysqlDatabase"));
        var hasMysqlPassword = Has(document.Get("MysqlSenha"));
        return new SanitizedConfiguration(
            tipoBancoDados,
            Number(document.Get("PortaHTTP")),
            Text(document.Get("SoftcomShopUrlBase")),
            Text(document.Get("SoftcomShopEmpresa")),
            Text(document.Get("SoftcomShopDevice")),
            Text(document.Get("SoftcomShopDeviceId")),
            Flag(document.Get("SmartEnabled")),
            Has(document.Get("RelayServer")) || Has(document.Get("RelayClientId")) || Has(document.Get("RelayServerClientId")),
            hasClientId,
            hasSecret,
            servidor,
            porta,
            usuario,
            bancoDados,
            hasDatabasePassword,
            desktopComplete,
            mysqlServer,
            mysqlPort,
            mysqlUser,
            mysqlDatabase,
            hasMysqlPassword,
            mysqlServer is not null && mysqlPort is not null && mysqlUser is not null && mysqlDatabase is not null && hasMysqlPassword,
            IsDesktopType(tipoBancoDados)
                ? desktopComplete
                : hasClientId && hasSecret && string.Equals(situation, "Cadastrado", StringComparison.OrdinalIgnoreCase));
    }

    private static List<ConfigurationChange> BuildChanges(
        IConfigurationDocument current,
        IReadOnlyDictionary<string, object?> updates,
        bool preview)
    {
        var changes = new List<ConfigurationChange>();
        foreach (var pair in updates)
        {
            var before = current.Get(pair.Key);
            if (pair.Key == "SoftcomShopClientId")
            {
                if (!Equivalent(before, pair.Value))
                    changes.Add(new(pair.Key, Has(before) ? "Presente" : "Ausente", preview ? "Será atualizado" : "Presente"));
                continue;
            }
            if (pair.Key is "SoftcomShopSecretId" or "Senha" or "MysqlSenha")
            {
                if (!Has(before)) changes.Add(new(pair.Key, "Ausente", preview ? "Será atualizado" : "Presente"));
                continue;
            }
            if (pair.Key == "SoftcomShopUrl")
            {
                if (!Equivalent(before, pair.Value))
                    changes.Add(new(pair.Key, SafeDeviceUrl(before), SafeDeviceUrl(pair.Value)));
                continue;
            }
            if (!Equivalent(before, pair.Value)) changes.Add(new(pair.Key, SafeValue(before), SafeValue(pair.Value)));
        }
        return changes;
    }

    private static bool Equivalent(object? left, object? right) =>
        string.Equals(left?.ToString()?.Trim(), right?.ToString()?.Trim(), StringComparison.Ordinal);
    private static object? SafeValue(object? value) => value is null || string.IsNullOrWhiteSpace(value.ToString()) ? null : value;
    private static string? SafeDeviceUrl(object? value)
    {
        var text = Text(value);
        if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) return text is null ? null : "<URL sanitizada>";
        return uri.GetLeftPart(UriPartial.Path) + (string.IsNullOrEmpty(uri.Query) ? string.Empty : "?<dados ocultos>");
    }
    private static string? Text(object? value) => string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString()!.Trim();
    private static int? Number(object? value) => int.TryParse(value?.ToString(), out var number) ? number : null;
    private static bool Flag(object? value) => bool.TryParse(value?.ToString(), out var flag) && flag;
    private static bool Has(object? value) => !string.IsNullOrWhiteSpace(value?.ToString());
    private static bool IsDesktop(DesiredConfiguration desired) =>
        string.Equals(desired.Backend?.Trim(), "softshop", StringComparison.OrdinalIgnoreCase);
    private static bool IsDesktopType(string? value) =>
        value?.Contains("desktop", StringComparison.OrdinalIgnoreCase) == true;
}
