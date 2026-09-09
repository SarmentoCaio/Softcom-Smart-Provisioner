using System.Diagnostics;
using System.Collections.Concurrent;
using SoftcomSmartProvisioner.Models;

namespace SoftcomSmartProvisioner.Services;

public sealed class ScrcpyService : IDisposable
{
    private readonly string _toolsDirectory;
    private readonly string _scrcpyPath;
    private readonly ConcurrentDictionary<int, Process> _sessions = new();

    public ScrcpyService(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
        _scrcpyPath = Path.Combine(_toolsDirectory, "scrcpy.exe");
    }

    public int SessionCount => _sessions.Count;
    public bool IsAvailable => File.Exists(_scrcpyPath);

    public IReadOnlyList<string> OpenDevices(IEnumerable<string> serials, MirrorOptions options)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("scrcpy.exe nao encontrado na pasta tools.", _scrcpyPath);
        }

        var opened = new List<string>();
        foreach (var serial in serials.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var arguments = BuildArguments(serial, options);
            var process = ProcessRunner.StartDetached(_scrcpyPath, arguments, _toolsDirectory);
            _sessions[process.Id] = process;
            opened.Add(serial);
        }

        return opened;
    }

    public int PruneExitedSessions()
    {
        var removed = 0;
        foreach (var pair in _sessions.ToArray())
        {
            try
            {
                if (!pair.Value.HasExited)
                {
                    continue;
                }
            }
            catch
            {
            }

            if (_sessions.TryRemove(pair.Key, out var process))
            {
                process.Dispose();
                removed++;
            }
        }

        return removed;
    }

    private static IReadOnlyList<string> BuildArguments(string serial, MirrorOptions options)
    {
        var args = new List<string> { "--serial", serial };

        if (options.StayAwake)
        {
            args.Add("--stay-awake");
        }

        if (options.TurnScreenOff)
        {
            args.Add("--turn-screen-off");
        }

        if (options.NoAudio)
        {
            args.Add("--no-audio");
        }

        if (options.MaxSize > 0)
        {
            args.Add("--max-size");
            args.Add(options.MaxSize.ToString());
        }

        if (options.MaxFps > 0)
        {
            args.Add("--max-fps");
            args.Add(options.MaxFps.ToString());
        }

        return args;
    }

    public void Dispose()
    {
        foreach (var pair in _sessions.ToArray())
        {
            try
            {
                pair.Value.Dispose();
            }
            catch
            {
            }
        }

        _sessions.Clear();
    }
}
