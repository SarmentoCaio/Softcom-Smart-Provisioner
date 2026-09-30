using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace SoftcomSmartProvisioner.Services;

public sealed class SelfHostServiceManager
{
    public const string MonitorServiceName = "Selfhost.MonitorService";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task StopAsync(string installRoot, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        if (await QueryStateAsync(cancellationToken) != 1)
        {
            var result = await RunScAsync("stop", MonitorServiceName, cancellationToken, tolerateFailure: false);
            _ = result;
            await WaitServiceStateAsync(1, limit, cancellationToken);
        }

        var deadline = DateTime.UtcNow + limit;
        var expectedExe = Path.GetFullPath(Path.Combine(installRoot, "SelfHost.exe"));
        await TerminateInstallationProcessAsync(
            "SelfHost", expectedExe, deadline, allowGracefulClose: true, cancellationToken);

        // O SCM pode informar STOPPED alguns instantes antes de o executável do
        // serviço desaparecer. O backup só começa quando os dois processos desta
        // instalação realmente encerraram, evitando a corrida observada no 4.0.
        var expectedMonitorExe = Path.GetFullPath(Path.Combine(installRoot, "Selfhost.MonitorService.exe"));
        await TerminateInstallationProcessAsync(
            "Selfhost.MonitorService", expectedMonitorExe, deadline, allowGracefulClose: false, cancellationToken);
        await WaitProcessExitAsync("SelfHost", expectedExe, deadline, cancellationToken);
        await WaitProcessExitAsync("Selfhost.MonitorService", expectedMonitorExe, deadline, cancellationToken);
        if (await QueryStateAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"O serviço {MonitorServiceName} voltou a iniciar antes do backup.");
    }

    public async Task StartAsync(string installRoot, int port, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        var expectedExe = Path.GetFullPath(Path.Combine(installRoot, "SelfHost.exe"));
        if (await QueryStateAsync(cancellationToken) != 4)
            await RunScAsync("start", MonitorServiceName, cancellationToken, tolerateFailure: false);
        await WaitServiceStateAsync(4, limit, cancellationToken);
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExpectedSelfHostProcess(expectedExe) && IsPortListening(port)) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"O SelfHost não iniciou e não abriu a porta {port} dentro do tempo limite.");
    }

    public static void EnsurePortAvailable(int desiredPort, int? currentSelfHostPort)
    {
        if (desiredPort == currentSelfHostPort || !IsPortListening(desiredPort)) return;
        throw new InvalidOperationException($"A porta {desiredPort} já está em uso por outro processo.");
    }

    private static async Task WaitServiceStateAsync(int expectedState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await QueryStateAsync(cancellationToken) == expectedState) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"O serviço {MonitorServiceName} não atingiu o estado esperado dentro do tempo limite.");
    }

    private static async Task<int?> QueryStateAsync(CancellationToken cancellationToken)
    {
        var output = await RunScAsync("query", MonitorServiceName, cancellationToken, tolerateFailure: true);
        var match = Regex.Match(output, @":\s*(?<state>[1-7])\s+");
        return match.Success && int.TryParse(match.Groups["state"].Value, out var state) ? state : null;
    }

    private static async Task<string> RunScAsync(string verb, string service, CancellationToken cancellationToken, bool tolerateFailure)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"{verb} {service}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Não foi possível executar o gerenciador de serviços do Windows.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        _ = await errorTask;
        if (!tolerateFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"O Windows não conseguiu {verb} o serviço {service}. Execute o Provisioner como administrador.");
        return output;
    }

    private static bool IsPortListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port);

    private static bool HasExpectedSelfHostProcess(string expectedExe)
    {
        foreach (var process in Process.GetProcessesByName("SelfHost"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, expectedExe, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static async Task WaitProcessExitAsync(
        string processName,
        string expectedExe,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasExpectedProcess(processName, expectedExe)) return;
            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"O processo {processName} não encerrou dentro do tempo limite.");
    }

    public static bool HasRunningInstallationProcess(string installRoot)
    {
        var root = Path.GetFullPath(installRoot);
        return HasExpectedProcess("SelfHost", Path.Combine(root, "SelfHost.exe")) ||
               HasExpectedProcess("Selfhost.MonitorService", Path.Combine(root, "Selfhost.MonitorService.exe"));
    }

    private static async Task TerminateInstallationProcessAsync(
        string processName,
        string expectedExe,
        DateTime deadline,
        bool allowGracefulClose,
        CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matching = GetExpectedProcesses(processName, expectedExe);
            if (matching.Count == 0) return;

            foreach (var process in matching)
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        if (allowGracefulClose && process.CloseMainWindow())
                        {
                            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            wait.CancelAfter(TimeSpan.FromSeconds(5));
                            try { await process.WaitForExitAsync(wait.Token); }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                        }
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: false);
                            await process.WaitForExitAsync(cancellationToken);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        throw new InvalidOperationException(
                            $"Não foi possível finalizar {processName} da instalação selecionada. Execute o Provisioner como administrador.",
                            ex);
                    }
                }
            }
        }

        throw new TimeoutException($"O processo {processName} não encerrou dentro do tempo limite.");
    }

    private static bool HasExpectedProcess(string processName, string expectedExe)
    {
        var matching = GetExpectedProcesses(processName, expectedExe);
        foreach (var process in matching)
        {
            using (process)
            {
                if (!process.HasExited) return true;
            }
        }

        return false;
    }

    private static List<Process> GetExpectedProcesses(string processName, string expectedExe)
    {
        var expectedPath = Path.GetFullPath(expectedExe);
        var matching = new List<Process>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var actualPath = process.MainModule?.FileName;
                if (!process.HasExited && !string.IsNullOrWhiteSpace(actualPath) &&
                    string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    matching.Add(process);
                    continue;
                }
            }
            catch { }
            process.Dispose();
        }
        return matching;
    }
}
