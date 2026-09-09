namespace SoftcomSmartProvisioner.Models;

public sealed record EnvironmentInfo(string Key, string Label, string Host, int Port = 3306);
