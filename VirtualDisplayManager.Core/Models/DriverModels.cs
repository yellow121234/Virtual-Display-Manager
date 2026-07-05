namespace VirtualDisplayManager.Core.Models;

public enum DriverInstallState
{
    NotInstalled,
    Installed,
    Error,
    RebootRequired
}

public enum OperationOutcome
{
    Success,
    AlreadyCompleted,
    Failed,
    Busy,
    RebootRequired
}

public sealed record DriverInfInfo(
    string Path,
    string FileName,
    bool IsLikelyVdd,
    int MatchScore,
    string HardwareId,
    string Provider,
    string DeviceName);

public sealed class DriverPackageInfo
{
    public required string RootPath { get; init; }
    public required IReadOnlyList<DriverInfInfo> InfFiles { get; init; }
    public DriverInfInfo? SelectedInf { get; set; }
    public bool DirectoryExists => Directory.Exists(RootPath);
}

public sealed record DriverPackageDownloadResult(
    string ReleaseTag,
    string AssetName,
    DriverPackageInfo Package);

public sealed record PackageValidationResult(
    bool IsValid,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ReferencedFiles);

public sealed record SignatureCheckResult(bool IsValid, string Message, IReadOnlyList<string> CatalogFiles);

public sealed record PnPDriverRecord(
    string PublishedName,
    string OriginalName,
    string ProviderName,
    string ClassName,
    string SignerName,
    string Version,
    IReadOnlyDictionary<string, string> Properties);

public sealed record VddDeviceInfo(
    string InstanceId,
    string Description,
    string Status,
    string DriverName,
    string Manufacturer,
    bool IsVdd,
    IReadOnlyDictionary<string, string> Properties);

public sealed record DriverStatusInfo(
    DriverInstallState State,
    string Summary,
    IReadOnlyList<VddDeviceInfo> Devices,
    IReadOnlyList<PnPDriverRecord> DriverPackages,
    bool SettingsFileExists,
    bool RebootRequired = false);

public sealed record DriverOperationResult(
    OperationOutcome Outcome,
    string Message,
    int ExitCode = 0,
    bool RebootRequired = false);
