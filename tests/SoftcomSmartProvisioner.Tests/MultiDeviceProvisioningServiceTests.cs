using SoftcomSmartProvisioner.Models;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class MultiDeviceProvisioningServiceTests
{
    [Fact]
    public async Task FailureOnOneDeviceDoesNotStopAnotherAndLogsStayIsolated()
    {
        using var service = new MultiDeviceProvisioningService(2);
        var failed = new ProvisioningJob(Device("FAIL"));
        var ok = new ProvisioningJob(Device("OK"));

        var result = await service.RunAsync(new[] { failed, ok }, (job, _) =>
        {
            job.Progress("device", $"log-{job.Serial}");
            return Task.FromResult(job.Serial == "FAIL"
                ? new ProvisioningJobOutcome(false, "device", "falhou")
                : new ProvisioningJobOutcome(true, "complete", "concluiu"));
        });

        Assert.Contains(result, x => x.Serial == "FAIL" && x.Status == ProvisioningJobStatus.Failed);
        Assert.Contains(result, x => x.Serial == "OK" && x.Status == ProvisioningJobStatus.Succeeded);
        Assert.DoesNotContain(result.Single(x => x.Serial == "OK").Logs, x => x.Message.Contains("FAIL"));
    }

    [Fact]
    public async Task IndividualCancellationDoesNotCancelOtherDevice()
    {
        using var service = new MultiDeviceProvisioningService(2);
        var one = new ProvisioningJob(Device("ONE"));
        var two = new ProvisioningJob(Device("TWO"));
        var run = service.RunAsync(new[] { one, two }, async (job, token) =>
        {
            if (job.Serial == "ONE") await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new ProvisioningJobOutcome(true, "complete", "ok");
        });
        await Task.Delay(100);
        Assert.True(service.Cancel("ONE"));
        var result = await run;
        Assert.Equal(ProvisioningJobStatus.Canceled, result.Single(x => x.Serial == "ONE").Status);
        Assert.Equal(ProvisioningJobStatus.Succeeded, result.Single(x => x.Serial == "TWO").Status);
    }

    [Fact]
    public async Task GlobalCancellationCancelsQueuedAndRunningJobs()
    {
        using var service = new MultiDeviceProvisioningService(1);
        var run = service.RunAsync(new[] { new ProvisioningJob(Device("ONE")), new ProvisioningJob(Device("TWO")) },
            async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(10), token); return new ProvisioningJobOutcome(true, "complete", "ok"); });
        await Task.Delay(100);
        service.CancelAll();
        var result = await run;
        Assert.All(result, x => Assert.Equal(ProvisioningJobStatus.Canceled, x.Status));
    }

    [Fact]
    public async Task DuplicateSelectionCreatesOnlyOneJobPerSerial()
    {
        using var service = new MultiDeviceProvisioningService();
        var result = await service.RunAsync(
            new[] { new ProvisioningJob(Device("ONE")), new ProvisioningJob(Device("ONE")) },
            (_, _) => Task.FromResult(new ProvisioningJobOutcome(true, "complete", "ok")));
        Assert.Single(result);
    }

    [Fact]
    public async Task StartsIndependentJobsConcurrently()
    {
        using var service = new MultiDeviceProvisioningService(3);
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = service.RunAsync(
            new[] { new ProvisioningJob(Device("ONE")), new ProvisioningJob(Device("TWO")) },
            async (_, token) =>
            {
                if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new ProvisioningJobOutcome(true, "complete", "ok");
            });

        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref entered));
        release.TrySetResult();
        var result = await run;
        Assert.All(result, x => Assert.Equal(ProvisioningJobStatus.Succeeded, x.Status));
    }

    [Fact]
    public void SnapshotKeepsTheTargetReservedForThatDevice()
    {
        using var first = new ProvisioningJob(Device("ONE"), "SELFHOST_caixa-1");
        using var second = new ProvisioningJob(Device("TWO"), "SELFHOST_caixa-2");

        Assert.Equal("SELFHOST_caixa-1", first.Snapshot().TargetDeviceName);
        Assert.Equal("SELFHOST_caixa-2", second.Snapshot().TargetDeviceName);
    }

    private static DeviceInfo Device(string serial) => new(
        serial, "device", "Model", "Product", "USB", "12", 100, 1, "adb-id", "8.1.0", "Smart 8.1+")
    {
        FriendlyName = $"Device {serial}",
        SmartPackage = $"package.{serial.ToLowerInvariant()}"
    };
}
