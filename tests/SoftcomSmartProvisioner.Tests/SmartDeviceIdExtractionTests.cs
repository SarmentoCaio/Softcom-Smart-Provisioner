using System.Reflection;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SmartDeviceIdExtractionTests
{
    [Theory]
    [InlineData("Device ID: d76185d2d89d7008", "d76185d2d89d7008")]
    [InlineData(
        "62 - Erro ao adicionar dispositivo [DeviceId: 5bddda6e1e423c12] - O dispositivo ja encontra-se em uso para essa Empresa.",
        "5bddda6e1e423c12")]
    [InlineData("Erro sem identificador", "")]
    public void ExtractsDeviceIdFromHeaderAndConflictMessage(string text, string expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "ExtractSmartDeviceIdFromText",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "ExtractSmartDeviceIdFromText");

        var actual = method.Invoke(null, new object?[] { text }) as string;

        Assert.Equal(expected, actual);
    }
}
