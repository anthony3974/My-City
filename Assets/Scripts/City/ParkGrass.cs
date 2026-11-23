using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Short 3D grass blades on park lawns near the camera. One 2 m patch of blades (in two densities) is drawn
/// many times with GPU instancing, so it costs almost no memory and nothing is kept for parks far away.
/// Patches are rotated and mirrored at random so the repeat isn't visible, and they shrink to nothing
/// towards the draw distance so they never pop in. Added by CityStreamer.
/// </summary>
public class ParkGrass : MonoBehaviour
{
    const float PatchSize = 2f;            // metres the patch mesh is built for
    const int NearTufts = 110, FarTufts = 40;
    const float PathHalfWidth = 1.25f;     // the park's cross-shaped footpaths (CityGenerator.BuildPark)
    const float LawnTop = 0.3f;
    const int MaxPerDraw = 500;

    [Tooltip("Blades are drawn on lawns within this many metres of the camera.")]
    public float drawDistance = 40f;
    [Tooltip("Inside this distance the denser patch is used.")]
    public float nearDistance = 16f;

    CityStreamer city;
    Camera cam;
    Material material;
    Mesh nearMesh, farMesh;
    readonly List<Matrix4x4> nearList = new(), farList = new();
    readonly Plane[] planes = new Plane[6];
    readonly Dictionary<Vector2Int, bool> parkCache = new();

    public void Init(CityStreamer streamer, Material bladeMaterial, Camera camera)
    {
        city = streamer;
        material = bladeMaterial;
        cam = camera;
        // The far mesh is the first tufts of the near one, so switching between them only thins the grass.
        nearMesh = BuildPatch(NearTufts, "Grass Patch Near");
        farMesh = BuildPatch(FarTufts, "Grass Patch Far");
    }

    void OnDestroy()
    {
        Destroy(nearMesh);
        Destroy(farMesh);
    }

