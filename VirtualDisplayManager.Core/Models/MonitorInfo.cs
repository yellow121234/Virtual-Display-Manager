namespace VirtualDisplayManager.Core.Models;

public sealed record MonitorInfo(
    string Id,
    string Name,
    string DeviceName,
    string DeviceId,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    bool IsVddEstimated)
{
    public string Position => $"{X}, {Y}";
    public string Resolution => $"{Width} × {Height}";
    public string PrimaryText => IsPrimary ? "예" : "아니요";
    public string VddText => IsVddEstimated ? "VDD 추정" : "일반";
}
