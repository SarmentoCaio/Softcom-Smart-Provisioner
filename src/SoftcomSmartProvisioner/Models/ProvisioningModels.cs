using System.Collections.Concurrent;
using System.Diagnostics;

namespace SoftcomSmartProvisioner.Models;

public enum ProvisioningJobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public sealed record ProvisioningLogEntry(DateTimeOffset Timestamp, string Stage, string Message, string Level);

public sealed record ProvisioningJobSnapshot(
    string Serial,
    string FriendlyName,
    string Acquirer,
    string TerminalModel,
    string SmartVersion,
    string SmartPackageName,
    string TargetDeviceName,
    string Stage,
    ProvisioningJobStatus Status,
    string Error,
    long ElapsedMilliseconds,
    IReadOnlyList<ProvisioningLogEntry> Logs);

public sealed class ProvisioningJob : IDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentQueue<ProvisioningLogEntry> _logs = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly CancellationTokenSource _cancellation = new();
    private string _stage = "Aguardando";
    private string _error = string.Empty;
    private ProvisioningJobStatus _status = ProvisioningJobStatus.Pending;

    public ProvisioningJob(DeviceInfo device, string targetDeviceName = "")
    {
        Serial = device.Serial;
        Acquirer = device.Acquirer;
        TerminalModel = device.TerminalModel;
        FriendlyName = device.FriendlyName;
        SmartVersion = device.SmartVersion;
        SmartPackageName = device.SmartPackage;
        TargetDeviceName = targetDeviceName;
    }

    public string Serial { get; }
    public string Acquirer { get; }
    public string TerminalModel { get; }
    public string FriendlyName { get; }
    public string SmartVersion { get; }
    public string SmartPackageName { get; }
    public string TargetDeviceName { get; }
    public CancellationToken CancellationToken => _cancellation.Token;

    public void Start()
    {
        lock (_sync) { _status = ProvisioningJobStatus.Running; _stage = "Iniciando"; _stopwatch.Start(); }
        Log("start", "Provisionamento iniciado.");
    }

    public void Progress(string stage, string message)
    {
        CancellationToken.ThrowIfCancellationRequested();
        lock (_sync) _stage = stage;
        Log(stage, message);
    }

    public void Complete(string message)
    {
        lock (_sync) { _status = ProvisioningJobStatus.Succeeded; _stage = "Concluído"; _stopwatch.Stop(); }
        Log("complete", message);
    }

    public void Fail(string stage, string error)
    {
        lock (_sync) { _status = ProvisioningJobStatus.Failed; _stage = stage; _error = error; _stopwatch.Stop(); }
        Log(stage, error, "ERROR");
    }

    public void MarkCanceled(string message = "Cancelado pelo usuário.")
    {
        lock (_sync) { _status = ProvisioningJobStatus.Canceled; _stage = "Cancelado"; _error = message; _stopwatch.Stop(); }
        Log("canceled", message, "WARN");
    }

    public void Cancel() => _cancellation.Cancel();

    public void Log(string stage, string message, string level = "INFO") =>
        _logs.Enqueue(new ProvisioningLogEntry(DateTimeOffset.Now, stage, message, level));

    public ProvisioningJobSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new ProvisioningJobSnapshot(Serial, FriendlyName, Acquirer, TerminalModel, SmartVersion,
                SmartPackageName, TargetDeviceName, _stage, _status, _error, _stopwatch.ElapsedMilliseconds, _logs.ToArray());
        }
    }

    public void Dispose() => _cancellation.Dispose();
}

public sealed record ProvisioningJobOutcome(bool Success, string Stage, string Message);
