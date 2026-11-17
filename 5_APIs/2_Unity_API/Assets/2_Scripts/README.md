# eTactileKit Unity API

This package integrates the **eTactileKit** electro-tactile hardware into Unity. It speaks the same
byte protocol as the reference Python API and gives you a small, clean set of components to:

- connect to the device (Serial or WiFi),
- load a per-electrode **calibration profile** (intensity + hardware mapping),
- define **tactile patterns** in code, and
- trigger those patterns from **collider penetration** in your scene.

There is intentionally **no UI** and **no hand rig** - just the API plus an example controller you can
drop onto any colliders (a tracked finger, a tool tip, etc.).

> Deploying to a **Quest 3 standalone build** (running on the headset, WiFi-only)? See
> [APP_TRANSITION.md](APP_TRANSITION.md) for the full Android/IL2CPP transition guide. This README
> covers the PC / Quest Link setup that both targets share.

---

## 1. Project setup (do this first)

1. **Api Compatibility Level** → `.NET Framework`
   `Project Settings ▸ Player ▸ Other Settings ▸ Configuration ▸ Api Compatibility Level`.
   This is the setting the project is built and tested with.
2. **Active Input Handling** → `Both`
   `Project Settings ▸ Player ▸ Other Settings ▸ Active Input Handling`.
3. **Install the Meta XR All-in-One SDK** (`com.meta.xr.sdk.all`) via the Package Manager (Unity
   Registry / Git URL), plus any other plugins your headset needs. This provides the VR hands /
   controllers you will attach the probe colliders to.

> Serial note: the kit enumerates as a `COMx` port (USB ID `303A:1001`, listed under Device Manager ▸
> Ports); its USB port ignores the baud rate. USB serial works in the Windows Editor and in Windows
> builds (Mono or IL2CPP); on the headset the kit is reached over WiFi.
>
> WiFi note: Unity connects by **IP address** or by **UDP discovery** (see §5). Unity/.NET has
> **no mDNS resolver**, so the board's `etactilekit-<ID>.local` hostname will **not** resolve here -
> use discovery (recommended) or a raw IP. Each board self-labels with a unique 6-hex `ID` (from its
> MAC); list every kit's ID and IP with the Python discovery script (`python -m api.discovery`), or
> read them from the Unity Console when the manager connects. With a single kit you need no ID at all.

---

## 2. How it fits together

```text
HapticController            (your scene)
  ├─ reads probe colliders, finds the deepest penetration each frame
  ├─ resolves the touched object's HapticProfile via HapticProfileProvider
  └─ plays the profile's pattern (frame timing + Once/Loop/Bounce)
        │
        ▼
ETactileKitManager         (connection + calibration + global params)
  └─ SendFrame(states): on-electrodes → calibrated intensity, off → 0
        │
        ▼
ETactileKit                (0_eTactileKitAPI: byte protocol port)
  └─ remaps to hardware channels, packs uint16 LE, writes one packet
        │
        ▼
Serial / WiFi handler  →  ESP32 hardware
```

| Concept | Script | Role |
|---|---|---|
| Device driver | `0_eTactileKitAPI/ETactileKit.cs` | Low-level protocol (no Unity types). |
| Transport | `SerialHandler.cs` / `WiFiHandler.cs` | USB serial (Win32) / TCP; one write per frame. |
| Discovery | `0_eTactileKitAPI/ETactileKitDiscovery.cs` | Find kits on the LAN by unique ID (UDP broadcast). |
| Manager | `1_Haptics/ETactileKitManager.cs` | Connection, global params, calibration, `SendFrame`. |
| Calibration | `CalibrationProfile.cs` | Per-electrode intensity + hardware mapping. |
| Patterns | `PatternDB.cs` | Code-defined `HapticPattern`s (frames + playback mode). |
| Profile asset | `HapticProfile.cs` | Which pattern + trigger depth for an object. |
| Provider | `HapticProfileProvider.cs` | Attaches a profile to an object. |
| Controller | `HapticController.cs` | Penetration → pattern playback. |

### Who owns the kit

The firmware listens on USB serial and WiFi at the same time, and the first host to connect owns the
kit until it disconnects:

