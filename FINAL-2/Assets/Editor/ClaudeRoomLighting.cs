using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// Warm, even, shadow-free fully-baked lighting for the room scene.
//
// Design: an enclosed room blocks environment (ambient) rays during the bake,
// so flat ambient alone cannot fill the interior. Shadowless BAKED directional
// lights skip visibility rays entirely — they shine through walls/roof — so a
// small rig of one warm key + two soft fills guarantees every surface
// orientation (floor, walls, ceiling) receives direct light. No cast shadows,
// no black corners, no hotspots.
public static class ClaudeRoomLighting
{
    const string ScenePath   = "Assets/Scenes/SampleScene.unity";
    const string SettingsPath = "Assets/Scenes/SampleScene Lighting.lighting";
    const string OutDir      = "Screenshots";
    const string ReportPath  = OutDir + "/claude_report.txt";
    const string DoneMarker  = OutDir + "/claude_bake_done.txt";
    const string FailMarker  = OutDir + "/claude_bake_failed.txt";
    const string PendingKey  = "ClaudeBakePending";

    // ---- tunables (iterated against preview renders) ----
    static readonly Color KeyColor  = new Color(1.00f, 0.945f, 0.855f); // warm ~4800K
    static readonly Color FillColor = new Color(1.00f, 0.960f, 0.900f); // slightly warm white
    const float KeyIntensity    = 0.75f;
    const float FillAIntensity  = 0.50f;   // opposite azimuth, lights far walls
    const float FillBIntensity  = 0.50f;   // points upward, lights the ceiling
    static readonly Vector3 KeyEuler   = new Vector3(50f, -30.6f, 0f);
    static readonly Vector3 FillAEuler = new Vector3(30f, 149.4f, 0f);
    static readonly Vector3 FillBEuler = new Vector3(-55f, 60f, 0f);
    static readonly Color AmbientColor = new Color(0.50f, 0.47f, 0.42f); // probe fill for dynamic objects
    const float EmissiveMaxHDR  = 2.0f;    // clamp ceiling for emissive materials

    static readonly StringBuilder log = new StringBuilder();

    [MenuItem("Tools/Claude Lighting/1 - Setup + Bake (All-In-One)")]
    public static void SetupAndBake()
    {
        Directory.CreateDirectory(OutDir);
        log.Length = 0;
        if (File.Exists(DoneMarker)) File.Delete(DoneMarker);
        if (File.Exists(FailMarker)) File.Delete(FailMarker);

        try
        {
            EnsureSceneOpen();
            RebuildLightRig();
            ConfigureEnvironment();
            ClampEmissiveMaterials();
            ConfigureLightingSettingsAsset();
            NeutralizeVolumeProfile();
            ReportUV2Status();
            DeleteUnusedIesAssets();

            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();

            Attach();
            SessionState.SetBool(PendingKey, true);
            if (Lightmapping.isRunning) Lightmapping.Cancel();
            bool started = Lightmapping.BakeAsync();
            log.AppendLine("BakeAsync started: " + started);
            Flush();
            if (!started)
            {
                SessionState.SetBool(PendingKey, false);
                File.WriteAllText(FailMarker, "BakeAsync returned false\n" + log);
            }
        }
        catch (Exception e)
        {
            log.AppendLine("EXCEPTION: " + e);
            Flush();
            File.WriteAllText(FailMarker, e.ToString());
        }
    }

    [MenuItem("Tools/Claude Lighting/2 - Render Previews Only")]
    public static void PreviewsOnly()
    {
        Directory.CreateDirectory(OutDir);
        log.Length = 0;
        EnsureSceneOpen();
        RenderPreviews();
        Flush();
    }

    [MenuItem("Tools/Claude Lighting/3 - Cancel Bake")]
    public static void CancelBake()
    {
        if (Lightmapping.isRunning) Lightmapping.Cancel();
    }

