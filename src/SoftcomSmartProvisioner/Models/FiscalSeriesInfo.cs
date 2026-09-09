namespace SoftcomSmartProvisioner.Models;

public sealed record FiscalSeriesInfo(
    long Id,
    string DocumentType,
    long CompanyId,
    string Series,
    int InitialNumber,
    int Environment,
    bool IsDefault,
    string? OAuthClientId,
    string? UpdatedAt);
