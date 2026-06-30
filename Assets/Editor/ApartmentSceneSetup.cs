using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class ApartmentSceneSetup
{
    const float SunIntensity     = 0.85f;
    const float SunTemperature   = 5200f;
    const float SunPitchDown     = 45f;
    const float SunYaw           = -125f;
    const float ShadowStrength   = 0.22f;

    const float AmbientIntensity = 0.9f;
    static readonly Color AmbientColor = new Color(0.92f, 0.93f, 0.95f);

    const bool  FogEnabled   = true;
    const float FogDensity   = 0.0035f;
    static readonly Color FogColor = new Color(0.86f, 0.89f, 0.93f);

    const float ColliderSizeLimit = 30f;

    const string ApartmentRootName = "Apartment";
    const string ApartmentRootFallback = "RoomPlan2";

    [MenuItem("Tools/Apartment/1 - Apply Natural Lighting")]
    public static void ApplyLighting()
    {
        Scene scene = SceneManager.GetActiveScene();

        Light sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                          .FirstOrDefault(l => l.type == LightType.Directional);

        if (sun == null)
        {
            var go = new GameObject("Sun (Directional)");
            Undo.RegisterCreatedObjectUndo(go, "Create Sun");
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
        }
        else
        {
            Undo.RecordObject(sun, "Tune Sun");
            Undo.RecordObject(sun.transform, "Tune Sun");
            if (sun.gameObject.name == "Directional Light")
                sun.gameObject.name = "Sun (Directional)";
        }

        sun.transform.rotation = Quaternion.Euler(SunPitchDown, SunYaw, 0f);
        sun.intensity          = SunIntensity;
        sun.useColorTemperature = true;
        sun.colorTemperature   = SunTemperature;
        sun.color              = Color.white;
        sun.shadows            = LightShadows.Soft;
        sun.shadowStrength     = ShadowStrength;
        sun.lightmapBakeType   = LightmapBakeType.Mixed;

        if (sun.GetComponent<UniversalAdditionalLightData>() == null)
            Undo.AddComponent<UniversalAdditionalLightData>(sun.gameObject);

        RenderSettings.sun             = sun;
        RenderSettings.ambientMode     = AmbientMode.Flat;
        RenderSettings.ambientIntensity = AmbientIntensity;
        RenderSettings.ambientLight    = AmbientColor;
        RenderSettings.fog             = FogEnabled;
        RenderSettings.fogMode         = FogMode.ExponentialSquared;
        RenderSettings.fogDensity      = FogDensity;
        RenderSettings.fogColor        = FogColor;

        EnablePostProcessing();

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[Apartment] Natural lighting applied. Re-bake via Window > Rendering > Lighting > Generate Lighting for best static quality.");
    }

    static void EnablePostProcessing()
    {
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null && !data.renderPostProcessing)
            {
                Undo.RecordObject(data, "Enable Post-processing");
                data.renderPostProcessing = true;
            }
        }
    }

    static GameObject FindApartment()
    {
        return GameObject.Find(ApartmentRootName) ?? GameObject.Find(ApartmentRootFallback);
    }

    [MenuItem("Tools/Apartment/2 - Fix Colliders")]
    public static void FixColliders()
    {
        GameObject root = FindApartment();
        if (root == null) { Debug.LogError("[Apartment] Model root not found."); return; }

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        int removedBig = 0, removedDup = 0, kept = 0;
        var seen = new HashSet<string>();

        foreach (var col in colliders)
        {
            if (col == null) continue;
            if (IsInteractionCollider(col)) { kept++; continue; }
            if (!col.gameObject.activeInHierarchy) { kept++; continue; }

            Vector3 size = col.bounds.size;
            float maxDim = Mathf.Max(size.x, Mathf.Max(size.y, size.z));

            if (maxDim > ColliderSizeLimit) { Undo.DestroyObjectImmediate(col); removedBig++; continue; }

            string sig = Signature(col);
            if (!seen.Add(sig)) { Undo.DestroyObjectImmediate(col); removedDup++; continue; }

            kept++;
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[Apartment] Colliders: removed {removedBig} oversized + {removedDup} duplicate, kept {kept}.");
    }

    static bool IsInteractionCollider(Collider col)
    {
        foreach (var mb in col.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            string n = mb.GetType().Name;
            if (n.Contains("Interactable") || n.Contains("Teleport")) return true;
        }
        return false;
    }

    static string Signature(Collider col)
    {
        string type = col is BoxCollider ? "B" : col is MeshCollider ? "M" :
                      col is SphereCollider ? "S" : col is CapsuleCollider ? "C" : "O";
        Bounds b = col.bounds;
        return $"{type}|{Round(b.center)}|{Round(b.size)}";
    }

    static string Round(Vector3 v) =>
        $"{Mathf.Round(v.x * 10f)}_{Mathf.Round(v.y * 10f)}_{Mathf.Round(v.z * 10f)}";

    [MenuItem("Tools/Apartment/3 - Organize Hierarchy")]
    public static void OrganizeHierarchy()
    {
        Scene scene = SceneManager.GetActiveScene();

        GameObject lighting = MakeGroup("=== LIGHTING ===");
        GameObject apartment = MakeGroup("=== APARTMENT ===");
        GameObject vr = MakeGroup("=== VR RIG ===");
        GameObject systems = MakeGroup("=== SYSTEMS ===");

        Reparent("Sun (Directional)", lighting);
        Reparent("Directional Light", lighting, "Sun (Directional)");
        Reparent("Area Light", lighting, "Area Light - Lamp Glow");
        Reparent("Area Light (1)", lighting, "Area Light - Window Fill");

        Reparent(ApartmentRootFallback, apartment, "Apartment");
        Reparent("Apartment", apartment);
        Reparent("Environment", apartment);
        Reparent("Interactables", apartment);

        Reparent("XR Origin (XR Rig)", vr);
        Reparent("XR Interaction Simulator", vr);

        Reparent("Reflection Probe", systems, "Reflection Probe - Room");
        Reparent("Reflection Probe (1)", systems, "Reflection Probe - Lamp");
        Reparent("Teleport Area Setup", systems);
        Reparent("UI", systems);

        foreach (var g in new[] { lighting, apartment, vr, systems })
            if (g.transform.childCount == 0) Undo.DestroyObjectImmediate(g);

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[Apartment] Hierarchy organized into LIGHTING / APARTMENT / VR RIG / SYSTEMS.");
    }

    static GameObject MakeGroup(string name)
    {
        GameObject g = GameObject.Find(name);
        if (g == null)
        {
            g = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(g, "Create Group");
        }
        return g;
    }

    static void Reparent(string objName, GameObject parent, string newName = null)
    {
        GameObject go = GameObject.Find(objName);
        if (go == null || go == parent) return;
        Undo.SetTransformParent(go.transform, parent.transform, "Reparent");
        if (newName != null)
        {
            Undo.RecordObject(go, "Rename");
            go.name = newName;
        }
    }

    [MenuItem("Tools/Apartment/4 - Organize Project Files")]
    public static void OrganizeProjectFiles()
    {
        EnsureFolder("Assets/Models");
        EnsureFolder("Assets/Textures");

        MoveAsset("Assets/RoomPlan2.fbx",             "Assets/Models/RoomPlan2.fbx");
        MoveAsset("Assets/RoomPlan.fbx",              "Assets/Models/RoomPlan.fbx");
        MoveAsset("Assets/new-york-city-panorama.jpg", "Assets/Textures/new-york-city-panorama.jpg");
        MoveAsset("Assets/New Material.mat",           "Assets/MATERIALS/Skybox NYC.mat");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[Apartment] Project files organized.");
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(path);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    static void MoveAsset(string from, string to)
    {
        if (AssetDatabase.LoadAssetAtPath<Object>(from) == null) return;
        string err = AssetDatabase.MoveAsset(from, to);
        if (string.IsNullOrEmpty(err)) Debug.Log($"[Apartment] Moved {from} -> {to}");
        else Debug.LogWarning($"[Apartment] Could not move {from}: {err}");
    }

    [MenuItem("Tools/Apartment/5 - Unpack Apartment Model")]
    public static void UnpackApartment()
    {
        GameObject go = FindApartment();
        if (go == null) { Debug.LogError("[Apartment] Model root not found."); return; }

        if (PrefabUtility.IsPartOfPrefabInstance(go))
        {
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.UserAction);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[Apartment] Apartment model unpacked. Furniture is now editable GameObjects.");
        }
        else
        {
            Debug.Log("[Apartment] Apartment model is already unpacked.");
        }
    }

    [MenuItem("Tools/Apartment/Log Hierarchy To Console")]
    public static void LogHierarchy()
    {
        var sb = new StringBuilder("[Apartment] Scene hierarchy:\n");
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            Dump(root.transform, 0, sb);
        Debug.Log(sb.ToString());
    }

    static void Dump(Transform t, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).Append("- ").Append(t.name);
        if (t.GetComponent<Light>() is Light l) sb.Append($"  [light {l.type}]");
        sb.AppendLine();
        for (int i = 0; i < t.childCount; i++)
            Dump(t.GetChild(i), depth + 1, sb);
    }
}
