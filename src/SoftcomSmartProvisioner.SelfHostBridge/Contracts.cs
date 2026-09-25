using System.Text.Json.Serialization;

namespace SoftcomSmartProvisioner.SelfHostBridge;

public sealed class BridgeCommand
{
    public string Action { get; set; } = string.Empty;
    public string InstallRoot { get; set; } = string.Empty;
    public string? LocalBaseUrl { get; set; }
    public DesiredConfiguration? Desired { get; set; }
}

public sealed class DesiredConfiguration
{
    public string Backend { get; set; } = "softcomshop";
    public int? PortaHttp { get; set; }
    public bool? SmartEnabled { get; set; }
    public RootDeviceInput? RootDevice { get; set; }
    public DesktopDatabaseInput? DesktopDatabase { get; set; }
    public TableDatabaseInput? TableDatabase { get; set; }
}

public sealed class RootDeviceInput
{
    public string DeviceUrl { get; set; } = string.Empty;
}

public sealed class TableDatabaseInput
{
    public string? Server { get; set; }
    public string? Port { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }
}

public sealed class DesktopDatabaseInput
{
    public string? Server { get; set; }
    public string? Port { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }
}

public sealed record SanitizedConfiguration(
    string? TipoBancoDados,
    int? PortaHttp,
    string? SoftcomShopUrlBase,
    string? SoftcomShopEmpresa,
    string? SoftcomShopDevice,
    string? SoftcomShopDeviceId,
    bool SmartEnabled,
    bool RelayConfigured,
    bool HasClientId,
    bool HasClientSecret,
    string? Servidor,
    string? Porta,
    string? Usuario,
    string? BancoDados,
    bool HasDatabasePassword,
    bool IsDesktopDatabaseComplete,
    string? MysqlServidor,
    string? MysqlPorta,
    string? MysqlUsuario,
    string? MysqlDatabase,
    bool HasMysqlPassword,
    bool IsTableDatabaseComplete,
    bool IsComplete);

public sealed record ConfigurationChange(string Field, object? From, object? To);

public sealed record ConfigurationValidation(
    bool Healthcheck,
    bool HealthcheckInfo,
    bool Authentication,
    bool Company,
    string Stage);

public sealed class BridgeResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public string? Version { get; init; }
    public string? Generation { get; init; }
    public SanitizedConfiguration? Configuration { get; init; }
    public IReadOnlyList<ConfigurationChange>? Changes { get; init; }
    public ConfigurationValidation? Validation { get; init; }
    public bool RestartRequired { get; init; }
    public int PreservedFieldCount { get; init; }
}
