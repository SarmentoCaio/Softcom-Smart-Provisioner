using System.Diagnostics;
using System.IO.Compression;
using System.Windows.Forms;

namespace SoftcomSmartProvisioner.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? backupDirectory = null;
        string? targetDirectory = null;
        try
        {
            var options = ParseArgs(args);
            var pid = int.Parse(Require(options, "pid"));
            var package = Require(options, "package");
            var target = Require(options, "target");
            var restart = Require(options, "restart");
            targetDirectory = target;

            WaitForProcess(pid);
            StopLocalToolProcesses(target);

            var staging = Path.Combine(Path.GetTempPath(), "SoftcomSmartProvisionerUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(package, staging, true);
            var sourceRoot = ResolveSourceRoot(staging);

            backupDirectory = CreateBackup(target);
            CopyTree(sourceRoot, target);

            TryDelete(package);
            try { Directory.Delete(staging, true); } catch { }

            var restarted = Process.Start(new ProcessStartInfo(restart)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(restart) ?? target
            });
            if (restarted is null)
            {
                throw new InvalidOperationException("Nao foi possivel reiniciar o Softcom Smart Provisioner.");
            }

            CleanupOldBackups(keep: 2);
            return 0;
        }
        catch (Exception ex)
        {
            var rollbackMessage = string.Empty;
            if (!string.IsNullOrWhiteSpace(backupDirectory) && !string.IsNullOrWhiteSpace(targetDirectory))
            {
                try
                {
                    RestoreBackup(backupDirectory, targetDirectory);
                    rollbackMessage = "\n\nA versao anterior foi restaurada automaticamente.";
                }
                catch (Exception rollbackEx)
                {
                    rollbackMessage = "\n\nTambem nao foi possivel restaurar automaticamente a versao anterior: " + rollbackEx.Message;
                }
            }

            MessageBox.Show(
                "Nao foi possivel concluir a atualizacao.\n\n" + ex.Message + rollbackMessage,
                "Softcom Smart Provisioner Updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void WaitForProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(45000))
            {
                try { process.Kill(true); } catch { }
                process.WaitForExit(10000);
            }
        }
        catch (ArgumentException) { }
    }

    private static void StopLocalToolProcesses(string target)
    {
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "adb", "scrcpy" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(true);
                        process.WaitForExit(5000);
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
    }

    private static string ResolveSourceRoot(string staging)
    {
        var files = Directory.GetFiles(staging);
        var dirs = Directory.GetDirectories(staging);
        if (files.Length == 0 && dirs.Length == 1)
        {
            return dirs[0];
        }
        return staging;
    }

    private static string CreateBackup(string target)
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftcomSmartProvisioner",
            "Backups");
        Directory.CreateDirectory(baseDirectory);

        var backup = Path.Combine(baseDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backup);
        CopyTree(target, backup);
        return backup;
    }

    private static void RestoreBackup(string backup, string target)
    {
        StopLocalToolProcesses(target);
        CopyTree(backup, target);
    }

    private static void CleanupOldBackups(int keep)
    {
        try
        {
            var baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftcomSmartProvisioner",
                "Backups");
            if (!Directory.Exists(baseDirectory)) return;

            foreach (var directory in new DirectoryInfo(baseDirectory)
                         .GetDirectories()
                         .OrderByDescending(x => x.CreationTimeUtc)
                         .Skip(Math.Max(keep, 0)))
            {
                try { directory.Delete(true); } catch { }
            }
        }
        catch { }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            result[args[i][2..]] = args[i + 1];
        }
        return result;
    }

    private static string Require(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Parametro --{key} nao informado.");

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }
}
