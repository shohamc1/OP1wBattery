using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OP1wBattery;

/// <summary>A single battery reading from the mouse.</summary>
internal readonly record struct Reading(int Percent, int Millivolts, bool Wired);

/// <summary>
/// Battery readout for the Endgame Gear OP1w 4k v2 over raw HID.
///
/// Protocol recovered from Endgame_Gear_OP1w_4k_v2_Configuration_Tool_v1_00.exe:
///
///   Interface   VID 0x3367, PID 0x1970 (dongle) or 0x1984 (cabled). Several HID
///               collections share those IDs; the control one reports usage page
///               0xFF01, usage 0x02.
///   Transport   Feature reports, ID 0xA1, 64 bytes including the ID.
///
///     request   HidD_SetFeature(64):  [0]=0xA1, [1]=command, rest zero
///     wait      ~350 ms
///     response  HidD_GetFeature(64) with [0]=0xA1 pre-set
///               [1]    status: 1 = ready, 3 = busy (sleep and re-read), 8 = gone
///               [16..] payload
///
///   cmd 0xB4    battery: payload[0] = percent, payload[1..2] = cell millivolts
///   cmd 0x0D    dongle firmware version at payload[1..2]
///   cmd 0x0E    mouse firmware version at payload[7..8], mouse PID at [2..3]
/// </summary>
internal static class MouseBattery
{
    const ushort VendorId = 0x3367;
    const ushort DonglePid = 0x1970;
    const ushort WiredPid = 0x1984;
    const ushort ControlUsagePage = 0xFF01;
    const ushort ControlUsage = 0x02;

    const byte ReportId = 0xA1;
    const int ReportLength = 64;
    const int PayloadAt = 0x10;
    const byte StatusReady = 1;
    const byte StatusBusy = 3;

    const int HidpStatusSuccess = 0x00110000;

    const byte CommandBattery = 0xB4;

    const uint GenericReadWrite = 0xC0000000;
    const uint ShareReadWrite = 3;
    const uint OpenExisting = 3;

    /// <summary>Take one reading, or null if the mouse is absent or asleep.</summary>
    public static Reading? Read()
    {
        var candidates = OpenControlInterfaces();
        if (candidates.Count == 0)
        {
            DebugLog.Write("no control interface present");
            return null;
        }

        try
        {
            foreach (var candidate in candidates)
            {
                var payload = SendCommand(candidate.Handle, CommandBattery);
                if (payload is null) continue; // asleep or gone: try the next interface
                return ParseBatteryPayload(payload, wired: candidate.ProductId == WiredPid);
            }
            return null;
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Handle.Dispose();
        }
    }

    /// <summary>
    /// Percent and cell millivolts from a 0xB4 payload. Pure, so tests can hit
    /// it without a mouse.
    /// </summary>
    internal static Reading ParseBatteryPayload(byte[] payload, bool wired) =>
        new(Percent: Math.Min((int)payload[0], 100),
            Millivolts: payload[1] | payload[2] << 8,
            Wired: wired);

    /// <summary>Send a vendor command and return its payload, or null on failure.</summary>
    static byte[]? SendCommand(SafeFileHandle handle, byte command,
                               int settleMs = 350, int retries = 3)
    {
        var request = new byte[ReportLength];
        request[0] = ReportId;
        request[1] = command;
        if (!HidD_SetFeature(handle, request, ReportLength))
        {
            DebugLog.Write($"cmd 0x{command:X2}: SetFeature failed");
            return null;
        }

        Thread.Sleep(settleMs);

        var retryMs = 200;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            var response = new byte[ReportLength];
            response[0] = ReportId;
            if (!HidD_GetFeature(handle, response, ReportLength))
            {
                DebugLog.Write($"cmd 0x{command:X2}: GetFeature failed");
                return null;
            }

            if (response[1] == StatusReady) return response[PayloadAt..];
            if (response[1] != StatusBusy)
            {
                DebugLog.Write($"cmd 0x{command:X2}: unexpected status {response[1]}");
                return null;
            }

            if (attempt < retries) // no point sleeping after the final attempt
            {
                Thread.Sleep(retryMs);
                retryMs += 200;
            }
        }

        DebugLog.Write($"cmd 0x{command:X2}: still busy after {retries + 1} attempts");
        return null;
    }

    /// <summary>
    /// Every vendor control interface currently present, wired first: when the
    /// mouse charges over cable with the dongle still plugged in, both are
    /// present and the wired interface is the one that reflects the mouse.
    /// The caller owns the handles and must dispose them all.
    /// </summary>
    static List<(SafeFileHandle Handle, ushort ProductId)> OpenControlInterfaces()
    {
        var candidates = new List<(SafeFileHandle Handle, ushort ProductId)>();
        try
        {
            foreach (var path in HidInterfacePaths())
            {
                var productId = ProductIdIn(path);
                if (productId is null) continue;

                var handle = CreateFileW(path, GenericReadWrite, ShareReadWrite,
                                         IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle.IsInvalid) continue;
                if (IsControlInterface(handle)) candidates.Add((handle, productId.Value));
                else handle.Dispose();
            }

            return candidates.OrderByDescending(c => c.ProductId == WiredPid).ToList();
        }
        catch
        {
            // A throw mid-loop would otherwise strand every handle opened so
            // far on the finalizer queue: the caller only sees the list once
            // it is returned.
            foreach (var candidate in candidates) candidate.Handle.Dispose();
            throw;
        }
    }

    static ushort? ProductIdIn(string devicePath)
    {
        foreach (var candidate in new[] { DonglePid, WiredPid })
            if (devicePath.Contains($"vid_{VendorId:x4}&pid_{candidate:x4}",
                                    StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }

    /// <summary>True for the collection that answers 0xA1 feature reports.</summary>
    static bool IsControlInterface(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return false;
        try
        {
            // HIDP_CAPS is 64 bytes and starts with Usage then UsagePage; the
            // rest is report lengths and counts we have no use for.
            var caps = new ushort[32];
            // HidP_GetCaps returns an NTSTATUS: HIDP_STATUS_SUCCESS is
            // 0x00110000, not 0.
            if (HidP_GetCaps(preparsed, caps) != HidpStatusSuccess) return false;
            return caps[0] == ControlUsage && caps[1] == ControlUsagePage;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>Device paths of every HID interface currently present.</summary>
    static IEnumerable<string> HidInterfacePaths()
    {
        // A device arriving between the size call and the list call makes the
        // buffer too small; the docs say to call the pair in a loop for exactly
        // that race, so retry a couple of times before giving up.
        const int CrBufferSmall = 26;

        HidD_GetHidGuid(out var hidClass);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, in hidClass, null, 0) != 0)
                return [];

            var buffer = new char[length];
            var result = CM_Get_Device_Interface_ListW(in hidClass, null, buffer, length, 0);
            if (result == 0)
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (result != CrBufferSmall)
                return [];
        }
        return [];
    }

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid hidClass);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, [Out] ushort[] caps);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle device, byte[] report, int length);

    // 0 in the final argument means "devices present right now".
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(
        out int length, in Guid interfaceClass, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(
        in Guid interfaceClass, string? deviceId, char[] buffer, int length, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(
        string path, uint access, uint shareMode, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
