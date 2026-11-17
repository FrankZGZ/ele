using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Scene-level owner of the eTactileKit hardware. Holds the connection settings, the global
/// stimulation parameters (set once at setup) and the active calibration profile, and exposes a
/// simple <see cref="SendFrame"/> entry point that the haptics layer uses to drive the hardware.
///
/// Calibration supplies the per-electrode hardware mapping and the per-electrode active intensity,
/// and also defines the electrode count - nothing here hard-codes it. A "frame" is the set of
/// electrode ids that are ON; when sent, those electrodes are driven at their calibrated intensity
/// (all others = 0), the array is remapped to hardware channels by <see cref="ETactileKit"/>, and
/// transmitted in one packet.
///
/// <see cref="AmplitudeGain"/> is a global multiplier applied to every calibrated intensity at send
/// time (1 = as calibrated). The scaled value is rounded to the nearest integer and clamped to the
/// hardware's 12-bit DAC range, so the per-electrode balance from calibration is preserved while the
/// overall strength can be adjusted in one place - live, without reconnecting.
/// </summary>
public class ETactileKitManager : MonoBehaviour
{
    /// <summary>Bounds of the global amplitude gain slider.</summary>
    public const float MinAmplitudeGain = 0f;
    public const float MaxAmplitudeGain = 1.5f;

    private const int ConnectionTestCycles = 10;   // on/off cycles of 0.5 s each

    [Header("Connection")]
    [SerializeField] private ConnectionType connectionType = ConnectionType.Serial;
    [SerializeField] private string portName = "COM12";
    [Tooltip("The kit's USB port ignores the baud rate; any value works.")]
    [SerializeField] private int baudRate = 921600;
    [Tooltip("WiFi: manual board IP. Used when Use Discovery is off, or when no kit answers " +
             "discovery and no Target Device Id is set (AP mode is always 192.168.4.1).")]
    [SerializeField] private string ipAddress = "192.168.4.1";
    [SerializeField] private int wifiPort = 8080;

    [Header("WiFi Discovery (STA mode / multiple kits)")]
    [Tooltip("Find the board automatically by UDP broadcast instead of a hard-coded IP. " +
             "Recommended when kits share one router. Unity cannot resolve mDNS (.local), so " +
             "this is how a specific kit is located in station mode.")]
    [SerializeField] private bool useDiscovery = false;
    [Tooltip("Connect to the kit with this unique 6-hex ID (on its label, the Console log, or " +
             "'python -m api.discovery'). Leave blank when only one kit answers; with several, " +
             "connecting is refused until an ID is set.")]
    [SerializeField] private string targetDeviceId = "";
    [Tooltip("How long to wait for kits to answer discovery, in milliseconds.")]
    [SerializeField] private int discoveryTimeoutMs = 1000;

    [Header("Global Stimulation Parameters (set once at setup)")]
    [Tooltip("One scan of every electrode must fit in 80% of the stimulation period (and under 250 ms): " +
             "setup is refused otherwise, which keeps every scan clear of its period.")]
    [SerializeField] private StimulationMode stimulationMode = StimulationMode.Monophasic;
    [SerializeField] private StimulationPolarity polarity = StimulationPolarity.Anodic;
    [Tooltip("Stimulation pulse width in microseconds (0-65535).")]
    [SerializeField] private int pulseWidthUs = 100;
    [Tooltip("Stimulation frequency in Hz (1-65535).")]
    [SerializeField] private int frequencyHz = 50;
    [Tooltip("Channel discharge time in microseconds.")]
    [Range(0, 255)]
    [SerializeField] private int channelDischargeTimeUs = 50;
    [Tooltip("Height of the sense pulse (0-255 of the DAC range) the kit sends in Impedance Only mode " +
             "to measure electrode voltages. Keep it below the sensation threshold.")]
    [Range(0, 255)]
    [SerializeField] private int sensePulseHeight = 0;
    [Tooltip("Width of the sense pulse, in microseconds, used in Impedance Only mode.")]
    [Range(0, 255)]
    [SerializeField] private int sensePulseWidth = 0;

