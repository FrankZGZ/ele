using System;
using Debug = UnityEngine.Debug;

/// <summary>
/// Low-level driver for the eTactileKit ESP32 hardware. This is a C# port of the reference Python
/// API (5_APIs/1_Python_API/api/etactilekit.py) and speaks the exact same byte protocol. Use it from
/// one thread.
///
/// Communication model:
///   * The kit serves one host at a time: the first host to connect (serial port or TCP connection)
///     owns it, and the other link is refused until that host disconnects. <see cref="ConnectSerial"/>
///     and <see cref="ConnectWiFi"/> return true only once the kit has answered on that link. Every
///     connect and disconnect makes the kit zero every amplitude and the sense pulse height; the kit
///     keeps its other settings (electrode number, mode, polarity, pulse widths, discharge time,
///     frequency) across connections.
///   * <see cref="Configure"/> sets the kit up and verifies it. The kit does not acknowledge settings;
///     <see cref="SyncCheck"/> confirms the link and the electrode number.
///   * The kit scans every electrode once per period. With a scan that does not fit in the period it
///     scans back to back, below the frequency that was set, and it ends a scan after 260 ms, leaving
///     out the electrodes it has not reached. Settings are refused unless <see cref="EstimateScanUs"/>
///     fits within <see cref="MaxScanFraction"/> of the period and below <see cref="MaxScanUs"/>, so
///     the kit always stimulates every electrode at the frequency that was set.
///   * A stimulation "frame" is a single <see cref="SendStimPattern"/> call carrying one
///     little-endian uint16 intensity (0-4095, 12-bit DAC) per electrode. The kit reads as many
///     values as its electrode number, so a pattern is only sent when it, the electrode mapping and
///     the kit's electrode number all have the same length. There is no hardware concept of a frame
///     count - temporal patterns are produced by sending successive patterns over time.
///   * The electrode mapping remaps application electrode indices to hardware channels:
///     hardwareChannel[mapping[i]] = intensity[applicationElectrode i].
///   * A failed write closes the link, so the kit ends the session and zeroes every amplitude;
///     <see cref="IsConnected"/> then turns false.
/// </summary>
public class ETactileKit
{
    // Maximum intensity the 12-bit DAC can represent.
    public const int MaxIntensity = 4095;
    // The electrode number is sent as one byte.
    public const int MaxElectrodes = 255;
    // Default reply timeouts: serial replies take under 1 ms, WiFi ones up to tens of ms; the kit
    // answers the HV513 count after the stimulation scan in progress, which it waits for up to 500 ms.
    public const int DefaultReplyTimeoutMs = 100;
    public const int DefaultHv513TimeoutMs = 1000;
    // Configure's sync check: long enough to ride out a WiFi retransmission (0.2-0.5 s).
    public const int ConfigureReplyTimeoutMs = 1000;

    // Scan-time limits. EstimateScanUs sits just above the scan times measured on the hardware.
    public const double MaxScanFraction = 0.8;    // of the period
    public const int MaxScanUs = 250000;          // below the 260 ms at which the kit ends a scan
    private const int ScanOverheadUs = 80;        // per scan: interrupt entry and clearing the HV513 chain

    private const int LinkCheckTimeoutMs = 300;   // Per attempt of the connection check
    private const int LinkCheckAttempts = 3;
    private const int LateReplyTimeoutMs = 2000;  // A reply the kit owes that has not arrived by then means the link has failed

    // PC -> ESP32 command opcodes (see api/etactilekit.py).
    private const byte PC_ESP32_MEASURE_REQUEST        = 0xFF; // request voltage readings
    private const byte PC_ESP32_STIM_PATTERN           = 0xFE; // stimulation pattern, n x uint16 LE
    private const byte PC_ESP32_STIMULATION_POLARITY   = 0xFD; // 1 byte
    private const byte PC_ESP32_ELECTRODE_NUM          = 0xFC; // 1 byte
    private const byte PC_ESP32_STIMULATION_MODE       = 0xFB; // 1 byte
    private const byte PC_ESP32_STIMULATION_PULSE_WIDTH = 0xFA; // uint16 LE (us)
    private const byte PC_ESP32_SENSE_PULSE_HEIGHT     = 0xF9; // 1 byte
    private const byte PC_ESP32_SENSE_PULSE_WIDTH      = 0xF8; // 1 byte
    private const byte PC_ESP32_CHANNEL_DISCHARGE_TIME = 0xF7; // 1 byte (us)
    private const byte PC_ESP32_STIMULATION_FREQUENCY  = 0xF6; // uint16 LE (Hz)
    private const byte PC_ESP32_HV513_NUM_REQUEST      = 0xF5; // request HV513 module count
    private const byte PC_ESP32_SYNC_CHECK             = 0xF4; // read back configured electrode number

