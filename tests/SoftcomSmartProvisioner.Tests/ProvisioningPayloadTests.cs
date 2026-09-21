using System.Reflection;
using System.Text.Json;

namespace SoftcomSmartProvisioner.Tests;

public sealed class ProvisioningPayloadTests
{
    [Fact]
    public void FreezesDifferentTargetForEachSerial()
    {
        using var source = JsonDocument.Parse("""
        {
          "serials": ["ADB-A", "ADB-B"],
          "oauthClient": { "clientId": "LAST", "name": "SELFHOST_ultimo" },
          "oauthClientsBySerial": {
            "ADB-A": { "clientId": "CLIENT-A", "name": "SELFHOST_caixa-a" },
            "ADB-B": { "clientId": "CLIENT-B", "name": "SELFHOST_caixa-b" }
          }
        }
        """);

        var first = WithSerial(source.RootElement, "ADB-A", true);
        var second = WithSerial(source.RootElement, "ADB-B", true);

        Assert.Equal("ADB-A", first.GetProperty("serial").GetString());
        Assert.Equal("CLIENT-A", first.GetProperty("oauthClient").GetProperty("clientId").GetString());
        Assert.Equal("ADB-B", second.GetProperty("serial").GetString());
        Assert.Equal("CLIENT-B", second.GetProperty("oauthClient").GetProperty("clientId").GetString());
        Assert.False(first.TryGetProperty("oauthClientsBySerial", out _));
    }

    [Fact]
    public void MultiDevicePayloadWithoutExactMappingDoesNotReuseGlobalTarget()
    {
        using var source = JsonDocument.Parse("""
        {
          "oauthClient": { "clientId": "LAST", "name": "SELFHOST_ultimo" },
          "oauthClientsBySerial": {
            "ADB-A": { "clientId": "CLIENT-A", "name": "SELFHOST_caixa-a" }
          }
        }
        """);

        var result = WithSerial(source.RootElement, "ADB-B", true);

        Assert.False(result.TryGetProperty("oauthClient", out _));
    }

    [Theory]
    [InlineData("smart_autopagamento")]
    [InlineData("smart_totem")]
    public void PerDevicePayloadPreservesRequestedSelfServiceModule(string module)
    {
        using var source = JsonDocument.Parse($$"""
        {
          "module": "{{module}}",
          "oauthClientsBySerial": {
            "KM54257740097": { "clientId": "CLIENT-K2", "name": "SELFHOST_K2" }
          }
        }
        """);

        var result = WithSerial(source.RootElement, "KM54257740097", true);

        Assert.Equal(module, result.GetProperty("module").GetString());
        Assert.Equal("CLIENT-K2", result.GetProperty("oauthClient").GetProperty("clientId").GetString());
    }

    private static JsonElement WithSerial(JsonElement payload, string serial, bool requireMappedClient)
    {
        var method = typeof(MainForm).GetMethod(
            "WithSerial",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(MainForm).FullName, "WithSerial");
        return (JsonElement)(method.Invoke(null, new object[] { payload, serial, requireMappedClient })
            ?? throw new InvalidOperationException("WithSerial nao retornou payload."));
    }
}
