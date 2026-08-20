using System.ComponentModel;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace OP1wBattery;

/// <summary>
/// Tray presence for the OP1w battery indicator: renders the current percentage
/// onto the notify icon, polls the mouse on a timer, and warns once when the
/// battery gets low.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    const int WarnPercent = 10;      // notify at or below this level
    const int RearmPercent = 15;     // re-arm the warning once the level recovers above this
    const int PollSeconds = 300;     // normal polling interval
    const int PollSecondsLow = 120;  // polling interval while the battery is low
    const int LowPollPercent = 15;   // poll faster at or below this level
    const float MinimumFontSize = 5f;
    const int MaxTooltipLength = 63; // NotifyIcon.Text throws above this

    const string AppName = "OP1w Battery";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "OP1wBatteryTray";

    // "at or below this level, use this colour", evaluated lowest first.
    static readonly (int Limit, int Rgb)[] LevelColors =
    [
        (WarnPercent, 0xF87171),
        (25, 0xFB923C),
        (50, 0xFACC15),
        (100, 0x4ADE80),
    ];
    const int ColorUnknown = 0x9CA3AF;
    const int ColorWired = 0x60A5FA; // wired beats the level colour

    readonly NotifyIcon _notifyIcon;
    readonly ContextMenuStrip _menu;
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _refreshItem;
    readonly ToolStripMenuItem _startupItem;
    readonly System.Windows.Forms.Timer _pollTimer;
    readonly int _iconSize;

    IntPtr _iconHandle;   // the HICON currently shown; ours to destroy
    Reading? _reading;
    bool _busy;
    bool _warned;
    bool _exiting;

    public TrayApp()
    {
        _iconSize = SystemInformation.SmallIconSize.Width;
        if (_iconSize == 0) _iconSize = 16;

        _notifyIcon = new NotifyIcon();
        UpdateIcon(); // shows the "unknown" placeholder until the first read lands

        _statusItem = new ToolStripMenuItem { Enabled = false };

        _refreshItem = new ToolStripMenuItem("Refresh now");
        _refreshItem.Click += async (_, _) => await RefreshAsync();

        _startupItem = new ToolStripMenuItem("Start with Windows");
        _startupItem.Click += (_, _) => ToggleStartup();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            _refreshItem,
            _startupItem,
            new ToolStripSeparator(),
            exitItem,
        ]);
        _menu.Opening += OnMenuOpening;
        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.Visible = true;

        // The first read is driven by the timer rather than called from here, so
        // it only ever runs once Application.Run has installed the WinForms
        // synchronization context and the await can resume on the UI thread.
        _pollTimer = new System.Windows.Forms.Timer { Interval = 1 };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();
    }

    // --- reading the mouse --------------------------------------------------

    async Task RefreshAsync()
    {
        if (_busy || _exiting) return;
        _busy = true;
        // Stop the timer for the duration of the read: it is re-armed by
        // RescheduleTimer below, and without this the startup poll (which
        // starts at a 1 ms interval) keeps firing no-op ticks while the
        // synchronous HID wait runs.
        _pollTimer.Stop();
        _refreshItem.Enabled = false; // a click during the read would no-op anyway
        try
        {
            _reading = await Task.Run(MouseBattery.Read);
        }
        catch (Exception ex)
        {
            // Never let a bad read take the tray down; show "?" and try again.
            DebugLog.Write($"read threw: {ex.GetType().Name}: {ex.Message}");
            _reading = null;
        }
        finally
        {
            _busy = false;
        }

        if (_exiting) return;
        _refreshItem.Enabled = true;
        try
        {
            OnReadingUpdated();
        }
        catch (Exception ex)
        {
            // Rendering or the shell failing must not stop the poll loop, so the
            // timer is rescheduled below whatever happened above.
            DebugLog.Write($"update failed: {ex.GetType().Name}: {ex.Message}");
            RescheduleTimer();
        }
    }

    void OnReadingUpdated()
    {
        UpdateIcon();

        if (_reading is { } r)
        {
            if (r.Percent >= RearmPercent)
            {
                _warned = false;
            }
            else if (r.Percent <= WarnPercent && !r.Wired && !_warned)
            {
                _warned = true;
                _notifyIcon.ShowBalloonTip(0, "Mouse battery low",
                    $"The OP1w is at {r.Percent}%. Time to charge it.", ToolTipIcon.Warning);
            }
        }

        RescheduleTimer();
    }

    /// <summary>Arms the next poll. Always reached, so polling cannot stall.</summary>
    void RescheduleTimer()
    {
        if (_exiting) return;
        var low = _reading is { } r && r.Percent <= LowPollPercent;
        _pollTimer.Interval = (low ? PollSecondsLow : PollSeconds) * 1000;
        _pollTimer.Start();
    }

    // --- tray presentation ---------------------------------------------------

    void UpdateIcon()
    {
        var text = _reading is { } r ? r.Percent.ToString() : "?";
        var icon = RenderIcon(text, IconColor(_reading), _iconSize, out var handle);

        var previousIcon = _notifyIcon.Icon;
        var previousHandle = _iconHandle;
        try
        {
            _notifyIcon.Icon = icon;
        }
        catch
        {
            // The setter stores the icon on the NotifyIcon before it updates
            // the shell, so on failure the tray is left holding the icon we
            // are about to free: put the previous one back first.
            _notifyIcon.Icon = previousIcon;
            icon.Dispose();
            DestroyIcon(handle);
            throw;
        }
        _iconHandle = handle;
        _notifyIcon.Text = TooltipFor(_reading);

        // Shell_NotifyIcon (called above, inside the Icon setter) copies the
        // icon for its own use, so the handle we just swapped out is safe to
        // free immediately. Icon.FromHandle wrappers do not own their HICON,
        // but disposing the old wrapper keeps it out of the finalizer queue.
        if (previousHandle != IntPtr.Zero) DestroyIcon(previousHandle);
        previousIcon?.Dispose();
    }

    static Icon RenderIcon(string text, int rgb, int size, out IntPtr handle)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Typographic metrics, and the same format used to measure and to
            // draw. The default format pads each side by about a sixth of an em,
            // which at 16px is enough to push a digit of "55" out of the icon.
            // NoWrap stops an overflow being silently wrapped onto a second line.
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap,
            };
            using var font = LargestFontThatFits(graphics, text, size, format);
            using var brush = new SolidBrush(
                Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF));

            // Centre by hand: rectangle alignment reintroduces the padding.
            var extent = graphics.MeasureString(text, font, PointF.Empty, format);
            graphics.DrawString(text, font, brush,
                                (size - extent.Width) / 2f, (size - extent.Height) / 2f,
                                format);
        }

        // Bitmap.GetHicon() allocates a native HICON that .NET does not track
        // or own. Icon.FromHandle() just wraps it for drawing/assignment; the
        // caller is responsible for eventually calling DestroyIcon() on it, or
        // it leaks one GDI icon per poll for as long as the app runs.
        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>
    /// The biggest Segoe UI Bold whose text still fits the icon width.
    /// </summary>
    /// <remarks>
    /// A tray icon is only about 16px square, so "8", "55" and "100" each need a
    /// different size, and measuring beats guessing a scale factor per digit
    /// count. The measurement is taken against the widest digits of the same
    /// length ("88" for any two-digit level) so the text keeps one size as the
    /// battery falls, instead of resizing every time a narrower glyph comes
    /// round. Only width is tested: digits have no descenders, so vertical
    /// centring handles the rest.
    /// </remarks>
    static Font LargestFontThatFits(Graphics graphics, string text, int size,
                                    StringFormat format)
    {
        var widest = new string('8', text.Length);
        for (var em = (float)size; em > MinimumFontSize; em -= 0.5f)
        {
            var candidate = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            if (graphics.MeasureString(widest, candidate, PointF.Empty, format).Width <= size - 1)
                return candidate;
            candidate.Dispose();
        }
        return new Font("Segoe UI", MinimumFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    static int IconColor(Reading? reading)
    {
        if (reading is not { } r) return ColorUnknown;
        if (r.Wired) return ColorWired;
        return ColorForPercent(r.Percent);
    }

    /// <summary>The level colour for a percentage; pure, so tests can hit it.</summary>
    internal static int ColorForPercent(int percent) =>
        LevelColors.First(level => percent <= level.Limit).Rgb;

    /// <summary>
    /// The tray tooltip. NotifyIcon.Text throws an ArgumentOutOfRangeException
    /// above 63 characters, and the setter is reached from a poll rather than
    /// from user input, so the length is clamped rather than trusted. Pure, so
    /// tests can hit it.
    /// </summary>
    internal static string TooltipFor(Reading? reading)
    {
        var text = $"{AppName}\n{StatusLine(reading)}";
        return text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
    }

    static string StatusLine(Reading? reading)
    {
        if (reading is not { } r) return "mouse not responding";

        var voltage = $"{r.Millivolts / 1000.0:F2} V";
        return r.Wired
            ? $"{r.Percent}%  wired  {voltage}"
            : $"{r.Percent}%  {voltage}";
    }

    // --- user interaction ------------------------------------------------

    void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        _statusItem.Text = StatusLine(_reading);
        try
        {
            _startupItem.Checked = IsStartupEnabled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A locked-down Run key must not break the menu; leave the
            // checkbox showing whatever it showed last time.
        }
    }

    void ToggleStartup()
    {
        try
        {
            SetStartupEnabled(!IsStartupEnabled());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or SecurityException or InvalidOperationException)
        {
            MessageBox.Show($"Could not update the Run key:\n{ex.Message}", AppName,
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ExitApp()
    {
        // Stops an in-flight read from touching a disposed NotifyIcon on the
        // way out; a poll can still be a second or more from returning.
        _exiting = true;
        _pollTimer.Stop();
        _notifyIcon.Visible = false; // hide first, or a ghost icon lingers until hovered
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            var lastIcon = _notifyIcon.Icon;
            _notifyIcon.Dispose();
            lastIcon?.Dispose(); // the FromHandle wrapper; the HICON goes below
            _menu.Dispose();
            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
        }
        base.Dispose(disposing);
    }

    // --- start with Windows ------------------------------------------------

    static bool IsStartupEnabled()
    {
        var command = StartupCommand();
        if (command is null) return false;

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return string.Equals(key?.GetValue(RunValueName) as string, command,
                             StringComparison.OrdinalIgnoreCase);
    }

    static void SetStartupEnabled(bool enable)
    {
        if (!enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        var command = StartupCommand()
            ?? throw new InvalidOperationException("The executable path is unavailable.");
        using var writableKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        writableKey.SetValue(RunValueName, command);
    }

    static string? StartupCommand() => Environment.ProcessPath is { } path
        ? $"\"{path}\""
        : null;

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
