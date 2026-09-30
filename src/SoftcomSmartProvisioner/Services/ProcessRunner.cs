using System.Diagnostics;
using System.Text;

namespace SoftcomSmartProvisioner.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
    public string CombinedOutput =>
        string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunTextAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 30000,
        string? standardInput = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        if (standardInput is not null)
            process.StartInfo.StandardInputEncoding = Encoding.UTF8;

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessResult(-1, await stdoutTask, "Tempo limite excedido.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    public static async Task<ProcessResult> RunTextStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        int timeoutMilliseconds,
        Action<string>? onOutputLine,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                if (value is null) process.StartInfo.Environment.Remove(name);
                else process.StartInfo.Environment[name] = value;
            }
        }

        process.Start();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, onOutputLine, cancellationToken);
        var stderrTask = ReadLinesAsync(process.StandardError, stderr, onOutputLine, cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            return new ProcessResult(-1, stdout.ToString(), "Tempo limite excedido.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder destination,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            destination.AppendLine(line);
            if (line.Length == 0 || onOutputLine is null) continue;
            try { onOutputLine(line); } catch { }
        }
    }

    private static async Task DrainAfterKillAsync(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks); } catch { }
    }

    public static async Task<BinaryProcessResult> RunBinaryAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 30000)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await copyTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new BinaryProcessResult(-1, Array.Empty<byte>(), "Tempo limite excedido.");
        }

        return new BinaryProcessResult(process.ExitCode, output.ToArray(), await stderrTask);
    }

    public static Process StartDetached(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            // scrcpy é distribuído como executável de console, mas sua interface útil
            // é a janela SDL de espelhamento. Não abra um console auxiliar ao iniciá-lo.
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Nao foi possivel iniciar {Path.GetFileName(fileName)}.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record BinaryProcessResult(int ExitCode, byte[] Data, string StandardError)
{
    public bool Success => ExitCode == 0;
}