- **A serial host owns the kit** from the moment it opens the port until it closes it. The kit
  switches its WiFi radio off meanwhile, so it is not discoverable: while the Unity Editor plays a
  scene set to `Serial`, the headset build and the Python API cannot find the kit. WiFi comes back a
  few seconds after the port closes.
- **A WiFi host owns the kit** while its TCP connection is open. Other TCP clients are refused,
  discovery reports the kit as busy, and a serial host gets no answers. A host that vanishes without
  closing (out of range, crashed) is dropped after about 5 s, and one that leaves a reply unread for
  0.25 s once its receive buffer is full is dropped too.
- **A connect succeeds only once the kit answers** a link check on that link (two sync requests), and
  the electrode number the kit reports is adopted, because the kit frames stimulation patterns by it.
  Connecting while already connected is refused; disconnect first.
- **A refused serial host stays refused until it closes and reopens its port**, so it is never
  handed the kit halfway through a command stream. A failed serial connect closes the port, so
  connecting again once the kit is free gets a new session.
- **Every connect and disconnect zeroes all amplitudes** (stimulation pattern and sense pulse height).
  The kit keeps every other setting — electrode number, mode, polarity, pulse widths, discharge time
  and frequency — so a new host inherits the previous host's until its setup runs.
- **The kit notices a closed serial port after 0.25 s.** A port reopened sooner continues the previous
  session, so the serial transport waits 0.4 s after closing a port before it reopens it. Leave the
  same gap when another program takes over the port.
- **A failed write closes the link**, so the kit ends the session and zeroes every amplitude instead
  of continuing a half-sent command (a failed WiFi send resets the connection, so frames still queued
  are discarded). The manager does not reconnect by itself; reconnect to carry on.
- **The kit ends a session whose byte stream is out of step.** The protocol has no checksum, so an
  unknown command, a mode or polarity outside its range, a frequency of 0, a pulse height above 4095 or a payload that
  does not complete within 2 s means the bytes that follow could be read as anything. The kit zeroes
  every amplitude and drops the host: a TCP connection is closed, and a serial host's port stops
  working (the kit re-enumerates on USB). This API never sends such a stream; reconnect after one.
- **WiFi frames go out at least 4 ms apart.** The kit's TCP stack falls behind faster bursts, which
  can stall the link for seconds (an OFF frame then waits too) or make the kit reset the connection,
  especially on a busy channel; streams slower than 250 frames/s are not delayed.
- **Replies arrive in order.** The bytes a timed-out reply still owes are read and discarded before
  the next request, so they are never taken for its answer; a reply still missing after 2 s closes
  the link.
- **`ETactileKit.Disconnect` sends a zero pattern and sense pulse height 0 before it closes**, so
  nothing keeps running for the 0.25 s the kit takes to notice a closed serial port.

The serial transport configures the port fully on every open and keeps RTS and DTR low, so opening
or closing the port never reboots the board (the ESP32-S3 resets when it sees RTS high with DTR low).

---

## 3. Calibration

Calibration stores **only** two things per electrode: the hardware **mapping** and the **intensity**
applied when that electrode is on. Everything else (pulse width, frequency, stim mode, …) is a global
parameter on the manager.

- **Generate a profile** with the **eTactileKitExplorer** desktop application, which exports the
  calibration JSON used here (per-electrode `mapping` + `intensity`).
- **Where it lives:** put the profile in `Assets/StreamingAssets/`. A sample is provided at
  `Assets/StreamingAssets/template_32_electrode_calibration.json` so it travels with the cloned repo.
- On the `ETactileKitManager`, set **Default Calibration File Name** to the file name inside
  `StreamingAssets` (default: `template_32_electrode_calibration.json`). On `Start` that file is loaded
  and replaces the profile stored in the scene; the stored profile is used only when the name is empty
  or the file is missing or invalid. The Console logs which one is used.
- In the manager inspector, **Calibration Profile** lets you **Import JSON** / **Export JSON** (the
  same format eTactileKitExplorer produces) and tune each electrode's **Intensity** directly. Import JSON
  also clears **Default Calibration File Name**, so the imported profile is the one used on Start; to
  play with tuned values, clear the name yourself (the inspector shows a note while a name is set, and
  a warning is logged when the file replaces a different stored profile).