    private ICommHandler commHandler;

    private int numberOfElectrodes = -1;   // The kit's electrode number; -1 until it is known
    private int[] electrodeMapping;

    // Settings as this connection last sent them; null until sent. The scan time is checked with them.
    private StimulationMode? mode;
    private int? pulseWidthUs;
    private int? sensePulseWidthUs;
    private int? dischargeTimeUs;
    private int? frequencyHz;

    private int owedBytes;   // Reply bytes the kit still owes for a request that timed out

    // Pre-built stimulation packet reused across sends to avoid per-frame allocation.
    private byte[] stimPacket;

    private int[] voltages = Array.Empty<int>();

    public bool IsConnected => commHandler != null && commHandler.IsOpen;

    /// <summary>
    /// The kit's electrode number: reported by the kit on connect, then set by
    /// <see cref="SendElectrodeNumber"/>. -1 until the first connect.
    /// </summary>
    public int NumberOfElectrodes => numberOfElectrodes;

    /// <summary>The HV513 chip count from the last <see cref="UpdateAndGetHv513Count"/>; -1 if it was not answered.</summary>
    public int Hv513Count { get; private set; } = -1;

    public int[] Voltages => voltages;

    /// <summary>
    /// Conservative duration in microseconds of one stimulation scan with these settings: per electrode,
    /// phases x (max(width, 10) + 8) + discharge + 5 us, plus a fixed overhead per scan.
    /// </summary>
    public static int EstimateScanUs(int electrodeNumber, StimulationMode mode, int pulseWidthUs,
                                     int dischargeTimeUs, int sensePulseWidthUs = 0)
    {
        if (electrodeNumber <= 0)
        {
            return 0;
        }
        int width = mode == StimulationMode.ImpedanceOnly ? sensePulseWidthUs : pulseWidthUs;
        int phases = mode == StimulationMode.Biphasic ? 2 : 1;
        return ScanOverheadUs + electrodeNumber * (phases * (Math.Max(width, 10) + 8) + dischargeTimeUs + 5);
    }