    // Survive a domain reload (package import, recompile) during an async bake:
    // re-attach the completion handlers if a bake was pending.
    [InitializeOnLoadMethod]
    static void ReattachAfterReload()
    {
        if (SessionState.GetBool(PendingKey, false)) Attach();
    }

    static void Attach()
    {
        Lightmapping.bakeCompleted -= OnBakeCompleted;
        Lightmapping.bakeCompleted += OnBakeCompleted;
        Lightmapping.bakeCancelled -= OnBakeCancelled;
        Lightmapping.bakeCancelled += OnBakeCancelled;
    }

    static void OnBakeCompleted()
    {
        SessionState.SetBool(PendingKey, false);
        try
        {
            log.AppendLine("--- bake completed " + DateTime.Now.ToString("HH:mm:ss") + " ---");
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            RenderPreviews();
            Flush();
            File.WriteAllText(DoneMarker, log.ToString());
        }
        catch (Exception e)
        {
            File.WriteAllText(FailMarker, "post-bake exception: " + e);
        }
    }

    static void OnBakeCancelled()
    {
        SessionState.SetBool(PendingKey, false);
        File.WriteAllText(FailMarker, "bake was cancelled");
    }

    // ---------------- phases ----------------

    static void EnsureSceneOpen()
    {
        var active = SceneManager.GetActiveScene();
        log.AppendLine("active scene: " + active.path);
        if (active.path != ScenePath)
        {
            EditorSceneManager.SaveOpenScenes();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            log.AppendLine("opened " + ScenePath);
        }
    }

