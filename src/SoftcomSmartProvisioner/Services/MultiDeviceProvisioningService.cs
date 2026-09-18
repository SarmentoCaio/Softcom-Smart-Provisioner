using System.Collections.Concurrent;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class MultiDeviceProvisioningService : IDisposable
{
    public const int DefaultMaxParallelism = 3;
    private readonly int _maxParallelism;
    private readonly ConcurrentDictionary<string, ProvisioningJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCancellation;

    public MultiDeviceProvisioningService(int maxParallelism = DefaultMaxParallelism) =>
        _maxParallelism = Math.Clamp(maxParallelism, 1, 8);

    public event Action<ProvisioningJobSnapshot>? JobChanged;
    public IReadOnlyCollection<ProvisioningJobSnapshot> Snapshots => _jobs.Values.Select(x => x.Snapshot()).ToArray();

    public async Task<IReadOnlyList<ProvisioningJobSnapshot>> RunAsync(
        IEnumerable<ProvisioningJob> jobs,
        Func<ProvisioningJob, CancellationToken, Task<ProvisioningJobOutcome>> executeAsync,
        CancellationToken cancellationToken = default)
    {
        var selected = jobs.GroupBy(x => x.Serial, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
        _runCancellation?.Dispose();
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var job in selected) _jobs[job.Serial] = job;

        using var workers = new SemaphoreSlim(_maxParallelism, _maxParallelism);
        await Task.WhenAll(selected.Select(job => RunOneAsync(job, workers, executeAsync, _runCancellation.Token)));
        return selected.Select(x => x.Snapshot()).ToArray();
    }

    public bool Cancel(string serial)
    {
        if (!_jobs.TryGetValue(serial, out var job)) return false;
        job.Cancel();
        return true;
    }

    public void CancelAll() => _runCancellation?.Cancel();

    public void NotifyChanged(ProvisioningJob job) => JobChanged?.Invoke(job.Snapshot());

    private async Task RunOneAsync(
        ProvisioningJob job,
        SemaphoreSlim workers,
        Func<ProvisioningJob, CancellationToken, Task<ProvisioningJobOutcome>> executeAsync,
        CancellationToken globalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(globalToken, job.CancellationToken);
        try
        {
            await workers.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled();
            NotifyChanged(job);
            return;
        }

        try
        {
            job.Start();
            NotifyChanged(job);
            var outcome = await executeAsync(job, linked.Token);
            if (outcome.Success) job.Complete(outcome.Message);
            else job.Fail(outcome.Stage, outcome.Message);
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled();
        }
        catch (Exception ex)
        {
            job.Fail("exception", ex.Message);
        }
        finally
        {
            workers.Release();
            NotifyChanged(job);
        }
    }

    public void Dispose()
    {
        _runCancellation?.Dispose();
        foreach (var job in _jobs.Values) job.Dispose();
    }
}
