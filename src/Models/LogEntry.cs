namespace filestore.Models;

/// <summary>One line in the activity log at the bottom of the main window.</summary>
public sealed class LogEntry
{
    public required DateTime Time { get; init; }
    public required string Message { get; init; }
    public bool IsError { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");
}
