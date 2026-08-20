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
        Mutex instanceMutex;
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
            // The mutex exists but is owned by another session: treat that as
            // "already running" and leave quietly.
            return;
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
