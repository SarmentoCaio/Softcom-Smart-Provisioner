using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class AutoTestCampaignPlannerTests
{
    [Theory]
    [InlineData("getnet_p2", true)]
    [InlineData("getnet_p3", true)]
    [InlineData("cielo", false)]
    [InlineData("totem_k2", false)]
    [InlineData("default", false)]
    public void OnlyTagsWithBothTargetsCanBeSelected(string tag, bool expected) =>
        Assert.Equal(expected, AutoTestCampaignPlanner.SupportsTag(tag));

    [Fact]
    public void SelectsOnlyConnectedDevicesWithBothExactTargets()
    {
        var devices = new[]
        {
            Device("DX", "getnet_dx8000"),
            Device("P2", "getnet_p2"),
            Device("P3", "getnet_p3"),
            Device("K2", "totem_k2"),
            Device("MP", "mercadopago") with { IsOnline = false }
        };
        var selfHost = new[]
        {
            Client("SELFHOST_device_getnet_dx8000"),
            Client("SELFHOST_device_getnet_p2"),
            Client("SELFHOST_device_getnet_p3")
        };
        var fafnir = new[]
        {
            Client("device_getnetdx8000"),
            Client("device_getnetp2"),
            Client("device_getnetp3")
        };

        var plan = AutoTestCampaignPlanner.Build(devices, selfHost, fafnir);

        Assert.Equal(new[] { "DX", "P2", "P3" }, plan.Devices.Select(x => x.Serial));
        Assert.Single(plan.Skipped);
        Assert.Contains("K2", plan.Skipped[0]);
    }

    [Fact]
    public void DoesNotGuessAClientWhenAnExactTargetIsMissing()
    {
        var plan = AutoTestCampaignPlanner.Build(
            [Device("P2", "getnet_p2")],
            [Client("SELFHOST_device_getnet_p3")],
            [Client("device_getnetp2")]);

        Assert.Empty(plan.Devices);
        Assert.Contains("SELFHOST_device_getnet_p2", Assert.Single(plan.Skipped));
    }

    [Fact]
    public void OnlyRequestedDevicesEnterThePlan()
    {
        var connected = new[] { Device("P2", "getnet_p2"), Device("P3", "getnet_p3") };
        var selected = connected.Where(x => x.Serial == "P2").ToArray();

        var plan = AutoTestCampaignPlanner.Build(selected,
            [Client("SELFHOST_device_getnet_p2"), Client("SELFHOST_device_getnet_p3")],
            [Client("device_getnetp2"), Client("device_getnetp3")]);

        Assert.Equal("P2", Assert.Single(plan.Devices).Serial);
    }

    private static TestAutomationDevice Device(string serial, string tag) =>
        new(serial, tag, "14", "", true, [tag], tag, false);

    private static OAuthClientInfo Client(string name) =>
        new(name + "-id", name, "", 1, null, null, null, null);
}
