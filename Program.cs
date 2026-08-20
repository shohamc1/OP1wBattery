namespace OP1wBattery;

internal static class Program
{
    // Static so it lives for the whole process: a Mutex is released when it is
    // garbage-collected, and a local could be collected while the app still runs.
    static Mutex? _instanceMutex;

    [STAThread]
    static void Main()
    {
        _instanceMutex = new Mutex(true, @"Global\OP1wBatteryTrayMutex", out var isFirstInstance);
        if (!isFirstInstance) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Disposed on the way out so TrayApp can pull its icon from the tray
        // and free the HICON it owns.
        using var tray = new TrayApp();
        Application.Run(tray);
    }
}
