// USB serial is driven through the Win32 API, so it works in the Windows Editor and in Windows
// players (Mono or IL2CPP). Everywhere else this file is compiled out and the kit is reached over WiFi.
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Debug = UnityEngine.Debug;

/// <summary>
/// Serial (USB) transport to the eTactileKit ESP32. The kit holds the host off with USB flow
/// control while it is busy, so each frame goes out in a single write and no byte is dropped; its
/// USB port ignores the baud rate. Reads return as soon as bytes arrive and repeat until their own
/// deadline. Mirrors the reference Python implementation (api/communication.py): pyserial's port
/// settings and the same line control at open, one write per frame, and the reopen gap. Use from one
/// thread.
///
/// The ESP32-S3 resets whenever its RTS line is high while DTR is low. The port is opened through
/// kernel32 instead of System.IO.Ports because only a full port configuration controls the lines
/// at open: Mono's SerialPort.Open re-applies the line settings the driver last stored, and a freshly
/// enumerated port stores DTR disabled with RTS enabled, which resets the kit.
/// </summary>
public class SerialHandler : ICommHandler
{
    /// <summary>
    /// The kit ends a serial session once the port has been closed for 250 ms; a port reopened
    /// sooner continues the previous session (amplitudes and any half-sent command included).
    /// Reopening only after this gap lets the kit zero the amplitudes and restart its parser first.
    /// </summary>
    public const int ReopenGapMs = 400;

    private const int OpenRetryMs = 2000;     // Retry a port that does not exist yet this long (the kit enumerates after a plug-in or reboot)
    private const int ReadPollMs = 10;        // Longest one read attempt waits for the first byte
    private const int WriteTimeoutMs = 1000;  // A write the kit has not taken by then means the link has stalled

    // Port key -> when this process last closed that port (ms on Clock), or StillOpen.
    private static readonly Dictionary<string, long> lastClose = new Dictionary<string, long>();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private const long StillOpen = long.MaxValue;   // Opened here and not closed through Close()

    private SafeFileHandle handle;
    private readonly string portName;
    private readonly string portKey;
    private readonly int baudRate;
    private byte[] readChunk = Array.Empty<byte>();

    public bool IsOpen => handle != null;

    public SerialHandler(string portName, int baudRate)
    {
        this.portName = portName;
        portKey = PortKey(portName);
        this.baudRate = baudRate;
        Open();
    }

