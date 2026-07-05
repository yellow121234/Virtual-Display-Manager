using System.Management;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VirtualDisplayManager.Core.Interop;
using VirtualDisplayManager.Core.Models;

namespace VirtualDisplayManager.Core.Services;

public sealed class DriverService
{
    public const string DriverRepositoryUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/VirtualDrivers/Virtual-Display-Driver/releases/latest";
    public const string DefaultInstallDirectory = @"C:\VirtualDisplayDriver";
    private const long MaximumDriverArchiveSize = 256L * 1024 * 1024;
    private static readonly string[] VddHints =
        ["mttvdd", "virtual display driver", "iddsampledriver", "mikethetech", "virtualdrivers"];
    private static readonly HttpClient ReleaseClient = CreateReleaseClient();
    private readonly LoggingService _log;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _packageDownloadLock = new(1, 1);

    public DriverService(LoggingService log) => _log = log;

    public DriverPackageInfo FindVddDriverPackage()
    {
        var candidates = new[]
        {
            GetDriverPackageCacheDirectory(),
            Path.Combine(AppContext.BaseDirectory, "Drivers", "Virtual-Display-Driver"),
            Path.Combine(Environment.CurrentDirectory, "Drivers", "Virtual-Display-Driver"),
            Path.Combine(Environment.CurrentDirectory, "VirtualDisplayManager.App", "Drivers", "Virtual-Display-Driver")
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var root = candidates.FirstOrDefault(candidate => FindVddInfFiles(candidate).Count > 0)
                   ?? candidates.FirstOrDefault(Directory.Exists)
                   ?? candidates[0];
        var infs = FindVddInfFiles(root);
        return new DriverPackageInfo
        {
            RootPath = root,
            InfFiles = infs,
            SelectedInf = infs.OrderByDescending(info => info.MatchScore).FirstOrDefault()
        };
    }

    public string GetDriverPackageCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualDisplayManager", "DriverPackages");

    public async Task<DriverPackageDownloadResult> DownloadLatestVddPackageAsync(
        CancellationToken cancellationToken = default)
    {
        await _packageDownloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _log.Info("GitHub에서 공식 Virtual Display Driver 최신 Release를 확인합니다.");
            using var releaseResponse = await ReleaseClient.GetAsync(LatestReleaseApiUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            releaseResponse.EnsureSuccessStatusCode();
            await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(releaseStream,
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("GitHub Release 응답이 비어 있습니다.");

            var asset = SelectDriverAsset(release.Assets)
                ?? throw new InvalidDataException("최신 Release에서 현재 PC 아키텍처용 드라이버 ZIP을 찾지 못했습니다.");
            if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("공식 GitHub HTTPS 다운로드 주소가 아닙니다.");

            var tag = MakeSafePathSegment(string.IsNullOrWhiteSpace(release.TagName)
                ? $"release-{release.Id}"
                : release.TagName);
            var cacheRoot = GetDriverPackageCacheDirectory();
            var finalDirectory = Path.Combine(cacheRoot, tag);
            var cached = TryGetValidPackage(finalDirectory);
            if (cached is not null)
            {
                _log.Success($"캐시된 드라이버 패키지를 사용합니다: {release.TagName}");
                return new(release.TagName, asset.Name, cached);
            }

            Directory.CreateDirectory(cacheRoot);
            var stagingDirectory = Path.Combine(cacheRoot, $".download-{Guid.NewGuid():N}");
            var archivePath = Path.Combine(stagingDirectory, "driver.zip");
            var extractedDirectory = Path.Combine(stagingDirectory, "package");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                _log.Info($"드라이버 다운로드 중: {asset.Name}");
                await DownloadFileAsync(downloadUri, archivePath, cancellationToken).ConfigureAwait(false);
                ExtractZipSafely(archivePath, extractedDirectory);

                var stagedPackage = TryGetValidPackage(extractedDirectory)
                    ?? throw new InvalidDataException("다운로드한 패키지에 유효하고 신뢰될 수 있는 VDD INF/CAT가 없습니다.");

                if (Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, true);
                Directory.Move(extractedDirectory, finalDirectory);
                var installedPackage = TryGetValidPackage(finalDirectory)
                    ?? throw new InvalidDataException("캐시로 이동한 드라이버 패키지를 다시 읽지 못했습니다.");
                _log.Success($"공식 드라이버 패키지 준비 완료: {release.TagName}");
                return new(release.TagName, asset.Name, installedPackage);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("공식 드라이버 Release를 다운로드하지 못했습니다. 인터넷 연결을 확인하세요.", ex);
        }
        finally
        {
            _packageDownloadLock.Release();
        }
    }

    public IReadOnlyList<DriverInfInfo> FindVddInfFiles(string? packagePath = null)
    {
        packagePath ??= FindVddDriverPackage().RootPath;
        if (!Directory.Exists(packagePath)) return [];

        return Directory.EnumerateFiles(packagePath, "*.inf", SearchOption.AllDirectories)
            .Select(ParseInfIdentity)
            .OrderByDescending(info => info.MatchScore)
            .ThenBy(info => info.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public PackageValidationResult ValidateVddPackage(string infPath)
    {
        var missing = new List<string>();
        var errors = new List<string>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(infPath))
            return new(false, [infPath], ["INF 파일이 없습니다."], []);

        var text = File.ReadAllText(infPath);
        var identity = ParseInfIdentity(infPath);
        if (!identity.IsLikelyVdd)
            errors.Add("선택한 INF에서 VirtualDrivers VDD 식별자(Root\\MttVDD/MttVDD/Virtual Display Driver)를 찾지 못했습니다.");

        foreach (Match match in Regex.Matches(text,
                     @"(?im)^\s*CatalogFile(?:\.[^=]+)?\s*=\s*([^;\r\n]+)"))
            referenced.Add(Unquote(match.Groups[1].Value.Trim()));

        var sourceSection = Regex.Match(text, @"(?ims)^\s*\[SourceDisksFiles\]\s*(?<body>.*?)(?=^\s*\[|\z)");
        if (sourceSection.Success)
        {
            foreach (Match match in Regex.Matches(sourceSection.Groups["body"].Value,
                         @"(?im)^\s*([^;=\r\n]+?)\s*="))
                referenced.Add(Unquote(match.Groups[1].Value.Trim()));
        }

        var directory = Path.GetDirectoryName(infPath)!;
        foreach (var file in referenced)
        {
            if (!File.Exists(Path.Combine(directory, file)) &&
                !Directory.EnumerateFiles(directory, Path.GetFileName(file), SearchOption.AllDirectories).Any())
                missing.Add(file);
        }

        if (!referenced.Any(path => path.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)))
            errors.Add("INF에 CatalogFile 항목이 없습니다.");
        if (!referenced.Any(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)))
            errors.Add("INF의 SourceDisksFiles에 SYS 또는 DLL 드라이버 바이너리가 없습니다.");
        if (!InfSupportsCurrentArchitecture(text))
            errors.Add($"현재 프로세서 아키텍처({RuntimeInformation.ProcessArchitecture})와 일치하는 INF 섹션을 찾지 못했습니다.");

        return new(missing.Count == 0 && errors.Count == 0, missing, errors, referenced.ToArray());
    }

    public SignatureCheckResult CheckVddSignature(string infPath)
    {
        if (!File.Exists(infPath)) return new(false, "INF 파일이 없습니다.", []);
        var text = File.ReadAllText(infPath);
        var directory = Path.GetDirectoryName(infPath)!;
        var catalogs = Regex.Matches(text, @"(?im)^\s*CatalogFile(?:\.[^=]+)?\s*=\s*([^;\r\n]+)")
            .Select(match => Path.Combine(directory, Unquote(match.Groups[1].Value.Trim())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (catalogs.Length == 0) return new(false, "INF가 서명 카탈로그를 참조하지 않습니다.", []);
        foreach (var catalog in catalogs)
        {
            if (!File.Exists(catalog)) return new(false, $"CAT 파일 없음: {catalog}", catalogs);
            var verification = SignatureVerifier.VerifyEmbeddedOrCatalogSignature(catalog);
            if (!verification.IsValid)
                return new(false, $"Windows가 CAT 서명을 신뢰하지 않습니다(0x{verification.ErrorCode:X8}): {Path.GetFileName(catalog)}", catalogs);
        }

        return new(true, "모든 참조 CAT 파일의 Authenticode 서명이 유효합니다.", catalogs);
    }

    public async Task<bool> IsVddInstalled(CancellationToken cancellationToken = default) =>
        (await GetStatusAsync(cancellationToken).ConfigureAwait(false)).Devices.Count > 0;

    public async Task<DriverStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var driversResult = await RunPnPUtilAsync(["/enum-drivers"], cancellationToken).ConfigureAwait(false);
            var devices = await RefreshVddDevices(cancellationToken).ConfigureAwait(false);
            var drivers = ParsePnPUtilEnumDrivers(driversResult.StandardOutput).Where(IsVddDriver).ToArray();
            var installed = devices.Any(device => device.IsVdd);
            var stagedOnly = !installed && drivers.Length > 0;
            var hasProblem = devices.Any(device => device.IsVdd && !IsHealthyStatus(device.Status));
            var state = hasProblem || stagedOnly ? DriverInstallState.Error : installed ? DriverInstallState.Installed : DriverInstallState.NotInstalled;
            var summary = state switch
            {
                DriverInstallState.Installed => $"설치됨 ({devices.Count(device => device.IsVdd)}개 VDD 장치)",
                DriverInstallState.Error when stagedOnly => "Driver Store에는 있으나 VDD 장치가 없음",
                DriverInstallState.Error => "오류 상태의 VDD 장치가 감지됨",
                _ => "설치 안 됨"
            };
            return new(state, summary, devices.Where(device => device.IsVdd).ToArray(), drivers,
                File.Exists(GetVddSettingsFilePath()));
        }
        catch (Exception ex)
        {
            return new(DriverInstallState.Error, $"상태 확인 실패: {ex.Message}", [], [],
                File.Exists(GetVddSettingsFilePath()));
        }
    }

    public async Task<DriverOperationResult> InstallVddAsync(string infPath,
        CancellationToken cancellationToken = default)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(OperationOutcome.Busy, "다른 드라이버 작업이 진행 중입니다.");
        try
        {
            return await InstallVddInternalAsync(infPath, cancellationToken).ConfigureAwait(false);
        }
        finally { _operationLock.Release(); }
    }

