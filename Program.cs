namespace OP1wBattery;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Session-local single instance: the tray icon and the HKCU Run entry
        // are per user and per session, so a machine-wide Global\ mutex would
        // be wrong. The using keeps the mutex alive (and owned) until Main
        // exits; a released or collected mutex would let a second instance in.
        Mutex? instanceMutex = null;
        try
        {
            instanceMutex = new Mutex(true, @"Local\OP1wBatteryTrayMutex", out var isFirstInstance);
            if (!isFirstInstance)
            {
                instanceMutex.Dispose();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists in this session but under a different security
            // context (e.g. an elevated instance), so it cannot be opened:
            // treat that as "already running" and leave quietly.
            return;
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            // A kernel object of some other type already holds the name, so the
            // single-instance check is unavailable. Start anyway: running twice
            // is a far better failure than silently refusing to start, which
            // looks to the user like nothing happened at all.
            DebugLog.Write($"single-instance check unavailable: {ex.Message}");
        }

        using (instanceMutex)
        {
            ApplicationConfiguration.Initialize();
            // Disposed on the way out so TrayApp can pull its icon from the tray
            // and free the HICON it owns.
            using var tray = new TrayApp();
            Application.Run(tray);
        }
    }
}
