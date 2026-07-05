using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.DXGI.Resource;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace VirtualDisplayManager.Core.Services;

public sealed class CaptureService : IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly LoggingService _log;
    private readonly object _resourceLock = new();
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private Device? _device;
    private OutputDuplication? _duplication;
    private Texture2D? _stagingTexture;
    private int _fpsLimit = 15;
    private double _previewScale = 0.5;

    public event EventHandler<BitmapSource>? FrameReady;
    public event EventHandler<string>? StateChanged;
    public event EventHandler<string>? CaptureFailed;

    public bool IsRunning => _captureTask is { IsCompleted: false };

    public CaptureService(MonitorService monitorService, LoggingService log)
    {
        _monitorService = monitorService;
        _log = log;
    }

    public Task StartPreview(string monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
            throw new ArgumentException("캡처 대상 모니터를 선택하세요.", nameof(monitorId));
        if (IsRunning) throw new InvalidOperationException("미리보기가 이미 실행 중입니다.");
        var monitor = _monitorService.RefreshMonitors().FirstOrDefault(item => item.Id == monitorId)
            ?? throw new InvalidOperationException("선택한 모니터가 더 이상 존재하지 않습니다.");

        _captureCancellation = new CancellationTokenSource();
        _captureTask = Task.Run(() => CaptureLoop(monitor.Id, monitor.DeviceName, _captureCancellation.Token));
        StateChanged?.Invoke(this, "미리보기 시작 중");
        return Task.CompletedTask;
    }

    public async Task StopPreview()
    {
        var cts = _captureCancellation;
        var task = _captureTask;
        if (cts is null || task is null)
        {
            DisposeCaptureResources();
            StateChanged?.Invoke(this, "중지됨");
            return;
        }

        cts.Cancel();
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Dispose();
            _captureCancellation = null;
            _captureTask = null;
            DisposeCaptureResources();
            StateChanged?.Invoke(this, "중지됨");
        }
    }

    public void SetFpsLimit(int fps)
    {
        if (fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(fps), "FPS는 1~60이어야 합니다.");
        Volatile.Write(ref _fpsLimit, fps);
    }

    public void SetPreviewScale(double scale)
    {
        if (scale is < 0.25 or > 1.0) throw new ArgumentOutOfRangeException(nameof(scale));
        lock (_resourceLock) _previewScale = scale;
    }

    public void DisposeCaptureResources()
    {
        lock (_resourceLock)
        {
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _duplication?.Dispose();
            _duplication = null;
            _device?.Dispose();
            _device = null;
        }
    }

    private void CaptureLoop(string monitorId, string deviceName, CancellationToken cancellationToken)
    {
        Factory1? factory = null;
        Adapter1? selectedAdapter = null;
        Output1? selectedOutput = null;
        try
        {
            factory = new Factory1();
            for (var adapterIndex = 0; adapterIndex < factory.GetAdapterCount1() && selectedOutput is null; adapterIndex++)
            {
                var adapter = factory.GetAdapter1(adapterIndex);
                for (var outputIndex = 0; outputIndex < adapter.GetOutputCount(); outputIndex++)
                {
                    using var output = adapter.GetOutput(outputIndex);
                    if (!output.Description.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                    selectedAdapter = adapter;
                    selectedOutput = output.QueryInterface<Output1>();
                    break;
                }
                if (selectedOutput is null) adapter.Dispose();
            }
            if (selectedAdapter is null || selectedOutput is null)
                throw new InvalidOperationException("선택한 모니터에 해당하는 DXGI 출력을 찾지 못했습니다.");

            lock (_resourceLock)
            {
                _device = new Device(selectedAdapter, DeviceCreationFlags.BgraSupport);
                _duplication = selectedOutput.DuplicateOutput(_device);
            }
            StateChanged?.Invoke(this, "미리보기 실행 중");
            _log.Info($"DXGI 미리보기 시작: {deviceName}");

            var lastMonitorCheck = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = Environment.TickCount64;
                if ((DateTime.UtcNow - lastMonitorCheck).TotalSeconds >= 1)
                {
                    if (_monitorService.RefreshMonitors().All(monitor => monitor.Id != monitorId))
                        throw new InvalidOperationException("캡처 대상 모니터가 제거되거나 비활성화되었습니다.");
                    lastMonitorCheck = DateTime.UtcNow;
                }

                Resource? screenResource = null;
                var frameAcquired = false;
                try
                {
                    OutputDuplicateFrameInformation frameInfo;
                    var acquireResult = _duplication!.TryAcquireNextFrame(250, out frameInfo, out screenResource);
                    if (acquireResult == SharpDX.DXGI.ResultCode.WaitTimeout)
                        continue;
                    acquireResult.CheckError();
                    frameAcquired = true;
                    using var screenTexture = screenResource!.QueryInterface<Texture2D>();
                    var description = screenTexture.Description;
                    EnsureStagingTexture(description);
                    _device!.ImmediateContext.CopyResource(screenTexture, _stagingTexture!);
                    var frame = CopyFrameToBitmap(description.Width, description.Height);
                    FrameReady?.Invoke(this, frame);
                }
                finally
                {
                    screenResource?.Dispose();
                    if (frameAcquired) _duplication?.ReleaseFrame();
                }

                var frameTime = Math.Max(1, 1000 / Volatile.Read(ref _fpsLimit));
                var remaining = frameTime - (int)(Environment.TickCount64 - started);
                if (remaining > 0) cancellationToken.WaitHandle.WaitOne(remaining);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error($"미리보기 중지: {ex.Message}");
            CaptureFailed?.Invoke(this, ex.Message);
        }
        finally
        {
            DisposeCaptureResources();
            selectedOutput?.Dispose();
            selectedAdapter?.Dispose();
            factory?.Dispose();
            StateChanged?.Invoke(this, "중지됨");
        }
    }

    private void EnsureStagingTexture(Texture2DDescription source)
    {
        if (_stagingTexture is not null && _stagingTexture.Description.Width == source.Width &&
            _stagingTexture.Description.Height == source.Height && _stagingTexture.Description.Format == source.Format)
            return;
        _stagingTexture?.Dispose();
        _stagingTexture = new Texture2D(_device!, new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        });
    }

    private BitmapSource CopyFrameToBitmap(int sourceWidth, int sourceHeight)
    {
        var context = _device!.ImmediateContext;
        var dataBox = context.MapSubresource(_stagingTexture!, 0, MapMode.Read, MapFlags.None);
        try
        {
            double scale;
            lock (_resourceLock) scale = _previewScale;
            var targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            var targetStride = targetWidth * 4;
            var pixels = new byte[targetStride * targetHeight];

            if (targetWidth == sourceWidth && targetHeight == sourceHeight)
            {
                for (var y = 0; y < sourceHeight; y++)
                    Marshal.Copy(IntPtr.Add(dataBox.DataPointer, y * dataBox.RowPitch), pixels, y * targetStride, targetStride);
            }
            else
            {
                var sourceRow = new byte[sourceWidth * 4];
                var currentSourceY = -1;
                for (var y = 0; y < targetHeight; y++)
                {
                    var sourceY = Math.Min(sourceHeight - 1, (int)(y / scale));
                    if (sourceY != currentSourceY)
                    {
                        Marshal.Copy(IntPtr.Add(dataBox.DataPointer, sourceY * dataBox.RowPitch), sourceRow, 0, sourceRow.Length);
                        currentSourceY = sourceY;
                    }
                    for (var x = 0; x < targetWidth; x++)
                    {
                        var sourceX = Math.Min(sourceWidth - 1, (int)(x / scale));
                        System.Buffer.BlockCopy(sourceRow, sourceX * 4, pixels, y * targetStride + x * 4, 4);
                    }
                }
            }

            var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, targetStride);
            bitmap.Freeze();
            return bitmap;
        }
        finally { context.UnmapSubresource(_stagingTexture!, 0); }
    }

    public void Dispose()
    {
        _captureCancellation?.Cancel();
        try { _captureTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _captureCancellation?.Dispose();
        DisposeCaptureResources();
        GC.SuppressFinalize(this);
    }
}