    [Header("Calibration")]
    [Tooltip("Calibration file inside StreamingAssets, loaded on Start. It takes precedence over the " +
             "profile stored in the scene, which is used only when the file is missing or invalid. " +
             "Leave empty to use the stored profile. Generate the file with the eTactileKitExplorer " +
             "desktop application.")]
    [SerializeField] private string defaultCalibrationFileName = "template_32_electrode_calibration.json";
    [SerializeField] private CalibrationProfile activeProfile = new CalibrationProfile();

    [Header("Amplitude")]
    [Tooltip("Global gain applied to every electrode's calibrated intensity before it is sent to the " +
             "hardware. 1 = use the calibration values as-is; below 1 attenuates, above 1 boosts. " +
             "Scaled values are rounded to the nearest integer and clamped to the 12-bit DAC range. " +
             "Takes effect immediately, including while playing: a frame that is on is sent again.")]
    [Range(MinAmplitudeGain, MaxAmplitudeGain)]
    [SerializeField] private float amplitudeGain = 1f;

    [Header("Testing")]
    [Tooltip("After connecting, pulse all electrodes on/off every 0.5 s for 10 cycles, to verify the " +
             "hardware link.")]
    [SerializeField] private bool runConnectionTest = false;

    private ETactileKit etk;
    private int[] cachedIntensities;    // per-electrode active intensity, indexed by electrode id
    private int[] frameIntensityBuffer; // reused scratch buffer for SendFrame / SendOff
    private bool isTesting;
    private int[] heldIds;              // electrode ids of the frame that is on, null when all are off
    private float heldGain;             // gain that frame was sent with
    private StimulationMode setUpMode;  // mode the kit was set up with

    public bool IsConnected => etk != null && etk.IsConnected;

    /// <summary>
    /// Electrode count: while connected, the count the kit was set up with; otherwise the active
    /// profile's.
    /// </summary>
    public int ElectrodeCount => IsConnected && cachedIntensities != null
        ? cachedIntensities.Length
        : activeProfile != null ? activeProfile.ElectrodeCount : 0;

    public CalibrationProfile ActiveProfile => activeProfile;

    /// <summary>
    /// Global gain applied to every electrode's calibrated intensity before it is sent (1 = as
    /// calibrated). Clamped to [<see cref="MinAmplitudeGain"/>, <see cref="MaxAmplitudeGain"/>].
    /// A frame that is on is sent again with the new gain - no reconnect or re-setup needed.
    /// </summary>
    public float AmplitudeGain
    {
        get => amplitudeGain;
        set => amplitudeGain = Mathf.Clamp(value, MinAmplitudeGain, MaxAmplitudeGain);
    }

    // Keeps the two-byte settings inside the ranges the kit accepts (the one-byte ones use [Range]).
    private void OnValidate()
    {
        pulseWidthUs = Mathf.Clamp(pulseWidthUs, 0, ushort.MaxValue);
        frequencyHz = Mathf.Clamp(frequencyHz, 1, ushort.MaxValue);
    }

    private void Start() => StartCoroutine(StartupRoutine());

    private IEnumerator StartupRoutine()
    {
        yield return LoadDefaultCalibrationRoutine();
        if (!isActiveAndEnabled)
        {
            yield break;   // Disabled while the calibration loaded
        }
        ConnectDevice();

        if (runConnectionTest && IsConnected)
        {
            StartCoroutine(RunConnectionTest());
        }
    }

    private void OnApplicationQuit() => DisconnectDevice();

    // The kit keeps running the last pattern it was sent, and the haptics layer sends only on
    // changes, so stimulation is turned off whenever the app stops updating: the headset is taken
    // off or put to sleep, or play mode is paused in the Editor.
    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            SendOff();
        }
    }

    private void LateUpdate()
    {
#if UNITY_EDITOR
        // Step runs single frames while play mode stays paused; each one ends with the output off.
        if (UnityEditor.EditorApplication.isPaused)
        {
            SendOff();
            return;
        }
#endif
        if (heldIds != null && !Mathf.Approximately(heldGain, amplitudeGain))
        {
            SendFrame(heldIds);
        }
    }

