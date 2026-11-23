using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// Endless city. Keeps a square of tiles (3x3 by default) loaded around the car, generating new ones on a
/// worker thread and building them a few blocks per frame so driving doesn't stutter. Far tiles are unloaded.
/// Also moves the whole world back towards (0,0,0) now and then, because floats lose precision far from the
/// origin (shaky car, flickering surfaces). Builds the player's taxi (PlayerCar) unless a car is assigned.
/// </summary>
public class CityStreamer : MonoBehaviour
{
    public CitySettings settings = new();
    [Tooltip("Indexed by CityMat. Filled in by Tools > City > Set Up Scene.")]
    public Material[] materials = new Material[(int)CityMat.Count];

    [Tooltip("Leave empty to have the taxi built in code at startup.")]
    public Transform car;
    [Tooltip("Leave empty to use the Main Camera's ThirdPersonCamera.")]
    public ThirdPersonCamera followCamera;
    public bool placeCarAtStart = true;

    [Header("Streaming")]
    [Tooltip("Tiles loaded in each direction around the car's tile. 1 = 3x3, 2 = 5x5.")]
    public int radius = 1;
    [Tooltip("Metres past a tile edge before the grid recentres, so driving along a border doesn't load and unload over and over.")]
    public float recenterMargin = 30f;
    [Tooltip("Main-thread time per frame spent building tiles.")]
    public float buildBudgetMs = 3f;
    [Tooltip("When the car is this far from (0,0,0), everything shifts back by whole tiles.")]
    public float originShiftDistance = 4000f;

    class Tile
    {
        public Vector2Int cell;
        public Task<TileData> task;
        public GameObject root;
        public int nextBlock = -1;   // -1 = not started
        public readonly List<Mesh> meshes = new();
        public bool Built => root != null && nextBlock >= task.Result.blocks.Count;
        public float buildMs;
        public int buildFrames;
        public bool failed;
    }

    /// <summary>Fired after everything moved back towards the origin; the argument is how far it moved.</summary>
    public event System.Action<Vector3> OriginShifted;

    readonly Dictionary<Vector2Int, Tile> tiles = new();
    readonly List<Vector2Int> scratch = new();
    readonly Stopwatch watch = new();
    Vector2Int originCell, center;
    Rigidbody carBody;
    Transform horizon;
    Mesh horizonMesh;
    readonly List<Material> runtimeMaterials = new();

    // Development-build stats.
    float statTime, statWorst;
    int statFrames;

    /// <summary>Grid cell of the tile that currently sits at (0,0,0). Moves when the origin shifts.</summary>
    public Vector2Int OriginCell => originCell;

    float T => settings.TileSize;
    static bool LogStats => Debug.isDebugBuild && !Application.isEditor;   // development builds on a device

    void Awake()
    {
        EnsureRuntimeMaterials();
        if (car == null) car = PlayerCar.Create(materials[(int)CityMat.WhitePaint]).transform;
        if (followCamera == null && Camera.main != null) followCamera = Camera.main.GetComponent<ThirdPersonCamera>();
        if (followCamera != null && followCamera.target == null) followCamera.target = car;
        carBody = car.GetComponent<Rigidbody>();

        var bladeMat = materials[(int)CityMat.GrassBlade];
        if (bladeMat != null)
            gameObject.AddComponent<ParkGrass>().Init(this, bladeMat,
                followCamera != null ? followCamera.GetComponent<Camera>() : Camera.main);
        else
            Debug.LogWarning("City: no GrassBlade material, park grass is off. Run Tools > City > Set Up Scene.");

        if (placeCarAtStart)
        {
            // Right-hand lane of a north-south road near the middle of tile (0,0), between intersections.
            float pitch = settings.Pitch;
            int mid = settings.blocksPerTile / 2;
            var pos = new Vector3(mid * pitch + settings.roadWidth * 0.75f, 0.6f, mid * pitch + settings.roadWidth + settings.blockSize * 0.3f);
            car.SetPositionAndRotation(pos, Quaternion.identity);
            if (carBody) { carBody.position = pos; carBody.rotation = Quaternion.identity; }
        }

        center = CellAt(car.position);
        BuildHorizon();

        // The tile under the car is built right away so it has ground to land on.
        var first = Request(center);
        first.task.Wait();
        BuildTile(first, float.MaxValue);
        UpdateWanted();
    }

    void Update()
    {
        if (car == null) return;

        ShiftOriginIfFar();
        Recenter();
        BuildSome();

        Vector3 p = car.position;
        float snap = CityLook.MetersPerRepeat(CityMat.Asphalt);
        horizon.position = new Vector3(Mathf.Round(p.x / snap) * snap, -0.05f, Mathf.Round(p.z / snap) * snap);

        if (LogStats) Stats();
    }

