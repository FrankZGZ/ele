using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using Debug = UnityEngine.Debug;

/// <summary>
/// TCP/IP transport to the eTactileKit ESP32 (WiFi). Each frame goes out in a single send; reads
/// wait on the socket until their deadline. Mirrors the reference Python implementation
/// (api/communication.py CommunicationWiFi).
/// </summary>
public class WiFiHandler : ICommHandler
{
    // Bound so an unreachable host (wrong IP, AP not joined) fails fast instead of blocking the
    // main thread for the OS default (~20 s). Matches the Python client's 5 s connect timeout.
    private const int ConnectTimeoutMs = 5000;
    private const int WriteTimeoutMs = 1000;   // A frame the kit's TCP stack has not taken by then means the link has stalled

    // The kit's TCP stack queues at most six received segments and refuses more until a timer retries
    // them, which stalls the link for 0.2-0.5 s. Frames written less than this far apart (each one
    // leaves as its own segment) are spaced out so the kit drains them; slower streams are not delayed.
    // Bursts of 200 frames stalled the link for seconds, or made the kit reset the connection, in 3 of 36
    // trials at 2 ms spacing on a busy channel, and in none of 32 at 4 ms or more.
    private const double MinFrameGapMs = 4.0;

    private TcpClient tcpClient;
    private Socket socket;
    private readonly string ipAddress;
    private readonly int port;
    private readonly Stopwatch sinceLastWrite = new Stopwatch();

    public bool IsOpen => socket != null && socket.Connected;

    public WiFiHandler(string ipAddress, int port)
    {
        this.ipAddress = ipAddress;
        this.port = port;
        Open();
    }

    private void Open()
    {
        try
        {
            tcpClient = new TcpClient { NoDelay = true, SendTimeout = WriteTimeoutMs };
            if (!tcpClient.ConnectAsync(ipAddress, port).Wait(ConnectTimeoutMs))
            {
                Debug.LogError($"[eTactileKit] WiFi connect to {ipAddress}:{port} timed out after {ConnectTimeoutMs} ms. " +
                               "Check the IP and that the kit is reachable; a kit connected to a USB serial host " +
                               "has its WiFi switched off.");
                tcpClient.Close();
                tcpClient = null;
                return;
            }
            socket = tcpClient.Client;
            Debug.Log($"[eTactileKit] WiFi socket {ipAddress}:{port} opened");
        }
        catch (Exception e)
        {
            // Wait(...) surfaces connect failures (e.g. refused) as an AggregateException.
            Exception reason = e is AggregateException ae && ae.InnerException != null ? ae.InnerException : e;
            if (reason is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // The kit only listens while no host owns it.
                Debug.LogError($"[eTactileKit] {ipAddress}:{port} refused the connection: the kit is in use by another host.");
            }
            else
            {
                Debug.LogError($"[eTactileKit] Failed to open WiFi socket {ipAddress}:{port}: {reason.Message}");
            }
            tcpClient?.Close();
            tcpClient = null;
            socket = null;
        }
    }

    public bool Write(byte[] data)
    {
        if (!IsOpen || data == null)
        {
            return false;
        }

        if (sinceLastWrite.IsRunning)
        {
            while (sinceLastWrite.Elapsed.TotalMilliseconds < MinFrameGapMs)
            {
                Thread.SpinWait(64);
            }
        }

        try
        {
            int sent = 0;
            while (sent < data.Length)
            {
                // SendTimeout does not bound a blocked send on every runtime (IL2CPP on Android keeps
                // retrying it), so wait for room in the send buffer explicitly.
                if (!socket.Poll(WriteTimeoutMs * 1000, SelectMode.SelectWrite))
                {
                    throw new TimeoutException($"the kit took no data for {WriteTimeoutMs} ms");
                }
                sent += socket.Send(data, sent, data.Length - sent, SocketFlags.None);
            }
            sinceLastWrite.Restart();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[eTactileKit] WiFi send to {ipAddress}:{port} failed ({e.Message}); resetting the " +
                             "connection so the kit ends the session and zeroes every amplitude.");
            Close(abort: true);
            return false;
        }
    }

    public byte[] Read(int length, int timeoutMs)
    {
        if (!IsOpen || length <= 0)
        {
            return Array.Empty<byte>();
        }

        byte[] buffer = new byte[length];
        int total = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (total < length)
            {
                long remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
                if (remainingMs <= 0 || !socket.Poll((int)Math.Min(remainingMs * 1000, int.MaxValue), SelectMode.SelectRead))
                {
                    break;   // Deadline reached
                }
                int read = socket.Receive(buffer, total, length - total, SocketFlags.None);
                if (read == 0)
                {
                    Debug.LogWarning($"[eTactileKit] The kit at {ipAddress}:{port} closed the connection.");
                    Close();
                    break;
                }
                total += read;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[eTactileKit] WiFi read from {ipAddress}:{port} failed ({e.Message}); closing the connection.");
            Close();
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
        if (!IsOpen)
        {
            return;
        }

        try
        {
            byte[] discard = new byte[256];
            while (socket.Available > 0)
            {
                socket.Receive(discard, Math.Min(socket.Available, discard.Length), SocketFlags.None);
            }
            // Readable with nothing to read: the kit has closed the connection.
            if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
            {
                Debug.LogWarning($"[eTactileKit] The kit at {ipAddress}:{port} closed the connection.");
                Close();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[eTactileKit] WiFi connection to {ipAddress}:{port} failed ({e.Message}); closing it.");
            Close();
        }
    }

    public void Close() => Close(abort: false);

    /// <summary>
    /// Close the connection. With <paramref name="abort"/>, reset it instead: frames still waiting in the
    /// send buffer are discarded, so the kit ends the session at once instead of running them first.
    /// </summary>
    private void Close(bool abort)
    {
        if (tcpClient == null)
        {
            return;
        }

        try
        {
            if (abort && socket != null)
            {
                // Linger 0 makes the close a reset. Close the socket itself: TcpClient.Close shuts the
                // connection down gracefully first, which would still deliver the backlog.
                try
                {
                    socket.LingerState = new LingerOption(true, 0);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[eTactileKit] Could not set a reset close on {ipAddress}:{port} ({e.Message}); closing normally.");
                }
                socket.Close();
            }
            tcpClient.Close();
            tcpClient.Dispose();
            Debug.Log($"[eTactileKit] WiFi socket {ipAddress}:{port} closed");
        }
        catch (Exception e)
        {
            Debug.LogError($"[eTactileKit] Failed to close WiFi socket {ipAddress}:{port}: {e.Message}");
        }
        finally
        {
            socket = null;
            tcpClient = null;
        }
    }
}
