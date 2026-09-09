namespace SoftcomSmartProvisioner.Models;

public sealed record CompanyInfo(long Id, string Name, string Cnpj)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Cnpj) ? Name : $"{Name} · {Cnpj}";
}
