using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// MOVE-ONLY reorganization (GUID-safe, no deletions except one empty folder) +
// force-reimport to rebind embedded-material textures + render verification.
public static class OrganizeAndFix
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    static readonly StringBuilder log = new StringBuilder();

    public static void Run()
    {
        try
        {
            Directory.CreateDirectory("Screenshots");

            // ---- 1. FIX: reimport textures then models (rebind embedded materials) ----
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            ReimportByType("t:Texture2D", "textures");
            ReimportByType("t:Model", "models");
            AssetDatabase.Refresh();

            // ---- 2. ORGANIZE: move-only ----
            EnsureFolder("Assets/MATERIALS");
            EnsureFolder("Assets/Models");
            EnsureFolder("Assets/Settings");
            EnsureFolder("Assets/Textures");

            // loose root files
            Move("Assets/LampLights.mat", "Assets/MATERIALS/LampLights.mat");
            Move("Assets/PorcelainCabinet_top.mat", "Assets/MATERIALS/PorcelainCabinet_top.mat");
            Move("Assets/SinkMarbleCountertop.mat", "Assets/MATERIALS/SinkMarbleCountertop.mat");
            Move("Assets/sink.mat", "Assets/MATERIALS/sink.mat");
            Move("Assets/UpdatedModels2.fbx", "Assets/Models/UpdatedModels2.fbx");
            Move("Assets/DefaultVolumeProfile.asset", "Assets/Settings/DefaultVolumeProfile.asset");

            // NewModels -> Models/Props (models+their mats) and Textures/Props (their textures)
            MoveFolder("Assets/NewModels/New Extracted Models", "Assets/Models/Props");
            MoveFolder("Assets/NewModels/New Materials", "Assets/Textures/Props");

            // delete the now-empty NewModels folder (safe: nothing left in it)
            if (AssetDatabase.IsValidFolder("Assets/NewModels"))
            {
                var remain = Directory.GetFileSystemEntries("Assets/NewModels")
                    .Where(e => !e.EndsWith(".meta")).ToArray();
                if (remain.Length == 0)
                {
                    AssetDatabase.DeleteAsset("Assets/NewModels");
                    log.AppendLine("deleted empty folder Assets/NewModels");
                }
                else
                {
                    log.AppendLine("NewModels NOT empty, leaving it: " + string.Join(",", remain));
                }
            }

            AssetDatabase.Refresh();
            // reimport moved models again so embedded-material bindings resolve at final locations
            ReimportByType("t:Model", "models(post-move)");
            AssetDatabase.Refresh();

            // ---- 3. VERIFY ----
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int missingMat = 0;
            foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var m in mr.sharedMaterials)
                    if (m == null) missingMat++;
            log.AppendLine("missing material slots: " + missingMat);
            RenderShots();

            File.WriteAllText("Screenshots/organize_report.txt", log.ToString());
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            log.AppendLine("EXCEPTION: " + e);
            File.WriteAllText("Screenshots/organize_report.txt", log.ToString());
            EditorApplication.Exit(3);
        }
    }

    static void ReimportByType(string filter, string label)
    {
        var guids = AssetDatabase.FindAssets(filter);
        int n = 0;
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.StartsWith("Assets/")) { AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate); n++; }
            }
        }
        finally { AssetDatabase.StopAssetEditing(); }
        log.AppendLine($"reimported {label}: {n}");
    }

    static void Move(string src, string dst)
    {
        if (!File.Exists(src)) { log.AppendLine("  (skip, not found) " + src); return; }
        if (File.Exists(dst)) { log.AppendLine("  (skip, dest exists) " + dst); return; }
        var res = AssetDatabase.MoveAsset(src, dst);
        log.AppendLine(string.IsNullOrEmpty(res) ? $"  MOVED {src} -> {dst}" : $"  MOVE FAIL {src}: {res}");
    }

    static void MoveFolder(string src, string dst)
    {
        if (!AssetDatabase.IsValidFolder(src)) { log.AppendLine("  (skip folder, not found) " + src); return; }
        if (AssetDatabase.IsValidFolder(dst)) { log.AppendLine("  (skip folder, dest exists) " + dst); return; }
        var res = AssetDatabase.MoveAsset(src, dst);
        log.AppendLine(string.IsNullOrEmpty(res) ? $"  MOVED FOLDER {src} -> {dst}" : $"  MOVE FOLDER FAIL {src}: {res}");
    }

    static void EnsureFolder(string folder)
    {
        folder = folder.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(folder)) return;
        var parts = folder.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }

    static void RenderShots()
    {
        var shots = new (string name, Vector3 pos, Vector3 look)[]
        {
            ("o_overview", new Vector3(-7.2f, 1.7f, 18.4f), new Vector3(-10.5f, 1.0f, 21.5f)),
            ("o_dining",   new Vector3(-9.2f, 1.5f, 21.9f), new Vector3(-10.6f, 0.6f, 20.3f)),
            ("o_living",   new Vector3(-6.6f, 1.5f, 19.0f), new Vector3(-8.3f, 0.6f, 20.5f)),
            ("o_plant",    new Vector3(-12.3f, 1.5f, 21.2f), new Vector3(-13.75f, 1.0f, 22.75f)),
        };
        var go = new GameObject("OCam");
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 65f; cam.nearClipPlane = 0.05f; cam.clearFlags = CameraClearFlags.Skybox;
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        var rt = new RenderTexture(1600, 900, 24);
        var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
        foreach (var s in shots)
        {
            go.transform.position = s.pos; go.transform.LookAt(s.look);
            cam.targetTexture = rt; cam.Render(); cam.Render();
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); tex.Apply();
            File.WriteAllBytes($"Screenshots/{s.name}.png", tex.EncodeToPNG());
            var px = tex.GetPixels(); float sum = 0; foreach (var c in px) sum += (c.r + c.g + c.b) / 3f;
            log.AppendLine($"shot {s.name} avgBrightness={(sum / px.Length):F3}");
        }
        RenderTexture.active = null;
        UnityEngine.Object.DestroyImmediate(go);
    }
}