    private async Task<DriverOperationResult> InstallVddInternalAsync(string infPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!PrivilegeService.IsRunAsAdministrator())
                return new(OperationOutcome.Failed, "관리자 권한이 필요합니다.");
            if (await IsVddInstalled(cancellationToken).ConfigureAwait(false))
                return new(OperationOutcome.AlreadyCompleted, "Virtual Display Driver가 이미 설치되어 있습니다.");

            var validation = ValidateVddPackage(infPath);
            if (!validation.IsValid)
                return new(OperationOutcome.Failed, "패키지 검증 실패: " +
                    string.Join("; ", validation.Errors.Concat(validation.MissingFiles.Select(path => $"누락: {path}"))));
            var signature = CheckVddSignature(infPath);
            if (!signature.IsValid)
                return new(OperationOutcome.Failed, "서명 검증 실패: " + signature.Message);

            _log.Info($"실행 명령: pnputil /add-driver \"{infPath}\" /install");
            var result = await RunPnPUtilAsync(["/add-driver", infPath, "/install"], cancellationToken).ConfigureAwait(false);
            LogCommandResult(result);
            if (result.ExitCode != 0)
                return Failure("드라이버 패키지 등록/설치 실패", result);

            await RefreshVddDevices(cancellationToken).ConfigureAwait(false);
            if (!await IsVddInstalled(cancellationToken).ConfigureAwait(false))
            {
                var identity = ParseInfIdentity(infPath);
                if (string.IsNullOrWhiteSpace(identity.HardwareId) ||
                    !identity.HardwareId.StartsWith("Root\\", StringComparison.OrdinalIgnoreCase))
                    return new(OperationOutcome.Failed,
                        "패키지는 Driver Store에 등록됐지만 Root VDD 장치를 만들 Hardware ID를 INF에서 찾지 못했습니다.");

                await RemoveOrphanedRootVddDevicesAsync(cancellationToken).ConfigureAwait(false);
                _log.Info($"서명 패키지 등록 후 Root VDD 장치 생성: {identity.HardwareId}");
                SetupApiDeviceCreator.CreateRootDisplayDevice(identity.HardwareId,
                    string.IsNullOrWhiteSpace(identity.DeviceName) ? "Virtual Display Driver" : identity.DeviceName);
                await RunPnPUtilAsync(["/scan-devices"], cancellationToken).ConfigureAwait(false);
                result = await RunPnPUtilAsync(["/add-driver", infPath, "/install"], cancellationToken).ConfigureAwait(false);
                LogCommandResult(result);
                if (result.ExitCode != 0)
                    return Failure("생성한 Root VDD 장치에 드라이버 연결 실패", result);
            }