    /// <summary>Key for a port name: Windows names are case-insensitive and may carry the device-namespace prefix.</summary>
    private static string PortKey(string name)
    {
        string key = name.Trim().ToUpperInvariant();
        return key.StartsWith(@"\\.\") ? key.Substring(4) : key;
    }

    private void Open()
    {
        long waitMs = 0;
        bool dropped = false;
        lock (lastClose)
        {
            if (lastClose.TryGetValue(portKey, out long closedAt))
            {
                dropped = closedAt == StillOpen;
                waitMs = dropped ? ReopenGapMs : ReopenGapMs - (Clock.ElapsedMilliseconds - closedAt);
            }
        }
        if (dropped)
        {
            // A handler this process dropped without Close() keeps the port open until its handle is
            // finalized; finalize it now, so the whole gap follows the close.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        if (waitMs > 0)
        {
            Thread.Sleep((int)waitMs);
        }

        var sinceStart = Stopwatch.StartNew();
        while (true)
        {
            // The device-namespace prefix is required for COM10 and above.
            handle = CreateFile(@"\\.\" + portKey, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                break;
            }
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            handle = null;
            // Only a port that does not exist yet is retried: it appears once the kit has enumerated,
            // with a new session. A port another program holds is not; opened the moment it is
            // released, it would continue that program's session, amplitudes included.
            if (error != ERROR_FILE_NOT_FOUND || sinceStart.ElapsedMilliseconds >= OpenRetryMs)
            {
                string hint =
                    error == ERROR_FILE_NOT_FOUND ? " Check the COM port name and that the kit is plugged in." :
                    error == ERROR_ACCESS_DENIED ? " Another program or another connection in this app has the port open " +
                                                   "(e.g. the Python API, a serial monitor or the desktop app)." :
                    "";
                Debug.LogError($"[eTactileKit] Failed to open serial port {portName}: {Describe(error)}.{hint}");
                return;
            }
            Thread.Sleep(100);
        }

        lock (lastClose)
        {
            lastClose[portKey] = StillOpen;
        }
        if (!Configure())
        {
            Debug.LogError($"[eTactileKit] Failed to configure serial port {portName}: {Describe(Marshal.GetLastWin32Error())}.");
            Close();
            return;
        }
        Debug.Log($"[eTactileKit] Serial port {portName} opened");
    }

    /// <summary>
    /// Applies pyserial's DCB (binary 8N1, no flow control, DTR and RTS enabled), then lowers RTS and
    /// then DTR. Reads return on the first byte instead of pyserial's fixed wait. False if a call failed.
    /// </summary>
    private bool Configure()
    {
        var timeouts = new COMMTIMEOUTS
        {
            // A read returns at once with whatever has arrived, or waits up to ReadPollMs for the first byte.
            ReadIntervalTimeout = uint.MaxValue,
            ReadTotalTimeoutMultiplier = uint.MaxValue,
            ReadTotalTimeoutConstant = ReadPollMs,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = WriteTimeoutMs
        };
        var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf(typeof(DCB)) };
        if (!SetupComm(handle, 4096, 4096) || !SetCommTimeouts(handle, ref timeouts) || !GetCommState(handle, ref dcb))
        {
            return false;
        }
        dcb.BaudRate = (uint)baudRate;
        // Binary 8N1 with no flow control, no character substitution and no abort on error. Enabling
        // DTR and RTS raises them together; lowering RTS, then DTR, never passes through the reset
        // state, and the port then closes with both low, so a session ends without a reboot. This is
        // also what the Python API leaves stored in the driver.
        dcb.Flags = DcbBinary | DcbDtrControlEnable | DcbRtsControlEnable;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;
        dcb.XonChar = 0x11;   // Windows rejects equal XON and XOFF characters even with XON/XOFF off
        dcb.XoffChar = 0x13;
        return SetCommState(handle, ref dcb)
               && PurgeComm(handle, PURGE_TXABORT | PURGE_RXABORT | PURGE_TXCLEAR | PURGE_RXCLEAR)
               && EscapeCommFunction(handle, CLRRTS)
               && EscapeCommFunction(handle, CLRDTR);
    }

    public bool Write(byte[] data)
    {
        if (!IsOpen || data == null)
        {
            return false;
        }

        bool ok = WriteFile(handle, data, data.Length, out int written, IntPtr.Zero);
        if (ok && written == data.Length)
        {
            return true;
        }
        string reason = ok ? $"timed out after {written} of {data.Length} bytes" : Describe(Marshal.GetLastWin32Error());
        Debug.LogWarning($"[eTactileKit] Serial write to {portName} failed ({reason}); closing the port so the " +
                         "kit ends the session and zeroes every amplitude.");
        Close();
        return false;
    }

    public byte[] Read(int length, int timeoutMs)
    {
        if (!IsOpen || length <= 0)
        {
            return Array.Empty<byte>();
        }

        if (readChunk.Length < length)
        {
            readChunk = new byte[length];
        }
        byte[] buffer = new byte[length];
        int total = 0;
        var stopwatch = Stopwatch.StartNew();

        while (total < length)
        {
            if (!ReadFile(handle, readChunk, length - total, out int read, IntPtr.Zero))
            {
                Debug.LogWarning($"[eTactileKit] Serial read from {portName} failed " +
                                 $"({Describe(Marshal.GetLastWin32Error())}); closing the port.");
                Close();
                break;
            }
            Buffer.BlockCopy(readChunk, 0, buffer, total, read);
            total += read;
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                break;
            }
        }

        if (total == length)
        {
            return buffer;
        }

        byte[] partial = new byte[total];
        Buffer.BlockCopy(buffer, 0, partial, 0, total);
        return partial;
    }

    public void ClearInputBuffer()
    {
        if (IsOpen && !PurgeComm(handle, PURGE_RXABORT | PURGE_RXCLEAR))
        {
            Debug.LogWarning($"[eTactileKit] Serial port {portName} failed ({Describe(Marshal.GetLastWin32Error())}); closing it.");
            Close();
        }
    }

    public void Close()
    {
        if (handle == null)
        {
            return;
        }

        handle.Dispose();
        handle = null;
        lock (lastClose)
        {
            lastClose[portKey] = Clock.ElapsedMilliseconds;
        }
        Debug.Log($"[eTactileKit] Serial port {portName} closed");
    }

    private static string Describe(int error)
    {
        string message = new Win32Exception(error).Message.TrimEnd('\0').Trim().TrimEnd('.');   // Mono pads it with NULs
        return $"{message} (Win32 error {error})";
    }

    //--------------------------------------------------------------------------------------//
    // kernel32
    //--------------------------------------------------------------------------------------//
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint PURGE_TXABORT = 0x1;
    private const uint PURGE_RXABORT = 0x2;
    private const uint PURGE_TXCLEAR = 0x4;
    private const uint PURGE_RXCLEAR = 0x8;
    private const uint CLRRTS = 4;
    private const uint CLRDTR = 6;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_ACCESS_DENIED = 5;

    // DCB bit fields
    private const uint DcbBinary = 0x0001;
    private const uint DcbDtrControlEnable = 0x0010;   // fDtrControl = DTR_CONTROL_ENABLE
    private const uint DcbRtsControlEnable = 0x1000;   // fRtsControl = RTS_CONTROL_ENABLE

    [StructLayout(LayoutKind.Sequential)]
    private struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                                                    uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetupComm(SafeFileHandle file, uint inQueue, uint outQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle file, ref COMMTIMEOUTS timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle file, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle file, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PurgeComm(SafeFileHandle file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EscapeCommFunction(SafeFileHandle file, uint function);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, int count, out int read, IntPtr overlapped);
}
#endif
