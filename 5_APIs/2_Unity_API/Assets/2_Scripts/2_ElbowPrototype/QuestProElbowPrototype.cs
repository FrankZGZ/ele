using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;
using XRDevice = UnityEngine.XR.InputDevice;

/// <summary>Meta body tracking -> elbow flexion -> existing eTactileKit transport.</summary>
[DefaultExecutionOrder(100)]
public class QuestProElbowPrototype : MonoBehaviour
{
    public OVRCameraRig rig;
    public OVRBody body;
    public ETactileKitManager manager;
    public enum TrackedArm { Left, Right }
    [Header("Which arm the electrode is on")]
    [Tooltip("Arm whose elbow angle drives the stimulation. Y on the left controller also toggles it at runtime.")]
    public TrackedArm arm = TrackedArm.Left;
    public bool leftArm { get => arm == TrackedArm.Left; set => arm = value ? TrackedArm.Left : TrackedArm.Right; }
    [Tooltip("On: drive every electrode the kit was set up with (same as test_etactilekit.py), so thresholds " +
             "measured there carry over. Off: drive only Electrode Ids.")]
    public bool allElectrodes = true;
    public int[] electrodeIds = { 0 };
    [Range(0, 150)] public float startAngle = 30;
    [Range(0, 150)] public float stopAngle = 20;
    [Range(1, 170)] public float fullAngle = 100;
    [Tooltip("Only used when Use Thresholds is off: gain on the calibration profile's intensity at Full Angle.")]
    [Range(0, 1)] public float maximumGain = 0.25f;
    [Range(1, 90)] public float sendRate = 50;

    [Header("Angle smoothing (reduces body-tracking jitter)")]
    public bool enableFilter = true;
    [Tooltip("Median over this many samples removes single-frame glitches. 1 = off, 3 adds ~1 sample (~14 ms) delay, 5 adds ~2.")]
    [Range(1, 7)] public int spikeMedianWindow = 3;
    [Tooltip("One Euro min cutoff (Hz). Lower = steadier when still, more lag.")]
    [Range(0.05f, 10f)] public float minCutoffHz = 1.0f;
    [Tooltip("One Euro speed coefficient (per deg/s). Higher = less lag during fast bends, more jitter.")]
    [Range(0f, 1f)] public float beta = 0.05f;
    [Range(0.1f, 10f)] public float derivativeCutoffHz = 1.0f;
    public Material lineMaterial;

    [Header("Body visuals")]
    [Tooltip("Capsule mannequin (torso, arms, hands) on the tracked joints, used when no humanoid avatar is set up.")]
    public bool showAvatar = true;
    [Tooltip("The original cyan stick-figure lines (useful for debugging tracking).")]
    public bool showSkeletonLines = false;

    [Header("Per-participant thresholds (raw DAC 0-4095, measured this session)")]
    [Tooltip("On: intensity runs from Perception Threshold at Start Angle to the upper bound at Full Angle. " +
             "Off: the original gain mapping (Maximum Gain x calibration intensity).")]
    public bool useThresholds = true;
    public string participantId = "P00";
    [Tooltip("Lowest intensity the participant reported feeling.")]
    [Range(0, 4095)] public int perceptionThreshold = 0;
    [Tooltip("Intensity at which the participant reported discomfort. Output never reaches it.")]
    [Range(0, 4095)] public int discomfortThreshold = 0;
    [Tooltip("Upper bound = PT + fraction x (DT - PT). 0.8 stops at 80% of the way to discomfort.")]
    [Range(0, 1)] public float upperFraction = 0.8f;
    [Tooltip("Absolute ceiling in DAC units, applied after everything else.")]
    [Range(0, 4095)] public int hardCap = 450;

    [Header("Live calibration (same stimulus as test_etactilekit.py)")]
    [Tooltip("Intensity change per Up/Down press (keyboard arrows or right thumbstick).")]
    [Range(1, 50)] public int calibrationStep = 5;
    [Tooltip("Highest intensity calibration can reach (test_etactilekit.py uses 450).")]
    [Range(0, 4095)] public int calibrationMax = 450;
    [Tooltip("On: 0.5 s on / 0.5 s off like test_etactilekit.py. Off: continuous.")]
    public bool calibrationBlink = true;
    [Range(0.1f, 2f)] public float blinkInterval = 0.5f;

    [Header("Logging (one CSV row per frame)")]
    public bool enableLog = true;

    public bool Calibrating { get; private set; }
    public int CalibrationIntensity { get; private set; }
    public bool Armed { get; private set; }
    public bool TrackingValid { get; private set; }
    public float Flexion { get; private set; }     // filtered angle that drives output
    public float FlexionRaw { get; private set; }  // unfiltered angle from the latest body sample
    public float OutputGain { get; private set; }
    public int OutputIntensity { get; private set; }
    public string Status { get; private set; } = "Waiting for body tracking";
    public string LogPath { get; private set; }

