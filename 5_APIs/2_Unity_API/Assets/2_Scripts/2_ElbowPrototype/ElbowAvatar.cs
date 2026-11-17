using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// First-person mannequin avatar driven directly by Meta body tracking (OVRBody, upper-body joint set):
/// torso, shoulders, upper arms, forearms, palms and fingers built from capsules and spheres.
/// Every limb sits exactly on the tracked joints, so the elbow the participant sees is the elbow whose
/// angle drives the stimulation (no humanoid retargeting offset). No head is drawn, so nothing blocks
/// the first-person camera.
/// If an OVRHand mesh (Meta hand tracking) is tracking a hand, the capsule fingers of that hand are
/// hidden so the realistic hand mesh is shown instead.
/// </summary>
[DefaultExecutionOrder(110)] // after QuestProElbowPrototype has read the body this frame
public class ElbowAvatar : MonoBehaviour
{
    public OVRCameraRig rig;
    public OVRBody body;
    [Tooltip("Optional. Leave empty to use URP Lit at runtime.")]
    public Material avatarMaterial;
    public Color avatarColor = new Color(0.78f, 0.80f, 0.84f);
    [Tooltip("Hide this side's capsule fingers while an OVRHand mesh for that hand is tracked.")]
    public bool preferHandMeshes = true;
    [Range(0.5f, 1.5f)] public float thickness = 1f;

    private struct Segment { public int a, b; public float r; public bool finger; public bool left; public Transform t; }
    private struct Joint { public int j; public float r; public bool finger; public bool left; public Transform t; }

    private readonly List<Segment> segments = new List<Segment>();
    private readonly List<Joint> joints = new List<Joint>();
    private Vector3[] pos = new Vector3[0];
    private bool[] ok = new bool[0];
    private OVRHand leftHandMesh, rightHandMesh;
    private Transform root;

    // OVRPlugin.BoneId.Body_* values for the upper-body joint set (Body_Start = 0)
    private const int Hips = 1, SpineLower = 2, SpineMiddle = 3, SpineUpper = 4, Chest = 5, Neck = 6;
    private const int LShoulder = 8, LUpper = 10, LElbow = 11, LWrist = 19, LThumb = 20, LIndex = 24;
    private const int RShoulder = 13, RUpper = 15, RElbow = 16, RWrist = 45, RThumb = 46, RIndex = 50;