    void EnsureRuntimeMaterials()
    {
        int need = (int)CityMat.Count;
        if (materials == null || materials.Length < need)
            System.Array.Resize(ref materials, need);

        var baseMat = materials[(int)CityMat.WhitePaint];
        if (baseMat == null) return;

        void Fill(CityMat slot, Color color, float smoothness, string name)
        {
            if (materials[(int)slot] != null) return;
            var m = Shapes.Tinted(baseMat, color, smoothness, name);
            materials[(int)slot] = m;
            runtimeMaterials.Add(m);
        }

        Fill(CityMat.TrafficPole, new Color(0.14f, 0.16f, 0.19f), 0.65f, "City_TrafficPole");
        Fill(CityMat.TrafficYellow, new Color(0.95f, 0.68f, 0.06f), 0.55f, "City_TrafficYellow");
        Fill(CityMat.TrafficDark, new Color(0.05f, 0.05f, 0.06f), 0.25f, "City_TrafficDark");
    }

    void OnDestroy()
    {
        foreach (var t in tiles.Values) Unload(t);
        tiles.Clear();
        foreach (var m in runtimeMaterials) Destroy(m);
        runtimeMaterials.Clear();
        Destroy(horizonMesh);
    }

    // ------------------------------------------------------------------ Grid

    Vector2Int CellAt(Vector3 worldPos) =>
        originCell + new Vector2Int(Mathf.FloorToInt(worldPos.x / T), Mathf.FloorToInt(worldPos.z / T));

    Vector3 CellOrigin(Vector2Int cell) => new Vector3((cell.x - originCell.x) * T, 0f, (cell.y - originCell.y) * T);

    void Recenter()
    {
        var c = CellAt(car.position);
        if (c == center) return;
        Vector3 local = car.position - CellOrigin(center);
        float past = Mathf.Max(Mathf.Max(-local.x, local.x - T), Mathf.Max(-local.z, local.z - T));
        if (past < recenterMargin) return;
        center = c;
        UpdateWanted();
    }