#if UNITY_EDITOR
    private void OnEnable() => UnityEditor.EditorApplication.pauseStateChanged += OnEditorPauseStateChanged;

    private void OnEditorPauseStateChanged(UnityEditor.PauseState state)
    {
        if (state == UnityEditor.PauseState.Paused)
        {
            SendOff();
        }
    }
#endif

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.pauseStateChanged -= OnEditorPauseStateChanged;
#endif
        DisconnectDevice();
    }

    //--------------------------------------------------------------------------------------//
    // Calibration
    //--------------------------------------------------------------------------------------//
    /// <summary>
    /// Load the calibration file from StreamingAssets when a file name is set; the profile stored in
    /// the scene is used when no name is set or the file is missing or invalid. Logs which one is
    /// used. On Android, StreamingAssets lives inside the compressed APK, so it is read with
    /// <see cref="UnityWebRequest"/>; on desktop it is read directly from disk. Generate the file
    /// with the eTactileKitExplorer desktop application.
    /// </summary>
    private IEnumerator LoadDefaultCalibrationRoutine()
    {
        if (string.IsNullOrEmpty(defaultCalibrationFileName))
        {
            LogStoredProfileUse("no calibration file name is set");
            yield break;
        }

        string path = Path.Combine(Application.streamingAssetsPath, defaultCalibrationFileName);
        string json = null;
        string failure = null;

#if UNITY_ANDROID && !UNITY_EDITOR
        using (UnityWebRequest request = UnityWebRequest.Get(path))
        {
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
            }
            else
            {
                failure = request.error;
            }
        }
#else
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            failure = ex.Message;
        }
