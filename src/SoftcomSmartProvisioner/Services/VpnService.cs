using System.Diagnostics;
using System.Net.Sockets;

namespace SoftcomSmartProvisioner.Services;

public sealed class VpnService
{
    private const string OpenVpnAmd64Url = "https://build.openvpn.net/downloads/releases/latest/openvpn-latest-stable-amd64.msi";
    private const string OpenVpnX86Url = "https://build.openvpn.net/downloads/releases/latest/openvpn-latest-stable-x86.msi";
    private const string ManagementHost = "127.0.0.1";
    private const int ManagementPort = 25347;

    private readonly SecretStore _secretStore;
    private readonly string _appDataDirectory;
    private readonly SettingsService _settingsService;
    private readonly Action<string>? _logger;

    public VpnService(SecretStore secretStore, SettingsService settingsService, string appDataDirectory, Action<string>? logger = null)
    {
        _secretStore = secretStore;
        _settingsService = settingsService;
        _appDataDirectory = appDataDirectory;
        _logger = logger;
    }

    private void Log(string message) => _logger?.Invoke(message);

    public bool IsOpenVpnRunning() => Process.GetProcesses().Any(p =>
    {
        try { return string.Equals(p.ProcessName, "openvpn", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    });

    public async Task<bool> CanReachAsync(string host, int port, int timeoutMs = 1500)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await client.ConnectAsync(host, port, cts.Token); return true; }
        catch { return false; }
    }

    public string? FindOpenVpn()
    {
        var explicitPath = Environment.GetEnvironmentVariable("OPENVPN_EXE");
        var candidates = new[]
        {
            explicitPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "bin", "openvpn.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public string? ResolveProfile()
    {
        var settings = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.VpnProfilePath) && File.Exists(settings.VpnProfilePath)) return settings.VpnProfilePath;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacy = Path.Combine(local, "Softcom", "RecuperadorVendasPDV", "vpn", "sofcom-virginia.ovpn");
        return File.Exists(legacy) ? legacy : null;
    }

    public async Task<string> ConnectAsync(string environmentKey, CancellationToken cancellationToken = default)
    {
        var environment = EnvironmentCatalog.Get(environmentKey);
        if (await CanReachAsync(environment.Host, environment.Port))
        {
            Log("Acesso ao banco ja disponivel; VPN nao precisou ser iniciada.");
            return "O acesso ao banco ja esta disponivel. A VPN nao precisou ser iniciada.";
        }

        Log($"Acesso a {environment.Host}:{environment.Port} indisponivel. Verificando VPN...");
        if (IsOpenVpnRunning())
        {
            Log("OpenVPN ja esta em execucao. Aguardando a rota ficar disponivel...");
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(700, cancellationToken);
                if (await CanReachAsync(environment.Host, environment.Port))
                    return "VPN ja estava ativa e o acesso ao banco foi confirmado.";
            }
            Log("OpenVPN estava ativo, mas sem acesso ao banco. Reiniciando a sessao.");
            await DisconnectAsync(cancellationToken);
        }

        var openVpn = FindOpenVpn() ?? await InstallOpenVpnAsync(cancellationToken);
        var profile = ResolveProfile() ?? throw new InvalidOperationException("Perfil OpenVPN nao localizado. Selecione o arquivo .ovpn em Configuracoes.");
        var username = _secretStore.Get(SecretStore.VpnUsername);
        var password = _secretStore.Get(SecretStore.VpnPassword);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Credenciais da VPN nao configuradas. Importe as credenciais do Recuperador.");

