using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

/// <summary>
/// Development builds on a device only: when a frame takes longer than a threshold, logs which engine
/// steps took the time (read with `adb logcat -s Unity:V | grep HITCH`). Added by CityStreamer.
/// </summary>
public class HitchLogger : MonoBehaviour
{
    public float thresholdMs = 40f;

    // Engine and script markers worth knowing about in a hitch.
    static readonly string[] Markers =
    {
        "PlayerLoop", "GC.Collect", "GC.Alloc",
        "BehaviourUpdate", "LateBehaviourUpdate", "FixedBehaviourUpdate", "Update.ScriptRunDelayedDynamicFrameRate",
        "FixedUpdate.PhysicsFixedUpdate", "Physics.Processing", "Physics.UpdateBodies", "Physics.Simulate",
        "Physics.SyncColliderTransform", "Physics.UpdateCloth",
        "PostLateUpdate.FinishFrameRendering", "Gfx.WaitForPresentOnGfxThread", "Gfx.PresentFrame",
        "Shader.CreateGPUProgram", "CreateGPUProgram", "Shader.Parse", "ShaderWarmup",
        "Destroy", "DestroyImmediate", "Object.Destroy", "CleanupUnusedCachedData", "Loading.AwakeFromLoad",
        "Mesh.UploadMeshData", "Mesh.CreateMesh", "UploadTexture", "Texture2D.Apply",
        "CityStreamer.Unload", "CityStreamer.Build", "CityStreamer.Wanted",
        "Inl_UniversalRenderTotal", "DrawOpaqueObjects", "MainLightShadow", "RenderLoop.ScheduleDraw",
        "Canvas.SendWillRenderCanvases", "GUI.Repaint", "PreLateUpdate.ScriptRunBehaviourLateUpdate",
        "WaitForTargetFPS", "TimeUpdate.WaitForLastPresentationAndUpdateTime",
    };

    readonly List<(string name, ProfilerRecorder rec, bool gcBytes)> recorders = new();
    readonly StringBuilder sb = new();
    int lastGcCount;

    void Start()
    {
        // Look the markers up by name among everything the profiler knows (any category).
        var handles = new List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);
        var wanted = new HashSet<string>(Markers);
        foreach (var h in handles)
        {
            var d = ProfilerRecorderHandle.GetDescription(h);
            if (!wanted.Remove(d.Name)) continue;
            var r = ProfilerRecorder.StartNew(d.Category, d.Name, 1);
            if (r.Valid) recorders.Add((d.Name, r, d.Name == "GC.Alloc"));
            else r.Dispose();
        }
        Debug.Log($"HITCH logger watching {recorders.Count} markers; not found: {string.Join(", ", wanted)}");
        lastGcCount = System.GC.CollectionCount(0);
    }

    void OnDisable()
    {
        foreach (var r in recorders) r.rec.Dispose();
        recorders.Clear();
    }

    void Update()
    {
        // Markers hold the previous frame's values; unscaledDeltaTime is the previous frame's length too.
        float ms = Time.unscaledDeltaTime * 1000f;
        int gc = System.GC.CollectionCount(0);
        bool collected = gc != lastGcCount;
        lastGcCount = gc;
        if (ms < thresholdMs) return;

        sb.Clear();
        sb.Append($"HITCH {ms:0}ms frame={Time.frameCount - 1} gcRan={collected} heap={System.GC.GetTotalMemory(false) / 1048576}MB |");
        foreach (var (name, rec, gcBytes) in recorders)
        {
            long v = rec.LastValue;
            if (gcBytes) { if (v > 64 * 1024) sb.Append($" {name}={v / 1024}KB"); continue; }
            float vms = v / 1e6f;
            if (vms >= 2f) sb.Append($" {name}={vms:0.0}");
        }
        Debug.Log(sb.ToString());
    }
}