    void LateUpdate()
    {
        if (city == null || material == null) return;
        if (cam == null) { cam = Camera.main; if (cam == null) return; }

        var s = city.settings;
        float pitch = s.Pitch;
        float half = (s.blockSize - 2f * s.sidewalkWidth) / 2f;
        float quadLen = half - PathHalfWidth;
        int n = Mathf.Max(1, Mathf.RoundToInt(quadLen / PatchSize));
        float ps = quadLen / n, scaleXZ = ps / PatchSize;
        float off = s.roadWidth + s.blockSize / 2f;
        int bpt = s.blocksPerTile;
        Vector2Int oc = city.OriginCell;

        Vector3 cp = cam.transform.position;
        float D = drawDistance, fadeStart = D * 0.7f;
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        nearList.Clear();
        farList.Clear();
        var nearBounds = new Bounds();
        var farBounds = new Bounds();

        if (parkCache.Count > 4096) parkCache.Clear();

        int lx0 = Mathf.CeilToInt((cp.x - D - half - off) / pitch), lx1 = Mathf.FloorToInt((cp.x + D + half - off) / pitch);
        int lz0 = Mathf.CeilToInt((cp.z - D - half - off) / pitch), lz1 = Mathf.FloorToInt((cp.z + D + half - off) / pitch);
        for (int lx = lx0; lx <= lx1; lx++)
        {
            for (int lz = lz0; lz <= lz1; lz++)
            {
                float cx = lx * pitch + off, cz = lz * pitch + off;
                float ex = Mathf.Max(0f, Mathf.Abs(cp.x - cx) - half), ez = Mathf.Max(0f, Mathf.Abs(cp.z - cz) - half);
                if (ex * ex + ez * ez > D * D) continue;

                var g = new Vector2Int(lx + oc.x * bpt, lz + oc.y * bpt);
                if (!parkCache.TryGetValue(g, out bool park))
                    parkCache[g] = park = CityGenerator.IsPark(s, g.x, g.y);
                if (!park) continue;

                int blockHash = CityGenerator.Hash(s.seed + 977, g.x, g.y);
                for (int q = 0; q < 4; q++)
                {
                    float sx = (q & 1) == 0 ? -1f : 1f, sz = (q & 2) == 0 ? -1f : 1f;
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            float px = cx + sx * (PathHalfWidth + (i + 0.5f) * ps);
                            float pz = cz + sz * (PathHalfWidth + (j + 0.5f) * ps);
                            float dx = px - cp.x, dy = LawnTop - cp.y, dz = pz - cp.z;
                            float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (dist > D) continue;

                            var b = new Bounds(new Vector3(px, LawnTop + 0.1f, pz), new Vector3(ps + 0.4f, 0.4f, ps + 0.4f));
                            if (!GeometryUtility.TestPlanesAABB(planes, b)) continue;

                            int h = CityGenerator.Hash(blockHash, q * 64 + i, j);
                            float fade = 1f - Mathf.SmoothStep(0f, 1f, (dist - fadeStart) / (D - fadeStart));
                            if (fade < 0.03f) continue;
                            float height = fade * (0.8f + 0.4f * ((h >> 3) & 255) / 255f);
                            var m = Matrix4x4.TRS(new Vector3(px, LawnTop - 0.005f, pz), Quaternion.Euler(0f, (h & 3) * 90f, 0f),
                                new Vector3((h & 4) == 0 ? scaleXZ : -scaleXZ, height, scaleXZ));

                            if (dist < nearDistance) Add(nearList, ref nearBounds, m, b);
                            else Add(farList, ref farBounds, m, b);
                        }
                    }
                }
            }
        }

        Draw(nearMesh, nearList, nearBounds);
        Draw(farMesh, farList, farBounds);
    }

    static void Add(List<Matrix4x4> list, ref Bounds bounds, Matrix4x4 m, Bounds b)
    {
        if (list.Count == 0) bounds = b; else bounds.Encapsulate(b);
        list.Add(m);
    }

    void Draw(Mesh mesh, List<Matrix4x4> list, Bounds bounds)
    {
        if (list.Count == 0) return;
        var rp = new RenderParams(material)
        {
            worldBounds = bounds,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = true,
            layer = gameObject.layer,
        };
        for (int start = 0; start < list.Count; start += MaxPerDraw)
            Graphics.RenderMeshInstanced(rp, mesh, 0, list, Mathf.Min(MaxPerDraw, list.Count - start), start);
    }

    /// <summary>
    /// A 2 m square of short grass: tufts of 3 to 5 thin triangle blades leaning out from a shared root.
    /// One triangle per blade; the material draws both sides. Normals point mostly up so blades light like
    /// the lawn under them. Each tuft picks a colour column of the GrassBlade texture (dark root, light tip).
    /// </summary>
    static Mesh BuildPatch(int tufts, string name)
    {
        var rnd = new System.Random(4242);
        float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        const float hs = PatchSize / 2f;

        for (int t = 0; t < tufts; t++)
        {
            var root = new Vector3(R(-hs + 0.05f, hs - 0.05f), 0f, R(-hs + 0.05f, hs - 0.05f));
            int blades = 3 + rnd.Next(3);
            float tuftH = R(0.06f, 0.13f);
            float u = (rnd.Next(16) + 0.5f) / 16f;
            float yaw0 = R(0f, Mathf.PI * 2f);
            for (int b = 0; b < blades; b++)
            {
                float yaw = yaw0 + b * (Mathf.PI * 2f / blades) + R(-0.4f, 0.4f);
                var lean = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw));   // which way the blade bends
                var across = new Vector3(-lean.z, 0f, lean.x);
                float w = R(0.018f, 0.03f), h = tuftH * R(0.7f, 1.15f);
                var bc = root + lean * R(0f, 0.025f);
                var tip = bc + lean * h * R(0.25f, 0.7f) + across * R(-0.01f, 0.01f) + Vector3.up * h;

                int i = verts.Count;
                verts.Add(bc - across * (w * 0.5f));
                verts.Add(bc + across * (w * 0.5f));
                verts.Add(tip);
                var nrm = (Vector3.up + lean * 0.35f).normalized;
                normals.Add(nrm); normals.Add(nrm); normals.Add(nrm);
                uvs.Add(new Vector2(u, 0.03f)); uvs.Add(new Vector2(u, 0.03f)); uvs.Add(new Vector2(u, 0.97f));
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
            }
        }

        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        return mesh;
    }
}