        var vpnDir = Path.Combine(_appDataDirectory, "vpn");
        Directory.CreateDirectory(vpnDir);
        var authFile = Path.Combine(vpnDir, ".openvpn-auth");
        var openVpnLog = Path.Combine(vpnDir, "openvpn.log");
        await File.WriteAllTextAsync(authFile, $"{username}{Environment.NewLine}{password}{Environment.NewLine}", cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = openVpn,
            WorkingDirectory = Path.GetDirectoryName(profile) ?? Environment.CurrentDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--config"); startInfo.ArgumentList.Add(profile);
        startInfo.ArgumentList.Add("--auth-user-pass"); startInfo.ArgumentList.Add(authFile);
        startInfo.ArgumentList.Add("--auth-nocache");
        startInfo.ArgumentList.Add("--management"); startInfo.ArgumentList.Add(ManagementHost); startInfo.ArgumentList.Add(ManagementPort.ToString());
        startInfo.ArgumentList.Add("--log-append"); startInfo.ArgumentList.Add(openVpnLog);

        Log("Iniciando OpenVPN em segundo plano.");
        Process.Start(startInfo);

        for (var i = 0; i < 35; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken);
            if (await CanReachAsync(environment.Host, environment.Port))
            {
                Log("VPN conectada e acesso ao banco confirmado.");
                LogOpenVpnTail(openVpnLog);
                return "VPN conectada e acesso ao banco confirmado.";
            }
        }
        LogOpenVpnTail(openVpnLog);
        throw new TimeoutException("A VPN foi iniciada, mas o acesso ao banco ainda nao ficou disponivel. Consulte a aba Logs.");
    }

    public async Task<string> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(1500);
            await client.ConnectAsync(ManagementHost, ManagementPort, timeout.Token);
            await using var stream = client.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync("signal SIGTERM");
            messages.Add("Sessao OpenVPN gerenciada encerrada.");
            await Task.Delay(900, cancellationToken);
        }
        catch { }

        var pids = Process.GetProcesses().Where(p =>
        {
            try { return string.Equals(p.ProcessName, "openvpn", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).Select(p => p.Id).Distinct().ToArray();

        if (pids.Length > 0)
        {
            var killInfo = new ProcessStartInfo { FileName = "taskkill.exe", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var pid in pids) { killInfo.ArgumentList.Add("/PID"); killInfo.ArgumentList.Add(pid.ToString()); }
            killInfo.ArgumentList.Add("/F");
            using var kill = Process.Start(killInfo) ?? throw new InvalidOperationException("Nao foi possivel encerrar o OpenVPN ativo.");
            await kill.WaitForExitAsync(cancellationToken);
            if (kill.ExitCode != 0) throw new InvalidOperationException($"O OpenVPN foi localizado, mas o encerramento retornou o codigo {kill.ExitCode}.");
            messages.Add($"{pids.Length} processo(s) OpenVPN encerrado(s).");
            await Task.Delay(1200, cancellationToken);
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOpenVpnRunning())
            {
                var result = messages.Count == 0 ? "Nenhum processo OpenVPN estava ativo." : string.Join(" ", messages) + " VPN desativada.";
                Log(result);
                return result;
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException("Ainda existe um processo openvpn.exe ativo. A preparacao foi interrompida.");
    }

    private void LogOpenVpnTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path).TakeLast(12))
                if (!string.IsNullOrWhiteSpace(line)) Log("OpenVPN: " + line.Trim());
        }
        catch { }
    }

    private async Task<string> InstallOpenVpnAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("A instalacao automatica do OpenVPN esta disponivel somente no Windows.");
        var vpnDir = Path.Combine(_appDataDirectory, "vpn"); Directory.CreateDirectory(vpnDir);
        var url = Environment.Is64BitOperatingSystem ? OpenVpnAmd64Url : OpenVpnX86Url;
        var installer = Path.Combine(vpnDir, Path.GetFileName(new Uri(url).LocalPath));
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(4) })
        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(installer);
            await source.CopyToAsync(target, cancellationToken);
        }
        var info = new FileInfo(installer);
        if (!info.Exists || info.Length < 1_000_000) throw new InvalidOperationException("O instalador do OpenVPN baixado parece incompleto.");
        await VerifyInstallerSignatureAsync(installer, cancellationToken);
        var startInfo = new ProcessStartInfo { FileName = "msiexec.exe", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        startInfo.ArgumentList.Add("/i"); startInfo.ArgumentList.Add(installer); startInfo.ArgumentList.Add("/passive"); startInfo.ArgumentList.Add("/norestart");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Nao foi possivel iniciar o instalador do OpenVPN.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode is not 0 and not 3010) throw new InvalidOperationException($"A instalacao do OpenVPN retornou o codigo {process.ExitCode}.");
        for (var i = 0; i < 20; i++) { var executable = FindOpenVpn(); if (executable is not null) return executable; await Task.Delay(500, cancellationToken); }
        throw new InvalidOperationException("O OpenVPN foi instalado, mas o executavel ainda nao foi localizado.");
    }

    private static async Task VerifyInstallerSignatureAsync(string installer, CancellationToken cancellationToken)
    {
        var escaped = installer.Replace("'", "''");
        var command = $"$s=Get-AuthenticodeSignature -LiteralPath '{escaped}'; $s.Status.ToString() + '|' + $s.SignerCertificate.Subject";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        var result = await ProcessRunner.RunTextAsync(powershell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command }, Environment.CurrentDirectory, cancellationToken, 30000);
        var output = result.StandardOutput.Trim();
        if (!result.Success || !output.StartsWith("Valid|", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nao foi possivel validar a assinatura digital do instalador do OpenVPN. A instalacao foi cancelada.");
    }
}
