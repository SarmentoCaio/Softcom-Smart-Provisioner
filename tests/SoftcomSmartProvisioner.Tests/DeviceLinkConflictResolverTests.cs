using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class DeviceLinkConflictResolverTests
{
    [Fact]
    public void FindsLinkThatExistsOnlyInSoftcomshop()
    {
        var entries = new[]
        {
            Entry("web-client", "Dispositivo WEB", "device-k2", DeviceRegistrySource.Softcomshop)
        };

        var result = DeviceLinkConflictResolver.FindConflicts(entries, null, "device-k2");

        var conflict = Assert.Single(result);
        Assert.Equal(DeviceRegistrySource.Softcomshop, conflict.Source);
    }

    [Fact]
    public void FindsLinkThatExistsOnlyInSelfHost()
    {
        var entries = new[]
        {
            Entry("child", "SELFHOST_K2", "device-k2", DeviceRegistrySource.SelfHost)
        };

        var result = DeviceLinkConflictResolver.FindConflicts(entries, null, "device-k2");

        var conflict = Assert.Single(result);
        Assert.Equal(DeviceRegistrySource.SelfHost, conflict.Source);
    }

    [Fact]
    public void FindsNormalDeviceExposedBySelfHostAdministrativeRegistry()
    {
        var entries = new[]
        {
            Entry("normal-device", "device_getnetp3", "device-getnet", DeviceRegistrySource.SelfHost)
        };

        var result = DeviceLinkConflictResolver.FindConflicts(entries, null, "device-getnet");

        var conflict = Assert.Single(result);
        Assert.Equal("device_getnetp3", conflict.Device.Name);
        Assert.Equal(DeviceRegistrySource.SelfHost, conflict.Source);
    }

    [Fact]
    public void FindsDifferentLinksInBothRegistries()
    {
        var entries = new[]
        {
            Entry("web-client", "Dispositivo WEB", "device-k2", DeviceRegistrySource.Softcomshop),
            Entry("child", "SELFHOST_K2", "device-k2", DeviceRegistrySource.SelfHost)
        };

        var result = DeviceLinkConflictResolver.FindConflicts(entries, null, "device-k2");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, x => x.Source == DeviceRegistrySource.Softcomshop);
        Assert.Contains(result, x => x.Source == DeviceRegistrySource.SelfHost);
    }

    [Fact]
    public void PrefersSelfHostWhenSameClientAppearsInBothRegistries()
    {
        var entries = new[]
        {
            Entry("same-client", "SELFHOST_K2", "device-k2", DeviceRegistrySource.Softcomshop),
            Entry("same-client", "SELFHOST_K2", "device-k2", DeviceRegistrySource.SelfHost)
        };

        var result = DeviceLinkConflictResolver.FindConflicts(entries, null, "device-k2");

        var conflict = Assert.Single(result);
        Assert.Equal(DeviceRegistrySource.SelfHost, conflict.Source);
    }

    [Fact]
    public void SelectedLinkedClientIsConflictEvenWithoutKnownDeviceId()
    {
        var entries = new[]
        {
            Entry("selected", "SELFHOST_K2", "unknown-id", DeviceRegistrySource.SelfHost),
            Entry("other", "SELFHOST_OUTRO", "other-id", DeviceRegistrySource.SelfHost)
        };

        var result = DeviceLinkConflictResolver.FindConflicts(entries, "selected");

        Assert.Equal("selected", Assert.Single(result).Device.ClientId);
    }

    [Fact]
    public void IgnoresUnlinkedEntriesAndUnrelatedDeviceIds()
    {
        var entries = new[]
        {
            Entry("selected", "SELFHOST_K2", "", DeviceRegistrySource.SelfHost),
            Entry("other", "Dispositivo WEB", "other-id", DeviceRegistrySource.Softcomshop)
        };

        var result = DeviceLinkConflictResolver.FindConflicts(entries, "selected", "device-k2");

        Assert.Empty(result);
    }

    private static DeviceRegistryEntry Entry(
        string clientId,
        string name,
        string deviceId,
        DeviceRegistrySource source) =>
        new(new OAuthClientInfo(clientId, name, deviceId, 1, null, null, null, null), source);
}
