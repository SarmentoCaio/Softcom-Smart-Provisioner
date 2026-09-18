using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class DeviceCatalogServiceTests
{
    [Fact]
    public void IdentifiesKnownStoneAndPrioritizesCatalogName()
    {
        var snapshot = DeviceCatalogService.Parse(new[] { "STONE_UDID=4AF303W9U" });
        var result = DeviceCatalogService.Identify("4AF303W9U", snapshot);
        Assert.True(result.IsKnownDevice);
        Assert.Equal("Stone", result.FriendlyName);
        Assert.Equal("Stone", result.Acquirer);
    }

    [Fact]
    public void IdentifiesK2SelfServiceTotem()
    {
        var snapshot = DeviceCatalogService.Parse(new[] { "TOTEM_K2_UDID=KM54257740097" });
        var result = DeviceCatalogService.Identify("KM54257740097", snapshot);
        Assert.True(result.IsKnownDevice);
        Assert.Equal("Totem - K2", result.FriendlyName);
        Assert.Equal("Totem", result.Acquirer);
        Assert.Equal("K2", result.TerminalModel);
    }

    [Theory]
    [InlineData("CIELO_DX8000_UDID=CIELO1", "CIELO1", "Cielo - DX8000")]
    [InlineData("GETNET_P3_UDID=GETNET1", "GETNET1", "Getnet - P3")]
    [InlineData("SIPAG_X990_UDID=SIPAG1", "SIPAG1", "Sipag - X990")]
    public void IdentifiesMappedAcquirerAndModel(string line, string serial, string expected)
    {
        var result = DeviceCatalogService.Identify(serial, DeviceCatalogService.Parse(new[] { line }));
        Assert.Equal(expected, result.FriendlyName);
    }

    [Fact]
    public void UnknownUdidIsNotInvented()
    {
        var result = DeviceCatalogService.Identify("UNKNOWN", DeviceCatalogService.Parse(Array.Empty<string>()));
        Assert.False(result.IsKnownDevice);
        Assert.Equal("Dispositivo não identificado", result.FriendlyName);
    }

    [Fact]
    public void EmptyUdidIsIgnored()
    {
        var snapshot = DeviceCatalogService.Parse(new[] { "STONE_UDID=" });
        Assert.Empty(snapshot.Entries);
        Assert.Contains("STONE_UDID", snapshot.EmptyVariables);
    }

    [Fact]
    public void PlaceholderUdidIsNotTreatedAsARealDevice()
    {
        var snapshot = DeviceCatalogService.Parse(new[] { "MERCADOPAGO_UDID=<PLACEHOLDER>" });
        Assert.Empty(snapshot.Entries);
        Assert.Contains("MERCADOPAGO_UDID", snapshot.EmptyVariables);
    }

    [Fact]
    public void ProvidedDuplicateSerialRemainsAmbiguous()
    {
        var snapshot = DeviceCatalogService.Parse(new[]
        {
            "REDE_N960K_UDID=0123456789ABCDEF",
            "MERCADOPAGO_UDID=0123456789ABCDEF"
        });
        var result = DeviceCatalogService.Identify("0123456789ABCDEF", snapshot);
        Assert.True(result.IsAmbiguous);
        Assert.Equal(2, result.MatchingVariables.Count);
    }

    [Theory]
    [InlineData("N950", "Mercado Pago - N950", "Mercado Pago", "mercadopagon950")]
    [InlineData("N960K", "Rede - N960K", "Rede", "reden960k")]
    public void ResolvesKnownDuplicateUdidByAndroidModel(
        string androidModel,
        string friendlyName,
        string acquirer,
        string provisioningProfile)
    {
        var snapshot = DeviceCatalogService.Parse(new[]
        {
            "REDE_N960K_UDID=0123456789ABCDEF",
            "MERCADOPAGO_UDID=0123456789ABCDEF"
        });

        var result = DeviceCatalogService.Identify("0123456789ABCDEF", snapshot, androidModel);

        Assert.True(result.IsKnownDevice);
        Assert.False(result.IsAmbiguous);
        Assert.Equal(friendlyName, result.FriendlyName);
        Assert.Equal(acquirer, result.Acquirer);
        Assert.Equal(provisioningProfile, result.ProvisioningProfile);
        Assert.Equal("UDID duplicado resolvido pelo modelo Android", result.Status);
    }

    [Fact]
    public void DuplicateUdidIsAmbiguous()
    {
        var snapshot = DeviceCatalogService.Parse(new[] { "STONE_UDID=SAME", "CIELO_DX8000_UDID=SAME" });
        var result = DeviceCatalogService.Identify("SAME", snapshot);
        Assert.True(result.IsAmbiguous);
        Assert.False(result.IsKnownDevice);
        Assert.Equal(2, result.MatchingVariables.Count);
    }

    [Fact]
    public void ParserReadsOnlyUdidVariables()
    {
        var snapshot = DeviceCatalogService.Parse(new[] { "CLIENT_SECRET=must-not-be-read", "STONE_UDID=STONE1" });
        Assert.Single(snapshot.Entries);
        Assert.DoesNotContain(snapshot.Entries, x => x.Serial == "must-not-be-read");
    }
}
