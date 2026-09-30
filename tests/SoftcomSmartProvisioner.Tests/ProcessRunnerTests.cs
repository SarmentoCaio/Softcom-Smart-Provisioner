using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task ProcessWithoutStdinDoesNotConfigureUnsupportedInputEncoding()
    {
        var command = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        var result = await ProcessRunner.RunTextAsync(
            command,
            ["/d", "/c", "echo", "adb-ok"],
            Environment.CurrentDirectory,
            CancellationToken.None,
            5000);

        Assert.True(result.Success, result.CombinedOutput);
        Assert.Contains("adb-ok", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessWithStdinReceivesUtf8Content()
    {
        var command = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        var result = await ProcessRunner.RunTextAsync(
            command,
            ["/d", "/c", "more"],
            Environment.CurrentDirectory,
            CancellationToken.None,
            5000,
            "impressora-P3");

        Assert.True(result.Success, result.CombinedOutput);
        Assert.Contains("impressora-P3", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingProcessReceivesOnlyItsConfiguredEnvironment()
    {
        const string variable = "SSP_CHILD_ENV_TEST";
        Environment.SetEnvironmentVariable(variable, null, EnvironmentVariableTarget.Process);
        var command = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        var result = await ProcessRunner.RunTextStreamingAsync(
            command,
            ["/d", "/c", $"echo %{variable}%"],
            Environment.CurrentDirectory,
            CancellationToken.None,
            5000,
            null,
            new Dictionary<string, string?> { [variable] = "legacy-appium" });

        Assert.True(result.Success, result.CombinedOutput);
        Assert.Contains("legacy-appium", result.StandardOutput, StringComparison.Ordinal);
        Assert.Null(Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process));
    }
}
