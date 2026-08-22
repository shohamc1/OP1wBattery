using System.ComponentModel;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

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
    const int MaxTooltipLength = 127; // NotifyIcon.Text throws above this (szTip is 128 WCHARs)

    const string AppName = "OP1w Battery";
    const string ShortcutName = "OP1w Battery.lnk";

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
    string? _renderedText; // what the current icon shows; skips redundant renders
    int _renderedRgb;
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
            // Rendering or the shell failing must not stop the poll loop.
            DebugLog.Write($"update failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
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
        var rgb = IconColor(_reading);

        // Only re-render when the visible icon would actually change; the
        // common poll leaves the percentage (and so the icon) as it was.
        if (text != _renderedText || rgb != _renderedRgb)
        {
            var icon = RenderIcon(text, rgb, _iconSize, out var handle);

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
                // are about to free: put the previous one back first. The restore
                // must not mask the original exception or skip the cleanup below.
                try { _notifyIcon.Icon = previousIcon; }
                catch { /* keep the original exception */ }
                icon.Dispose();
                DestroyIcon(handle);
                throw;
            }
            _iconHandle = handle;
            _renderedText = text;
            _renderedRgb = rgb;

            // Shell_NotifyIcon (called above, inside the Icon setter) copies the
            // icon for its own use, so the handle we just swapped out is safe to
            // free immediately. Icon.FromHandle wrappers do not own their HICON,
            // but disposing the old wrapper keeps it out of the finalizer queue.
            if (previousHandle != IntPtr.Zero) DestroyIcon(previousHandle);
            previousIcon?.Dispose();
        }

        // Always set: the tooltip carries the voltage, which drifts between
        // polls even while the percentage (and so the icon) stays put.
        _notifyIcon.Text = TooltipFor(_reading);
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
        try
        {
            return Icon.FromHandle(handle);
        }
        catch
        {
            // The caller never sees the handle on a throw, so free it here.
            DestroyIcon(handle);
            handle = IntPtr.Zero;
            throw;
        }
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

    /// <summary>
    /// The level colour for a percentage; pure, so tests can hit it. Values
    /// above the top limit fall back to the top colour rather than relying on
    /// callers to clamp.
    /// </summary>
    internal static int ColorForPercent(int percent) =>
        LevelColors.FirstOrDefault(level => percent <= level.Limit, LevelColors[^1]).Rgb;

    /// <summary>
    /// The tray tooltip. NotifyIcon.Text throws an ArgumentOutOfRangeException
    /// above 127 characters (szTip is 128 WCHARs; the 63-char cap was .NET
    /// Framework), and the setter is reached from a poll rather than from user
    /// input, so the length is clamped rather than trusted. Pure, so tests can
    /// hit it.
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
        _startupItem.Checked = IsStartupEnabled();
    }

    void ToggleStartup()
    {
        try
        {
            SetStartupEnabled(!IsStartupEnabled());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or COMException)
        {
            MessageBox.Show($"Could not update the startup shortcut:\n{ex.Message}", AppName,
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
    //
    // A shortcut in the Startup folder rather than an HKCU Run value: Windows
    // leaves a Run value of ours out of the startup inventory it keeps under
    // StartupApproved, so it shows in neither Task Manager nor Settings, and
    // Explorer does not launch it at logon. A shortcut is inventoried and run.

    /// <summary>
    /// Our shortcut in the per-user Startup folder, or null if the shell cannot
    /// name that folder.
    /// </summary>
    static string? ShortcutPath()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, ShortcutName);
    }

    static bool IsStartupEnabled() => ShortcutPath() is { } path && File.Exists(path);

    static void SetStartupEnabled(bool enable)
    {
        var shortcut = ShortcutPath()
            ?? throw new InvalidOperationException("The Startup folder is unavailable.");

        if (!enable)
        {
            File.Delete(shortcut); // deleting nothing is not an error
            return;
        }

        var target = Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unavailable.");

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);
            link.SetDescription(AppName);
            ((IPersistFile)link).Save(shortcut, remember: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        // In vtable order, and complete: the unused members above SetPath still
        // have to be declared, or the ones below them land on the wrong slot.
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
                     int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon,
                             int maxPath, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, uint reserved);
        void Resolve(IntPtr owner, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid classId); // inherited from IPersist
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? file,
                  [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
