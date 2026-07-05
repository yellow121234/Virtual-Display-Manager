namespace VirtualDisplayManager.Core.Models;

public enum LogLevel { Info, Warning, Error, Success }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public override string ToString() => $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
}