    private void Start()
    {
        if (rig == null || body == null) { enabled = false; return; }
        var mat = avatarMaterial;
        if (mat == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Standard");
            mat = new Material(shader);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.35f);
        }
        else mat = new Material(mat);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", avatarColor);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", avatarColor);

        if (FindFirstObjectByType<Light>() == null) // the prototype scene has no light; Lit would render black
        {
            var lightGo = new GameObject("Avatar key light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.52f);
        }

        root = new GameObject("Elbow avatar").transform;
        root.SetParent(transform, false);
        float k = thickness;

        // Torso and shoulders
        Seg(Hips, SpineMiddle, 0.11f * k, mat); Seg(SpineMiddle, Chest, 0.12f * k, mat); Seg(Chest, Neck, 0.06f * k, mat);
        Seg(LUpper, RUpper, 0.055f * k, mat);                 // shoulder girdle
        Seg(Chest, LShoulder, 0.05f * k, mat); Seg(Chest, RShoulder, 0.05f * k, mat);
        // Arms
        foreach (bool left in new[] { true, false })
        {
            int up = left ? LUpper : RUpper, el = left ? LElbow : RElbow, wr = left ? LWrist : RWrist;
            Seg(up, el, 0.042f * k, mat); Seg(el, wr, 0.034f * k, mat);
            Sph(up, 0.05f * k, mat, false, left); Sph(el, 0.04f * k, mat, false, left); Sph(wr, 0.03f * k, mat, false, left);
            // Hand: palm plus five fingers
            int th = left ? LThumb : RThumb, ix = left ? LIndex : RIndex;
            int middleProx = ix + 6, littleProx = ix + 16, indexProx = ix + 1;
            Seg(wr, middleProx, 0.026f * k, mat, true, left);
            Seg(indexProx, littleProx, 0.016f * k, mat, true, left);
            Seg(wr, th, 0.016f * k, mat, true, left);
            for (int i = 0; i < 3; i++) Seg(th + i, th + i + 1, 0.011f * k, mat, true, left);    // thumb
            for (int f = 0; f < 4; f++)                                                          // index..little
            {
                int b = ix + f * 5; // metacarpal, proximal, intermediate, distal, tip
                for (int i = 1; i < 4; i++) Seg(b + i, b + i + 1, (f == 3 ? 0.008f : 0.0095f) * k, mat, true, left);
            }
        }

        foreach (var h in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
        {
            var sk = h.GetComponent<OVRSkeleton>();
            if (sk == null) continue;
            var type = sk.GetSkeletonType();
            if (type == OVRSkeleton.SkeletonType.HandLeft || type == OVRSkeleton.SkeletonType.XRHandLeft) leftHandMesh = h;
            if (type == OVRSkeleton.SkeletonType.HandRight || type == OVRSkeleton.SkeletonType.XRHandRight) rightHandMesh = h;
        }
    }

    private void Seg(int a, int b, float r, Material m, bool finger = false, bool left = false)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(root, false);
        go.GetComponent<MeshRenderer>().sharedMaterial = m;
        go.SetActive(false);
        segments.Add(new Segment { a = a, b = b, r = r, finger = finger, left = left, t = go.transform });
    }

    private void Sph(int j, float r, Material m, bool finger, bool left)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(root, false);
        go.transform.localScale = Vector3.one * (2f * r);
        go.GetComponent<MeshRenderer>().sharedMaterial = m;
        go.SetActive(false);
        joints.Add(new Joint { j = j, r = r, finger = finger, left = left, t = go.transform });
    }

    private void LateUpdate()
    {
        if (root == null) return;
        var state = body.isActiveAndEnabled ? body.BodyState : null;
        var locs = state.HasValue ? state.Value.JointLocations : null;
        int n = locs != null ? locs.Length : 0;
        if (pos.Length != n) { pos = new Vector3[n]; ok = new bool[n]; }
        for (int i = 0; i < n; i++)
        {
            ok[i] = locs[i].PositionValid;
            if (!ok[i]) continue;
            var p = locs[i].Pose.Position;
            pos[i] = rig.trackingSpace.TransformPoint(new Vector3(p.x, p.y, -p.z)); // OVR right-handed -> Unity
        }
        bool hideLeftFingers = preferHandMeshes && leftHandMesh != null && leftHandMesh.IsTracked;
        bool hideRightFingers = preferHandMeshes && rightHandMesh != null && rightHandMesh.IsTracked;

        foreach (var s in segments)
        {
            bool hide = s.finger && (s.left ? hideLeftFingers : hideRightFingers);
            bool show = !hide && s.a < n && s.b < n && ok[s.a] && ok[s.b];
            Vector3 d = show ? pos[s.b] - pos[s.a] : Vector3.zero;
            show &= d.sqrMagnitude > 1e-6f;
            if (s.t.gameObject.activeSelf != show) s.t.gameObject.SetActive(show);
            if (!show) continue;
            float len = d.magnitude;
            s.t.position = (pos[s.a] + pos[s.b]) * 0.5f;
            s.t.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
            // Unity capsule: height 2 x scale.y, radius 0.5 x scale.x; keep the caps inside the segment
            s.t.localScale = new Vector3(2f * s.r, Mathf.Max(len * 0.5f, s.r), 2f * s.r);
        }
        foreach (var j in joints)
        {
            bool hide = j.finger && (j.left ? hideLeftFingers : hideRightFingers);
            bool show = !hide && j.j < n && ok[j.j];
            if (j.t.gameObject.activeSelf != show) j.t.gameObject.SetActive(show);
            if (show) j.t.position = pos[j.j];
        }
    }

    private void OnDisable()
    {
        if (root != null) root.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        if (root != null) root.gameObject.SetActive(true);
    }
}
