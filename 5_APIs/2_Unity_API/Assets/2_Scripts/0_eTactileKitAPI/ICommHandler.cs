// Transport abstraction for the eTactileKit. Implemented by SerialHandler and WiFiHandler.
public interface ICommHandler
{
    bool IsOpen { get; }

    /// <summary>
    /// Send one fully built frame. Returns false if the link is not open, or if the frame could not be
    /// sent whole; the link is then closed, so the kit ends the session and zeroes every amplitude.
    /// </summary>
    bool Write(byte[] data);

    /// <summary>
    /// Read exactly <paramref name="length"/> bytes, or fewer on timeout or link failure. A failed link,
    /// or one the kit has closed, is closed (<see cref="IsOpen"/> turns false).
    /// </summary>
    byte[] Read(int length, int timeoutMs);

    /// <summary>
    /// Discard any pending inbound bytes before issuing a request. A failed link, or one the kit has
    /// closed, is closed.
    /// </summary>
    void ClearInputBuffer();

    void Close();
}
