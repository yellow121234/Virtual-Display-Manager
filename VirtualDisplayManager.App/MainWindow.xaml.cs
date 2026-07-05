using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VirtualDisplayManager.Core.Models;
using VirtualDisplayManager.Core.Services;
using VirtualDisplayManager.App.Services;

namespace VirtualDisplayManager.App;

public partial class MainWindow : Window
{
    private readonly LoggingService _log = new();
    private readonly DriverService _driverService;
    private readonly MonitorService _monitorService = new();
    private readonly CaptureService _captureService;
    private readonly DispatcherTimer _autoRefreshTimer;
    private GlobalHotkeyService? _hotkeyService;
    private PreviewWindow? _previewWindow;
    private string? _capturedMonitorId;
    private bool _displayRecoveryRunning;
    private DriverPackageInfo? _package;
    private bool _operationRunning;

    public MainWindow()
    {
        InitializeComponent();
        _driverService = new DriverService(_log);
        _captureService = new CaptureService(_monitorService, _log);
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAllAsync(false);

        _log.EntryAdded += (_, entry) => Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText(entry + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
        _captureService.FrameReady += (_, frame) => Dispatcher.BeginInvoke(() =>
        {
            PreviewImage.Source = frame;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            _previewWindow?.SetFrame(frame);
        }, DispatcherPriority.Render);
        _captureService.StateChanged += (_, state) => Dispatcher.BeginInvoke(() =>
        {
            PreviewStatusText.Text = state;
            var running = state.Contains("실행 중", StringComparison.Ordinal);
            StartPreviewButton.IsEnabled = !running && MonitorGrid.SelectedItem is MonitorInfo;
            StopPreviewButton.IsEnabled = running;
            _previewWindow?.SetStatus(state);
        });
        _captureService.CaptureFailed += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            PreviewStatusText.Text = "중지됨 · " + message;
            PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            _capturedMonitorId = null;
            _previewWindow?.ClearFrame("미리보기가 중지되었습니다: " + message);
            MessageBox.Show(this, message, "미리보기 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _hotkeyService = new GlobalHotkeyService(this);
            _hotkeyService.F11Pressed += async (_, _) => await RecoverPhysicalDisplayAsync("F11 전역 단축키");
            _hotkeyService.F12Pressed += async (_, _) => await SwitchPrimaryDisplayAsync("F12 전역 단축키");
            _hotkeyService.RegisterF11AndF12();
            _log.Success("F11/F12 전역 단축키가 등록되었습니다. F11은 물리 모니터를 주 화면으로 지정하고, F12는 확장 상태를 유지한 채 주 화면만 다음 모니터로 전환합니다.");
        }
        catch (Exception ex)
        {
            _log.Warning("F11/F12 전역 단축키 등록 실패: " + ex.Message);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var isAdmin = PrivilegeService.IsRunAsAdministrator();
        AdminStatusText.Text = isAdmin ? "관리자 권한: 예" : "관리자 권한: 아니요 (제한 모드)";
        ElevateButton.IsEnabled = !isAdmin;
        CreateVirtualMonitorButton.IsEnabled = isAdmin;
        InstallButton.IsEnabled = isAdmin;
        UninstallButton.IsEnabled = isAdmin;
        SettingsPathText.Text = _driverService.GetVddSettingsFilePath();
        _log.Info("프로그램 시작. 화면 캡처는 사용자가 시작할 때까지 비활성 상태입니다.");
        await RefreshAllAsync(true);
    }

    private async Task RefreshAllAsync(bool logResult)
    {
        try
        {
            _package = _driverService.FindVddDriverPackage();
            PackagePathText.Text = _package.RootPath;
            var oldInf = (InfComboBox.SelectedItem as DriverInfInfo)?.Path;
            InfComboBox.ItemsSource = _package.InfFiles;
            InfComboBox.SelectedItem = _package.InfFiles.FirstOrDefault(item => item.Path == oldInf) ?? _package.SelectedInf;

            var monitors = _monitorService.RefreshMonitors();
            var selectedId = (MonitorGrid.SelectedItem as MonitorInfo)?.Id;
            MonitorGrid.ItemsSource = monitors;
            MonitorGrid.SelectedItem = monitors.FirstOrDefault(monitor => monitor.Id == selectedId);
            VddMonitorText.Text = monitors.Count(monitor => monitor.IsVddEstimated) is var count && count > 0
                ? $"{count}개 감지" : "감지 안 됨";

            var status = await _driverService.GetStatusAsync();
            DriverStatusText.Text = "드라이버: " + status.Summary;
            DeviceStatusText.Text = status.Devices.Count == 0
                ? "감지 안 됨"
                : string.Join(" · ", status.Devices.Select(device => $"{device.Description} [{device.Status}]"));
            RebootStatusText.Text = status.RebootRequired || status.State == DriverInstallState.RebootRequired ? "예" : "아니요";
            if (logResult) _log.Info($"새로고침 완료: 모니터 {monitors.Count}개, {status.Summary}");
        }
        catch (Exception ex)
        {
            DriverStatusText.Text = "드라이버: 상태 확인 오류";
            _log.Error("새로고침 실패: " + ex.Message);
            if (logResult) MessageBox.Show(this, ex.Message, "새로고침 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationRunning) { _log.Warning("설치/삭제 작업이 이미 진행 중입니다."); return; }
        var inf = InfComboBox.SelectedItem as DriverInfInfo;
        if (inf is null)
        {
            inf = await DownloadLatestDriverPackageAsync();
            if (inf is null) return;
        }

        var validation = _driverService.ValidateVddPackage(inf.Path);
        if (!validation.IsValid)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine,
                validation.Errors.Concat(validation.MissingFiles.Select(file => "누락: " + file))),
                "패키지 검증 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var signature = _driverService.CheckVddSignature(inf.Path);
        if (!signature.IsValid)
        {
            MessageBox.Show(this, signature.Message + "\n서명되지 않거나 신뢰되지 않는 드라이버는 설치하지 않습니다.",
                "서명 검증 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (MessageBox.Show(this,
                $"서명 검증된 Virtual Display Driver 패키지를 설치합니다.\n\nINF: {inf.Path}\n{signature.Message}\n\n계속하시겠습니까?",
                "드라이버 설치 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await RunDriverOperationAsync(() => _driverService.InstallVddAsync(inf.Path));
    }

    private async Task<DriverInfInfo?> DownloadLatestDriverPackageAsync()
    {
        _operationRunning = true;
        CreateVirtualMonitorButton.IsEnabled = InstallButton.IsEnabled = UninstallButton.IsEnabled = RefreshButton.IsEnabled = false;
        PackagePathText.Text = "공식 최신 Release 확인 중…";
        try
        {
            var download = await _driverService.DownloadLatestVddPackageAsync();
            _package = download.Package;
            PackagePathText.Text = _package.RootPath;
            InfComboBox.ItemsSource = _package.InfFiles;
            InfComboBox.SelectedItem = _package.SelectedInf;
            _log.Success($"드라이버 Release {download.ReleaseTag} 자동 준비 완료 ({download.AssetName})");
            return _package.SelectedInf;
        }
        catch (Exception ex)
        {
            _log.Error("드라이버 자동 다운로드 실패: " + ex.Message);
            MessageBox.Show(this, ex.Message,
                "드라이버 다운로드 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            _operationRunning = false;
            var isAdmin = PrivilegeService.IsRunAsAdministrator();
            CreateVirtualMonitorButton.IsEnabled = InstallButton.IsEnabled = UninstallButton.IsEnabled = isAdmin;
            RefreshButton.IsEnabled = true;
            if (_package is null || _package.SelectedInf is null)
                PackagePathText.Text = _driverService.GetDriverPackageCacheDirectory();
        }
    }

    private async void CreateVirtualMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationRunning) { _log.Warning("설치/삭제 작업이 이미 진행 중입니다."); return; }

        var inf = InfComboBox.SelectedItem as DriverInfInfo;
        if (inf is null)
        {
            inf = await DownloadLatestDriverPackageAsync();
            if (inf is null) return;
        }

        if (!TryReadPositiveInteger(MonitorCountBox, "가상 모니터 개수", out var count) ||
            !TryReadPositiveInteger(WidthBox, "가로 해상도", out var width) ||
            !TryReadPositiveInteger(HeightBox, "세로 해상도", out var height) ||
            !TryReadPositiveInteger(RefreshRateBox, "주사율", out var refreshRate))
            return;

        if (MessageBox.Show(this,
                $"가상 모니터 {count}개를 생성하도록 VDD 설정을 만들고 드라이버를 적용합니다.\n\n해상도: {width}x{height} @ {refreshRate}Hz\nINF: {inf.Path}\n\n계속하시겠습니까?",
                "가상 모니터 만들기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await RunDriverOperationAsync(() => _driverService.CreateVirtualMonitorAsync(inf.Path, count, width, height, refreshRate));
    }

    private bool TryReadPositiveInteger(TextBox box, string name, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
            return true;

        MessageBox.Show(this, $"{name} 값이 올바르지 않습니다.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        box.SelectAll();
        return false;
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationRunning) { _log.Warning("설치/삭제 작업이 이미 진행 중입니다."); return; }
        if (MessageBox.Show(this,
                "가상 디스플레이 드라이버를 삭제하면 현재 가상 디스플레이가 사라질 수 있습니다. 미리보기는 먼저 중지됩니다. 계속하시겠습니까?",
                "드라이버 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await _captureService.StopPreview();
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        await RunDriverOperationAsync(() => _driverService.UninstallVddAsync());
    }

    private async Task RunDriverOperationAsync(Func<Task<DriverOperationResult>> operation)
    {
        _operationRunning = true;
        CreateVirtualMonitorButton.IsEnabled = InstallButton.IsEnabled = UninstallButton.IsEnabled = RefreshButton.IsEnabled = false;
        try
        {
            var result = await operation();
            if (result.Outcome is OperationOutcome.Success or OperationOutcome.AlreadyCompleted)
                _log.Success($"{result.Message} (오류 코드 {result.ExitCode})");
            else if (result.Outcome == OperationOutcome.RebootRequired)
                _log.Warning(result.Message);
            else
                _log.Error($"{result.Message} (오류 코드 {result.ExitCode})");
            RebootStatusText.Text = result.RebootRequired ? "예" : "아니요";
            MessageBox.Show(this, result.Message, "드라이버 작업",
                MessageBoxButton.OK, result.Outcome is OperationOutcome.Failed or OperationOutcome.Busy
                    ? MessageBoxImage.Error : MessageBoxImage.Information);
        }
        finally
        {
            _operationRunning = false;
            var isAdmin = PrivilegeService.IsRunAsAdministrator();
            CreateVirtualMonitorButton.IsEnabled = InstallButton.IsEnabled = UninstallButton.IsEnabled = isAdmin;
            RefreshButton.IsEnabled = true;
            await RefreshAllAsync(false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync(true);

    private async void RecoverDisplayButton_Click(object sender, RoutedEventArgs e)
        => await RecoverPhysicalDisplayAsync("버튼");

    private async void SwitchPrimaryButton_Click(object sender, RoutedEventArgs e)
        => await SwitchPrimaryDisplayAsync("버튼");

    private async Task RecoverPhysicalDisplayAsync(string source)
    {
        if (_displayRecoveryRunning)
        {
            _log.Warning("화면 복구가 이미 실행 중입니다.");
            return;
        }

        _displayRecoveryRunning = true;
        RecoverDisplayButton.IsEnabled = false;
        SwitchPrimaryButton.IsEnabled = false;
        try
        {
            _log.Warning($"{source}: 확장 상태를 유지하고 물리 모니터를 주 화면으로 전환합니다.");
            var result = await DisplayRecoveryService.MakePhysicalDisplayPrimaryPreserveExtendAsync();
            if (result.Changed)
                _log.Success($"주 화면 전환 완료: {result.PrimaryDisplayName} ({result.PrimaryDeviceName}) · 물리 {result.PhysicalDisplayCount}개 / VDD {result.VirtualDisplayCount}개 · 확장 유지");
            else
                _log.Success($"이미 물리 모니터가 주 화면입니다: {result.PrimaryDisplayName} ({result.PrimaryDeviceName}) · 확장 유지");
            await RefreshAllAsync(false);
        }
        catch (Exception ex)
        {
            _log.Error("물리 모니터 주 화면 전환 실패: " + ex.Message);
            MessageBox.Show(this,
                "물리 모니터 주 화면 전환에 실패했습니다.\n\n" + ex.Message + "\n\n급한 수동 복구: Win+R → DisplaySwitch.exe /internal → Enter",
                "화면 복구 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _displayRecoveryRunning = false;
            RecoverDisplayButton.IsEnabled = true;
            SwitchPrimaryButton.IsEnabled = true;
        }
    }

    private async Task SwitchPrimaryDisplayAsync(string source)
    {
        if (_displayRecoveryRunning)
        {
            _log.Warning("주 화면 전환이 이미 실행 중입니다.");
            return;
        }

        _displayRecoveryRunning = true;
        RecoverDisplayButton.IsEnabled = false;
        SwitchPrimaryButton.IsEnabled = false;
        try
        {
            _log.Info($"{source}: 확장 상태를 유지한 채 주 화면만 다음 모니터로 전환합니다.");
            var result = await DisplayRecoveryService.SwitchPrimaryDisplayPreserveExtendAsync();
            _log.Success($"주 화면 전환 완료: {result.PrimaryDisplayName} ({result.PrimaryDeviceName}) · 물리 {result.PhysicalDisplayCount}개 / VDD {result.VirtualDisplayCount}개 · 확장 유지");
            await RefreshAllAsync(false);
        }
        catch (Exception ex)
        {
            _log.Error("주 화면 전환 실패: " + ex.Message);
            MessageBox.Show(this,
                "주 화면 전환에 실패했습니다.\n\n" + ex.Message,
                "주 화면 전환 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _displayRecoveryRunning = false;
            RecoverDisplayButton.IsEnabled = true;
            SwitchPrimaryButton.IsEnabled = true;
        }
    }

    private async void StartPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorGrid.SelectedItem is not MonitorInfo monitor)
        {
            MessageBox.Show(this, "미리보기 전에 캡처 대상 모니터를 선택하세요.", "대상 없음",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            ApplyCaptureSettings();
            await _captureService.StartPreview(monitor.Id);
            _capturedMonitorId = monitor.Id;
            StartPreviewButton.IsEnabled = false;
            StopPreviewButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error("미리보기 시작 실패: " + ex.Message);
            MessageBox.Show(this, ex.Message, "미리보기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        await _captureService.StopPreview();
        _capturedMonitorId = null;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        _previewWindow?.ClearFrame("미리보기가 중지되었습니다.");
        _log.Info("사용자가 미리보기를 중지했습니다.");
    }

    private async void OpenPreviewWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorGrid.SelectedItem is not MonitorInfo monitor)
        {
            MessageBox.Show(this, "새 창으로 볼 모니터를 먼저 선택하세요.", "대상 없음",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_captureService.IsRunning && _capturedMonitorId != monitor.Id)
            {
                await _captureService.StopPreview();
                _capturedMonitorId = null;
            }

            if (!_captureService.IsRunning)
            {
                ApplyCaptureSettings();
                await _captureService.StartPreview(monitor.Id);
                _capturedMonitorId = monitor.Id;
            }

            ShowPreviewWindow(monitor.Name);
        }
        catch (Exception ex)
        {
            _log.Error("새 창 미리보기 시작 실패: " + ex.Message);
            MessageBox.Show(this, ex.Message, "미리보기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowPreviewWindow(string monitorName)
    {
        if (_previewWindow is not null)
        {
            _previewWindow.SetMonitorName(monitorName);
            if (_previewWindow.WindowState == WindowState.Minimized)
                _previewWindow.WindowState = WindowState.Normal;
            _previewWindow.Activate();
            return;
        }

        _previewWindow = new PreviewWindow(monitorName) { Owner = this };
        _previewWindow.Closed += (_, _) => _previewWindow = null;
        _previewWindow.Show();
    }

    private void MonitorGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorGrid.SelectedItem is MonitorInfo monitor)
        {
            PreviewStatusText.Text = _captureService.IsRunning ? "미리보기 실행 중" : $"선택됨: {monitor.Name}";
            StartPreviewButton.IsEnabled = !_captureService.IsRunning;
            OpenPreviewWindowButton.IsEnabled = true;
        }
        else
        {
            StartPreviewButton.IsEnabled = false;
            OpenPreviewWindowButton.IsEnabled = false;
        }
    }

    private void InfComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_package is not null && InfComboBox.SelectedItem is DriverInfInfo info) _package.SelectedInf = info;
    }

    private void FpsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyCaptureSettings();
    }

    private void ScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyCaptureSettings();
    }

    private void ApplyCaptureSettings()
    {
        if (FpsComboBox.SelectedItem is ComboBoxItem fpsItem && int.TryParse(fpsItem.Content?.ToString(), out var fps))
            _captureService.SetFpsLimit(fps);
        if (ScaleComboBox.SelectedItem is ComboBoxItem scaleItem &&
            double.TryParse(scaleItem.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
            _captureService.SetPreviewScale(scale);
    }

    private void AutoRefreshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshCheckBox.IsChecked == true) _autoRefreshTimer.Start(); else _autoRefreshTimer.Stop();
    }

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        var result = PrivilegeService.TryRestartAsAdministrator();
        switch (result)
        {
            case ElevationResult.Restarted: Application.Current.Shutdown(); break;
            case ElevationResult.Denied:
                _log.Warning("사용자가 UAC 요청을 취소했습니다. 제한 모드를 유지합니다.");
                break;
            case ElevationResult.Failed:
                MessageBox.Show(this, "관리자 권한으로 다시 시작하지 못했습니다.", "권한 상승 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _autoRefreshTimer.Stop();
        _hotkeyService?.Dispose();
        _previewWindow?.Close();
        _captureService.Dispose();
    }
}