    /// <summary>Highest intensity the threshold mapping can send, or -1 when the thresholds are unusable.</summary>
    public int UpperIntensity
    {
        get
        {
            if (perceptionThreshold <= 0 || discomfortThreshold <= perceptionThreshold) return -1;
            int upper = Mathf.RoundToInt(perceptionThreshold + upperFraction * (discomfortThreshold - perceptionThreshold));
            upper = Mathf.Min(upper, discomfortThreshold - 1, hardCap);
            return upper < perceptionThreshold ? -1 : upper;
        }
    }

    private readonly OVRPlugin.BoneId[] joints = {
        OVRPlugin.BoneId.Body_LeftArmUpper, OVRPlugin.BoneId.Body_LeftArmLower,
        OVRPlugin.BoneId.Body_LeftHandWrist, OVRPlugin.BoneId.Body_RightArmUpper,
        OVRPlugin.BoneId.Body_RightArmLower, OVRPlugin.BoneId.Body_RightHandWrist,
        OVRPlugin.BoneId.Body_Hips, OVRPlugin.BoneId.Body_SpineUpper,
        OVRPlugin.BoneId.Body_Head
    };
    private readonly Vector3[] points = new Vector3[9];
    private readonly bool[] valid = new bool[9];
    private readonly int[,] links = { {0,1}, {1,2}, {3,4}, {4,5}, {0,7}, {3,7}, {6,7}, {7,8} };
    private LineRenderer[] lines;
    private Text hud;
    private bool active, outputSent, paused;
    private bool calPhaseOn;
    private float nextBlink, repeatAt;
    private int heldDir, lastCalSent;

    // Input plumbing: previous XR button states, per-action debounce, one-shot requests from the PC panel
    private readonly bool[] xrPrev = new bool[8];
    private readonly float[] lastFire = new float[8];
    private bool guiStop, guiArm, guiCal, guiPt, guiDt, guiReconnect, guiSwitch;
    private int guiStep;
    private bool prevActive;
    private LineRenderer indicator;
    private string inputLine = "", bodyCalLine = "";
    private float wheelAcc;

    /// <summary>One unambiguous word for what the prototype is doing right now.</summary>
    public string ModeName => manager == null || !manager.IsConnected ? "OFFLINE"
        : Calibrating ? $"THRESHOLD CALIBRATION (level {CalibrationIntensity}/{calibrationMax})"
        : Armed && active ? "STIMULATING"
        : Armed ? "ENABLED (bend to stimulate)"
        : "PREVIEW (output off)";
    private double lastSample = double.NaN;
    private float freshAt, nextSend;
    private string armRefusal;

    // Logging state for the current frame
    private StreamWriter log;
    private float nextFlush;
    private string frameEvent;   // "on" when a stimulation frame went out this frame, "off" for a zero frame
    private double frameSendMs;  // duration of the blocking send call(s) this frame
    private readonly Stopwatch sendTimer = new Stopwatch();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private void Start()
    {
        if (rig == null || body == null || manager == null)
        {
            Debug.LogError("Elbow prototype needs rig, body and manager references.", this);
            enabled = false;
            return;
        }
        manager.AmplitudeGain = 0;
        lines = new LineRenderer[links.GetLength(0)];
        for (int i = 0; i < lines.Length; i++)
        {
            var go = new GameObject("Body segment " + i);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = lineMaterial;
            line.positionCount = 2;
            line.startWidth = line.endWidth = 0.008f;
            line.startColor = line.endColor = Color.cyan;
            line.enabled = false;
            lines[i] = line;
        }
        CreateHud();
        // Capsule mannequin only when no humanoid avatar (eTactileKit > Quest Pro > Set Up Rocketbox Avatar + XR Hands) is in the scene
        if (showAvatar && GetComponent<ElbowAvatar>() == null && FindFirstObjectByType<ElbowHumanoidAvatar>() == null)
        {
            var avatar = gameObject.AddComponent<ElbowAvatar>();
            avatar.rig = rig;
            avatar.body = body;
        }
        CreateIndicator();
        if (enableLog) OpenLog();
    }

    private void LateUpdate()
    {
        if (rig == null || body == null || manager == null) return;
        frameEvent = "";
        frameSendMs = 0;
        TrackingValid = ReadBody();
        // Controllers are read through both OVRInput and Unity's XR input: with the Unity OpenXR
        // Touch profile active (as over Link), OVRInput buttons can stay silent.
        XRDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        XRDevice right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        bool stopPressed  = Fire(0, OVRInput.GetDown(OVRInput.Button.Two)   | XrDown(right, CommonUsages.secondaryButton, 0)  | guiStop);
        bool armPressed   = Fire(1, OVRInput.GetDown(OVRInput.Button.One)   | XrDown(right, CommonUsages.primaryButton, 1)    | guiArm);
        bool calPressed   = Fire(2, OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick) | XrDown(left, CommonUsages.primary2DAxisClick, 2) | guiCal);
        bool setPtPressed = Fire(3, OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger) | XrDown(right, CommonUsages.triggerButton, 3) | guiPt);
        bool setDtPressed = Fire(4, OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger)   | XrDown(left, CommonUsages.triggerButton, 4)  | guiDt);
        bool reconnectPressed = Fire(5, OVRInput.GetDown(OVRInput.Button.Three) | XrDown(left, CommonUsages.primaryButton, 5) | guiReconnect);
        bool switchPressed    = Fire(6, OVRInput.GetDown(OVRInput.Button.Four)  | XrDown(left, CommonUsages.secondaryButton, 6) | guiSwitch);
        float stickY = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).y;
        if (right.isValid && right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 xrStick) && Mathf.Abs(xrStick.y) > Mathf.Abs(stickY))
            stickY = xrStick.y;
        bool upHeld = stickY > 0.7f, downHeld = stickY < -0.7f;
        int guiDir = guiStep; // one-shot steps from the PC panel
        guiStop = guiArm = guiCal = guiPt = guiDt = guiReconnect = guiSwitch = false;
        guiStep = 0;
