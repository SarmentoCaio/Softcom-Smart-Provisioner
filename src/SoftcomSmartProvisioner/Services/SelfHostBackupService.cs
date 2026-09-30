using System.Diagnostics;

namespace SoftcomSmartProvisioner.Services;

public sealed class SelfHostBackupService
{
    public void EnsureConfigurationToolsClosed()
    {
        if (Process.GetProcessesByName("Selfhost.Gerenciador").Any())
            throw new InvalidOperationException("Feche o Selfhost.Gerenciador antes de configurar o SelfHost.");
    }

    public string CreateStoppedBackup(string installRoot, string generation)
    {
        EnsureConfigurationToolsClosed();
        if (SelfHostServiceManager.HasRunningInstallationProcess(installRoot))
            throw new InvalidOperationException("O backup exige SelfHost e MonitorService parados.");

        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Softcom", "SmartProvisioner", "SelfHostBackups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupRoot);

        var copied = 0;
        if (generation == "SelfHost 4.0")
        {
            copied += CopyIfExists(installRoot, backupRoot, "Config.json");
            copied += CopyIfExists(installRoot, backupRoot, "Config2.json");
            copied += CopyIfExists(installRoot, backupRoot, "GeradorPaineisAgent.json");
            copied += CopyIfExists(installRoot, backupRoot, "GeradorPaineisAgent2.json");
        }
        else
        {
            var dataDirectory = Path.Combine(installRoot, "data");
            copied += CopyIfExists(dataDirectory, backupRoot, "selfhost-config.db");
            copied += CopyIfExists(dataDirectory, backupRoot, "selfhost-config.db-wal");
            copied += CopyIfExists(dataDirectory, backupRoot, "selfhost-config.db-shm");
        }

        if (copied == 0)
        {
            // Uma instalação 4.0 ainda não configurada não possui Config.json nem
            // Config2.json. Esse é um estado válido de primeira configuração, não
            // uma falha de backup. O marcador registra que não havia estado anterior
            // sem criar um arquivo de configuração falso na pasta do SelfHost.
            File.WriteAllText(
                Path.Combine(backupRoot, "SEM-CONFIGURACAO-ANTERIOR.txt"),
                $"SelfHost: {generation}{Environment.NewLine}" +
                $"Data UTC: {DateTime.UtcNow:O}{Environment.NewLine}" +
                "Nenhum arquivo de configuração anterior existia nesta instalação.");
        }
        return backupRoot;
    }

    private static int CopyIfExists(string sourceDirectory, string targetDirectory, string name)
    {
        var source = Path.Combine(sourceDirectory, name);
        if (!File.Exists(source)) return 0;
        File.Copy(source, Path.Combine(targetDirectory, name), overwrite: false);
        return 1;
    }
}
