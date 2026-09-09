using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class UpdateService
{
    private readonly string _appVersion;
    private readonly string _appDataDirectory;
    private readonly string _baseDirectory;
    private readonly Action<string> _log;
    private readonly HttpClient _httpClient;

    public UpdateService(string appVersion, string appDataDirectory, string baseDirectory, Action<string> log)
    {
        _appVersion = appVersion;
        _appDataDirectory = appDataDirectory;
        _baseDirectory = baseDirectory;
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"SoftcomSmartProvisioner/{appVersion}");
    }

    public async Task<UpdateCheckResult> CheckAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var manifestUrl = ResolveManifestUrl(settings);
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult(false, false, null, "URL do manifesto de atualizacao ainda nao configurada.");
        }

        _log($"Consultando atualizacoes em {manifestUrl}");
        using var response = await _httpClient.GetAsync(manifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Manifesto de atualizacao invalido.");

        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.DownloadUrl))
        {
            throw new InvalidOperationException("Manifesto de atualizacao incompleto: version/downloadUrl obrigatorios.");
        }

        var available = CompareVersions(manifest.Version, _appVersion) > 0;
        return new UpdateCheckResult(true, available, manifest,
            available ? $"Nova versao {manifest.Version} disponivel." : $"Versao {_appVersion} ja esta atualizada.");
    }

    public async Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        var updateDirectory = Path.Combine(_appDataDirectory, "Updates", manifest.Version);
        Directory.CreateDirectory(updateDirectory);
        var packagePath = Path.Combine(updateDirectory, "update.zip");

        _log($"Baixando pacote {manifest.Version}...");
        using var response = await _httpClient.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(packagePath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var expected = NormalizeHash(manifest.Sha256);
            await using var stream = File.OpenRead(packagePath);
            var actualBytes = await SHA256.HashDataAsync(stream, cancellationToken);
            var actual = Convert.ToHexString(actualBytes).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(packagePath);
                throw new InvalidOperationException("O SHA-256 do pacote baixado nao confere com o manifesto.");
            }
        }

        return packagePath;
    }

    public void LaunchUpdater(string packagePath)
    {
        var updaterSource = Path.Combine(_baseDirectory, "SoftcomSmartProvisioner.Updater.exe");
        if (!File.Exists(updaterSource))
        {
            throw new FileNotFoundException("Updater nao localizado na pasta publicada.", updaterSource);
        }

        var updaterDirectory = Path.Combine(_appDataDirectory, "Updater");
        Directory.CreateDirectory(updaterDirectory);
        var updaterTemp = Path.Combine(updaterDirectory, $"Updater-{DateTime.Now:yyyyMMddHHmmss}.exe");
        File.Copy(updaterSource, updaterTemp, true);

        var mainExe = Environment.ProcessPath ?? Path.Combine(_baseDirectory, "SoftcomSmartProvisioner.exe");
        var args = string.Join(" ", new[]
        {
            "--pid", Environment.ProcessId.ToString(),
            "--package", Quote(packagePath),
            "--target", Quote(_baseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            "--restart", Quote(mainExe)
        });

        _ = Process.Start(new ProcessStartInfo(updaterTemp, args)
        {
            UseShellExecute = true,
            WorkingDirectory = updaterDirectory
        }) ?? throw new InvalidOperationException("Nao foi possivel iniciar o Updater.");
    }

    private static string ResolveManifestUrl(AppSettings settings)
    {
        var channel = string.Equals(settings.UpdateChannel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";
        return channel == "beta" ? settings.BetaManifestUrl.Trim() : settings.StableManifestUrl.Trim();
    }

    private static string NormalizeHash(string value) =>
        value.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();

    private static int CompareVersions(string left, string right)
    {
        static Version Parse(string input)
        {
            var clean = input.Trim().TrimStart('v', 'V');
            var suffix = clean.IndexOfAny(['-', '+']);
            if (suffix >= 0) clean = clean[..suffix];
            return Version.TryParse(clean, out var version) ? version : new Version(0, 0, 0, 0);
        }

        return Parse(left).CompareTo(Parse(right));
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

public sealed record UpdateCheckResult(
    bool Configured,
    bool UpdateAvailable,
    UpdateManifest? Manifest,
    string Message);