    // Delete every existing light, then create the key + fill rig fresh (idempotent).
    static void RebuildLightRig()
    {
        foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var go = l.gameObject;
            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                log.AppendLine("SKIPPED prefab-child light: " + go.name);
                continue;
            }
            log.AppendLine("DELETED light: " + go.name + " (type " + l.type + ")");
            UnityEngine.Object.DestroyImmediate(go);
        }

        Transform parent = GameObject.Find("LIGHTING")?.transform;
        Light key = CreateDirectional("Sun (Warm Key)", parent, KeyEuler, KeyColor, KeyIntensity, 1.2f);
        CreateDirectional("Fill A (Far Walls)", parent, FillAEuler, FillColor, FillAIntensity, 1.0f);
        CreateDirectional("Fill B (Ceiling)",  parent, FillBEuler, FillColor, FillBIntensity, 0.5f);
        RenderSettings.sun = key;
        log.AppendLine($"rig: key {KeyIntensity} + fillA {FillAIntensity} + fillB {FillBIntensity}, all baked, shadows None");
    }

    static Light CreateDirectional(string name, Transform parent, Vector3 euler, Color color, float intensity, float bounce)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.rotation = Quaternion.Euler(euler);
        var l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = color;
        l.useColorTemperature = false;
        l.intensity = intensity;
        l.bounceIntensity = bounce;
        l.shadows = LightShadows.None;               // no cast shadows at all
        l.lightmapBakeType = LightmapBakeType.Baked; // fully baked
        return l;
    }

    static void ConfigureEnvironment()
    {
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = AmbientColor; // ambient probe: keeps dynamic objects (controllers) lit
        RenderSettings.fog = false;
        log.AppendLine("ambient: Flat " + AmbientColor);
    }

    // HDR emissive hotspots (e.g. lamp material at intensity ~1825) blow out the view.
    // Clamp them and stop them from injecting light into the lightmap.
    // Scope: only the room's material folders, not template/sample assets.
    static void ClampEmissiveMaterials()
    {
        var folders = new List<string> { "Assets/MATERIALS" };
        folders.AddRange(new[] { "Assets/Baked Textures", "Assets/Baked Textures 1" }
                         .Where(AssetDatabase.IsValidFolder));

        foreach (var guid in AssetDatabase.FindAssets("t:Material", folders.ToArray()))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || !mat.HasProperty("_EmissionColor")) continue;
            Color c = mat.GetColor("_EmissionColor");
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max <= 0.01f) continue;

            if (max > EmissiveMaxHDR + 0.5f)
            {
                Color clamped = c * (EmissiveMaxHDR / max);
                clamped.a = c.a;
                mat.SetColor("_EmissionColor", clamped);
                log.AppendLine($"clamped emission {Path.GetFileName(path)}: max {max:F1} -> {EmissiveMaxHDR}");
            }
            // visible glow stays, but no light injected into the bake
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(mat);
        }
    }

    // Scene-owned settings asset — leaves the shared VR-template asset untouched.
    static void ConfigureLightingSettingsAsset()
    {
        var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(SettingsPath);
        if (ls == null)
        {
            ls = new LightingSettings { name = "SampleScene Lighting" };
            AssetDatabase.CreateAsset(ls, SettingsPath);
            log.AppendLine("created " + SettingsPath);
        }
        ls.bakedGI = true;
        ls.realtimeGI = false;
        ls.ao = false;                    // no ambient occlusion -> no dark corners
        ls.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
        ls.lightmapMaxSize = 2048;
        ls.lightmapResolution = 40f;
        ls.directSampleCount = 128;
        ls.indirectSampleCount = 2048;
        ls.environmentSampleCount = 512;
        ls.maxBounces = 3;
        Lightmapping.SetLightingSettingsForScene(SceneManager.GetActiveScene(), ls);
        EditorUtility.SetDirty(ls);
        log.AppendLine("lighting settings: scene-owned, AO off, bakedGI on, GPU");
    }

    // Kill post effects that could darken edges or crush blacks; keep neutral tonemapping.
    static void NeutralizeVolumeProfile()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/DefaultVolumeProfile.asset");
        if (profile == null) { log.AppendLine("no DefaultVolumeProfile found"); return; }

        var disable = new[] {
            "DepthOfField", "FilmGrain", "ChromaticAberration", "MotionBlur",
            "LensDistortion", "PaniniProjection", "ScreenSpaceLensFlare", "Bloom",
            "SplitToning", "ChannelMixer", "ColorCurves", "ColorLookup",
            "LiftGammaGain", "ShadowsMidtonesHighlights", "WhiteBalance", "Vignette"
        };
        foreach (var comp in profile.components)
        {
            if (comp == null) continue;
            string n = comp.GetType().Name;
            if (disable.Contains(n) && comp.active)
            {
                comp.active = false;
                log.AppendLine("volume: disabled " + n);
            }
        }
        if (profile.TryGet<ColorAdjustments>(out var ca))
        {
            ca.contrast.value = 0f;       // was +4, deepened shadows
            ca.postExposure.value = 0f;
        }
        if (profile.TryGet<Tonemapping>(out var tm))
        {
            tm.active = true;
            tm.mode.overrideState = true;
            tm.mode.value = TonemappingMode.Neutral; // soft highlight rolloff, no clipping
        }
        EditorUtility.SetDirty(profile);
        log.AppendLine("volume profile neutralized (contrast 0, tonemap Neutral)");
    }

    static void ReportUV2Status()
    {
        int noUv2 = 0, total = 0;
        foreach (var mf in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var go = mf.gameObject;
            if ((GameObjectUtility.GetStaticEditorFlags(go) & StaticEditorFlags.ContributeGI) == 0) continue;
            if (mf.sharedMesh == null) continue;
            total++;
            if (mf.sharedMesh.uv2 == null || mf.sharedMesh.uv2.Length == 0) noUv2++;
        }
        log.AppendLine($"static meshes: {total}, without UV2 (fall back to UV1 for lightmap): {noUv2}");
    }

    static void DeleteUnusedIesAssets()
    {
        foreach (var p in new[] { "Assets/Light.ies", "Assets/Real-IES_wide_soft.ies" })
            if (File.Exists(p))
            {
                AssetDatabase.DeleteAsset(p);   // backed up in Backup_before_lighting_*
                log.AppendLine("deleted unused asset: " + p);
            }
    }

    // ---------------- previews ----------------

    static void RenderPreviews()
    {
        Bounds b = ComputeStaticBounds();
        log.AppendLine($"static bounds center={b.center} size={b.size}");

        float eyeY = b.min.y + 1.55f;
        Vector3 c = new Vector3(b.center.x, eyeY, b.center.z);

        var shots = new List<(string name, Vector3 pos, float yaw, float pitch)>();
        for (int i = 0; i < 4; i++)
            shots.Add(($"center_yaw{i * 90}", c, i * 90f, 8f));
        shots.Add(("center_up_a", c, 45f, -40f));
        shots.Add(("center_up_b", c, 225f, -40f));

        // inset corner views
        Vector3 e = b.extents * 0.55f;
        shots.Add(("corner_nw", new Vector3(b.center.x - e.x, eyeY, b.center.z + e.z), 135f, 10f));
        shots.Add(("corner_ne", new Vector3(b.center.x + e.x, eyeY, b.center.z + e.z), 225f, 10f));
        shots.Add(("corner_sw", new Vector3(b.center.x - e.x, eyeY, b.center.z - e.z), 45f, 10f));
        shots.Add(("corner_se", new Vector3(b.center.x + e.x, eyeY, b.center.z - e.z), 315f, 10f));

        var rig = GameObject.Find("VR RIG");
        if (rig != null)
        {
            Vector3 rp = rig.transform.position + Vector3.up * 1.65f;
            for (int i = 0; i < 4; i++)
                shots.Add(($"rig_yaw{i * 90}", rp, i * 90f, 5f));
        }

        var go = new GameObject("ClaudePreviewCam");
        try
        {
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);

            foreach (var s in shots)
            {
                go.transform.position = s.pos;
                go.transform.rotation = Quaternion.Euler(s.pitch, s.yaw, 0f);
                cam.targetTexture = rt;

                var req = new RenderPipeline.StandardRequest();
                if (RenderPipeline.SupportsRenderRequest(cam, req))
                {
                    req.destination = rt;
                    RenderPipeline.SubmitRenderRequest(cam, req);
                }
                else
                {
                    // Camera.Render() is a no-op under URP — previews would be garbage
                    log.AppendLine("ERROR: StandardRequest unsupported, preview " + s.name + " skipped");
                    continue;
                }

                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                File.WriteAllBytes($"{OutDir}/preview_{s.name}.png", tex.EncodeToPNG());

                var px = tex.GetPixels();
                float sum = 0; int black = 0, blown = 0;
                foreach (var p in px)
                {
                    float lum = (p.r + p.g + p.b) / 3f;
                    sum += lum;
                    if (lum < 0.04f) black++;
                    if (lum > 0.98f) blown++;
                }
                log.AppendLine($"shot {s.name}: avg={(sum / px.Length):F3} black%={(100f * black / px.Length):F1} blown%={(100f * blown / px.Length):F1}");
            }
            cam.targetTexture = null;
            rt.Release();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static Bounds ComputeStaticBounds()
    {
        Bounds b = default; bool has = false;
        foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if ((GameObjectUtility.GetStaticEditorFlags(mr.gameObject) & StaticEditorFlags.ContributeGI) == 0) continue;
            if (mr.bounds.size.magnitude > 60f) continue; // ignore rogue oversized meshes
            if (!has) { b = mr.bounds; has = true; }
            else b.Encapsulate(mr.bounds);
        }
        if (!has) b = new Bounds(Vector3.zero, new Vector3(8, 3, 8));
        return b;
    }

    static void Flush()
    {
        Directory.CreateDirectory(OutDir);
        File.AppendAllText(ReportPath, log.ToString() + "\n");
        Debug.Log("[ClaudeRoomLighting]\n" + log);
    }
}
