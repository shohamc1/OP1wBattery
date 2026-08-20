namespace OP1wBattery;

/// <summary>
/// Opt-in diagnostics for field reports. Set the OP1WBATTERY_DEBUG environment
/// variable to any value and read failures are appended to
/// %TEMP%\OP1wBattery-debug.log; without it this never touches the disk.
/// </summary>
internal static class DebugLog
{
    static readonly string? LogFile =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OP1WBATTERY_DEBUG"))
            ? null
            : Path.Combine(Path.GetTempPath(), "OP1wBattery-debug.log");

    public static void Write(string message)
    {
        if (LogFile is null) return;
        try
        {
            File.AppendAllText(LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }
}
