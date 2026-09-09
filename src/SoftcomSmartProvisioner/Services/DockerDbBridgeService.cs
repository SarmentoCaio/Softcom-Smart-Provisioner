using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace SoftcomSmartProvisioner.Services;

public sealed class DockerDbBridgeService
{
    public const int LocalPort = 13306;
    private const string ContainerName = "softcom-smart-provisioner-dbbridge";
    private const string ImageName = "softcom-smart-provisioner-dbbridge:1";

    private readonly SecretStore _secretStore;
    private readonly SettingsService _settingsService;
    private readonly string _appDataDirectory;
    private readonly string _bridgeAssetsDirectory;
    private readonly Action<string>? _logger;

    public DockerDbBridgeService(
        SecretStore secretStore,
        SettingsService settingsService,
        string appDataDirectory,
        string bridgeAssetsDirectory,
        Action<string>? logger = null)
    {
        _secretStore = secretStore;
        _settingsService = settingsService;
        _appDataDirectory = appDataDirectory;
        _bridgeAssetsDirectory = bridgeAssetsDirectory;
        _logger = logger;
    }

    private void Log(string message) => _logger?.Invoke(message);

    public bool IsDockerAvailable() => ResolveDockerExecutable() is not null;

    public async Task<bool> IsDockerRunningAsync(CancellationToken cancellationToken = default)
    {
        var docker = ResolveDockerExecutable();
        if (docker is null) return false;
        try
        {
            var result = await ProcessRunner.RunTextAsync(
                docker,
                new[] { "info", "--format", "{{.ServerVersion}}" },
                Environment.CurrentDirectory,
                cancellationToken,
                8000);
            return result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch { return false; }
    }

    public async Task<string> StartAsync(string environmentKey, CancellationToken cancellationToken = default)
    {
        var docker = ResolveDockerExecutable()
            ?? throw new InvalidOperationException("Docker Desktop nao localizado. Instale/inicie o Docker Desktop ou use o modo VPN local.");

        if (!await IsDockerRunningAsync(cancellationToken))
            throw new InvalidOperationException("Docker Desktop foi localizado, mas o engine nao esta em execucao.");

        var profile = ResolveProfile()
            ?? throw new InvalidOperationException("Perfil OpenVPN nao localizado. Selecione o arquivo .ovpn em Configuracoes.");
        var username = _secretStore.Get(SecretStore.VpnUsername);
        var password = _secretStore.Get(SecretStore.VpnPassword);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Credenciais da VPN nao configuradas. Importe as credenciais antes de usar o Docker isolado.");

        if (!Directory.Exists(_bridgeAssetsDirectory) || !File.Exists(Path.Combine(_bridgeAssetsDirectory, "Dockerfile")))
            throw new InvalidOperationException("Arquivos do DB Bridge Docker nao foram encontrados na instalacao do Provisioner.");

        var environment = EnvironmentCatalog.Get(environmentKey);
        var runtimeDirectory = Path.Combine(_appDataDirectory, "docker-db-bridge");
        Directory.CreateDirectory(runtimeDirectory);
        var authFile = Path.Combine(runtimeDirectory, "auth.txt");
        await File.WriteAllTextAsync(authFile, $"{username}{Environment.NewLine}{password}{Environment.NewLine}", new UTF8Encoding(false), cancellationToken);

        await StopAsync(cancellationToken);

        Log("Preparando imagem Docker isolada para acesso ao banco.");
        var build = await ProcessRunner.RunTextAsync(
            docker,
            new[] { "build", "-t", ImageName, _bridgeAssetsDirectory },
            _bridgeAssetsDirectory,
            cancellationToken,
            180000);
        if (!build.Success)
            throw new InvalidOperationException("Falha ao preparar o DB Bridge Docker. " + LastMeaningfulLine(build.CombinedOutput));

        var stagedProfile = PrepareVpnProfile(profile, runtimeDirectory);
        var profileDirectoryMount = ToDockerPath(stagedProfile.Directory);
        var profileFileName = stagedProfile.ProfileFileName;
        var authMount = ToDockerPath(authFile);
        var args = new[]
        {
            "run", "-d",
            "--name", ContainerName,
            "--cap-add=NET_ADMIN",
            "--device", "/dev/net/tun:/dev/net/tun",
            "-p", $"127.0.0.1:{LocalPort}:3306",
            "--mount", $"type=bind,source={profileDirectoryMount},target=/vpn/profile,readonly",
            "--mount", $"type=bind,source={authMount},target=/vpn/auth.txt,readonly",
            "-e", $"OVPN_FILE=/vpn/profile/{profileFileName}",
            "-e", $"DB_HOST={environment.Host}",
            "-e", $"DB_PORT={environment.Port}",
            ImageName
        };

        Log($"Iniciando container isolado para {environment.Label}. Somente o container entra na VPN.");
        var run = await ProcessRunner.RunTextAsync(docker, args, Environment.CurrentDirectory, cancellationToken, 30000);
        if (!run.Success)
            throw new InvalidOperationException("Nao foi possivel iniciar o DB Bridge Docker. " + LastMeaningfulLine(run.CombinedOutput));

        for (var attempt = 0; attempt < 45; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CanReachLocalBridgeAsync(cancellationToken) &&
                await CanReachRemoteFromContainerAsync(environment.Host, environment.Port, cancellationToken))
            {
                Log($"DB Bridge validado: VPN ativa e banco remoto acessivel. Proxy em 127.0.0.1:{LocalPort}.");
                return $"Docker isolado conectado. Banco validado em 127.0.0.1:{LocalPort}.";
            }

            if (!await IsContainerRunningAsync(cancellationToken))
            {
                var earlyLogs = await GetLogsAsync(cancellationToken);
                throw new InvalidOperationException("O DB Bridge Docker encerrou durante a validacao. " + LastMeaningfulLine(earlyLogs));
            }

            await Task.Delay(1000, cancellationToken);
        }

        var logs = await GetLogsAsync(cancellationToken);
        await StopAsync(cancellationToken);
        throw new TimeoutException("O container iniciou, mas o tunel para o banco nao ficou disponivel. " + LastMeaningfulLine(logs));
    }


