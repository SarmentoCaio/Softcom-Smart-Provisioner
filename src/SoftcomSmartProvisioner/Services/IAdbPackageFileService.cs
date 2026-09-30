namespace SoftcomSmartProvisioner.Services;

public interface IAdbPackageFileService
{
    Task<ProcessResult> ForceStopPackageAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<ProcessResult> LaunchPackageAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<ProcessResult> RunAsAsync(
        string serial,
        string packageName,
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken = default,
        int timeoutMilliseconds = 30000);

    Task<ProcessResult> ReadRunAsTextFileAsync(
        string serial,
        string packageName,
        string relativePath,
        CancellationToken cancellationToken = default,
        int timeoutMilliseconds = 30000);

    Task<ProcessResult> WriteRunAsTextFileAsync(
        string serial,
        string packageName,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default,
        int timeoutMilliseconds = 30000);
}
