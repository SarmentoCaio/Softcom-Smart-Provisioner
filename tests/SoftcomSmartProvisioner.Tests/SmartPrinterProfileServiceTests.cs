using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SmartPrinterProfileServiceTests
{
    private readonly SmartPrinterProfileService _service = new();

    [Fact]
    public void ResolvesGetnetP3ForComanda()
    {
        var profile = _service.Resolve("smart_comanda", "getnetp3");

        Assert.NotNull(profile);
        Assert.Equal("P3", profile.Description);
        Assert.Equal("GETNET", profile.Driver);
        Assert.Equal("BLUETOOTH", profile.Type);
    }

    [Theory]
    [InlineData("smart_autopagamento")]
    [InlineData("smart_totem")]
    public void ResolvesK2ByModule(string module)
    {
        var profile = _service.Resolve(module, "totemk2");

        Assert.NotNull(profile);
        Assert.Equal("K2", profile.Description);
        Assert.Equal("K2_MINI", profile.Driver);
    }

    [Fact]
    public void ProfilesRemainSeparatedByModule()
    {
        var autoPagamento = _service.Resolve("smart_autopagamento", "totemk2");
        var totem = _service.Resolve("smart_totem", "totemk2");

        Assert.NotNull(autoPagamento);
        Assert.NotNull(totem);
        Assert.NotEqual(autoPagamento.Module, totem.Module);
    }

    [Fact]
    public void UnknownCombinationIsNotSelectedSilently()
    {
        Assert.Null(_service.Resolve("smart_pdv", "getnetp3"));
        Assert.Null(_service.Resolve("smart_comanda", "totemk2"));
    }
}