    void UpdateWanted()
    {
        scratch.Clear();
        foreach (var t in tiles.Values)
            if (Mathf.Max(Mathf.Abs(t.cell.x - center.x), Mathf.Abs(t.cell.y - center.y)) > radius)
                scratch.Add(t.cell);
        foreach (var cell in scratch)
        {
            Unload(tiles[cell]);
            tiles.Remove(cell);
        }

        for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                var cell = center + new Vector2Int(x, z);
                if (!tiles.ContainsKey(cell)) Request(cell);
            }
    }

    Tile Request(Vector2Int cell)
    {
        var s = settings;
        var tile = new Tile { cell = cell, task = Task.Run(() => CityGenerator.GenerateTile(s, cell)) };
        tiles[cell] = tile;
        return tile;
    }

    void Unload(Tile t)
    {
        if (t.root != null) Destroy(t.root);
        foreach (var m in t.meshes) Destroy(m);
        t.meshes.Clear();
    }

    // ------------------------------------------------------------------ Building GameObjects

    void BuildSome()
    {
        watch.Restart();
        while (watch.Elapsed.TotalMilliseconds < buildBudgetMs)
        {
            // Nearest finished-but-unbuilt tile first.
            Tile next = null;
            int best = int.MaxValue;
            foreach (var t in tiles.Values)
            {
                if (t.failed || !t.task.IsCompleted || (t.root != null && t.Built)) continue;
                if (t.task.IsFaulted)
                {
                    Debug.LogException(t.task.Exception);
                    t.failed = true;
                    continue;
                }
                int d = Mathf.Max(Mathf.Abs(t.cell.x - center.x), Mathf.Abs(t.cell.y - center.y));
                if (d < best) { best = d; next = t; }
            }
            if (next == null) return;

            float remaining = buildBudgetMs - (float)watch.Elapsed.TotalMilliseconds;
            BuildTile(next, remaining);
        }
    }

    void BuildTile(Tile t, float budgetMs)
    {
        var sw = Stopwatch.StartNew();
        var data = t.task.Result;
        if (t.root == null)
        {
            t.root = new GameObject($"City Tile {t.cell.x},{t.cell.y}");
            t.root.transform.SetParent(transform, false);
            t.root.transform.position = CellOrigin(t.cell);

            var ground = new GameObject("Ground");
            ground.transform.SetParent(t.root.transform, false);
            AddMesh(t, ground, data.ground, castShadows: false);
            var col = ground.AddComponent<BoxCollider>();
            col.center = new Vector3(T / 2f, -0.5f, T / 2f);
            col.size = new Vector3(T, 1f, T);
            t.nextBlock = 0;
        }

        while (t.nextBlock < data.blocks.Count && sw.Elapsed.TotalMilliseconds < budgetMs)
        {
            var b = data.blocks[t.nextBlock++];
            var go = new GameObject("Block");
            go.transform.SetParent(t.root.transform, false);
            go.transform.localPosition = b.localPos;
            AddMesh(t, go, b.mesh, castShadows: true);
            foreach (var (c, size) in b.colliders)
            {
                var bc = go.AddComponent<BoxCollider>();
                bc.center = c;
                bc.size = size;
            }
        }

        t.buildMs += (float)sw.Elapsed.TotalMilliseconds;
        t.buildFrames++;
        if (t.Built)
        {
            // The tile was built with the same data; drop it so the garbage collector can have it.
            data.blocks.Clear();
            if (LogStats)
                Debug.Log($"CITY tile {t.cell} built in {t.buildMs:0.0} ms over {t.buildFrames} frames");
        }
    }

    void AddMesh(Tile t, GameObject go, MeshData md, bool castShadows)
    {
        var mesh = new Mesh { name = go.name };
        if (md.verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(md.verts);
        mesh.SetNormals(md.normals);
        mesh.SetTangents(md.tangents);
        mesh.SetUVs(0, md.uvs);

        var used = new List<Material>();
        int sub = 0;
        for (int m = 0; m < md.tris.Length; m++)
            if (md.tris[m] != null) used.Add(materials[m]);
        mesh.subMeshCount = used.Count;
        for (int m = 0; m < md.tris.Length; m++)
            if (md.tris[m] != null) mesh.SetTriangles(md.tris[m], sub++, false);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);   // frees the CPU copy; colliders are boxes, so nothing needs to read it
        t.meshes.Add(mesh);

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = used.ToArray();
        mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
    }

    // ------------------------------------------------------------------ Horizon and origin

    /// <summary>A big flat ground under everything, so the edge of the loaded city fades into fog instead of sky.</summary>
    void BuildHorizon()
    {
        const int cells = 40;
        const float size = 4000f;
        float step = size / cells, m = CityLook.MetersPerRepeat(CityMat.Asphalt);
        var md = new MeshData();
        for (int x = 0; x < cells; x++)
            for (int z = 0; z < cells; z++)
            {
                float x0 = -size / 2f + x * step, z0 = -size / 2f + z * step, x1 = x0 + step, z1 = z0 + step;
                md.Quad(CityMat.Asphalt, new Vector3(x0, 0, z0), new Vector3(x0, 0, z1), new Vector3(x1, 0, z1),
                    new Vector3(x1, 0, z0), Vector3.up, new Vector4(1, 0, 0, -1),
                    new Vector2(x0 / m, z0 / m), new Vector2(x1 / m, z1 / m));
            }
        var go = new GameObject("Horizon Ground");
        go.transform.SetParent(transform, false);
        var holder = new Tile();
        AddMesh(holder, go, md, castShadows: false);
        horizonMesh = holder.meshes[0];
        go.GetComponent<MeshRenderer>().receiveShadows = false;
        horizon = go.transform;
    }

    void ShiftOriginIfFar()
    {
        Vector3 p = car.position;
        if (Mathf.Abs(p.x) < originShiftDistance && Mathf.Abs(p.z) < originShiftDistance) return;

        int sx = Mathf.Abs(p.x) < originShiftDistance ? 0 : Mathf.RoundToInt(p.x / T);
        int sz = Mathf.Abs(p.z) < originShiftDistance ? 0 : Mathf.RoundToInt(p.z / T);
        var delta = new Vector3(-sx * T, 0f, -sz * T);
        originCell += new Vector2Int(sx, sz);

        foreach (var t in tiles.Values)
            if (t.root != null) t.root.transform.position += delta;

        car.position += delta;
        if (carBody) carBody.position += delta;
        if (followCamera) followCamera.Shift(delta);
        Physics.SyncTransforms();
        OriginShifted?.Invoke(delta);

        if (LogStats) Debug.Log($"CITY origin shifted by {delta}, origin cell now {originCell}");
    }

    void Stats()
    {
        float dt = Time.unscaledDeltaTime;
        statTime += dt;
        statFrames++;
        statWorst = Mathf.Max(statWorst, dt);
        if (statTime < 5f) return;
        Debug.Log($"CITY fps={statFrames / statTime:0.0} worst={statWorst * 1000f:0}ms tiles={tiles.Count} " +
                  $"cell={CellAt(car.position)} speed={(carBody ? carBody.linearVelocity.magnitude * 3.6f : 0):0}km/h");
        statTime = statWorst = 0f;
        statFrames = 0;
    }
}