#endif

        CalibrationProfile profile = null;
        if (json != null)
        {
            profile = ParseProfile(json, out failure);
            string error = null;
            if (profile != null && !profile.Validate(out error))
            {
                failure = error;
                profile = null;
            }
        }

        if (profile == null)
        {
            Debug.LogError($"[eTactileKit] Could not use the calibration file '{path}': {failure}");
            LogStoredProfileUse("the calibration file could not be used");
            yield break;
        }
        if (activeProfile != null && activeProfile.Validate(out _) && !SameCalibration(activeProfile, profile))
        {
            Debug.LogWarning($"[eTactileKit] The calibration file '{path}' replaces a different profile stored in the " +
                             "scene. Clear Default Calibration File Name to use the stored profile.");
        }
        ApplyImportedProfile(profile);
        Debug.Log($"[eTactileKit] Calibration loaded from '{path}' ({profile.ElectrodeCount} electrodes).");
    }

    private static bool SameCalibration(CalibrationProfile a, CalibrationProfile b)
    {
        if (a.ElectrodeCount != b.ElectrodeCount)
        {
            return false;
        }
        int[] mapA = a.BuildMapping(), mapB = b.BuildMapping();
        int[] intA = a.BuildIntensities(), intB = b.BuildIntensities();
        for (int i = 0; i < mapA.Length; i++)
        {
            if (mapA[i] != mapB[i] || intA[i] != intB[i])
            {
                return false;
            }
        }
        return true;
    }

    private void LogStoredProfileUse(string why)
    {
        if (activeProfile != null && activeProfile.Validate(out _))
        {
            Debug.Log($"[eTactileKit] Using the calibration profile stored in the scene ({why}).");
        }
        else
        {
            Debug.LogError($"[eTactileKit] No usable calibration: {why}, and the scene stores no valid profile. " +
                           "Place a profile in StreamingAssets or import one in the inspector.");
        }
    }

    /// <summary>Parse a calibration JSON. Returns null, with the reason, if it is not valid JSON.</summary>
    public static CalibrationProfile ParseProfile(string json, out string error)
    {
        try
        {
            CalibrationProfile profile = CalibrationProfile.FromJson(json);
            error = profile == null ? "the file is empty" : null;
            return profile;
        }
        catch (ArgumentException ex)
        {
            error = $"not valid JSON ({ex.Message})";
            return null;
        }
    }

    /// <summary>Replace the active profile (used by the inspector import and default loading).</summary>
    public void ApplyImportedProfile(CalibrationProfile profile)
    {
        if (profile == null)
        {
            Debug.LogError("[eTactileKit] Tried to apply a null calibration profile.");
            return;
        }
        if (!profile.Validate(out string error))
        {
            Debug.LogError($"[eTactileKit] Calibration profile rejected: {error}");
            return;
        }
        activeProfile = profile;
        if (IsConnected)
        {
            // Stop the connection test and turn the electrodes off while the buffers still match the
            // kit's electrode number, then set the kit up again for the new profile.
            isTesting = false;
            SendOff();
            SetupDevice();
        }
    }

    //--------------------------------------------------------------------------------------//
    // Connection lifecycle
    //--------------------------------------------------------------------------------------//
    public void ConnectDevice()
    {
        if (IsConnected)
        {
            return;
        }
        string error = "No calibration profile assigned.";
        if (activeProfile == null || !activeProfile.Validate(out error))
        {
            Debug.LogError($"[eTactileKit] Cannot connect without a valid calibration profile. {error}");
            return;
        }

#if !UNITY_EDITOR_WIN && !UNITY_STANDALONE_WIN
        // USB serial is available in the Windows Editor and Windows builds only; on the headset (and
        // other targets) the kit is reached over WiFi. Force WiFi so a scene left on Serial still connects.
        if (connectionType == ConnectionType.Serial)
        {
            Debug.LogWarning("[eTactileKit] USB serial is unavailable on this platform - using WiFi.");
            connectionType = ConnectionType.WiFi;
        }
#endif

        etk = new ETactileKit();
        bool connected;
        if (connectionType == ConnectionType.Serial)
        {
            connected = etk.ConnectSerial(portName, baudRate);
        }
        else
        {
            string host = ResolveWiFiHost(out int resolvedPort);
            connected = host != null && etk.ConnectWiFi(host, resolvedPort);
        }

        if (!connected)
        {
            Debug.LogError("[eTactileKit] Connection failed - check the device and settings.");
            etk = null;
            return;
        }

        SetupDevice();
    }

    /// <summary>
    /// Choose the WiFi host to connect to. With discovery off, the manually configured
    /// <see cref="ipAddress"/>. With discovery on, the kit matching <see cref="targetDeviceId"/>, or
    /// the only kit that answers; the manual IP (also the AP-mode address 192.168.4.1) is tried only
    /// when no kit answers and no ID is set. Returns null, with the reason logged, when several kits
    /// answer and no ID is set, when the requested ID does not answer, or when the chosen kit is in
    /// use by another WiFi host, so a kit someone else is using is never driven by mistake. Unity/.NET cannot
    /// resolve mDNS, so discovery is how a specific kit is located in station mode.
    /// </summary>
    private string ResolveWiFiHost(out int resolvedPort)
    {
        resolvedPort = wifiPort;
        if (!useDiscovery)
        {
            return ipAddress;
        }

        List<ETactileKitDiscovery.DiscoveredDevice> devices =
            ETactileKitDiscovery.Discover(discoveryTimeoutMs);

        if (devices.Count == 0)
        {
            const string why = "(a kit connected over USB serial has its WiFi switched off)";
            if (!string.IsNullOrEmpty(targetDeviceId))
            {
                // The manual IP could belong to any kit, so it is no substitute for a requested one.
                Debug.LogError($"[eTactileKit] Kit '{targetDeviceId}' did not answer discovery {why}.");
                return null;
            }
            Debug.LogWarning($"[eTactileKit] No kits answered discovery {why}; trying IP '{ipAddress}'.");
            return ipAddress;
        }

        ETactileKitDiscovery.DiscoveredDevice chosen;
        if (string.IsNullOrEmpty(targetDeviceId))
        {
            if (devices.Count > 1)
            {
                Debug.LogError($"[eTactileKit] Several kits found ({DeviceIds(devices)}). Set Target Device Id to choose one.");
                return null;
            }
            chosen = devices[0];
        }
        else
        {
            chosen = devices.Find(d => string.Equals(d.id, targetDeviceId, StringComparison.OrdinalIgnoreCase));
            if (chosen == null)
            {
                Debug.LogError($"[eTactileKit] Kit '{targetDeviceId}' not found. Discovered: {DeviceIds(devices)}.");
                return null;
            }
        }

        if (chosen.busy)
        {
            Debug.LogError($"[eTactileKit] Kit {chosen.id} is in use by another WiFi host.");
            return null;
        }

        Debug.Log($"[eTactileKit] Discovered kit {chosen.id} at {chosen.ip}:{chosen.port} ({chosen.mode}).");
        resolvedPort = chosen.port;
        return chosen.ip;
    }

    private static string DeviceIds(List<ETactileKitDiscovery.DiscoveredDevice> devices)
        => string.Join(", ", devices.ConvertAll(d => d.id).ToArray());

    /// <summary>
    /// Set the kit up for the active profile and the global parameters with
    /// <see cref="ETactileKit.Configure"/>: the settings go out in an order that can never overrun the
    /// scan period, then the kit must report the profile's electrode number. Returns false, after
    /// closing the link, if the profile or a parameter is refused (including a scan that would not
    /// fit in the period), the link fails or the check does not match: patterns are never sent to a
    /// kit whose setup is unverified.
    /// </summary>
    private bool SetupDevice()
    {
        if (!IsConnected)
        {
            return false;
        }

        cachedIntensities = activeProfile.BuildIntensities();
        frameIntensityBuffer = new int[cachedIntensities.Length];
        heldIds = null;
        setUpMode = stimulationMode;

        if (!etk.Configure(activeProfile.BuildMapping(), stimulationMode, polarity, pulseWidthUs, frequencyHz,
                           channelDischargeTimeUs, sensePulseHeight, sensePulseWidth))
        {
            Debug.LogError("[eTactileKit] Setup failed (see the error above). Disconnected.");
            isTesting = false;
            etk.Disconnect();
            etk = null;
            return false;
        }

        int hv513 = etk.Hv513Count;
        Debug.Log($"[eTactileKit] Setup complete. Electrodes={ElectrodeCount}, " +
                  $"HV513 chips={(hv513 < 0 ? "no answer" : hv513.ToString())}.");
        return true;
    }

    public void DisconnectDevice()
    {
        if (etk == null)
        {
            return;
        }
        isTesting = false;
        SendOff();
        etk.Disconnect();
        etk = null;
    }

    //--------------------------------------------------------------------------------------//
    // Stimulation
    //--------------------------------------------------------------------------------------//
    /// <summary>
    /// Calibrated intensity of one electrode scaled by <see cref="AmplitudeGain"/>, rounded to the
    /// nearest integer (the hardware takes integers only) and clamped to the 12-bit DAC range.
    /// </summary>
    private int ScaledIntensity(int electrodeId)
    {
        return Mathf.Clamp(
            Mathf.RoundToInt(cachedIntensities[electrodeId] * amplitudeGain),
            0, ETactileKit.MaxIntensity);
    }

    /// <summary>
    /// Send a single frame. <paramref name="activeElectrodeIds"/> lists the electrode ids that are ON
    /// this frame; each is driven at its calibrated intensity scaled by <see cref="AmplitudeGain"/>,
    /// and all others are 0. Ids outside [0, ElectrodeCount) are ignored with a warning. Returns false
    /// if nothing was sent: not connected, testing, or the link failed (it is then closed and the kit
    /// zeroes every amplitude).
    /// </summary>
    public bool SendFrame(int[] activeElectrodeIds)
    {
        if (!IsConnected || isTesting || cachedIntensities == null || frameIntensityBuffer == null)
        {
            return false;
        }

        for (int i = 0; i < frameIntensityBuffer.Length; i++)
        {
            frameIntensityBuffer[i] = 0;
        }

        if (activeElectrodeIds != null)
        {
            foreach (int id in activeElectrodeIds)
            {
                if (id >= 0 && id < frameIntensityBuffer.Length)
                {
                    frameIntensityBuffer[id] = ScaledIntensity(id);
                }
                else
                {
                    Debug.LogWarning($"[eTactileKit] Electrode id {id} is out of range " +
                                     $"[0, {ElectrodeCount - 1}] and was ignored.");
                }
            }
        }

        bool sent = etk.SendStimPattern(frameIntensityBuffer);
        heldIds = sent && activeElectrodeIds != null && activeElectrodeIds.Length > 0
            ? (int[])activeElectrodeIds.Clone()
            : null;
        heldGain = amplitudeGain;
        return sent;
    }

    /// <summary>
    /// Send a single frame with one absolute DAC intensity (0-4095, clamped) on every listed electrode,
    /// bypassing the calibration profile and <see cref="AmplitudeGain"/>. Used when the intensity comes
    /// from per-participant thresholds. The frame is not re-sent on gain changes. Returns false if
    /// nothing was sent (not connected, testing, or the link failed).
    /// </summary>
    public bool SendIntensity(int[] activeElectrodeIds, int intensity)
    {
        if (!IsConnected || isTesting || frameIntensityBuffer == null)
        {
            return false;
        }

        int value = Mathf.Clamp(intensity, 0, ETactileKit.MaxIntensity);
        for (int i = 0; i < frameIntensityBuffer.Length; i++)
        {
            frameIntensityBuffer[i] = 0;
        }
        if (activeElectrodeIds != null)
        {
            foreach (int id in activeElectrodeIds)
            {
                if (id >= 0 && id < frameIntensityBuffer.Length)
                {
                    frameIntensityBuffer[id] = value;
                }
                else
                {
                    Debug.LogWarning($"[eTactileKit] Electrode id {id} is out of range " +
                                     $"[0, {ElectrodeCount - 1}] and was ignored.");
                }
            }
        }

        heldIds = null; // absolute frames are not gain-scaled, so LateUpdate must not re-send them
        return etk.SendStimPattern(frameIntensityBuffer);
    }

    /// <summary>
    /// Turn every electrode off: a zero pattern, and in Impedance Only mode (where the kit ignores
    /// patterns and sends the sense pulse to every electrode) also sense pulse height 0. Sensing then
    /// resumes at the next setup (reconnect or profile import).
    /// </summary>
    public void SendOff()
    {
        heldIds = null;
        if (!IsConnected || frameIntensityBuffer == null)
        {
            return;
        }
        for (int i = 0; i < frameIntensityBuffer.Length; i++)
        {
            frameIntensityBuffer[i] = 0;
        }
        etk.SendStimPattern(frameIntensityBuffer);
        if (setUpMode == StimulationMode.ImpedanceOnly)
        {
            etk.SendSensePulseHeight(0);
        }
    }

    //--------------------------------------------------------------------------------------//
    // Connection test
    //--------------------------------------------------------------------------------------//
    private IEnumerator RunConnectionTest()
    {
        isTesting = true;
        heldIds = null;
        Debug.Log($"[eTactileKit] Connection test running - all electrodes pulse on/off every 0.5 s, " +
                  $"{ConnectionTestCycles} times.");

        // Sized by the setup that ran, which is what the kit is configured for.
        int count = cachedIntensities.Length;
        int[] allOn = new int[count];
        int[] allOff = new int[count];

        for (int step = 0; step < ConnectionTestCycles * 2 && isTesting && IsConnected &&
                           cachedIntensities.Length == count; step++)
        {
            bool on = step % 2 == 0;
            if (on)
            {
                // Rebuilt each cycle so the amplitude slider takes effect live during the test.
                for (int i = 0; i < count; i++)
                {
                    allOn[i] = ScaledIntensity(i);
                }
            }

            etk.SendStimPattern(on ? allOn : allOff);
            yield return new WaitForSecondsRealtime(0.5f);   // Real time: a paused game cannot hold an "on" step
        }

        // Ended by running out of cycles (a stop from elsewhere turns the output off itself).
        if (isTesting && IsConnected && cachedIntensities.Length == count)
        {
            etk.SendStimPattern(allOff);
        }
        isTesting = false;
    }
}
