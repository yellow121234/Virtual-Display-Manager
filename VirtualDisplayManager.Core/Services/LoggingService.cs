using VirtualDisplayManager.Core.Models;

namespace VirtualDisplayManager.Core.Services;

public sealed class LoggingService
{
    public event EventHandler<LogEntry>? EntryAdded;

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Success(string message) => Write(LogLevel.Success, message);
    public void Warning(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Write(LogLevel level, string message) =>
        EntryAdded?.Invoke(this, new LogEntry(DateTimeOffset.Now, level, message));
}
