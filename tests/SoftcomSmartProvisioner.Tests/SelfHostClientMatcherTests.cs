using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SelfHostClientMatcherTests
{
    [Theory]
    [InlineData("https://balerion.meusoftcom.com.br", "balerion")]
    [InlineData("https://JORMUNGANDR.meusoftcom.com.br:7711", "softcoms_softcomshop_jormungandr")]
    public void Matches_UsesSoftcomshopClientHost(string configuredUrl, string database)
    {
        Assert.True(SelfHostClientMatcher.Matches(Configuration(configuredUrl), database));
    }

    [Fact]
    public void Matches_DoesNotUseInternalCompanyName()
    {
        var configuration = Configuration("https://balerion.meusoftcom.com.br", company: "MATRIZ");

        Assert.False(SelfHostClientMatcher.Matches(configuration, "jormungandr"));
    }

    [Fact]
    public void DescribeMismatch_IdentifiesBothClients()
    {
        var message = SelfHostClientMatcher.DescribeMismatch(
            Configuration("https://balerion.meusoftcom.com.br"),
            "jormungandr");

        Assert.Contains("balerion", message);
        Assert.Contains("jormungandr", message);
        Assert.Contains("Reconfigure", message);
    }

    private static SelfHostConfigurationSummary Configuration(string url, string company = "MATRIZ") => new(
        "Softcomshop (Web)", 7711, url, company, "ROOT", "PC",
        true, false, true, true, null, null, null, null, false, false,
        null, null, null, null, false, false, true);
}
