using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SelfHostDeviceServiceTests
{
    [Theory]
    [InlineData("4.0.0.11", "SelfHost 4.0")]
    [InlineData("4.1.0.0", "SelfHost 4.1+")]
    [InlineData("4.1.0.12", "SelfHost 4.1+")]
    public void ClassifiesInstalledSelfHostGeneration(string version, string expected)
    {
        Assert.Equal(expected, SelfHostDeviceService.ClassifySelfHostGeneration(version));
    }

    [Theory]
    [InlineData("TesteSarmento", "SELFHOST_TesteSarmento")]
    [InlineData("SELFHOST_TesteSarmento", "SELFHOST_TesteSarmento")]
    [InlineData("SELFHOST_SELFHOST_TesteSarmento", "SELFHOST_TesteSarmento")]
    public void NormalizesChildDeviceNameForAllSelfHostGenerations(string name, string expected)
    {
        Assert.Equal(expected, SelfHostDeviceService.NormalizeDeviceName(name));
    }
}