- A profile is rejected (with the reason in the Console) if, for example, it has duplicate or
  out-of-range electrode ids, a mapping that is not a permutation of `0..count-1`, more than 255
  electrodes, or an intensity outside 0–4095.

### Global amplitude (gain)

The manager's **Amplitude Gain** slider scales **every** electrode's calibrated intensity as it is
sent, so you can adjust overall strength in one place while keeping the per-electrode balance from
calibration:

- Range **0 – 1.5**, default **1** (1 = use the calibration values exactly as they are).
- The scaled value is **rounded to the nearest integer** (the hardware only takes integers) and
  **clamped to the 12-bit DAC range** (0 – 4095), so a high calibration value with a gain above 1 can
  never overflow.
- It applies **live** - drag the slider while playing and a frame that is on is sent again with the
  new gain; no reconnect or re-calibration needed. It does not modify the calibration profile itself.
- From code: `manager.AmplitudeGain = 0.8f;` (clamped to the same range).

The manager runs this setup on connect and after a profile import, through `ETactileKit.Configure`:

```text
link check (the kit's electrode number is adopted) →
check that one scan fits the period (nothing is sent otherwise) → set mapping →
electrode number 0 → frequency → stim mode → polarity → pulse width →
sense pulse height → sense pulse width → channel discharge time → electrode number →
sync check (must equal the profile's electrode count) → read HV513 count
```

Each setting takes effect while the kit keeps scanning at its current (possibly a previous host's)
period, so the electrode number is 0 while the others change: no intermediate state can overrun.
The sync check runs before the HV513 query so that a late HV513 answer cannot be read as the electrode
number. If the profile or a setting is refused, the link fails, or the sync check does not match, the
manager logs why and disconnects, so nothing is streamed to a kit whose setup is unverified. An HV513
count with fewer outputs (8 per chip) than the profile has electrodes gives a warning.

**Scan time.** The kit pulses every electrode once per period, one after another. With a scan that
does not fit in the period the kit scans back to back, below the frequency that was set, and it ends a
scan after 260 ms, leaving out the electrodes it has not reached. The API keeps the kit away from
both. `ETactileKit.EstimateScanUs` gives a conservative scan time,
`n × (phases × (max(pulse width, 10) + 8) + discharge time + 5) + 80` µs (phases = 2 for biphasic; the
sense pulse width replaces the pulse width in Impedance Only mode), and every setting that would make
it exceed **80% of the period or 250 ms** is refused (the message gives the highest frequency that
would fit, when the scan is under 250 ms). For example 32 electrodes biphasic at 100 µs with 50 µs discharge take about 8.8 ms, so at
most 91 Hz; 128 electrodes take about 35 ms, so at most 23 Hz. A setting changed later through
`ETactileKit` (for example the frequency) is checked the same way.

---

## 4. Creating a tactile pattern

Patterns are defined in code in `1_Haptics/PatternDB.cs`. A pattern is a list of **frames**; each
frame **lists the electrode ids that are ON** (value 1) plus an **on-time** and **off-time**
(milliseconds). When a frame is sent, each listed electrode is driven at its calibrated intensity and
all others are off. Listing active ids keeps patterns **independent of the electrode count** - that
count comes from the loaded calibration profile and is never hard-coded.

A pattern also has a **playback mode**:

- `Once` – play the frames one time, then stay silent until the probe re-enters.
- `Loop` – repeat frames `0..N-1` continuously while penetrated.
- `Bounce` – play frames forward then backward (ping-pong) while penetrated.

The two example patterns (`HorizontalScan`, `VerticalScan`) are authored by hand for the default
"Fingerpad TactDeform 32" layout - a 6×6 grid with the four corners removed:

```text
        col0  col1  col2  col3  col4  col5
 row0:    ·     0     1     2     3     ·
 row1:    4     5     6     7     8     9
 row2:   10    11    12    13    14    15
 row3:   16    17    18    19    20    21
 row4:   22    23    24    25    26    27
 row5:    ·    28    29    30    31     ·
```

`HorizontalScan` lights one **column** per frame (a vertical bar moving left→right); `VerticalScan`
lights one **row** per frame.

Patterns can be authored two ways:

- **Manual** – list the active ids per frame (`HorizontalScan`, `VerticalScan`). Best for spatial
  shapes tied to your electrode layout.
- **Programmatic** – build frames with a loop from the electrode count (`Blink`, `Buzz`). These take
  the active count so they work for any layout, and the result is cached:
  - `Blink` – all electrodes on for 200 ms, off for 200 ms, looping.
  - `Buzz` – one electrode per frame, 40 ms on / 0 ms off, scanning through all of them.

To add your own pattern:

1. Add a value to the `PatternId` enum.
2. Build a `HapticPattern` – either list the active ids per frame, or generate them in a loop (see
   `Blink` / `Buzz`).
3. Return it from `PatternDB.Get(id, electrodeCount)`.

If your hardware numbers electrodes differently, adjust the ids per frame to match. Ids outside the
active electrode range are ignored with a warning.

---

## 5. Example scene, step by step

1. **Manager** – create an empty GameObject, add `ETactileKitManager`.
   - **Serial:** choose `Serial` and set `Port Name` to the kit's `COMx` (`Baud Rate` is ignored).
   - **WiFi (recommended, STA):** choose `WiFi` and turn `Use Discovery` on (it is off by default). For a **single kit**,
     leave `Target Device Id` blank - it connects to the only board found. For **several kits**, set
     `Target Device Id` to the intended board's 6-hex code (from its label, the Unity Console log, or
     `python -m api.discovery`): while more than one kit answers and no ID is set, the manager refuses
     to connect rather than pick one, and it never connects to a kit that is busy with another WiFi
     host. The manager broadcasts on connect and resolves the kit's current IP automatically, so many
     kits can share one router. `Discovery Timeout Ms` sets how long it waits.
   - **WiFi (manual / AP fallback):** turn `Use Discovery` off and set `IP Address` - a fixed/reserved
     IP, or `192.168.4.1` after joining a board's own `eTactileKit_<ID>` hotspot. With discovery on,
     the manager also tries `IP Address` when no kit answers and no `Target Device Id` is set (a
     requested kit is never substituted). `Wifi Port` is `8080`.
   - Set **Default Calibration File Name** to your profile in `StreamingAssets` (the sample
     `template_32_electrode_calibration.json` works out of the box), or **Import JSON** your own.
   - Leave the global stim params at their defaults (Monophasic, Anodic, 100 µs, 50 Hz) unless you have
     reason to change them. The sense pulse settings are only used in `Impedance Only` mode.
   - Per-electrode **Intensity** and mapping edits reach the kit at the next connect or profile import;
     **Amplitude Gain** applies live.
