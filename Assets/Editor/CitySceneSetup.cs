using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > City > Set Up Scene: bakes the textures and materials, removes the old pre-built city from the scene,
/// adds the City Streamer (the endless city, which also builds the taxi) and sets up fog to hide the edge of the
/// loaded area. Removes the old "Player Car" from the scene if it is still there.
/// Batch: Unity -batchmode -buildTarget Android -executeMethod CitySceneSetup.CommandLine
/// </summary>
public static class CitySceneSetup
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    // Matches the default sky at the horizon, so far buildings fade into it.
    static readonly Color FogColor = new Color(0.78f, 0.83f, 0.84f);
    const float FogStart = 80f, FogEnd = 420f, FarClip = 700f;

    [MenuItem("Tools/City/Set Up Scene")]
    public static void Run()
    {
        var mats = CityTextureBaker.BakeAll();

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath);

        foreach (var name in new[] { "Generated City", "Player Car" })
        {
            var old = GameObject.Find(name);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
        }

        var go = GameObject.Find("City Streamer");
        if (go == null) go = new GameObject("City Streamer");
        var streamer = go.GetComponent<CityStreamer>();
        if (streamer == null) streamer = go.AddComponent<CityStreamer>();
        streamer.materials = mats;
        streamer.car = null;   // built in code at startup
        var cam = Camera.main;
        streamer.followCamera = cam != null ? cam.GetComponent<ThirdPersonCamera>() : null;
        if (streamer.followCamera == null) Debug.LogError("City setup: the Main Camera needs a ThirdPersonCamera.");

        if (cam != null)
        {
            cam.farClipPlane = FarClip;
            EditorUtility.SetDirty(cam);
        }

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = FogColor;
        RenderSettings.fogStartDistance = FogStart;
        RenderSettings.fogEndDistance = FogEnd;

        EditorUtility.SetDirty(streamer);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("City setup: done.");
    }

    public static void CommandLine()
    {
        bool ok = true;
        try { Run(); }
        catch (Exception e) { Debug.LogException(e); ok = false; }
        EditorApplication.Exit(ok ? 0 : 1);
    }
}
