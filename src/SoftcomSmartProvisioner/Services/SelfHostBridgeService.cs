using System.Diagnostics;
using System.Text.Json;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class SelfHostBridgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TimeSpan _timeout;
    private readonly string? _bridgePathOverride;
    private readonly Func<string, ProcessStartInfo>? _startInfoFactory;

    public SelfHostBridgeService(
        TimeSpan? timeout = null,
        string? bridgePathOverride = null,
        Func<string, ProcessStartInfo>? startInfoFactory = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
        _bridgePathOverride = bridgePathOverride;
        _startInfoFactory = startInfoFactory;
    }

    public Task<SelfHostBridgeResult> ReadAsync(string installRoot, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new { action = "read", installRoot }, cancellationToken);

    public Task<SelfHostBridgeResult> PreviewAsync(
        string installRoot,
        SelfHostDesiredConfiguration desired,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BuildCommand("preview", installRoot, desired), cancellationToken);

    public Task<SelfHostBridgeResult> ConfigureAsync(
        string installRoot,
        SelfHostDesiredConfiguration desired,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BuildCommand("configure", installRoot, desired), cancellationToken);

    public Task<SelfHostBridgeResult> ValidateAsync(
        string installRoot,
        string localBaseUrl,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new { action = "validate", installRoot, localBaseUrl }, cancellationToken);

    private async Task<SelfHostBridgeResult> ExecuteAsync(object command, CancellationToken cancellationToken)
    {
        var bridgePath = ResolveBridgePath();
        var start = _startInfoFactory?.Invoke(bridgePath) ?? new ProcessStartInfo
        {
            FileName = bridgePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(bridgePath)!
        };

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar o SelfHostBridge.");

        var requestJson = JsonSerializer.Serialize(command, JsonOptions);
        await process.StandardInput.WriteAsync(requestJson.AsMemory(), cancellationToken);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask; // stderr externo nunca é propagado para evitar vazamento de credenciais.
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("O SelfHostBridge encerrou com erro e não retornou resposta.");
            var response = ParseResponse(output);
            if (!response.Success)
                throw new SelfHostBridgeException(response.ErrorCode ?? "bridge_error", SensitiveDataSanitizer.Clean(response.Message));
            if (process.ExitCode != 0)
                throw new InvalidOperationException("O SelfHostBridge encerrou com erro após produzir a resposta.");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            throw new TimeoutException("O SelfHostBridge excedeu o tempo limite da operação.");
        }
    }

    public static SelfHostBridgeResult ParseResponse(string output)
    {
        try
        {
            return JsonSerializer.Deserialize<SelfHostBridgeResult>(output, JsonOptions)
                ?? throw new InvalidOperationException("O SelfHostBridge não retornou resposta.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("O SelfHostBridge retornou uma resposta inválida.");
        }
    }

    private string ResolveBridgePath()
    {
        var candidates = new[]
        {
            _bridgePathOverride,
            Path.Combine(AppContext.BaseDirectory, "SelfHostBridge", "SoftcomSmartProvisioner.SelfHostBridge.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SoftcomSmartProvisioner.SelfHostBridge", "bin", "Debug", "net10.0-windows", "win-x64", "SoftcomSmartProvisioner.SelfHostBridge.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SoftcomSmartProvisioner.SelfHostBridge", "bin", "Release", "net10.0-windows", "win-x64", "SoftcomSmartProvisioner.SelfHostBridge.exe"))
        };
        return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x))
            ?? throw new FileNotFoundException("SelfHostBridge não foi localizado. Recompile a solução com o .NET 10 SDK.");
    }

    private static object BuildCommand(string action, string installRoot, SelfHostDesiredConfiguration desired) => new
    {
        action,
        installRoot,
        desired = new
        {
            backend = desired.Backend,
            portaHttp = desired.PortaHttp,
            smartEnabled = desired.SmartEnabled,
            rootDevice = string.IsNullOrWhiteSpace(desired.RootDeviceUrl) ? null : new { deviceUrl = desired.RootDeviceUrl },
            desktopDatabase = desired.Backend == "softshop"
                ? new
                {
                    server = desired.Servidor,
                    port = desired.Porta,
                    user = desired.Usuario,
                    password = desired.DatabasePassword,
                    database = desired.BancoDados
                }
                : null,
            tableDatabase = desired.ConfigureTableDatabase
                ? new
                {
                    server = desired.MysqlServidor,
                    port = desired.MysqlPorta,
                    user = desired.MysqlUsuario,
                    password = desired.MysqlPassword,
                    database = desired.MysqlDatabase
                }
                : null
        }
    };

    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}

public sealed class SelfHostBridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