#if UNITY_EDITOR || UNITY_STANDALONE
        // PC keyboard for Link mode (click the Game view first)
        stopPressed |= Input.GetKeyDown(KeyCode.Escape);
        armPressed |= Input.GetKeyDown(KeyCode.Space);
        calPressed |= Input.GetKeyDown(KeyCode.C);
        setPtPressed |= Input.GetKeyDown(KeyCode.P);
        setDtPressed |= Input.GetKeyDown(KeyCode.D);
        upHeld |= Input.GetKey(KeyCode.UpArrow);
        downHeld |= Input.GetKey(KeyCode.DownArrow);
        // Mouse wheel over the Game view: one notch = one step (touchpads send fractions, so accumulate)
        wheelAcc += Input.mouseScrollDelta.y;
        while (wheelAcc >= 1f) { guiDir += 1; wheelAcc -= 1f; }
        while (wheelAcc <= -1f) { guiDir -= 1; wheelAcc += 1f; }
        if (!Calibrating) wheelAcc = 0f;
#endif
        bool wasArmed = Armed, wasCal = Calibrating;
        if (armPressed || stopPressed || calPressed)
            Debug.Log($"[Elbow] {(stopPressed ? "stop" : calPressed ? "calibrate" : "enable")} pressed: tracking={TrackingValid} connected={manager.IsConnected} paused={paused}", this);
        if (stopPressed) { StopCalibration(); Disarm(); } // B always wins
        else if (calPressed) { if (Calibrating) StopCalibration(); else StartCalibration(); }
        else if (armPressed) { StopCalibration(); Arm(); if (!Armed) Buzz(right, 0.3f, 0.05f); } // tiny tick = refused
        if (reconnectPressed) { StopCalibration(); Disarm(); manager.ConnectDevice(); }
        if (switchPressed) { StopCalibration(); Disarm(); leftArm = !leftArm; Buzz(left, 0.5f, 0.08f); }
        if (Armed && !wasArmed) Buzz(right, 1f, 0.25f);            // long buzz = output enabled
        if (Calibrating && !wasCal) Buzz(left, 1f, 0.25f);         // long buzz on left = calibration on
        if ((wasArmed && !Armed) || (wasCal && !Calibrating)) { Buzz(right, 0.6f, 0.08f); Buzz(left, 0.6f, 0.08f); }

        if (Calibrating)
        {
            if (paused || !manager.IsConnected) { StopCalibration(); Status = "WiFi disconnected (X: reconnect)"; }
            else
            {
                int dir = StepRepeat(upHeld, downHeld) + guiDir;
                if (setPtPressed || setDtPressed) Buzz(setPtPressed ? right : left, 1f, 0.15f);
                if (dir != 0) CalibrationIntensity = Mathf.Clamp(CalibrationIntensity + dir * calibrationStep, 0, calibrationMax);
                if (setPtPressed) RecordThreshold(true);
                else if (setDtPressed) RecordThreshold(false);
                UpdateCalibrationOutput();
            }
        }
        else if (!TrackingValid || paused || !manager.IsConnected)
        {
            Disarm();
            Status = !TrackingValid ? "Waiting for valid body joints" : "WiFi disconnected (X: reconnect)";
        }
        else if (!Armed) { StopOutput(); Status = armRefusal ?? "Preview (A: enable output)"; }
        else
        {
            active = active ? Flexion > stopAngle : Flexion >= startAngle;
            if (!active) { StopOutput(); Status = "Armed / arm straight"; }
            else if (Time.unscaledTime >= nextSend)
            {
                // Advance by whole periods so the average rate really is sendRate (scheduling from "now"
                // rounded every send up to the next frame: 50 Hz at 72 fps came out as ~36 Hz)
                float period = 1f / Mathf.Max(1, sendRate);
                nextSend = Mathf.Max(nextSend + period, Time.unscaledTime - period);
                float t = Mathf.InverseLerp(startAngle, fullAngle, Flexion);
                sendTimer.Restart();
                if (useThresholds)
                {
                    OutputGain = t;
                    OutputIntensity = Mathf.RoundToInt(Mathf.Lerp(perceptionThreshold, UpperIntensity, t));
                    outputSent = manager.SendIntensity(ActiveIds(), OutputIntensity);
                }
                else
                {
                    OutputGain = maximumGain * t;
                    manager.AmplitudeGain = OutputGain;
                    OutputIntensity = -1; // per electrode: calibration intensity x gain
                    outputSent = manager.SendFrame(ActiveIds());
                }
                sendTimer.Stop();
                frameSendMs += sendTimer.Elapsed.TotalMilliseconds;
                frameEvent = "on";
                Status = outputSent ? "Sending feedback" : "Output rejected";
                if (!outputSent) Disarm();
            }
        }
        // Input diagnostics: is the A button seen at all, and by which API?
        bool ovrAHeld = OVRInput.Get(OVRInput.Button.One);
        bool xrAHeld = right.isValid && right.TryGetFeatureValue(CommonUsages.primaryButton, out bool xa) && xa;
        inputLine = $"Right controller: {(right.isValid ? right.name : "NOT FOUND")}   A held: OVR {(ovrAHeld ? 1 : 0)} / XR {(xrAHeld ? 1 : 0)}";
        bodyCalLine = body.GetBodyTrackingCalibrationStatus().ToString();

        if (Armed && active && !prevActive) Buzz(right, 0.4f, 0.1f); // short buzz = stimulation started
        prevActive = Armed && active;
        UpdateIndicator();

        string level = useThresholds
            ? $"Int {OutputIntensity}   range [{perceptionThreshold}-{UpperIntensity}]"
            : $"Gain {OutputGain:F2}";
        if (hud != null) hud.text =
            $"ELBOW  {participantId}   {(leftArm ? "LEFT" : "RIGHT")} arm   WiFi {(manager.IsConnected ? "connected" : "OFFLINE")}\n" +
            $"Angle {Flexion:F1} deg   {level}\n" +
            $"MODE: {ModeName}   PT {perceptionThreshold}   DT {discomfortThreshold}\n" +
            $"> {Status}\n" +
            $"Meta body-tracking calibration: {bodyCalLine}\n{inputLine}\n" +
            "A/Space enable   B/Esc stop all   X reconnect   Y switch arm\n" +
            "Calibrate: L-stick click/C   R-stick, Up/Down or mouse wheel +-" + calibrationStep + "\n" +
            "   R-trigger/P = set PT   L-trigger/D = set DT (drops to 0)";
        WriteLogRow();
    }

    //--------------------------------------------------------------------------------------//
    // Live calibration: the same all-electrode stimulus as test_etactilekit.py, stepped by hand
    //--------------------------------------------------------------------------------------//
    private void StartCalibration()
    {
        if (manager == null || !manager.IsConnected || paused) { Status = "Calibration needs the kit connected"; return; }
        Disarm();
        Calibrating = true;
        CalibrationIntensity = 0; // always start from zero
        calPhaseOn = true;
        nextBlink = Time.unscaledTime + blinkInterval;
        lastCalSent = 0;
        heldDir = 0;
        Debug.Log("[Elbow] Calibration started at intensity 0", this);
        LogNote("calibration start");
    }

    private void StopCalibration()
    {
        if (!Calibrating) return;
        Calibrating = false;
        CalibrationIntensity = 0;
        lastCalSent = 0;
        StopOutput();
        Debug.Log("[Elbow] Calibration stopped", this);
        LogNote("calibration stop");
    }

    private int StepRepeat(bool upHeld, bool downHeld)
    {
        int dir = upHeld == downHeld ? 0 : (upHeld ? 1 : -1);
        if (dir == 0) { heldDir = 0; return 0; }
        if (dir != heldDir) { heldDir = dir; repeatAt = Time.unscaledTime + 0.4f; return dir; } // new press
        if (Time.unscaledTime >= repeatAt) { repeatAt = Time.unscaledTime + 0.1f; return dir; }  // held: repeat
        return 0;
    }

    private void UpdateCalibrationOutput()
    {
        if (calibrationBlink && Time.unscaledTime >= nextBlink)
        {
            calPhaseOn = !calPhaseOn;
            nextBlink = Time.unscaledTime + blinkInterval;
        }
        int send = CalibrationIntensity > 0 && (!calibrationBlink || calPhaseOn) ? CalibrationIntensity : 0;
        if (send != lastCalSent)
        {
            if (send > 0)
            {
                sendTimer.Restart();
                bool ok = manager.SendIntensity(ActiveIds(), send);
                sendTimer.Stop();
                frameSendMs += sendTimer.Elapsed.TotalMilliseconds;
                frameEvent = "cal_on";
                if (!ok) { StopCalibration(); Status = "Output rejected"; return; }
                outputSent = true;
            }
            else StopOutput(); // sends the zero frame if something is on
            lastCalSent = send;
        }
        OutputGain = 0;
        OutputIntensity = send;
        string next = perceptionThreshold <= 0 ? "first feel it -> P / R-trigger"
                    : discomfortThreshold <= perceptionThreshold ? "uncomfortable -> D / L-trigger"
                    : "PT+DT set: C to finish, then Space/A";
        Status = $"CALIBRATING  level {CalibrationIntensity}  ({(send > 0 ? "ON" : "off")})   {next}";
    }

    private void RecordThreshold(bool perception)
    {
        int v = CalibrationIntensity;
        if (perception) perceptionThreshold = v;
        else
        {
            discomfortThreshold = v;
            CalibrationIntensity = 0; // back off immediately once discomfort is reached
        }
        string msg = $"set {(perception ? "PT" : "DT")}={v} (PT={perceptionThreshold} DT={discomfortThreshold} upper={UpperIntensity})";
        Debug.Log("[Elbow] Calibration " + msg, this);
        LogNote(msg);
#if UNITY_EDITOR
        ElbowCalibrationCarry.Store(perceptionThreshold, discomfortThreshold);
#endif
    }

    private bool ReadBody()
    {
        var sample = body.isActiveAndEnabled ? body.BodyState : null;
        bool available = sample.HasValue && sample.Value.JointLocations != null;
        if (available && sample.Value.Time != lastSample)
        {
            lastSample = sample.Value.Time;
            freshAt = Time.unscaledTime;
        }
        bool newSample = available && sample.Value.Time != filterSampleTime;
        available &= !double.IsNaN(lastSample) && Time.unscaledTime - freshAt < 0.2f;
        for (int i = 0; i < joints.Length; i++)
        {
            valid[i] = false;
            int index = (int)joints[i];
            if (!available || index >= sample.Value.JointLocations.Length) continue;
            var joint = sample.Value.JointLocations[index];
            if (!joint.PositionValid) continue;
            var p = joint.Pose.Position;
            var local = new Vector3(p.x, p.y, -p.z); // OVR right-handed -> Unity left-handed
            if (!Finite(local)) continue;
            points[i] = rig.trackingSpace.TransformPoint(local);
            valid[i] = true;
        }
        if (lines != null) for (int i = 0; i < lines.Length; i++)
        {
            int a = links[i,0], b = links[i,1];
            lines[i].enabled = showSkeletonLines && valid[a] && valid[b];
            if (lines[i].enabled) { lines[i].SetPosition(0, points[a]); lines[i].SetPosition(1, points[b]); }
        }
        int first = leftArm ? 0 : 3;
        if (!valid[first] || !valid[first+1] || !valid[first+2] || first != filterArm) { ResetFilter(); filterArm = first; }
        if (!valid[first] || !valid[first+1] || !valid[first+2]) return false;
        Vector3 upper = points[first] - points[first+1], lower = points[first+2] - points[first+1];
        if (upper.sqrMagnitude < 0.0001f || lower.sqrMagnitude < 0.0001f) { ResetFilter(); return false; }
        if (newSample)
        {
            double t = sample.Value.Time;
            float dt = double.IsNaN(filterSampleTime) ? 1f / 72f : Mathf.Clamp((float)(t - filterSampleTime), 1f / 500f, 0.1f);
            filterSampleTime = t;
            FlexionRaw = 180f - Vector3.Angle(upper, lower);
            Flexion = enableFilter ? FilterAngle(FlexionRaw, dt) : FlexionRaw;
        }
        return true;
    }

    //--------------------------------------------------------------------------------------//
    // Angle smoothing, run once per new body-tracking sample:
    //   1. median of the last N samples removes single-sample tracking glitches (spikes)
    //   2. One Euro filter (Casiez et al., CHI 2012): strong smoothing when the arm is still,
    //      little lag when it moves fast. Cutoff = minCutoff + beta x |angular speed|.
    //--------------------------------------------------------------------------------------//
    private readonly float[] medianBuf = new float[7];
    private readonly float[] medianSort = new float[7];
    private int medianCount, medianHead, filterArm = -1;
    private double filterSampleTime = double.NaN;
    private bool euroInit;
    private float euroX, euroDx;

    private void ResetFilter() { medianCount = 0; medianHead = 0; euroInit = false; filterSampleTime = double.NaN; }

    private float FilterAngle(float raw, float dt)
    {
        int n = Mathf.Clamp(spikeMedianWindow | 1, 1, medianBuf.Length); // odd, 1..7
        medianBuf[medianHead] = raw;
        medianHead = (medianHead + 1) % n;
        medianCount = Mathf.Min(medianCount + 1, n);
        Array.Copy(medianBuf, medianSort, medianCount);
        Array.Sort(medianSort, 0, medianCount);
        float x = medianSort[medianCount / 2];

        if (!euroInit) { euroInit = true; euroX = x; euroDx = 0; return x; }
        float dx = (x - euroX) / dt;
        euroDx = Mathf.Lerp(euroDx, dx, Alpha(derivativeCutoffHz, dt));
        float cutoff = minCutoffHz + beta * Mathf.Abs(euroDx);
        euroX = Mathf.Lerp(euroX, x, Alpha(cutoff, dt));
        return euroX;
    }

    private static float Alpha(float cutoffHz, float dt)
    {
        float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.001f, cutoffHz));
        return 1f / (1f + tau / dt);
    }

    private static bool Finite(Vector3 p) => !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
        || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));

    private int[] allIds;
    private int[] ActiveIds()
    {
        if (!allElectrodes) return electrodeIds;
        int n = manager != null ? manager.ElectrodeCount : 0;
        if (allIds == null || allIds.Length != n)
        {
            allIds = new int[n];
            for (int i = 0; i < n; i++) allIds[i] = i;
        }
        return allIds;
    }

    public void Arm()
    {
        if (paused || !TrackingValid || manager == null || !manager.IsConnected) return;
        int[] ids = ActiveIds();
        if (ids == null || ids.Length == 0) return;
        foreach (int id in ids) if (id < 0 || id >= manager.ElectrodeCount) return;
        if (useThresholds && UpperIntensity < 0)
        {
            armRefusal = perceptionThreshold <= 0 || discomfortThreshold <= 0
                ? $"Not armed: PT={perceptionThreshold} DT={discomfortThreshold}. Press C, step up, P when first felt, D when uncomfortable"
                : $"Not armed: need PT < DT and Hard Cap >= PT (PT={perceptionThreshold} DT={discomfortThreshold} cap={hardCap})";
            Debug.LogWarning("[Elbow] " + armRefusal, this);
            return;
        }
        armRefusal = null;
        Armed = true;
    }
    public void Disarm() { Armed = false; active = false; StopOutput(); }
    private void StopOutput()
    {
        OutputGain = 0;
        OutputIntensity = 0;
        if (manager != null)
        {
            manager.AmplitudeGain = 0;
            if (outputSent)
            {
                sendTimer.Restart();
                manager.SendOff();
                sendTimer.Stop();
                frameSendMs += sendTimer.Elapsed.TotalMilliseconds;
                frameEvent = "off";
            }
        }
        outputSent = false;
    }
    private void OnApplicationPause(bool value) { paused = value; if (value) { StopCalibration(); Disarm(); } }
    private void OnApplicationFocus(bool value) { if (!value) { StopCalibration(); Disarm(); } }
    private void OnDisable()
    {
        StopCalibration();
        Disarm();
        WriteLogRow();
        CloseLog();
        if (lines != null) foreach (var line in lines) if (line != null) line.enabled = false;
    }
    private void OnApplicationQuit() => CloseLog();
    private void OnValidate()
    {
        stopAngle = Mathf.Clamp(stopAngle, 0, startAngle);
        fullAngle = Mathf.Max(startAngle + 1, fullAngle);
        discomfortThreshold = Mathf.Max(discomfortThreshold, perceptionThreshold);
    }

    //--------------------------------------------------------------------------------------//
    // CSV log: one row per LateUpdate. Columns:
    //   t_unity_s         Time.realtimeSinceStartupAsDouble when the row was written
    //   ovr_now_s         OVRPlugin.GetTimeInSeconds() at the same moment (OVR clock)
    //   body_sample_s     timestamp of the body-tracking sample used this frame (OVR clock)
    //   tracking_age_ms   ovr_now_s - body_sample_s (negative if the runtime reports a predicted time)
    //   flexion_deg       filtered elbow flexion (drives output); flexion_raw_deg (last column) = unfiltered
    //   tracking_valid, armed, active
    //   intensity         DAC value commanded on each listed electrode (-1 = gain mode, 0 = off)
    //   gain              0-1 position in the angle range (threshold mode) or the manager gain (gain mode)
    //   event             "on" = an elbow stimulation frame was sent this row, "cal_on" = a calibration
    //                     frame, "off" = a zero frame, "" = none. Calibration start/stop and PT/DT
    //                     settings are written as "#" note lines in between.
    //   send_ms           time spent inside the blocking send call(s) this row
    //   status
    //--------------------------------------------------------------------------------------//
    private void OpenLog()
    {
        try
        {
            string folder = Application.isEditor
                ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ElbowLogs"))
                : Path.Combine(Application.persistentDataPath, "ElbowLogs");
            Directory.CreateDirectory(folder);
            string safeId = string.IsNullOrWhiteSpace(participantId) ? "P00" : participantId.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) safeId = safeId.Replace(c, '_');
            LogPath = Path.Combine(folder, $"elbow_{safeId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            log = new StreamWriter(LogPath, false, new UTF8Encoding(false));
            log.WriteLine(string.Format(Inv,
                "# participant={0} arm={1} electrodes={2} all_electrodes={14} use_thresholds={3} PT={4} DT={5} upper_fraction={6} " +
                "hard_cap={7} upper={8} start_deg={9} stop_deg={10} full_deg={11} send_rate_hz={12} started={13:o} filter={15} median={16} min_cutoff_hz={17} beta={18}",
                safeId, leftArm ? "left" : "right", string.Join(";", electrodeIds ?? new int[0]), useThresholds,
                perceptionThreshold, discomfortThreshold, upperFraction, hardCap, UpperIntensity,
                startAngle, stopAngle, fullAngle, sendRate, DateTime.Now, allElectrodes,
                enableFilter, spikeMedianWindow, minCutoffHz, beta));
            log.WriteLine("t_unity_s,ovr_now_s,body_sample_s,tracking_age_ms,flexion_deg,tracking_valid,armed,active," +
                          "intensity,gain,event,send_ms,status,flexion_raw_deg");
            Debug.Log("[Elbow] Logging to " + LogPath, this);
        }
        catch (Exception e)
        {
            log = null;
            Debug.LogError("[Elbow] Could not open log: " + e.Message, this);
        }
    }

    private void WriteLogRow()
    {
        if (log == null) return;
        double ovrNow = OVRPlugin.GetTimeInSeconds();
        double age = double.IsNaN(lastSample) ? double.NaN : (ovrNow - lastSample) * 1000.0;
        log.WriteLine(string.Format(Inv, "{0:F6},{1:F6},{2:F6},{3:F2},{4:F2},{5},{6},{7},{8},{9:F4},{10},{11:F3},\"{12}\",{13:F2}",
            Time.realtimeSinceStartupAsDouble, ovrNow, lastSample, age, Flexion,
            TrackingValid ? 1 : 0, Armed ? 1 : 0, active ? 1 : 0,
            OutputIntensity, OutputGain, frameEvent, frameSendMs, (Status ?? "").Replace("\"", "'"), FlexionRaw));
        if (Time.unscaledTime >= nextFlush)
        {
            log.Flush();
            nextFlush = Time.unscaledTime + 1f;
        }
    }

    /// <summary>Writes a "#" line into the CSV (skipped by pd.read_csv(comment='#')).</summary>
    private void LogNote(string note)
    {
        if (log == null) return;
        log.WriteLine(string.Format(Inv, "# t_unity_s={0:F6} {1}", Time.realtimeSinceStartupAsDouble, note));
    }

    private void CloseLog()
    {
        if (log == null) return;
        try { log.Flush(); log.Dispose(); } catch (Exception) { }
        log = null;
    }

    /// <summary>World-space UI panel locked to the head. uGUI's default material renders in both eyes
    /// under single-pass instanced stereo, unlike the legacy TextMesh font shader.</summary>
    private void CreateHud()
    {
        var canvasGo = new GameObject("Elbow status", typeof(RectTransform), typeof(Canvas));
        canvasGo.transform.SetParent(rig.centerEyeAnchor, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 100;
        var rt = (RectTransform)canvasGo.transform;
        rt.sizeDelta = new Vector2(640, 250);
        rt.localPosition = new Vector3(0f, -0.16f, 0.75f); // slightly below eye line, 75 cm ahead
        rt.localRotation = Quaternion.Euler(12f, 0f, 0f);  // tilted toward the eyes
        rt.localScale = Vector3.one * 0.001f;               // 1 px = 1 mm

        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(canvasGo.transform, false);
        Stretch((RectTransform)bg.transform, 0);
        bg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(canvasGo.transform, false);
        Stretch((RectTransform)textGo.transform, 10);
        hud = textGo.GetComponent<Text>();
        hud.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hud.fontSize = 20;
        hud.lineSpacing = 1.05f;
        hud.color = Color.white;
        hud.alignment = TextAnchor.UpperLeft;
        hud.horizontalOverflow = HorizontalWrapMode.Overflow;
        hud.verticalOverflow = VerticalWrapMode.Overflow;
    }

    private static void Stretch(RectTransform r, float pad)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(pad, pad);
        r.offsetMax = new Vector2(-pad, -pad);
    }

    //--------------------------------------------------------------------------------------//
    // Feedback that does not depend on the HUD panel
    //--------------------------------------------------------------------------------------//
    /// <summary>Head-locked bar drawn with the same LineRenderer setup as the skeleton.
    /// Colour = state (grey preview, yellow enabled, red stimulating, blue calibrating, dark red offline),
    /// length = current intensity relative to the upper bound / calibration max.</summary>
    private void CreateIndicator()
    {
        var go = new GameObject("Elbow state bar");
        go.transform.SetParent(rig.centerEyeAnchor, false);
        go.transform.localPosition = new Vector3(0f, -0.22f, 0.7f);
        indicator = go.AddComponent<LineRenderer>();
        indicator.sharedMaterial = lineMaterial;
        indicator.useWorldSpace = false;
        indicator.positionCount = 2;
        indicator.startWidth = indicator.endWidth = 0.025f;
    }

    private void UpdateIndicator()
    {
        if (indicator == null) return;
        Color c; float fill;
        if (!manager.IsConnected) { c = new Color(0.5f, 0f, 0f); fill = 1f; }
        else if (Calibrating) { c = new Color(0.2f, 0.5f, 1f); fill = (float)CalibrationIntensity / Mathf.Max(1, calibrationMax); }
        else if (Armed && active) { c = Color.red; fill = UpperIntensity > 0 ? (float)OutputIntensity / UpperIntensity : OutputGain; }
        else if (Armed) { c = Color.yellow; fill = 0.15f; }
        else { c = new Color(0.6f, 0.6f, 0.6f); fill = 0.15f; }
        float half = 0.02f + 0.13f * Mathf.Clamp01(fill);
        indicator.SetPosition(0, new Vector3(-half, 0, 0));
        indicator.SetPosition(1, new Vector3(half, 0, 0));
        indicator.startColor = indicator.endColor = c;
        // BodyLine.mat is URP Unlit, which ignores vertex colours; override its base colour instead
        if (indicatorProps == null) indicatorProps = new MaterialPropertyBlock();
        indicator.GetPropertyBlock(indicatorProps);
        indicatorProps.SetColor("_BaseColor", c);
        indicatorProps.SetColor("_Color", c);
        indicator.SetPropertyBlock(indicatorProps);
    }
    private MaterialPropertyBlock indicatorProps;

    /// <summary>True at most once per 0.25 s per action, so OVRInput and XR input firing on
    /// neighbouring frames do not toggle twice.</summary>
    private bool Fire(int action, bool pressed)
    {
        if (!pressed || Time.unscaledTime - lastFire[action] < 0.25f) return false;
        lastFire[action] = Time.unscaledTime;
        return true;
    }

    private bool XrDown(XRDevice d, InputFeatureUsage<bool> usage, int slot)
    {
        bool now = d.isValid && d.TryGetFeatureValue(usage, out bool v) && v;
        bool down = now && !xrPrev[slot];
        xrPrev[slot] = now;
        return down;
    }

    private static void Buzz(XRDevice d, float amplitude, float seconds)
    {
        if (d.isValid && d.TryGetHapticCapabilities(out HapticCapabilities caps) && caps.supportsImpulse)
            d.SendHapticImpulse(0, amplitude, seconds);
    }

