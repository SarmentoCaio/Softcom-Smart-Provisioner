namespace SoftcomSmartProvisioner.Models;

public sealed record TestAutomationSuite(
    string Id,
    string Name,
    string RelativePath,
    IReadOnlyList<string> TestCases,
    IReadOnlyList<string> Tags);

public sealed record TestAutomationProgress(
    DateTimeOffset Timestamp,
    string Stage,
    string Message,
    string Level);

public sealed record TestAutomationDevice(
    string Serial,
    string FriendlyName,
    string AndroidVersion,
    string SmartVersion,
    bool IsOnline,
    IReadOnlyList<string> DeviceTags,
    string SuggestedDeviceTag,
    bool RequiresProfileSelection);

public sealed record TestAutomationPrerequisites(
    bool ProjectAvailable,
    bool RunnerAvailable,
    bool EnvironmentAvailable,
    bool DevicesCatalogAvailable,
    bool UvAvailable,
    bool AppiumAvailable);

public sealed record TestAutomationCatalog(
    string? ProjectRoot,
    TestAutomationPrerequisites Prerequisites,
    IReadOnlyList<TestAutomationSuite> Suites,
    IReadOnlyList<TestAutomationDevice> Devices,
    string? ReportPath,
    IReadOnlyList<string> Warnings);

public sealed record TestAutomationRunRequest(
    string Serial,
    string DeviceTag,
    string SuiteId,
    string? TestCase,
    string? IncludeTag);

public sealed record TestAutomationRunResult(
    bool Success,
    bool Canceled,
    int ExitCode,
    string Serial,
    string DeviceTag,
    string SuiteName,
    string TestCase,
    long ElapsedMilliseconds,
    string Message,
    string? ReportPath,
    string Summary);
