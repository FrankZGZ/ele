using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Drives a humanoid avatar (e.g. Microsoft Rocketbox) from Meta body tracking for first-person
/// embodiment, with Unity XR Hands providing the hands.
///
/// What is driven, every frame, starting from the avatar's rest pose:
///   root     hips placed on the tracked hips, yaw turned to the tracked shoulder line, uniform scale
///            fitted to the user's arm length
///   spine    torso tilted to the tracked hips->chest direction, shoulder line to the tracked one
///   arms     upper arm and forearm DIRECTIONS copied from the tracked shoulder->elbow and elbow->wrist
///            vectors, so the elbow angle you see is the angle that drives the stimulation; the upper
///            arm is twisted so the elbow hinges in the tracked bending plane
///   hands    the avatar's own hand is shrunk away while XR Hands tracks that hand (its mesh is shown
///            instead) and comes back when the hand is not tracked, e.g. while holding a controller
///   head     shrunk away so it never blocks the first-person camera
/// </summary>
[DefaultExecutionOrder(120)]
public class ElbowHumanoidAvatar : MonoBehaviour
{
    public OVRCameraRig rig;
    public OVRBody body;
    public Animator avatar;
    public bool hideHead = true;
    [Tooltip("Shrink the avatar's own hand while XR Hands is tracking that hand.")]
    public bool useXrHands = true;
    [Tooltip("Fit the avatar's size to the user's arm length (shoulder->wrist).")]
    public bool fitArmLength = true;
    [Tooltip("Pin the avatar's shoulders, elbows and wrists onto the tracked joints (bones stretch slightly) so the arm meets the XR hand.")]
    public bool pinArmJoints = true;
    [Tooltip("Limit on how much a bone may stretch or shrink when pinning (fraction of its rest length).")]
    [Range(0f, 1f)] public float maxStretch = 0.5f;

    private const int Hips = 1, Chest = 5, LUpper = 10, LElbow = 11, LWrist = 19, RUpper = 15, RElbow = 16, RWrist = 45;

    private Transform hipsB, spineB, chestB, headB, neckB;
    private readonly Transform[] upperB = new Transform[2], lowerB = new Transform[2], handB = new Transform[2];
    private readonly Dictionary<Transform, Quaternion> rest = new Dictionary<Transform, Quaternion>();
    private readonly Dictionary<Transform, Vector3> restPos = new Dictionary<Transform, Vector3>();
    private readonly Vector3[] hingeLocal = new Vector3[2];
    private readonly Vector3[] lastHinge = new Vector3[2];
    private Quaternion rootRestRot;
    private Vector3 restForwardFlat;
    private float restArmLen, scale = 1f;
    private Vector3[] pos = new Vector3[0];
    private bool[] ok = new bool[0];
    private XRHandSubsystem hands;
    private Renderer[] renderers = new Renderer[0];
    private bool ready, visible = true;