#if UNITY_EDITOR || UNITY_STANDALONE
    private GUIStyle guiBox, guiBtn;
    /// <summary>Control panel on the PC Game view (not in the headset). Clicking works without keyboard focus.</summary>
    private void OnGUI()
    {
        if (manager == null) return;
        if (guiBox == null)
        {
            guiBox = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.UpperLeft, wordWrap = false };
            guiBox.normal.textColor = Color.white;
            guiBtn = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        }
        string state = "MODE: " + ModeName;
        Color old = GUI.backgroundColor;
        GUI.backgroundColor = !manager.IsConnected ? new Color(0.5f, 0, 0) : Calibrating ? new Color(0.2f, 0.5f, 1f)
                            : Armed && active ? Color.red : Armed ? Color.yellow : Color.gray;
        GUI.Box(new Rect(10, 10, 560, 196),
            $"{state}\n{(leftArm ? "LEFT" : "RIGHT")} arm   angle {Flexion:F1} deg   tracking {(TrackingValid ? "ok" : "LOST")}\n" +
            $"Output {OutputIntensity}   range [{perceptionThreshold}-{UpperIntensity}]   PT {perceptionThreshold}  DT {discomfortThreshold}\n" +
            $"{Status}\nMeta body-tracking calibration: {bodyCalLine}\n{inputLine}", guiBox);
        GUI.backgroundColor = old;
        float y = 214, w = 104, h = 34;
        if (GUI.Button(new Rect(10, y, w, h), "Enable", guiBtn)) guiArm = true;
        if (GUI.Button(new Rect(118, y, w, h), "STOP", guiBtn)) guiStop = true;
        if (GUI.Button(new Rect(226, y, w, h), Calibrating ? "End calib" : "Calibrate", guiBtn)) guiCal = true;
        if (GUI.Button(new Rect(334, y, w, h), "Switch arm", guiBtn)) guiSwitch = true;
        y += h + 6;
        GUI.enabled = Calibrating;
        if (GUI.Button(new Rect(10, y, w, h), "+" + calibrationStep, guiBtn)) guiStep += 1;
        if (GUI.Button(new Rect(118, y, w, h), "-" + calibrationStep, guiBtn)) guiStep -= 1;
        if (GUI.Button(new Rect(226, y, w, h), "Set PT", guiBtn)) guiPt = true;
        if (GUI.Button(new Rect(334, y, w, h), "Set DT", guiBtn)) guiDt = true;
        GUI.enabled = true;
        if (GUI.Button(new Rect(10, y + h + 6, w, h), "Reconnect", guiBtn)) guiReconnect = true;
    }
