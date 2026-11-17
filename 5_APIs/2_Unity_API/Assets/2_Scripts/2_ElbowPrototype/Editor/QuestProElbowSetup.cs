using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.Rendering.Universal;
using UnityEditor.Rendering;

public static class QuestProElbowSetup
{
    public const string ScenePath = "Assets/1_Scenes/QuestPro_Elbow_Prototype.unity";

    // Headless entry point: -buildTarget Android -executeMethod QuestProElbowSetup.BuildBatch -elbowApk <path>
    public static void BuildBatch()
    {
        CreateScene();
        ConfigureAndroid();
        ConfigurePrototypeRendering();
        var kit = Object.FindFirstObjectByType<ETactileKitManager>();
        var kitSettings = new SerializedObject(kit);
        kitSettings.FindProperty("useDiscovery").boolValue = true;
        kitSettings.FindProperty("targetDeviceId").stringValue = "604FE0";
        kitSettings.FindProperty("discoveryTimeoutMs").intValue = 2000;
        kitSettings.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(kit.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        var args = System.Environment.GetCommandLineArgs();
        int index = System.Array.IndexOf(args, "-elbowApk");
        if (index < 0 || index + 1 >= args.Length) throw new System.ArgumentException("Missing -elbowApk output path.");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { ScenePath }, locationPathName = args[index + 1],
            target = BuildTarget.Android, options = BuildOptions.Development
        });
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            throw new System.Exception("Quest Pro APK build failed: " + report.summary.result);
        Debug.Log("ELBOW_APK_BUILD_SUCCEEDED " + args[index + 1]);
    }

    private static void ConfigurePrototypeRendering()
    {
        const string folder = "Assets/2_Scripts/2_ElbowPrototype/";
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(folder + "QuestProPipeline.asset");
        if (pipeline == null)
        {
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, folder + "QuestProRenderer.asset");
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.msaaSampleCount = 4;
            pipeline.supportsHDR = false;
            AssetDatabase.CreateAsset(pipeline, folder + "QuestProPipeline.asset");
        }
        GraphicsSettings.defaultRenderPipeline = pipeline;
        int previous = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = pipeline;
        }
        QualitySettings.SetQualityLevel(previous, false);
        var global = AssetDatabase.LoadAssetAtPath<RenderPipelineGlobalSettings>(folder + "QuestProGlobalSettings.asset");
        if (global == null)
        {
            var type = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings", true);
            global = RenderPipelineGlobalSettingsUtils.Create(type, folder + "QuestProGlobalSettings.asset");
        }
        EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(global);
        AssetDatabase.SaveAssets();
    }

    [MenuItem("eTactileKit/Quest Pro/Create Elbow Prototype Scene")]
    public static void CreateScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (File.Exists(ScenePath))
        {
            EditorSceneManager.OpenScene(ScenePath);
            return;
        }
        string prefabPath = AssetDatabase.GUIDToAssetPath("126d619cf4daa52469682f85c1378b4a");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) throw new System.InvalidOperationException("Resolve Meta XR Core 201.0.0 in Package Manager first.");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var rig = rigObject.GetComponent<OVRCameraRig>();
        var ovr = rigObject.GetComponent<OVRManager>();
        if (ovr == null) ovr = rigObject.AddComponent<OVRManager>();
        var settings = new SerializedObject(ovr);
        var request = settings.FindProperty("requestBodyTrackingPermissionOnStartup");
        if (request == null) throw new System.InvalidOperationException("Meta body permission setting was not found.");
        request.boolValue = true;
        settings.ApplyModifiedPropertiesWithoutUndo();
        var bodyObject = new GameObject("Meta Upper Body");
        bodyObject.transform.SetParent(rig.trackingSpace, false);
        var body = bodyObject.AddComponent<OVRBody>();
        body.ProvidedSkeletonType = OVRPlugin.BodyJointSet.UpperBody;
        var root = new GameObject("Elbow WiFi Prototype");
        var manager = root.AddComponent<ETactileKitManager>();
        var ms = new SerializedObject(manager);
        ms.FindProperty("connectionType").enumValueIndex = (int)ConnectionType.WiFi;
        ms.FindProperty("amplitudeGain").floatValue = 0;
        ms.FindProperty("runConnectionTest").boolValue = false;
        ms.FindProperty("useDiscovery").boolValue = false;
        ms.ApplyModifiedPropertiesWithoutUndo();
        var controller = root.AddComponent<QuestProElbowPrototype>();
        controller.rig = rig;
        controller.body = body;
        controller.manager = manager;
        const string materialPath = "Assets/2_Scripts/2_ElbowPrototype/BodyLine.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader == null) throw new System.InvalidOperationException("No unlit line shader found.");
            material = new Material(shader);
            material.SetColor(material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", Color.cyan);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        controller.lineMaterial = material;
        rig.centerEyeAnchor.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
        rig.centerEyeAnchor.GetComponent<Camera>().backgroundColor = new Color(0.025f, 0.035f, 0.06f);
        ConfigureAndroid();
        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = root;
        AssetDatabase.SaveAssets();
        Debug.Log("Elbow scene created. Set the manager's IP and calibration file, then build Android. A enables output; B stops.");
    }

    /// <summary>Adds Meta's tracked hand meshes (OVRHandPrefab) under the rig's hand anchors and turns on
    /// controller-driven hand poses, so a hand is shown both with bare hands and while holding controllers.
    /// The mannequin avatar hides its capsule fingers whenever one of these meshes is tracking.</summary>
    [MenuItem("eTactileKit/Quest Pro/Add Hand Meshes (Meta OVRHand)")]
    public static void AddHandMeshes()
    {
        var rig = Object.FindFirstObjectByType<OVRCameraRig>();
        if (rig == null) { Debug.LogError("Open the elbow prototype scene first (no OVRCameraRig found)."); return; }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.meta.xr.sdk.core/Prefabs/OVRHandPrefab.prefab");
        if (prefab == null) { Debug.LogError("OVRHandPrefab not found in Meta XR Core SDK."); return; }
        foreach (bool left in new[] { true, false })
        {
            Transform anchor = left ? rig.leftHandAnchor : rig.rightHandAnchor;
            string name = left ? "Left Hand Mesh" : "Right Hand Mesh";
            if (anchor.Find(name) != null) continue; // already added
            var hand = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
            hand.name = name;
            Undo.RegisterCreatedObjectUndo(hand, "Add hand mesh");
            SetInt(hand.GetComponent<OVRHand>(), "HandType", left ? (int)OVRHand.Hand.HandLeft : (int)OVRHand.Hand.HandRight);
            SetInt(hand.GetComponent<OVRSkeleton>(), "_skeletonType", left ? (int)OVRSkeleton.SkeletonType.HandLeft : (int)OVRSkeleton.SkeletonType.HandRight);
            SetInt(hand.GetComponent<OVRMesh>(), "_meshType", left ? (int)OVRMesh.MeshType.HandLeft : (int)OVRMesh.MeshType.HandRight);
        }
        var ovr = rig.GetComponent<OVRManager>();
        if (ovr != null)
        {
            Undo.RecordObject(ovr, "Controller driven hands");
            ovr.controllerDrivenHandPosesType = OVRManager.ControllerDrivenHandPosesType.Natural;
            EditorUtility.SetDirty(ovr);
        }
        var config = OVRProjectConfig.CachedProjectConfig;
        config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
        OVRProjectConfig.CommitProjectConfig(config);
        EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
        Debug.Log("Hand meshes added under the rig's hand anchors (controller-driven hand poses: Natural). Save the scene (Ctrl+S). " +
                  "Over Link, hand tracking needs 'Developer Runtime Features' on in the Meta Quest Link app.");
    }

    public const string RocketboxFbx = "Assets/3_Avatars/Rocketbox/Female_Adult_01/Export/Female_Adult_01.fbx";
    public const string HandSampleDir = "Assets/Samples/XR Hands/1.7.2/HandVisualizer/";

    /// <summary>Puts the Rocketbox avatar in the open scene as the participant's own body (driven by
    /// ElbowHumanoidAvatar from body tracking) and adds the XR Hands hand meshes. Running it again
    /// replaces nothing that already exists.</summary>
    [MenuItem("eTactileKit/Quest Pro/Set Up Rocketbox Avatar + XR Hands")]
    public static void SetUpHumanoidAvatar()
    {
        var rig = Object.FindFirstObjectByType<OVRCameraRig>();
        var body = Object.FindFirstObjectByType<OVRBody>();
        var proto = Object.FindFirstObjectByType<QuestProElbowPrototype>();
        if (rig == null || body == null || proto == null) { Debug.LogError("Open the elbow prototype scene first."); return; }
        const string folder = "Assets/3_Avatars/Rocketbox/";

        // Avatar
        var driver = Object.FindFirstObjectByType<ElbowHumanoidAvatar>();
        if (driver == null)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(RocketboxFbx);
            if (fbx == null) { Debug.LogError("Rocketbox FBX not found at " + RocketboxFbx); return; }
            var avatarGo = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            avatarGo.name = "Self Avatar (Rocketbox Female_Adult_01)";
            Undo.RegisterCreatedObjectUndo(avatarGo, "Add self avatar");
            var bodyMat = AssetDatabase.LoadAssetAtPath<Material>(folder + "Rocketbox_Body_URP.mat");
            if (bodyMat == null)
            {
                bodyMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                bodyMat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "Female_Adult_01/Textures/f001_body_color.tga"));
                var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "Female_Adult_01/Textures/f001_body_normal.tga");
                if (normal != null) { bodyMat.SetTexture("_BumpMap", normal); bodyMat.EnableKeyword("_NORMALMAP"); }
                bodyMat.SetFloat("_Smoothness", 0.25f);
                AssetDatabase.CreateAsset(bodyMat, folder + "Rocketbox_Body_URP.mat");
            }
            foreach (var r in avatarGo.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = bodyMat; // head/opacity slots are hidden with the head
                r.sharedMaterials = mats;
            }
            var holder = new GameObject("Self Avatar Driver");
            Undo.RegisterCreatedObjectUndo(holder, "Add self avatar");
            driver = holder.AddComponent<ElbowHumanoidAvatar>();
            driver.rig = rig;
            driver.body = body;
            driver.avatar = avatarGo.GetComponent<Animator>();
            if (driver.avatar == null) driver.avatar = avatarGo.AddComponent<Animator>();
        }

        // XR Hands meshes (Unity XR Hands HandVisualizer sample prefabs)
        var handMat = AssetDatabase.LoadAssetAtPath<Material>(folder + "XRHand_Skin_URP.mat");
        if (handMat == null)
        {
            handMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            handMat.SetColor("_BaseColor", new Color(0.87f, 0.72f, 0.62f)); // change to match the avatar if needed
            handMat.SetFloat("_Smoothness", 0.3f);
            AssetDatabase.CreateAsset(handMat, folder + "XRHand_Skin_URP.mat");
        }
        foreach (bool left in new[] { true, false })
        {
            string name = left ? "XR Hand Left" : "XR Hand Right";
            if (rig.trackingSpace.Find(name) != null) continue;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandSampleDir + (left ? "Prefabs/Left Hand Tracking.prefab" : "Prefabs/Right Hand Tracking.prefab"));
            if (prefab == null) { Debug.LogError("XR Hands sample prefab missing under " + HandSampleDir); continue; }
            var hand = (GameObject)PrefabUtility.InstantiatePrefab(prefab, rig.trackingSpace); // XR Hands poses are in tracking space
            hand.name = name;
            Undo.RegisterCreatedObjectUndo(hand, "Add XR hand");
            foreach (var r in hand.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = handMat;
                r.sharedMaterials = mats;
            }
        }

        // OpenXR hand tracking subsystem for both PC (Link) and Android
        foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
        {
            var xr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (xr == null) continue;
            foreach (var feature in xr.GetFeatures<OpenXRFeature>())
                if (feature.GetType().Name == "HandTracking") { feature.enabled = true; EditorUtility.SetDirty(feature); }
            EditorUtility.SetDirty(xr);
        }
        var config = OVRProjectConfig.CachedProjectConfig;
        config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
        OVRProjectConfig.CommitProjectConfig(config);

        proto.showSkeletonLines = false;
        EditorUtility.SetDirty(proto);
        EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = driver.gameObject;
        Debug.Log("Self avatar + XR Hands set up. Save the scene (Ctrl+S). Over Link, hand tracking needs " +
                  "'Developer Runtime Features' on in the Meta Quest Link app.");
    }

    private static void SetInt(Object target, string field, int value)
    {
        if (target == null) return;
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { Debug.LogWarning($"Field {field} not found on {target.GetType().Name}"); return; }
        p.intValue = value;
        so.ApplyModifiedProperties();
    }

    [MenuItem("eTactileKit/Quest Pro/Configure Android Prototype")]
    public static void ConfigureAndroid()
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        config.bodyTrackingSupport = OVRProjectConfig.FeatureSupport.Required;
        config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
        OVRProjectConfig.CommitProjectConfig(config);
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.productName = "Quest Pro Elbow Prototype";
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.etactilekit.questpro.elbow");
        PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>("Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
        if (perTarget == null) throw new System.InvalidOperationException("XR per-platform settings asset was not found.");
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        general.InitManagerOnStart = true;
        if (!XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android))
            throw new System.InvalidOperationException("Could not assign Android OpenXR Loader.");
        var xr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (xr == null) throw new System.InvalidOperationException("Android OpenXR settings are not available.");
        bool metaFound = false;
        foreach (var feature in xr.GetFeatures<OpenXRFeature>())
        {
            string type = feature.GetType().Name;
            if (type == "MetaXRFeature") { feature.enabled = true; metaFound = true; }
            if (type == "OculusTouchControllerProfile") feature.enabled = true;
            if (type == "OculusQuestFeature") feature.enabled = false; // use the Meta provider
        }
        if (!metaFound) throw new System.InvalidOperationException("Meta XR Feature missing: resolve packages, then run setup again.");
        EditorUtility.SetDirty(xr);
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(general.Manager);
        EditorUtility.SetDirty(perTarget);
        AssetDatabase.SaveAssets();
    }

    [MenuItem("eTactileKit/Quest Pro/Build Elbow Prototype APK")]
    public static void BuildApk()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            Debug.Log("Switching to Android. Wait for import to finish, then select Build Elbow Prototype APK again.");
            return;
        }
        if (!File.Exists(ScenePath)) CreateScene();
        if (!File.Exists(ScenePath)) return;
        ConfigureAndroid();
        string path = EditorUtility.SaveFilePanel("Build Quest Pro prototype", "", "QuestPro-Elbow.apk", "apk");
        if (string.IsNullOrEmpty(path)) return;
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { ScenePath }, locationPathName = path,
            target = BuildTarget.Android, options = BuildOptions.Development
        });
        Debug.Log("Quest Pro build: " + report.summary.result);
    }
}