    private void Start()
    {
        if (rig == null || body == null || avatar == null || !avatar.isHuman)
        {
            Debug.LogError("[ElbowAvatar] Needs rig, body and a humanoid Animator.", this);
            enabled = false;
            return;
        }
        hipsB = avatar.GetBoneTransform(HumanBodyBones.Hips);
        spineB = avatar.GetBoneTransform(HumanBodyBones.Spine);
        chestB = avatar.GetBoneTransform(HumanBodyBones.UpperChest) ?? avatar.GetBoneTransform(HumanBodyBones.Chest) ?? spineB;
        neckB = avatar.GetBoneTransform(HumanBodyBones.Neck);
        headB = avatar.GetBoneTransform(HumanBodyBones.Head);
        upperB[0] = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        lowerB[0] = avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        handB[0] = avatar.GetBoneTransform(HumanBodyBones.LeftHand);
        upperB[1] = avatar.GetBoneTransform(HumanBodyBones.RightUpperArm);
        lowerB[1] = avatar.GetBoneTransform(HumanBodyBones.RightLowerArm);
        handB[1] = avatar.GetBoneTransform(HumanBodyBones.RightHand);
        avatar.enabled = false; // no animation controller: we pose the bones ourselves

        foreach (var t in avatar.GetComponentsInChildren<Transform>()) rest[t] = t.localRotation;
        for (int s = 0; s < 2; s++)
            foreach (var t in new[] { upperB[s], lowerB[s], handB[s] }) if (t != null) restPos[t] = t.localPosition;
        rootRestRot = avatar.transform.rotation;
        Vector3 right = upperB[1].position - upperB[0].position;
        restForwardFlat = Flat(Vector3.Cross(right, Vector3.up));
        for (int s = 0; s < 2; s++)
        {
            // Elbow hinge in the rest pose: flexion swings the forearm toward the body's front
            Vector3 upDir = (lowerB[s].position - upperB[s].position).normalized;
            Vector3 hinge = Vector3.Cross(upDir, restForwardFlat).normalized;
            hingeLocal[s] = upperB[s].InverseTransformDirection(hinge);
            lastHinge[s] = hinge;
        }
        restArmLen = 0.5f * (Arm(0) + Arm(1)) / Mathf.Max(1e-4f, avatar.transform.lossyScale.y);
        renderers = avatar.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true; // bounds follow the posed arms
        }
        ready = true;
    }

    private float Arm(int s) => Vector3.Distance(upperB[s].position, lowerB[s].position) + Vector3.Distance(lowerB[s].position, handB[s].position);
    private static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; }

    private void LateUpdate()
    {
        if (!ready) return;
        var state = body.isActiveAndEnabled ? body.BodyState : null;
        var locs = state.HasValue ? state.Value.JointLocations : null;
        int n = locs != null ? locs.Length : 0;
        if (pos.Length != n) { pos = new Vector3[n]; ok = new bool[n]; }
        for (int i = 0; i < n; i++)
        {
            ok[i] = locs[i].PositionValid;
            if (!ok[i]) continue;
            var p = locs[i].Pose.Position;
            pos[i] = rig.trackingSpace.TransformPoint(new Vector3(p.x, p.y, -p.z));
        }
        bool torso = Has(Hips) && Has(Chest) && Has(LUpper) && Has(RUpper);
        if (torso != visible)
        {
            visible = torso; // hide (not deactivate) while tracking is lost, so this script keeps running
            foreach (var r in renderers) if (r != null) r.enabled = torso;
        }
        if (!torso) return;

        // 1. Rest pose
        foreach (var kv in rest) kv.Key.localRotation = kv.Value;
        foreach (var kv in restPos) kv.Key.localPosition = kv.Value;

        // 2. Root: scale, yaw, hips position
        if (fitArmLength && Has(LElbow) && Has(LWrist) && Has(RElbow) && Has(RWrist))
        {
            float user = 0.5f * (Vector3.Distance(pos[LUpper], pos[LElbow]) + Vector3.Distance(pos[LElbow], pos[LWrist]) +
                                 Vector3.Distance(pos[RUpper], pos[RElbow]) + Vector3.Distance(pos[RElbow], pos[RWrist]));
            scale = Mathf.Lerp(scale, Mathf.Clamp(user / Mathf.Max(1e-4f, restArmLen), 0.75f, 1.3f), 0.02f);
        }
        avatar.transform.localScale = Vector3.one * scale;
        Vector3 trackedForward = Flat(Vector3.Cross(pos[RUpper] - pos[LUpper], Vector3.up));
        avatar.transform.rotation = Quaternion.FromToRotation(restForwardFlat, trackedForward) * rootRestRot;
        avatar.transform.position += pos[Hips] - hipsB.position;

        // 3. Torso: lean, then shoulder line, then put the avatar's shoulders on the tracked shoulders
        //    (matters more in first person than the hips: otherwise the neck/collar can sit in front of the camera)
        Rotate(spineB, (upperB[0].position + upperB[1].position) * 0.5f - hipsB.position,
                       (pos[LUpper] + pos[RUpper]) * 0.5f - pos[Hips]);
        Rotate(chestB, upperB[1].position - upperB[0].position, pos[RUpper] - pos[LUpper]);
        avatar.transform.position += (pos[LUpper] + pos[RUpper]) * 0.5f - (upperB[0].position + upperB[1].position) * 0.5f;

        // 4. Arms (directions only, so the visible elbow angle equals the tracked one)
        for (int s = 0; s < 2; s++)
        {
            int sh = s == 0 ? LUpper : RUpper, el = s == 0 ? LElbow : RElbow, wr = s == 0 ? LWrist : RWrist;
            if (!Has(sh) || !Has(el)) continue;
            Vector3 upperDir = (pos[el] - pos[sh]).normalized;
            if (pinArmJoints) upperB[s].position = pos[sh];
            Rotate(upperB[s], lowerB[s].position - upperB[s].position, upperDir);
            if (pinArmJoints) Pin(upperB[s], lowerB[s], pos[el]);
            if (!Has(wr)) continue;
            Vector3 wristTarget = XrWrist(s, out Vector3 xrWrist) ? xrWrist : pos[wr];
            Vector3 foreDir = (wristTarget - pos[el]).normalized;
            // Twist the upper arm about its own axis so the elbow bends in the tracked plane
            Vector3 hinge = Vector3.Cross(upperDir, foreDir);
            if (hinge.sqrMagnitude > 0.03f) lastHinge[s] = hinge.normalized; // keep the last plane when nearly straight
            Vector3 cur = Vector3.ProjectOnPlane(upperB[s].TransformDirection(hingeLocal[s]), upperDir);
            Vector3 want = Vector3.ProjectOnPlane(lastHinge[s], upperDir);
            if (cur.sqrMagnitude > 1e-6f && want.sqrMagnitude > 1e-6f)
                upperB[s].rotation = Quaternion.AngleAxis(Vector3.SignedAngle(cur, want, upperDir), upperDir) * upperB[s].rotation;
            Rotate(lowerB[s], handB[s].position - lowerB[s].position, foreDir);
            if (pinArmJoints) Pin(lowerB[s], handB[s], wristTarget); // forearm ends where the XR hand's wrist is
        }

        // 5. Hands and head
        for (int s = 0; s < 2; s++)
            handB[s].localScale = useXrHands && XrTracked(s) ? Vector3.one * 0.001f : Vector3.one;
        // Shrink from the neck so the neck and collar skin cannot sit in front of the first-person camera
        //    (only when the arms do not hang off the neck: in 3ds Max Biped rigs such as Rocketbox the
        //    clavicles are children of the neck, so shrinking it would shrink the arms too)
        var headRoot = neckB != null && !upperB[0].IsChildOf(neckB) && !upperB[1].IsChildOf(neckB) ? neckB : headB;
        if (headRoot != null) headRoot.localScale = hideHead ? Vector3.one * 0.001f : Vector3.one;
    }

    private bool Has(int j) => j < pos.Length && ok[j];

    /// <summary>Slide the child joint along the (already aimed) bone so it lands on the tracked joint.</summary>
    private void Pin(Transform parent, Transform child, Vector3 target)
    {
        if (parent == null || child == null) return;
        Vector3 bone = child.position - parent.position;
        float len = bone.magnitude;
        if (len < 1e-5f) return;
        float want = Mathf.Clamp(Vector3.Distance(parent.position, target), len * (1f - maxStretch), len * (1f + maxStretch));
        child.position = parent.position + bone / len * want;
    }

    private static void Rotate(Transform bone, Vector3 from, Vector3 to)
    {
        if (bone == null || from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return;
        bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
    }

    private XRHandSubsystem Hands()
    {
        if (hands != null && hands.running) return hands;
        var list = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(list);
        hands = list.Find(h => h.running);
        return hands;
    }

    private bool XrTracked(int side)
    {
        var h = Hands();
        if (h == null) return false;
        return side == 0 ? h.leftHand.isTracked : h.rightHand.isTracked;
    }

    /// <summary>XR Hands wrist in world space, so the avatar's forearm meets the XR hand mesh.</summary>
    private bool XrWrist(int side, out Vector3 world)
    {
        world = default;
        if (!useXrHands || !XrTracked(side)) return false;
        var hand = side == 0 ? hands.leftHand : hands.rightHand;
        if (!hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose p)) return false;
        world = rig.trackingSpace.TransformPoint(p.position); // XR Hands poses are in XR origin (tracking) space
        return true;
    }
}