#endif
}

#if UNITY_EDITOR
/// <summary>Play-mode edits are thrown away when Play stops. This keeps the last PT/DT set during
/// live calibration and writes them into the Inspector when the Editor returns to edit mode
/// (with Undo, and a Console message), so the calibration is not lost.</summary>
[UnityEditor.InitializeOnLoad]
public static class ElbowCalibrationCarry
{
    private const string Key = "ElbowCalibrationCarry.";

    static ElbowCalibrationCarry()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredEditMode) return;
            if (!UnityEditor.SessionState.GetBool(Key + "has", false)) return;
            UnityEditor.SessionState.SetBool(Key + "has", false);
            int pt = UnityEditor.SessionState.GetInt(Key + "pt", 0);
            int dt = UnityEditor.SessionState.GetInt(Key + "dt", 0);
            foreach (var p in UnityEngine.Object.FindObjectsByType<QuestProElbowPrototype>(FindObjectsSortMode.None))
            {
                UnityEditor.Undo.RecordObject(p, "Apply live elbow calibration");
                p.perceptionThreshold = pt;
                p.discomfortThreshold = Mathf.Max(dt, pt);
                UnityEditor.EditorUtility.SetDirty(p);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(p.gameObject.scene);
                Debug.Log($"[Elbow] Applied live calibration to Inspector: PT={pt} DT={p.discomfortThreshold} " +
                          "(Ctrl+Z to undo, Ctrl+S to save the scene)", p);
            }
        };
    }

    public static void Store(int pt, int dt)
    {
        UnityEditor.SessionState.SetBool(Key + "has", true);
        UnityEditor.SessionState.SetInt(Key + "pt", pt);
        UnityEditor.SessionState.SetInt(Key + "dt", dt);
    }
}
#endif
