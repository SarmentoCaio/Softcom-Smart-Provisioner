namespace SoftcomSmartProvisioner.Models;

public sealed record SelfHostConfigurationSummary(
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

public sealed record SelfHostConfigurationChange(string Field, object? From, object? To);

public sealed record SelfHostConfigurationValidation(
    bool Healthcheck,
    bool HealthcheckInfo,
    bool Authentication,
    bool Company,
    string Stage);

public sealed record SelfHostBridgeResult(
    bool Success,
    string? ErrorCode,
    string? Message,
    string? Version,
    string? Generation,
    SelfHostConfigurationSummary? Configuration,
    IReadOnlyList<SelfHostConfigurationChange>? Changes,
    SelfHostConfigurationValidation? Validation,
    bool RestartRequired,
    int PreservedFieldCount);

public sealed record SelfHostDesiredConfiguration(
    string Backend,
    int PortaHttp,
    bool SmartEnabled,
    string? RootDeviceUrl,
    string? Servidor,
    string? Porta,
    string? Usuario,
    string? DatabasePassword,
    string? BancoDados,
    bool ConfigureTableDatabase,
    string? MysqlServidor,
    string? MysqlPorta,
    string? MysqlUsuario,
    string? MysqlPassword,
    string? MysqlDatabase);