    private async Task<bool> CanReachRemoteFromContainerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var docker = ResolveDockerExecutable();
        if (docker is null) return false;
        try
        {
            var result = await ProcessRunner.RunTextAsync(
                docker,
                new[] { "exec", ContainerName, "nc", "-z", "-w", "3", host, port.ToString() },
                Environment.CurrentDirectory,
                cancellationToken,
                6000);
            return result.Success;
        }
        catch { return false; }
    }

    private async Task<bool> IsContainerRunningAsync(CancellationToken cancellationToken = default)
    {
        var docker = ResolveDockerExecutable();
        if (docker is null) return false;
        try
        {
            var result = await ProcessRunner.RunTextAsync(
                docker,
                new[] { "inspect", "-f", "{{.State.Running}}", ContainerName },
                Environment.CurrentDirectory,
                cancellationToken,
                6000);
            return result.Success && result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var docker = ResolveDockerExecutable();
        if (docker is null) return;
        try
        {
            await ProcessRunner.RunTextAsync(
                docker,
                new[] { "rm", "-f", ContainerName },
                Environment.CurrentDirectory,
                cancellationToken,
                12000);
        }
        catch { }
    }

    public async Task<bool> CanReachLocalBridgeAsync(CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(1200);
        try
        {
            await client.ConnectAsync("127.0.0.1", LocalPort, timeout.Token);
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> IsReadyForEnvironmentAsync(
        string environmentKey,
        CancellationToken cancellationToken = default)
    {
        if (!await CanReachLocalBridgeAsync(cancellationToken))
            return false;

        var docker = ResolveDockerExecutable();
        if (docker is null) return false;

        var environment = EnvironmentCatalog.Get(environmentKey);
        try
        {
            var inspect = await ProcessRunner.RunTextAsync(
                docker,
                new[]
                {
                    "inspect",
                    "-f",
                    "{{range .Config.Env}}{{println .}}{{end}}",
                    ContainerName
                },
                Environment.CurrentDirectory,
                cancellationToken,
                6000);

            if (!inspect.Success) return false;

            var expectedHost = $"DB_HOST={environment.Host}";
            var expectedPort = $"DB_PORT={environment.Port}";
            var lines = inspect.StandardOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return lines.Any(x => string.Equals(x, expectedHost, StringComparison.OrdinalIgnoreCase))
                && lines.Any(x => string.Equals(x, expectedPort, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetLogsAsync(CancellationToken cancellationToken = default)
    {
        var docker = ResolveDockerExecutable();
        if (docker is null) return string.Empty;
        var result = await ProcessRunner.RunTextAsync(
            docker,
            new[] { "logs", "--tail", "30", ContainerName },
            Environment.CurrentDirectory,
            cancellationToken,
            8000);
        return result.CombinedOutput;
    }


    private sealed record StagedVpnProfile(string Directory, string ProfileFileName);

    private StagedVpnProfile PrepareVpnProfile(string profilePath, string runtimeDirectory)
    {
        var sourceProfile = Path.GetFullPath(profilePath);
        var sourceDirectory = Path.GetDirectoryName(sourceProfile)
            ?? throw new InvalidOperationException("Pasta do perfil OpenVPN invalida.");

        var stagingDirectory = Path.Combine(runtimeDirectory, "vpn-profile");
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
        Directory.CreateDirectory(stagingDirectory);

        var profileLines = File.ReadAllLines(sourceProfile);
        var rewritten = new List<string>(profileLines.Length);
        var copiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directivesWithFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ca", "cert", "key", "pkcs12", "tls-auth", "tls-crypt", "tls-crypt-v2", "crl-verify"
        };

        foreach (var originalLine in profileLines)
        {
            var trimmed = originalLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
            {
                rewritten.Add(originalLine);
                continue;
            }

            var parts = SplitOpenVpnDirective(trimmed);
            if (parts.Count < 2 || !directivesWithFiles.Contains(parts[0]))
            {
                rewritten.Add(originalLine);
                continue;
            }

            // Conteudo inline (<ca>...</ca>, por exemplo) nao precisa de arquivo externo.
            if (parts[1].Equals("[inline]", StringComparison.OrdinalIgnoreCase))
            {
                rewritten.Add(originalLine);
                continue;
            }

            var referenced = parts[1];
            var resolved = ResolveVpnReferencedFile(referenced, sourceDirectory);
            if (resolved is null)
                throw new InvalidOperationException($"O perfil OpenVPN referencia o arquivo '{referenced}', mas ele nao foi localizado. Coloque o arquivo junto do .ovpn ou selecione um perfil completo nas Configuracoes.");

            var destinationName = Path.GetFileName(resolved);
            if (!copiedNames.Add(destinationName))
            {
                // Mesmo nome ja copiado. Mantemos o primeiro somente se for o mesmo arquivo.
                var existing = Path.Combine(stagingDirectory, destinationName);
                if (!File.Exists(existing) || !FilesEqual(existing, resolved))
                    throw new InvalidOperationException($"O perfil OpenVPN usa mais de um arquivo chamado '{destinationName}'. Renomeie os arquivos para nomes unicos antes de usar o Docker isolado.");
            }
            else
            {
                File.Copy(resolved, Path.Combine(stagingDirectory, destinationName), overwrite: true);
                Log($"Arquivo auxiliar da VPN preparado para o Docker: {destinationName}.");
            }

            parts[1] = $"/vpn/profile/{destinationName}";
            rewritten.Add(string.Join(' ', parts.Select(QuoteOpenVpnArgumentIfNeeded)));
        }

        var stagedProfileName = Path.GetFileName(sourceProfile);
        var stagedProfilePath = Path.Combine(stagingDirectory, stagedProfileName);
        File.WriteAllLines(stagedProfilePath, rewritten, new UTF8Encoding(false));
        Log($"Perfil OpenVPN preparado para o Docker com {copiedNames.Count} arquivo(s) auxiliar(es).");
        return new StagedVpnProfile(stagingDirectory, stagedProfileName);
    }

    private static List<string> SplitOpenVpnDirective(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        char quote = '\0';

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == quote)
                {
                    inQuotes = false;
                    continue;
                }
                current.Append(c);
                continue;
            }

            if (c is '\'' or '"')
            {
                inQuotes = true;
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string QuoteOpenVpnArgumentIfNeeded(string value)
    {
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string? ResolveVpnReferencedFile(string referenced, string profileDirectory)
    {
        var normalized = referenced.Trim().Trim('"', '\'');
        var candidates = new List<string>();

        if (Path.IsPathRooted(normalized))
            candidates.Add(normalized);
        else
            candidates.Add(Path.Combine(profileDirectory, normalized));

        // Perfis exportados as vezes mantem caminho Windows absoluto mesmo quando o .ovpn foi movido.
        // Se esse caminho nao existir, procuramos pelo nome do arquivo em locais OpenVPN conhecidos.
        var fileName = Path.GetFileName(normalized.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            candidates.Add(Path.Combine(profileDirectory, fileName));

            try
            {
                foreach (var found in Directory.EnumerateFiles(profileDirectory, fileName, SearchOption.AllDirectories).Take(5))
                    candidates.Add(found);
            }
            catch { }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var knownDirectories = new[]
            {
                Path.Combine(userProfile, "OpenVPN", "config"),
                Path.Combine(programFiles, "OpenVPN", "config"),
                Path.Combine(programFiles, "OpenVPN", "config-auto"),
                Path.Combine(programFilesX86, "OpenVPN", "config"),
                Path.Combine(programFilesX86, "OpenVPN", "config-auto")
            };

            foreach (var dir in knownDirectories.Where(Directory.Exists))
            {
                candidates.Add(Path.Combine(dir, fileName));
                try
                {
                    foreach (var found in Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).Take(5))
                        candidates.Add(found);
                }
                catch { }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool FilesEqual(string first, string second)
    {
        try
        {
            var a = new FileInfo(first);
            var b = new FileInfo(second);
            if (a.Length != b.Length) return false;
            using var sa = File.OpenRead(first);
            using var sb = File.OpenRead(second);
            for (var i = 0; i < a.Length; i++)
                if (sa.ReadByte() != sb.ReadByte()) return false;
            return true;
        }
        catch { return false; }
    }

    private string? ResolveProfile()
    {
        var settings = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.VpnProfilePath) && File.Exists(settings.VpnProfilePath))
            return settings.VpnProfilePath;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacy = Path.Combine(local, "Softcom", "RecuperadorVendasPDV", "vpn", "sofcom-virginia.ovpn");
        return File.Exists(legacy) ? legacy : null;
    }

    private static string? ResolveDockerExecutable()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOCKER_EXE"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe"),
            "docker.exe"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return candidate;
                continue;
            }

            try
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var full = Path.Combine(dir.Trim(), candidate);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
        }

        return null;
    }

    private static string ToDockerPath(string path) => Path.GetFullPath(path);

    private static string LastMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Consulte a aba Logs.";
        return text.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(3)
            .Aggregate((a, b) => a + " | " + b);
    }
}