    //--------------------------------------------------------------------------------------//
    // Connection lifecycle
    //--------------------------------------------------------------------------------------//
    /// <summary>
    /// Connect over USB serial (Windows Editor and Windows builds). Returns true once the kit has
    /// answered on this port; false if already connected, if the port could not be opened, or if the
    /// kit did not answer (it is connected to a WiFi host). The kit's USB port ignores
    /// <paramref name="baudRate"/>.
    /// </summary>
    public bool ConnectSerial(string portName, int baudRate)
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        if (string.IsNullOrWhiteSpace(portName))
        {
            Debug.LogError("[eTactileKit] No serial port name given.");
            return false;
        }
        return ReadyToConnect() &&
               ClaimLink(new SerialHandler(portName, baudRate), $"USB serial ({portName})",
                         "no answer: the kit is connected to a WiFi host, or is not running eTactileKit firmware");
#else
        Debug.LogError("[eTactileKit] USB serial is available in the Windows Editor and Windows builds only - use WiFi.");
        return false;
#endif
    }

    /// <summary>
    /// Connect over WiFi (TCP). Returns true once the kit has accepted the connection and answered;
    /// false if already connected or if the kit refused, could not be reached or did not answer.
    /// </summary>
    public bool ConnectWiFi(string ipAddress, int port)
        => ReadyToConnect() &&
           ClaimLink(new WiFiHandler(ipAddress, port), $"WiFi ({ipAddress}:{port})", "no answer from the kit");

    /// <summary>
    /// Turn every output off and close the link. The kit also zeroes every amplitude once it notices
    /// its host has gone, which takes 0.25 s after a serial port closes.
    /// </summary>
    public void Disconnect()
    {
        if (IsConnected)
        {
            if (numberOfElectrodes > 0)
            {
                byte[] zero = new byte[1 + numberOfElectrodes * 2];
                zero[0] = PC_ESP32_STIM_PATTERN;
                commHandler.Write(zero);
            }
            if (IsConnected)
            {
                commHandler.Write(new[] { PC_ESP32_SENSE_PULSE_HEIGHT, (byte)0 });
            }
        }
        if (commHandler != null)
        {
            commHandler.Close();
            commHandler = null;
        }
        owedBytes = 0;
    }

    /// <summary>False if a link is already open; otherwise drops a link that has failed.</summary>
    private bool ReadyToConnect()
    {
        if (IsConnected)
        {
            Debug.LogError("[eTactileKit] Already connected. Disconnect first before connecting again.");
            return false;
        }
        Disconnect();
        return true;
    }

    /// <summary>
    /// Keep <paramref name="handler"/> only if the kit answers on it: the firmware answers only the
    /// host that owns it. Two sync requests must bring back exactly two equal bytes (the kit's
    /// electrode number), so a byte left over from an earlier session cannot pass; the check is
    /// repeated on a cleared buffer. The electrode number the kit reports is adopted, because it
    /// frames stimulation patterns by it; the other settings are unknown until sent.
    /// </summary>
    private bool ClaimLink(ICommHandler handler, string label, string refusedReason)
    {
        if (!handler.IsOpen)
        {
            handler.Close();   // The transport has logged why it could not open
            return false;
        }

        for (int attempt = 0; attempt < LinkCheckAttempts && handler.IsOpen; attempt++)
        {
            handler.ClearInputBuffer();
            if (!handler.Write(new[] { PC_ESP32_SYNC_CHECK, PC_ESP32_SYNC_CHECK }))
            {
                break;
            }
            byte[] reply = handler.Read(2, LinkCheckTimeoutMs);
            if (reply.Length == 2 && reply[0] == reply[1] && handler.Read(64, 20).Length == 0 && handler.IsOpen)
            {
                if (numberOfElectrodes >= 0 && numberOfElectrodes != reply[0])
                {
                    Debug.LogWarning($"[eTactileKit] The kit is set to {reply[0]} electrodes, not {numberOfElectrodes}; " +
                                     "run Configure again before sending patterns.");
                }
                numberOfElectrodes = reply[0];
                mode = null;
                pulseWidthUs = sensePulseWidthUs = dischargeTimeUs = frequencyHz = null;
                owedBytes = 0;
                commHandler = handler;
                Debug.Log($"[eTactileKit] Connected to eTactileKit over {label}.");
                return true;
            }
        }

        Debug.LogError($"[eTactileKit] {label}: {refusedReason}.");
        handler.Close();
        return false;
    }

    //--------------------------------------------------------------------------------------//
    // Mapping (must be set before sending patterns)
    //--------------------------------------------------------------------------------------//
    /// <summary>
    /// Define the application-electrode -> hardware-channel mapping. Index i is the application
    /// electrode; <paramref name="mapping"/>[i] is the hardware channel that electrode drives. Each
    /// hardware index 0..n-1 must appear exactly once. Also (re)allocates the reusable stimulation
    /// packet buffer. Returns false (and keeps the previous mapping) if the mapping is invalid.
    /// </summary>
    public bool SetElectrodeMapping(int[] mapping)
    {
        if (mapping == null || mapping.Length == 0 || mapping.Length > MaxElectrodes)
        {
            Debug.LogError($"[eTactileKit] SetElectrodeMapping needs 1 to {MaxElectrodes} entries.");
            return false;
        }

        var seen = new bool[mapping.Length];
        foreach (int channel in mapping)
        {
            if (channel < 0 || channel >= mapping.Length || seen[channel])
            {
                Debug.LogError("[eTactileKit] The electrode mapping must list each hardware index 0..n-1 exactly once.");
                return false;
            }
            seen[channel] = true;
        }

        electrodeMapping = (int[])mapping.Clone();
        stimPacket = new byte[1 + electrodeMapping.Length * 2];
        stimPacket[0] = PC_ESP32_STIM_PATTERN;
        return true;
    }

    //--------------------------------------------------------------------------------------//
    // Setup
    //--------------------------------------------------------------------------------------//
    /// <summary>
    /// Set the kit up for stimulation and verify it. Each setting takes effect while the kit keeps
    /// scanning at its current (possibly a previous host's) period, so the electrode number goes to 0
    /// first and to its real value (the mapping's length) last: no intermediate state can overrun. Then
    /// the sync check must report the electrode number, and the HV513 count is read into
    /// <see cref="Hv513Count"/> (a warning if it gives fewer outputs than electrodes). Returns false,
    /// with the reason logged, if a value is out of range or the scan would not fit in the period
    /// (nothing is sent), or, after disconnecting, if the link failed or the sync check did not match.
    /// </summary>
    public bool Configure(int[] electrodeMapping, StimulationMode stimulationMode, StimulationPolarity polarity,
                          int pulseWidth, int frequency, int dischargeTime, int sensePulseHeight, int sensePulseWidth)
    {
        if (!IsConnected)
        {
            Debug.LogError("[eTactileKit] Configure called while not connected.");
            return false;
        }
        int n = electrodeMapping?.Length ?? 0;
        if (!Enum.IsDefined(typeof(StimulationMode), stimulationMode) ||
            !Enum.IsDefined(typeof(StimulationPolarity), polarity))
        {
            Debug.LogError("[eTactileKit] Configure: unknown stimulation mode or polarity; nothing sent.");
            return false;
        }
        if (!InRange(pulseWidth, 0, ushort.MaxValue, "pulse width (us)") ||
            !InRange(frequency, 1, ushort.MaxValue, "frequency (Hz)") ||
            !InRange(dischargeTime, 0, byte.MaxValue, "channel discharge time (us)") ||
            !InRange(sensePulseHeight, 0, byte.MaxValue, "sense pulse height") ||
            !InRange(sensePulseWidth, 0, byte.MaxValue, "sense pulse width") ||
            !ScanFits(n, stimulationMode, pulseWidth, dischargeTime, sensePulseWidth, frequency) ||
            !SetElectrodeMapping(electrodeMapping))
        {
            return false;
        }

        bool sent = SendElectrodeNumber(0)
                    && SendStimulationFrequency(frequency)
                    && SendStimulationMode(stimulationMode)
                    && SendStimulationPolarity(polarity)
                    && SendStimulationPulseWidth(pulseWidth)
                    && SendSensePulseHeight(sensePulseHeight)
                    && SendSensePulseWidth(sensePulseWidth)
                    && SendChannelDischargeTime(dischargeTime)
                    && SendElectrodeNumber(n);
        if (!sent)
        {
            return AbortConfigure(!IsConnected ? "the link failed" : "a setting was refused (see the error above)");
        }

        int sync = SyncCheck(ConfigureReplyTimeoutMs);
        if (sync != n)
        {
            return AbortConfigure(!IsConnected ? "the link failed" :
                $"the kit reports {(sync < 0 ? "nothing" : sync.ToString())} instead of {n} electrodes");
        }

        int hv513 = UpdateAndGetHv513Count();
        if (!IsConnected)
        {
            return AbortConfigure("the link failed");
        }
        if (hv513 >= 0 && hv513 * 8 < n)
        {
            Debug.LogWarning($"[eTactileKit] The kit detects {hv513} HV513 chips ({hv513 * 8} outputs), fewer than " +
                             $"{n} electrodes.");
        }
        return true;
    }

    private bool AbortConfigure(string reason)
    {
        Debug.LogError($"[eTactileKit] Setup failed: {reason}. Disconnected.");
        Disconnect();
        return false;
    }

    //--------------------------------------------------------------------------------------//
    // Settings. Each returns true once sent; false if the value is out of range (logged, never
    // wrapped), the scan would not fit in the period, or the link is not open or failed.
    //--------------------------------------------------------------------------------------//
    public bool SendElectrodeNumber(int electrodeNumber)
    {
        if (!InRange(electrodeNumber, 0, MaxElectrodes, "electrode number") ||
            !ScanFitsWith(electrodeNumber: electrodeNumber) ||
            !Write(new[] { PC_ESP32_ELECTRODE_NUM, (byte)electrodeNumber }))
        {
            return false;
        }
        numberOfElectrodes = electrodeNumber;
        return true;
    }

    public bool SendStimulationMode(StimulationMode stimulationMode)
    {
        if (!Enum.IsDefined(typeof(StimulationMode), stimulationMode))
        {
            Debug.LogError($"[eTactileKit] Unknown stimulation mode {(int)stimulationMode}; not sent.");
            return false;
        }
        if (!ScanFitsWith(stimulationMode: stimulationMode) ||
            !Write(new[] { PC_ESP32_STIMULATION_MODE, (byte)stimulationMode }))
        {
            return false;
        }
        mode = stimulationMode;
        return true;
    }

    public bool SendStimulationPolarity(StimulationPolarity polarity)
    {
        if (!Enum.IsDefined(typeof(StimulationPolarity), polarity))
        {
            Debug.LogError($"[eTactileKit] Unknown stimulation polarity {(int)polarity}; not sent.");
            return false;
        }
        return Write(new[] { PC_ESP32_STIMULATION_POLARITY, (byte)polarity });
    }

    public bool SendStimulationPulseWidth(int pulseWidth)
    {
        if (!InRange(pulseWidth, 0, ushort.MaxValue, "pulse width (us)") ||
            !ScanFitsWith(pulseWidth: pulseWidth) ||
            !Write(new[] { PC_ESP32_STIMULATION_PULSE_WIDTH, Low(pulseWidth), High(pulseWidth) }))
        {
            return false;
        }
        pulseWidthUs = pulseWidth;
        return true;
    }

    /// <summary>Sense pulse height, 0-255 of the 12-bit DAC range, used in Impedance Only mode.</summary>
    public bool SendSensePulseHeight(int pulseHeight)
        => InRange(pulseHeight, 0, byte.MaxValue, "sense pulse height") &&
           Write(new[] { PC_ESP32_SENSE_PULSE_HEIGHT, (byte)pulseHeight });

    /// <summary>Sense pulse width in us (0-255), used in Impedance Only mode.</summary>
    public bool SendSensePulseWidth(int pulseWidth)
    {
        if (!InRange(pulseWidth, 0, byte.MaxValue, "sense pulse width") ||
            !ScanFitsWith(sensePulseWidth: pulseWidth) ||
            !Write(new[] { PC_ESP32_SENSE_PULSE_WIDTH, (byte)pulseWidth }))
        {
            return false;
        }
        sensePulseWidthUs = pulseWidth;
        return true;
    }

    public bool SendChannelDischargeTime(int dischargeTime)
    {
        if (!InRange(dischargeTime, 0, byte.MaxValue, "channel discharge time (us)") ||
            !ScanFitsWith(dischargeTime: dischargeTime) ||
            !Write(new[] { PC_ESP32_CHANNEL_DISCHARGE_TIME, (byte)dischargeTime }))
        {
            return false;
        }
        dischargeTimeUs = dischargeTime;
        return true;
    }

    /// <summary>Stimulation frequency in Hz, 1-65535.</summary>
    public bool SendStimulationFrequency(int frequency)
    {
        if (!InRange(frequency, 1, ushort.MaxValue, "frequency (Hz)") ||
            !ScanFitsWith(frequency: frequency) ||
            !Write(new[] { PC_ESP32_STIMULATION_FREQUENCY, Low(frequency), High(frequency) }))
        {
            return false;
        }
        frequencyHz = frequency;
        return true;
    }

    /// <summary>
    /// The scan-time check with one setting changed and the others as this connection last sent them.
    /// With 0 electrodes every setting fits. While the kit scans electrodes, a setting that changes the
    /// scan time can only be checked once this connection has sent every setting the scan time depends
    /// on.
    /// </summary>
    private bool ScanFitsWith(int? electrodeNumber = null, StimulationMode? stimulationMode = null, int? pulseWidth = null,
                              int? dischargeTime = null, int? sensePulseWidth = null, int? frequency = null)
    {
        int n = electrodeNumber ?? numberOfElectrodes;
        if (n <= 0)
        {
            return true;
        }
        StimulationMode? m = stimulationMode ?? mode;
        int? width = m == StimulationMode.ImpedanceOnly ? sensePulseWidth ?? sensePulseWidthUs : pulseWidth ?? pulseWidthUs;
        int? discharge = dischargeTime ?? dischargeTimeUs;
        int? f = frequency ?? frequencyHz;
        if (m == null || width == null || discharge == null || f == null)
        {
            var unknown = new System.Collections.Generic.List<string>();
            if (m == null) unknown.Add("mode");
            if (width == null) unknown.Add(m == StimulationMode.ImpedanceOnly ? "sense pulse width" : "pulse width");
            if (discharge == null) unknown.Add("discharge time");
            if (f == null) unknown.Add("frequency");
            Debug.LogError($"[eTactileKit] Not sent: the kit scans {n} electrodes and this connection has not set its " +
                           $"{string.Join(", ", unknown)}, so the scan time cannot be checked. Send electrode number 0 " +
                           "first, then the settings, then the electrode number - Configure does this.");
            return false;
        }
        int pw = m == StimulationMode.ImpedanceOnly ? 0 : width.Value;
        int spw = m == StimulationMode.ImpedanceOnly ? width.Value : 0;
        return ScanFits(n, m.Value, pw, discharge.Value, spw, f.Value);
    }

    private static bool ScanFits(int n, StimulationMode stimulationMode, int pulseWidth, int dischargeTime,
                                 int sensePulseWidth, int frequency)
    {
        int scanUs = EstimateScanUs(n, stimulationMode, pulseWidth, dischargeTime, sensePulseWidth);
        int periodUs = 1000000 / frequency;
        double limitUs = Math.Min(MaxScanFraction * periodUs, MaxScanUs);
        if (scanUs <= limitUs)
        {
            return true;
        }
        string fix = scanUs > MaxScanUs
            ? "Lower the electrode count, the pulse width or the discharge time."
            : $"Lower the frequency to {(int)(1000000 / Math.Ceiling(scanUs / MaxScanFraction))} Hz or less, or the " +
              "electrode count, the pulse width or the discharge time.";
        Debug.LogError($"[eTactileKit] Not sent: one scan of {n} electrodes would take about {scanUs / 1000.0:F1} ms, more " +
                       $"than {limitUs / 1000.0:F1} ms ({MaxScanFraction:P0} of the {periodUs / 1000.0:F1} ms period at " +
                       $"{frequency} Hz, at most {MaxScanUs / 1000} ms). A scan that reaches its period makes the kit stimulate below the set " +
                       $"frequency or leave electrodes out. {fix}");
        return false;
    }

    //--------------------------------------------------------------------------------------//
    // Stimulation
    //--------------------------------------------------------------------------------------//
    /// <summary>
    /// Send one stimulation frame. <paramref name="intensities"/> holds one value per application
    /// electrode (in electrode-id order). Values are clamped to [0, 4095], remapped to hardware
    /// channels, packed little-endian and written in a single packet. Returns true once sent; false
    /// if nothing was sent because the pattern or the mapping does not match the kit's electrode
    /// number, or if the link failed (it is then closed and the kit zeroes every amplitude).
    /// </summary>
    public bool SendStimPattern(int[] intensities)
    {
        if (!IsConnected)
        {
            return false;
        }
        if (electrodeMapping == null || stimPacket == null)
        {
            Debug.LogError("[eTactileKit] SendStimPattern called before SetElectrodeMapping.");
            return false;
        }
        if (electrodeMapping.Length != numberOfElectrodes)
        {
            Debug.LogError($"[eTactileKit] The electrode mapping has {electrodeMapping.Length} entries but the kit " +
                           $"is set to {numberOfElectrodes} electrodes; send the electrode number first.");
            return false;
        }
        if (intensities == null || intensities.Length != numberOfElectrodes)
        {
            Debug.LogError($"[eTactileKit] Stimulation pattern length must equal electrode count " +
                           $"({numberOfElectrodes}). Received: {(intensities?.Length ?? 0)}");
            return false;
        }

        // The mapping is a permutation, so every hardware channel is written exactly once.
        for (int ch = 0; ch < numberOfElectrodes; ch++)
        {
            int value = intensities[ch];
            if (value < 0) value = 0;
            else if (value > MaxIntensity) value = MaxIntensity;

            int byteIndex = 1 + electrodeMapping[ch] * 2;
            stimPacket[byteIndex] = (byte)(value & 0xFF);
            stimPacket[byteIndex + 1] = (byte)((value >> 8) & 0xFF);
        }

        return commHandler.Write(stimPacket);
    }

    //--------------------------------------------------------------------------------------//
    // Measurement / verification
    //--------------------------------------------------------------------------------------//
    /// <summary>
    /// Request voltage readings for all electrodes (12-bit ADC values, application-electrode order).
    /// Returns null if the kit did not answer within <paramref name="timeoutMs"/> or the electrode
    /// mapping does not match the kit's electrode number.
    /// </summary>
    public int[] GetVoltageReadings(int timeoutMs = DefaultReplyTimeoutMs)
    {
        if (!IsConnected)
        {
            return null;
        }
        if (electrodeMapping == null || electrodeMapping.Length != numberOfElectrodes)
        {
            Debug.LogError("[eTactileKit] The electrode mapping must match the kit's electrode number " +
                           $"({numberOfElectrodes}) to read voltages.");
            return null;
        }

        byte[] raw = Request(PC_ESP32_MEASURE_REQUEST, numberOfElectrodes * 2, timeoutMs);
        if (raw.Length != numberOfElectrodes * 2)
        {
            Debug.LogWarning($"[eTactileKit] Voltage read expected {numberOfElectrodes * 2} bytes, got {raw.Length}");
            return null;
        }

        int[] hardware = new int[numberOfElectrodes];
        for (int i = 0; i < numberOfElectrodes; i++)
        {
            hardware[i] = raw[i * 2] | (raw[i * 2 + 1] << 8);
        }

        // Reverse the mapping so the caller gets values in application-electrode order.
        voltages = new int[numberOfElectrodes];
        for (int i = 0; i < numberOfElectrodes; i++)
        {
            voltages[i] = hardware[electrodeMapping[i]];
        }
        return voltages;
    }

    /// <summary>
    /// Query the number of HV513 driver chips (8 outputs each) the kit detects. Each stacked
    /// switching module adds 8 (standard board) or 4 (compact board). The kit finishes the
    /// stimulation scan in progress, pauses stimulation while it senses, then answers; if that scan
    /// does not end within 500 ms, it answers the last count it sensed without sensing again.
    /// Returns -1 if the kit did not answer within <paramref name="timeoutMs"/>.
    /// </summary>
    public int UpdateAndGetHv513Count(int timeoutMs = DefaultHv513TimeoutMs)
    {
        byte[] raw = Request(PC_ESP32_HV513_NUM_REQUEST, 1, timeoutMs);
        if (raw.Length != 1)
        {
            if (IsConnected)
            {
                Debug.LogWarning("[eTactileKit] No answer to the HV513 count request.");
            }
            Hv513Count = -1;
            return -1;
        }
        Hv513Count = raw[0];
        return raw[0];
    }

    /// <summary>Read back the kit's electrode number to verify the link. Returns -1 if it did not answer.</summary>
    public int SyncCheck(int timeoutMs = DefaultReplyTimeoutMs)
    {
        byte[] raw = Request(PC_ESP32_SYNC_CHECK, 1, timeoutMs);
        return raw.Length == 1 ? raw[0] : -1;
    }

    /// <summary>
    /// Send a one-byte request and read its reply (shorter on timeout or link failure). The kit answers
    /// every request in order, so a reply that arrives late would be read as the answer to the next
    /// request: the bytes it still owes are read and discarded before the next request is sent. A reply
    /// still missing after <see cref="LateReplyTimeoutMs"/> means the link has failed, and it is closed.
    /// </summary>
    private byte[] Request(byte command, int replyLength, int timeoutMs)
    {
        if (!IsConnected)
        {
            return Array.Empty<byte>();
        }
        if (owedBytes > 0)
        {
            int owed = owedBytes;
            owedBytes = 0;
            if (commHandler.Read(owed, LateReplyTimeoutMs).Length < owed)
            {
                if (IsConnected)
                {
                    Debug.LogWarning($"[eTactileKit] A reply the kit owes did not arrive within {LateReplyTimeoutMs} ms; " +
                                     "closing the link.");
                    commHandler.Close();
                }
                return Array.Empty<byte>();
            }
        }
        commHandler.ClearInputBuffer();
        if (!Write(new[] { command }))
        {
            return Array.Empty<byte>();
        }
        byte[] raw = commHandler.Read(replyLength, timeoutMs);
        owedBytes = IsConnected ? replyLength - raw.Length : 0;
        return raw;
    }

    //--------------------------------------------------------------------------------------//
    // Helpers
    //--------------------------------------------------------------------------------------//
    private bool Write(byte[] data) => IsConnected && commHandler.Write(data);

    private static bool InRange(int value, int min, int max, string what)
    {
        if (value >= min && value <= max)
        {
            return true;
        }
        Debug.LogError($"[eTactileKit] The {what} must be {min}-{max} (got {value}); not sent.");
        return false;
    }

    private static byte Low(int value) => (byte)(value & 0xFF);
    private static byte High(int value) => (byte)((value >> 8) & 0xFF);
}