            var status = await WaitForVddInstallationAsync(cancellationToken).ConfigureAwait(false);
            if (status.State == DriverInstallState.Installed)
                return new(OperationOutcome.Success, "Virtual Display Driver 설치 및 장치 감지를 확인했습니다.");

            var reboot = OutputRequiresReboot(result.CombinedOutput);
            return reboot
                ? new(OperationOutcome.RebootRequired, "설치는 완료됐지만 적용을 위해 재부팅이 필요합니다.", result.ExitCode, true)
                : new(OperationOutcome.Failed, "pnputil은 완료됐지만 VDD 장치가 감지되지 않았습니다.", result.ExitCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"설치 예외: {ex.Message}");
            return new(OperationOutcome.Failed, $"설치 중 오류: {ex.Message}", Marshal.GetLastWin32Error());
        }
    }

    public async Task<DriverOperationResult> UninstallVddAsync(CancellationToken cancellationToken = default)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(OperationOutcome.Busy, "다른 드라이버 작업이 진행 중입니다.");
        try
        {
            if (!PrivilegeService.IsRunAsAdministrator())
                return new(OperationOutcome.Failed, "관리자 권한이 필요합니다.");

            var devices = await RefreshVddDevices(cancellationToken).ConfigureAwait(false);
            var enumResult = await RunPnPUtilAsync(["/enum-drivers"], cancellationToken).ConfigureAwait(false);
            var packages = ParsePnPUtilEnumDrivers(enumResult.StandardOutput).Where(IsVddDriver).ToArray();
            if (!devices.Any(device => device.IsVdd) && packages.Length == 0)
                return new(OperationOutcome.AlreadyCompleted, "Virtual Display Driver가 이미 삭제되어 있습니다.");

            var reboot = false;
            foreach (var device in devices.Where(device => device.IsVdd && !string.IsNullOrWhiteSpace(device.InstanceId)))
            {
                _log.Info($"실행 명령: pnputil /remove-device \"{device.InstanceId}\"");
                var remove = await RunPnPUtilAsync(["/remove-device", device.InstanceId], cancellationToken).ConfigureAwait(false);
                LogCommandResult(remove);
                reboot |= OutputRequiresReboot(remove.CombinedOutput);
                if (remove.ExitCode != 0) return Failure("VDD 장치 제거 실패", remove);
            }

            foreach (var package in packages.Where(package => package.PublishedName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)))
            {
                _log.Info($"실행 명령: pnputil /delete-driver {package.PublishedName} /uninstall");
                var delete = await RunPnPUtilAsync(["/delete-driver", package.PublishedName, "/uninstall"], cancellationToken).ConfigureAwait(false);
                LogCommandResult(delete);
                reboot |= OutputRequiresReboot(delete.CombinedOutput);
                if (delete.ExitCode != 0) return Failure("Driver Store 패키지 삭제 실패", delete);
            }

            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.State != DriverInstallState.NotInstalled && !reboot)
                return new(OperationOutcome.Failed, "제거 명령 후에도 VDD 패키지 또는 장치가 감지됩니다.");
            return reboot
                ? new(OperationOutcome.RebootRequired, "드라이버를 제거했습니다. Windows 재부팅이 필요합니다.", 0, true)
                : new(OperationOutcome.Success, "Virtual Display Driver를 제거했습니다.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"삭제 예외: {ex.Message}");
            return new(OperationOutcome.Failed, $"삭제 중 오류: {ex.Message}", Marshal.GetLastWin32Error());
        }
        finally { _operationLock.Release(); }
    }

    public string GetVddInstallDirectory() => DefaultInstallDirectory;
    public string GetVddSettingsFilePath() => Path.Combine(GetVddInstallDirectory(), "vdd_settings.xml");

    public DriverOperationResult WriteVddSettingsXml(
        int monitorCount = 1,
        int width = 1920,
        int height = 1080,
        int refreshRate = 60,
        bool backupExisting = true)
    {
        try
        {
            if (!PrivilegeService.IsRunAsAdministrator())
                return new(OperationOutcome.Failed, "관리자 권한이 필요합니다.");
            if (monitorCount is < 1 or > 16)
                return new(OperationOutcome.Failed, "가상 모니터 개수는 1~16 사이여야 합니다.");
            if (width is < 640 or > 7680 || height is < 480 or > 4320)
                return new(OperationOutcome.Failed, "해상도는 640x480~7680x4320 범위여야 합니다.");
            if (refreshRate is < 24 or > 240)
                return new(OperationOutcome.Failed, "주사율은 24~240Hz 범위여야 합니다.");

            var installDirectory = GetVddInstallDirectory();
            var settingsPath = GetVddSettingsFilePath();
            Directory.CreateDirectory(installDirectory);

            if (backupExisting && File.Exists(settingsPath))
                BackupVddSettingsXml();

            var document = BuildVddSettingsDocument(monitorCount, width, height, refreshRate);
            using var writer = new StreamWriter(settingsPath, false, new UTF8Encoding(false));
            document.Save(writer);

            _log.Success($"VDD 설정 파일 생성/갱신 완료: 모니터 {monitorCount}개, {width}x{height}@{refreshRate}Hz");
            return new(OperationOutcome.Success, $"VDD 설정 파일을 생성했습니다: {settingsPath}");
        }
        catch (Exception ex)
        {
            _log.Error($"VDD 설정 파일 생성 실패: {ex.Message}");
            return new(OperationOutcome.Failed, $"VDD 설정 파일 생성 실패: {ex.Message}", Marshal.GetLastWin32Error());
        }
    }

    public async Task<DriverOperationResult> CreateVirtualMonitorAsync(
        string infPath,
        int monitorCount = 1,
        int width = 1920,
        int height = 1080,
        int refreshRate = 60,
        CancellationToken cancellationToken = default)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(OperationOutcome.Busy, "다른 드라이버 작업이 진행 중입니다.");
        try
        {
            if (!PrivilegeService.IsRunAsAdministrator())
                return new(OperationOutcome.Failed, "관리자 권한이 필요합니다.");

            var settingsResult = WriteVddSettingsXml(monitorCount, width, height, refreshRate);
            if (settingsResult.Outcome != OperationOutcome.Success)
                return settingsResult;

            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            DriverOperationResult installResult = new(OperationOutcome.AlreadyCompleted, "Virtual Display Driver가 이미 설치되어 있습니다.");
            if (status.State != DriverInstallState.Installed)
            {
                installResult = await InstallVddInternalAsync(infPath, cancellationToken).ConfigureAwait(false);
                if (installResult.Outcome is OperationOutcome.Failed or OperationOutcome.Busy)
                    return installResult;
            }

            var restart = await RestartVddDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (restart.Outcome == OperationOutcome.Failed)
                return restart;

            await RunPnPUtilAsync(["/scan-devices"], cancellationToken).ConfigureAwait(false);
            var refreshedStatus = await WaitForVddInstallationAsync(cancellationToken).ConfigureAwait(false);
            if (refreshedStatus.State == DriverInstallState.Installed)
                return new(OperationOutcome.Success,
                    $"가상 모니터 {monitorCount}개 설정을 적용했습니다. Windows 디스플레이 설정에서 확장/배치를 확인하세요.");

            var rebootRequired = installResult.RebootRequired || restart.RebootRequired;
            return rebootRequired
                ? new(OperationOutcome.RebootRequired, "설정은 적용했지만 Windows 재부팅 후 가상 모니터가 나타날 수 있습니다.", 0, true)
                : new(OperationOutcome.Failed, "설정과 설치를 수행했지만 VDD 장치가 아직 감지되지 않습니다.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"가상 모니터 생성 예외: {ex.Message}");
            return new(OperationOutcome.Failed, $"가상 모니터 생성 중 오류: {ex.Message}", Marshal.GetLastWin32Error());
        }
        finally { _operationLock.Release(); }
    }

    public async Task<DriverOperationResult> RestartVddDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!PrivilegeService.IsRunAsAdministrator())
                return new(OperationOutcome.Failed, "관리자 권한이 필요합니다.");

            var devices = await RefreshVddDevices(cancellationToken).ConfigureAwait(false);
            var vddDevices = devices.Where(device => device.IsVdd && !string.IsNullOrWhiteSpace(device.InstanceId)).ToArray();
            if (vddDevices.Length == 0)
                return new(OperationOutcome.AlreadyCompleted, "재시작할 VDD 장치가 아직 없습니다.");

            var reboot = false;
            foreach (var device in vddDevices)
            {
                _log.Info($"실행 명령: pnputil /restart-device \"{device.InstanceId}\"");
                var restart = await RunPnPUtilAsync(["/restart-device", device.InstanceId], cancellationToken)
                    .ConfigureAwait(false);
                LogCommandResult(restart);
                reboot |= OutputRequiresReboot(restart.CombinedOutput);
                if (restart.ExitCode != 0)
                {
                    _log.Warning("장치 직접 재시작 실패. 장치 재검색으로 계속 진행합니다.");
                    break;
                }
            }

            await RunPnPUtilAsync(["/scan-devices"], cancellationToken).ConfigureAwait(false);
            return reboot
                ? new(OperationOutcome.RebootRequired, "VDD 장치 재시작을 요청했습니다. 재부팅이 필요할 수 있습니다.", 0, true)
                : new(OperationOutcome.Success, "VDD 장치를 재시작했습니다.");
        }
        catch (Exception ex)
        {
            _log.Error($"VDD 장치 재시작 실패: {ex.Message}");
            return new(OperationOutcome.Failed, $"VDD 장치 재시작 실패: {ex.Message}", Marshal.GetLastWin32Error());
        }
    }

    private static XDocument BuildVddSettingsDocument(int monitorCount, int width, int height, int refreshRate)
    {
        static XElement Resolution(int w, int h, int hz) => new("resolution",
            new XElement("width", w),
            new XElement("height", h),
            new XElement("refresh_rate", hz));

        var resolutions = new List<XElement>
        {
            Resolution(width, height, refreshRate),
            Resolution(1920, 1080, 60),
            Resolution(2560, 1440, 60),
            Resolution(3840, 2160, 60)
        };

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("vdd_settings",
                new XElement("monitors", new XElement("count", monitorCount)),
                new XElement("gpu", new XElement("friendlyname", "default")),
                new XElement("global", new XElement("g_refresh_rate", refreshRate)),
                new XElement("resolutions", resolutions
                    .GroupBy(node => $"{node.Element("width")?.Value}x{node.Element("height")?.Value}@{node.Element("refresh_rate")?.Value}")
                    .Select(group => group.First())),
                new XElement("auto_resolutions",
                    new XElement("enabled", "false"),
                    new XElement("source_priority", "manual"),
                    new XElement("preferred_mode",
                        new XElement("use_edid_preferred", "false"),
                        new XElement("fallback_width", width),
                        new XElement("fallback_height", height),
                        new XElement("fallback_refresh", refreshRate))),
                new XElement("logging",
                    new XElement("SendLogsThroughPipe", "true"),
                    new XElement("logging", "false"),
                    new XElement("debuglogging", "false")),
                new XElement("colour",
                    new XElement("SDR10bit", "false"),
                    new XElement("HDRPlus", "false"),
                    new XElement("ColourFormat", "RGB"))));
    }

    public string BackupVddSettingsXml()
    {
        var source = GetVddSettingsFilePath();
        if (!File.Exists(source)) throw new FileNotFoundException("VDD 설정 파일이 없습니다.", source);
        var backup = source + $".{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(source, backup, false);
        _log.Success($"설정 백업 생성: {backup}");
        return backup;
    }

    public async Task<IReadOnlyList<VddDeviceInfo>> RefreshVddDevices(CancellationToken cancellationToken = default)
    {
        var result = await RunPnPUtilAsync(["/enum-devices", "/class", "Display"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"pnputil 장치 조회 실패 ({result.ExitCode}): {result.CombinedOutput}");
        var records = ParsePnPUtilEnumDevices(result.StandardOutput).ToList();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DeviceName, DriverProviderName, InfName, Status FROM Win32_PnPSignedDriver WHERE DeviceClass='DISPLAY'");
            foreach (ManagementObject item in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instanceId = item["DeviceID"]?.ToString() ?? string.Empty;
                if (records.Any(record => record.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))) continue;
                var description = item["DeviceName"]?.ToString() ?? string.Empty;
                var provider = item["DriverProviderName"]?.ToString() ?? string.Empty;
                var driverName = item["InfName"]?.ToString() ?? string.Empty;
                var status = item["Status"]?.ToString() ?? string.Empty;
                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Instance ID"] = instanceId,
                    ["Description"] = description,
                    ["Manufacturer"] = provider,
                    ["Driver Name"] = driverName,
                    ["Status"] = status
                };
                records.Add(new(instanceId, description, status, driverName, provider,
                    ContainsVddHint(instanceId, description, provider, driverName), properties));
            }
        }
        catch (ManagementException ex)
        {
            _log.Warning($"WMI 보조 조회 실패(계속 진행): {ex.Message}");
        }
        return records;
    }

    public IReadOnlyList<PnPDriverRecord> ParsePnPUtilEnumDrivers(string output) =>
        SplitPropertyBlocks(output).Select(properties => new PnPDriverRecord(
            GetProperty(properties, "published", "게시", "veröffentlicht", "publié"),
            GetProperty(properties, "original", "원본", "원래", "ursprüng"),
            GetProperty(properties, "provider", "공급자", "anbieter", "fournisseur"),
            GetProperty(properties, "class name", "클래스 이름", "klassenname", "nom de classe"),
            GetProperty(properties, "signer", "서명자", "unterzeichner", "signataire"),
            GetProperty(properties, "driver version", "드라이버 버전", "treiberversion"),
            properties)).Where(record => !string.IsNullOrWhiteSpace(record.PublishedName) ||
                                         !string.IsNullOrWhiteSpace(record.OriginalName)).ToArray();

    public IReadOnlyList<VddDeviceInfo> ParsePnPUtilEnumDevices(string output) =>
        SplitPropertyBlocks(output).Select(properties =>
        {
            var id = GetProperty(properties, "instance id", "인스턴스 id", "인스턴스 ID", "instanz-id");
            var description = GetProperty(properties, "device description", "장치 설명", "gerätebeschreibung");
            var status = GetProperty(properties, "status", "상태");
            var driverName = GetProperty(properties, "driver name", "드라이버 이름", "treibername");
            var manufacturer = GetProperty(properties, "manufacturer", "제조업체", "hersteller");
            return new VddDeviceInfo(id, description, status, driverName, manufacturer,
                ContainsVddHint(id, description, driverName, manufacturer), properties);
        }).Where(record => !string.IsNullOrWhiteSpace(record.InstanceId)).ToArray();

    private static DriverInfInfo ParseInfIdentity(string infPath)
    {
        var text = File.ReadAllText(infPath);
        var strings = ParseStrings(text);
        var hardware = Regex.Match(text, @"(?im)^\s*%[^%]+%\s*=\s*[^,]+,\s*(Root\\[^\s;,]+|MttVDD)\s*(?:;.*)?$")
            .Groups[1].Value.Trim();
        var providerToken = Regex.Match(text, @"(?im)^\s*Provider\s*=\s*([^;\r\n]+)").Groups[1].Value.Trim();
        var provider = ResolveInfValue(providerToken, strings);
        var deviceToken = Regex.Match(text, @"(?im)^\s*DeviceName\s*=\s*([^;\r\n]+)").Groups[1].Value.Trim();
        var deviceName = ResolveInfValue(deviceToken, strings);
        var haystack = $"{Path.GetFileName(infPath)} {text}".ToLowerInvariant();
        var score = VddHints.Sum(hint => haystack.Contains(hint) ? 2 : 0) +
                    (haystack.Contains("root\\mttvdd") ? 8 : 0) +
                    (haystack.Contains("class = display") || haystack.Contains("class=display") ? 3 : 0);
        return new(infPath, Path.GetFileName(infPath), score >= 7, score, hardware, provider, deviceName);
    }

    private static Dictionary<string, string> ParseStrings(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = Regex.Match(text, @"(?ims)^\s*\[Strings(?:\.[^\]]+)?\]\s*(?<body>.*?)(?=^\s*\[|\z)");
        if (!section.Success) return values;
        foreach (Match match in Regex.Matches(section.Groups["body"].Value, @"(?im)^\s*([^;=]+?)\s*=\s*([^;\r\n]+)"))
            values[match.Groups[1].Value.Trim()] = Unquote(match.Groups[2].Value.Trim());
        return values;
    }

    private static string ResolveInfValue(string token, IReadOnlyDictionary<string, string> values)
    {
        token = Unquote(token);
        if (token.Length > 2 && token.StartsWith('%') && token.EndsWith('%') &&
            values.TryGetValue(token[1..^1], out var value)) return value;
        return token;
    }

    private static bool InfSupportsCurrentArchitecture(string text)
    {
        var lower = text.ToLowerInvariant();
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => lower.Contains("ntamd64") || lower.Contains("nt$arch$") || !lower.Contains("ntx86"),
            Architecture.Arm64 => lower.Contains("ntarm64") || lower.Contains("nt$arch$"),
            _ => false
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> SplitPropertyBlocks(string output)
    {
        var blocks = new List<IReadOnlyDictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0) { blocks.Add(current); current = new(StringComparer.OrdinalIgnoreCase); }
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            current[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (current.Count > 0) blocks.Add(current);
        return blocks;
    }

    private static string GetProperty(IReadOnlyDictionary<string, string> properties, params string[] hints)
    {
        foreach (var pair in properties)
            foreach (var hint in hints)
                if (pair.Key.Contains(hint, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return string.Empty;
    }

    private static bool IsVddDriver(PnPDriverRecord record) =>
        ContainsVddHint(record.OriginalName, record.ProviderName, record.SignerName) &&
        (record.ClassName.Contains("display", StringComparison.OrdinalIgnoreCase) ||
         record.ClassName.Contains("디스플레이", StringComparison.OrdinalIgnoreCase) ||
         record.OriginalName.Contains("mttvdd", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsVddHint(params string[] values)
    {
        var combined = string.Join(' ', values).ToLowerInvariant();
        return VddHints.Any(combined.Contains) || combined.Contains("root\\mttvdd");
    }

    private static bool IsHealthyStatus(string status) => string.IsNullOrWhiteSpace(status) ||
        status.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("started", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("시작", StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string value) => value.Trim().Trim('"');
    private static bool OutputRequiresReboot(string output) =>
        output.Contains("reboot", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("restart", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("재부팅", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("다시 시작", StringComparison.OrdinalIgnoreCase);

    private static DriverOperationResult Failure(string prefix, ProcessResult result) =>
        new(OperationOutcome.Failed, $"{prefix} (오류 코드 {result.ExitCode}): {FriendlyPnPError(result)}", result.ExitCode);

    private static string FriendlyPnPError(ProcessResult result)
    {
        var output = result.CombinedOutput.Trim();
        if (output.Contains("access", StringComparison.OrdinalIgnoreCase) || output.Contains("액세스", StringComparison.OrdinalIgnoreCase))
            return "액세스가 거부되었습니다. 관리자 권한을 확인하세요.";
        if (output.Contains("signature", StringComparison.OrdinalIgnoreCase) || output.Contains("서명", StringComparison.OrdinalIgnoreCase))
            return "Windows가 드라이버 서명을 신뢰하지 않습니다.";
        return string.IsNullOrWhiteSpace(output) ? "pnputil이 세부 오류를 반환하지 않았습니다." : output;
    }

    private async Task RemoveOrphanedRootVddDevicesAsync(CancellationToken cancellationToken)
    {
        var result = await RunPnPUtilAsync(
            ["/enum-devices", "/class", "Display", "/deviceids", "/drivers"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return;

        var orphanedDevices = ParsePnPUtilEnumDevices(result.StandardOutput)
            .Where(device => device.InstanceId.StartsWith("ROOT\\VIRTUAL_DISPLAY_DRIVER\\",
                                 StringComparison.OrdinalIgnoreCase) &&
                             string.IsNullOrWhiteSpace(device.DriverName))
            .ToArray();
        foreach (var device in orphanedDevices)
        {
            _log.Warning($"이전 설치 실패로 남은 Root VDD 장치 정리: {device.InstanceId}");
            var remove = await RunPnPUtilAsync(["/remove-device", device.InstanceId], cancellationToken)
                .ConfigureAwait(false);
            LogCommandResult(remove);
            if (remove.ExitCode != 0)
                throw new InvalidOperationException($"잘못 등록된 VDD 장치를 정리하지 못했습니다: {device.InstanceId}");
        }
    }

    private async Task<DriverStatusInfo> WaitForVddInstallationAsync(CancellationToken cancellationToken)
    {
        DriverStatusInfo? status = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.State == DriverInstallState.Installed) return status;
            if (attempt < 19) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return status!;
    }

    private DriverPackageInfo? TryGetValidPackage(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return null;
        IReadOnlyList<DriverInfInfo> infFiles;
        try { infFiles = FindVddInfFiles(rootPath); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var validInfs = new List<DriverInfInfo>();
        foreach (var inf in infFiles)
        {
            try
            {
                if (ValidateVddPackage(inf.Path).IsValid && CheckVddSignature(inf.Path).IsValid)
                    validInfs.Add(inf);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (validInfs.Count == 0) return null;
        return new DriverPackageInfo
        {
            RootPath = rootPath,
            InfFiles = validInfs,
            SelectedInf = validInfs.OrderByDescending(info => info.MatchScore).First()
        };
    }

    private static GitHubReleaseAsset? SelectDriverAsset(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        var candidates = assets.Where(asset =>
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains("VirtualDisplayDriver", StringComparison.OrdinalIgnoreCase) &&
                !asset.Name.Contains("Control", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        candidates = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => candidates.Where(asset =>
                !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                (asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                 asset.Name.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
                 asset.Name.Contains("x86", StringComparison.OrdinalIgnoreCase))).ToArray(),
            Architecture.Arm64 => candidates.Where(asset =>
                asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase)).ToArray(),
            _ => []
        };

        return candidates
            .OrderByDescending(asset => asset.Name.Contains("Driver.Only", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                                       asset.Name.Contains("amd64", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static async Task DownloadFileAsync(Uri uri, string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await ReleaseClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDriverArchiveSize)
            throw new InvalidDataException("드라이버 압축 파일이 허용 크기를 초과합니다.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            totalBytes += read;
            if (totalBytes > MaximumDriverArchiveSize)
                throw new InvalidDataException("드라이버 압축 파일이 허용 크기를 초과합니다.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ExtractZipSafely(string archivePath, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        var normalizedRoot = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        long totalUncompressedBytes = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            totalUncompressedBytes += entry.Length;
            if (totalUncompressedBytes > MaximumDriverArchiveSize * 2)
                throw new InvalidDataException("드라이버 압축 해제 크기가 허용 범위를 초과합니다.");

            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!destinationPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("드라이버 ZIP에 안전하지 않은 경로가 포함되어 있습니다.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, false);
        }
    }

    private static string MakeSafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "latest" : result;
    }

    private static HttpClient CreateReleaseClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VirtualDisplayManager", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubReleaseAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; init; } = string.Empty;
    }

    private async Task<ProcessResult> RunPnPUtilAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("pnputil.exe를 찾을 수 없습니다.", path);
        return await ProcessRunner.RunAsync(path, arguments, cancellationToken).ConfigureAwait(false);
    }

    private void LogCommandResult(ProcessResult result)
    {
        var message = $"pnputil 종료 코드: {result.ExitCode}";
        if (result.ExitCode == 0) _log.Success(message); else _log.Error(message);
        if (!string.IsNullOrWhiteSpace(result.CombinedOutput)) _log.Info(result.CombinedOutput.Trim());
    }
}