2. **Profile asset** – `Assets ▸ Create ▸ eTactileKit ▸ Haptic Profile`.
   - Pick a `Pattern Id` (e.g. `HorizontalScan`) and a `Min Penetration Depth Meters` (e.g. `0.002`).
3. **Touchable object** – on the object you want to feel:
   - add a `Collider`,
   - add `HapticProfileProvider` and assign the profile,
   - put the object on a dedicated layer (e.g. create an `Interactable` layer).
4. **Probe(s)** – add a `SphereCollider` to each finger tip / tool tip that should trigger haptics,
   and put them on a dedicated **probe layer** (e.g. create a `Probe` layer). No Rigidbody is
   required - penetration is computed directly, and probes are found automatically by layer.
5. **Controller** – add `HapticController` (anywhere):
   - assign `Manager`,
   - set `Probe Layer` to your probe layer,
   - set `Interactable Layer` to the touchable object's layer.
6. **Kill switch** – add a `HapticKillSwitch` component (e.g. on the manager's GameObject); assign
   `Manager` and your left/right `OVRHand`s. Emergency-stop gesture: middle + ring + pinky pinch held
   ~1 s (see §6).
7. **Play** – push a probe into the object past the threshold depth; the pattern plays at the
   calibrated intensity and stops when the probe withdraws.

> **Layers are the #1 cause of "nothing is detected".** The probe collider (finger/tool tip) and the
> touchable object must be on **two different layers**, and each must be selected on the controller:
> the finger's layer → `Probe Layer`, the object's layer → `Interactable Layer`. A finger left on the
> `Default` layer while `Probe Layer` points at a `Haptic Probe` layer will never be found - the
> controller logs a warning when it finds no probes. Use a dedicated probe layer (not `Default`) so
> unrelated colliders are not scanned. No Rigidbody is needed and the probe may be `Is Trigger`.
>
> Playback is **tied to the touched object** (its `HapticProfileProvider`). While the same object
> stays penetrated past the threshold the pattern keeps advancing, also when contact moves between its
> child colliders - it is *not* replayed from the start each frame. A different object (or leaving and
> re-entering) starts the pattern over. A momentary loss of contact (physics jitter, depth chatter at
> the threshold) within `Reacquire Grace Seconds` (default 0.2 s) **resumes** at the current frame
> instead of restarting; set it to 0 to restart on any loss. While the game is paused with
> `Time.timeScale = 0` the output is off; the pattern resumes afterwards. Enable `Log State Changes` on
> the controller to watch start/resume/suspend transitions in the Console.
>
> Tip: enable **Run Connection Test** on the manager to pulse all electrodes on/off every 0.5 s for 10
> cycles after connecting, as a quick hardware check (it ignores controller input while running).

---

## 6. Safety

- **Emergency stop = hand gesture.** Add a `HapticKillSwitch` component and assign the manager plus
  your `OVRHand`s. Holding an uncommon pose - **middle + ring + pinky pinched to the thumb** (index
  left free) for ~1 s on either hand - immediately sends OFF and disconnects. The same gesture works
  on PC / Quest Link and on the standalone headset (no keyboard needed), and it is timed in real time,
  so it also works while the game is paused with `Time.timeScale = 0`.
- OFF is also sent automatically on disconnect, on `OnDisable`, on application quit, and when the
  app pauses (headset taken off or asleep, play mode paused in the Editor): the kit keeps running the
  last pattern it was sent, and the haptics layer only sends on changes.
- **The kit zeroes every amplitude when its host disconnects**, including when a write fails (the
  link is closed) or the host vanishes. A serial write the kit has not taken within 1 s closes the
  link; over WiFi that limit applies once the socket's send buffer is full, and the kit drops a host
  that stops answering after about 5 s. A closed serial port ends the session after 0.25 s.
- In `Impedance Only` mode the kit ignores patterns and sends the sense pulse to every electrode on
  every scan, so the manager's OFF also sets the sense pulse height to 0; sensing resumes at the next
  setup (reconnect or profile import).
- **Scan time:** setup refuses settings whose scan would not fit in 80% of the period (see §3), which
  keeps every scan clear of its period.
- **Amplitude Gain** above `1` drives every electrode *harder than calibrated* — raise it slowly and
  only if the calibrated level is too weak. `0` silences the output without disconnecting.

---

## 7. Building for the Quest 3 headset (standalone)

The same project also runs **directly on the Quest 3** (no PC). The code already builds for both
targets — serial is auto-excluded on Android, the calibration loads from inside the APK, and the
`HapticKillSwitch` gesture is identical — so this is mostly build settings. Full detail, deployment
and troubleshooting are in [APP_TRANSITION.md](APP_TRANSITION.md); the essentials:

1. **Install Android Build Support** (Unity Hub), then `File ▸ Build Settings ▸ Android ▸ Switch
   Platform`.
2. **Run the Meta XR Project Setup Tool** (`Meta ▸ Tools ▸ Project Setup Tool`, Android tab → *Apply
   All*) — it sets IL2CPP, ARM64, Vulkan, API levels, etc.
3. **XR Plug-in Management ▸ Android** → enable **Oculus**; enable **Hand Tracking Support** on
   `OVRManager` (needed for the probes and the kill switch).
4. **Player ▸ Android:** Scripting Backend **IL2CPP**, Target Architecture **ARM64**, Api Compatibility
   **.NET Framework**, **Internet Access = Require** (adds the `INTERNET` permission), unique Package Name.
5. **Manager:** Connection Type **WiFi**, **Use Discovery off**, **IP Address** = the kit's IP (STA:
   the IP reserved for it on the router; AP: `192.168.4.1`), port `8080`. The kit must be free: stop play mode in the
   Editor (on `Serial` it switches the kit's WiFi off) and disconnect any other host.
6. **Deploy:** enable Developer Mode on the Quest, connect USB-C, allow USB debugging, then **Build &
   Run** (`Ctrl+B`). View logs with `adb logcat -s Unity`.

On the headset the kit is reached over **WiFi only** (USB serial is Windows-only); everything else —
patterns, calibration, penetration → haptics — behaves exactly as over Quest Link.

To switch a headset-configured project **back to PC / Quest Link**, see
[APP_TRANSITION.md §11 (Android → Windows)](APP_TRANSITION.md) — it's a platform toggle with **no code
changes** (serial and file-based calibration re-enable themselves; turn `Use Discovery` back on if you
want discovery).

---

## 8. Troubleshooting

| Symptom | Check |
|---|---|
| No collisions detected at all | Finger/tool collider's Layer must match the controller's `Probe Layer` (a `Default`-layer finger with a different `Probe Layer` is never found - watch for the "found no probe colliders" warning). Object's Layer must be in `Interactable Layer`, and the two must differ. |
| Detected but no stimulation | Penetration ≥ profile `Min Penetration Depth Meters`? Profile `Enable Haptics` on? Probe actually overlapping (not just touching) the object? Device connected and not in Run Connection Test? **Amplitude Gain** not 0? |
| Stimulation too weak / too strong | Adjust the manager's **Amplitude Gain** (0–1.5, default 1) — it scales all calibrated intensities live. If only *some* electrodes feel wrong, the per-electrode balance is off: re-calibrate in eTactileKitExplorer, or tune individual **Intensity** values in the manager's calibration list (they reach the kit at the next connect or profile import; clear `Default Calibration File Name` first, or the file replaces them at Start). |
| "Cannot connect without a valid calibration profile" | Assign / import a valid profile (mapping must be a permutation of `0..count-1`). |
| WiFi: "No kits answered discovery" | Is the kit connected over USB serial (a Unity Editor playing on `Serial`, the Python API, the desktop app)? Its WiFi is then off. PC and kits on the same router/subnet? Router allowing UDP broadcast (disable client/AP isolation)? Windows Firewall allowing Unity inbound UDP:8888? Did the board fall back to AP mode? (its `eTactileKit_<ID>` SSID would show in your Wi-Fi list). With no `Target Device Id` set, the manager then tries `IP Address`. |
| WiFi: "Several kits found" | Set `Target Device Id` to the intended board's 6-hex code; the manager never picks one of several kits on its own. |
| WiFi: "in use by another WiFi host" / "refused the connection" | Another host (a headset build, the Editor, the Python API, the desktop app) owns the kit. Disconnect it; a host that vanished is dropped by the kit after about 5 s. |
| WiFi: connect timed out | Wrong IP, PC not on the kit's network, or a USB serial host owns the kit (its WiFi is then off). |
| WiFi: `*.local` never connects | Expected - Unity/.NET can't resolve mDNS; use `Use Discovery` or a raw IP. |
| Serial: "no answer: the kit is connected to a WiFi host" | A WiFi host owns the kit; disconnect it, then connect again. Also check the board runs eTactileKit firmware. |
| Serial: "Access is denied (Win32 error 5)" | Another program has the port open (the Python API, a serial monitor, the desktop app, another Unity instance); close it. |
| Serial: "The system cannot find the file specified (Win32 error 2)" | Wrong `COMx`, or the kit is not plugged in (Device Manager ▸ Ports, USB ID `303A:1001`). |
| "USB serial is unavailable on this platform - using WiFi" | Expected on the headset and other non-Windows targets: the manager switches to WiFi. (Code that calls `ETactileKit.ConnectSerial` directly gets "USB serial is available in the Windows Editor and Windows builds only".) |
| "Setup failed: ... Disconnected." | The reason is in the message. A mismatched electrode number means another host changed the kit during setup or the link lost data; reconnect. |
| "Not sent: one scan of N electrodes would take about ..." | The global parameters would overrun the scan period; lower `Frequency Hz` to the value given, or the electrode count, pulse width or discharge time. |
| "Using the calibration profile stored in the scene" / "Could not use the calibration file" | `Default Calibration File Name` is empty, or the StreamingAssets file it names is missing or invalid (the reason is logged), so the scene's stored profile is used. |
| The kit reboots when a program opens or closes its port | The ESP32-S3 resets when RTS is high while DTR is low. This API and the Python API never do that; serial monitors and older tools can (set them to DTR and RTS off). |
| "Electrode id N is out of range" warning | A pattern frame lists an id ≥ the calibration's electrode count; fix the ids in `PatternDB.cs`. |
