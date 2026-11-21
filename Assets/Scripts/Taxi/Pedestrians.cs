using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Blocky placeholder people (boxes, legs swing as they walk) on the sidewalks around the car. Walkers loop around their block,
/// now and then turning back or stopping for a moment. Fares stand at the curb with a green light over their
/// head, waiting for the taxi. People who end up too far from the car are moved to a block near it again.
/// Created and set up by TaxiGame.
/// </summary>
public class Pedestrians : MonoBehaviour
{
    public int walkers = 40;
    public int fares = 5;
    [Tooltip("People are placed on blocks up to this many blocks from the car.")]
    public int spawnRadiusBlocks = 4;
    public float despawnDistance = 280f;
    public float minWalkSpeed = 0.8f, maxWalkSpeed = 1.5f;
    public Color fareLightColor = new(0.3f, 1f, 0.4f, 0.9f);

    const float Height = 1.7f;           // top of the head
    const float HipHeight = 0.6f;
    const float SidewalkTop = 0.2f;     // top of the raised block slab, see CityGenerator
    public const float WalkInset = 1.25f;   // middle of the 2.5 m sidewalk
    const float FareInset = 0.6f;       // at the curb

    class Person
    {
        public Transform body, light, legL, legR;
        public Vector2Int block;
        public float u, dir, speed, timer, pause, phase;
        public bool fare;
    }

    readonly List<Person> people = new();
    CityStreamer city;
    Transform car;
    float pitch, roadWidth, blockSize;
    Mesh sphere, walkerMesh, fareMesh, legMesh;
    Material[] clothes, skins, pants;
    Material eyeMat, fareMat;
    readonly List<Material> owned = new();

    static readonly Color[] ClothesColors =
    {
        new(0.15f, 0.25f, 0.55f), new(0.55f, 0.12f, 0.12f), new(0.2f, 0.45f, 0.25f), new(0.85f, 0.85f, 0.82f),
        new(0.12f, 0.12f, 0.14f), new(0.6f, 0.45f, 0.25f), new(0.45f, 0.2f, 0.5f), new(0.9f, 0.55f, 0.15f),
    };
    static readonly Color[] SkinColors =
    {
        new(0.95f, 0.8f, 0.66f), new(0.88f, 0.7f, 0.54f), new(0.66f, 0.47f, 0.33f), new(0.42f, 0.29f, 0.2f),
    };
    static readonly Color[] PantsColors =
    {
        new(0.13f, 0.14f, 0.17f), new(0.18f, 0.24f, 0.38f), new(0.55f, 0.5f, 0.38f),
    };

    public void Init(CityStreamer streamer, Transform carTransform, Material lit, Material glow, Mesh beacon)
    {
        city = streamer;
        car = carTransform;
        pitch = city.settings.Pitch;
        roadWidth = city.settings.roadWidth;
        blockSize = city.settings.blockSize;
        city.OriginShifted += OnOriginShifted;

        var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere = tmp.GetComponent<MeshFilter>().sharedMesh;   // built-in mesh, outlives the object
        Destroy(tmp);

        walkerMesh = BodyMesh(hailing: false);
        fareMesh = BodyMesh(hailing: true);
        legMesh = LegMesh();

        if (lit == null) lit = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        clothes = Tinted(lit, ClothesColors, "Clothes");
        skins = Tinted(lit, SkinColors, "Skin");
        pants = Tinted(lit, PantsColors, "Pants");
        eyeMat = Tinted(lit, new[] { new Color(0.05f, 0.05f, 0.05f) }, "Eyes")[0];
        if (glow != null)
        {
            fareMat = new Material(glow) { name = "Fare Light" };
            fareMat.SetColor("_BaseColor", fareLightColor);
            owned.Add(fareMat);
        }

        for (int i = 0; i < walkers + fares; i++)
        {
            var p = new Person { fare = i >= walkers };
            BuildPerson(p);
            if (p.fare && fareMat != null) p.light = FareLight(beacon);
            Respawn(p, nearOk: !p.fare);
            people.Add(p);
        }
    }

    void OnDestroy()
    {
        if (city != null) city.OriginShifted -= OnOriginShifted;
        foreach (var m in owned) Destroy(m);
        Destroy(walkerMesh);
        Destroy(fareMesh);
        Destroy(legMesh);
    }

    void OnOriginShifted(Vector3 delta)
    {
        var shift = new Vector2Int(Mathf.RoundToInt(delta.x / pitch), Mathf.RoundToInt(delta.z / pitch));
        foreach (var p in people) p.block += shift;
    }

    // ------------------------------------------------------------------ Taxi game hooks

    public bool NearestFare(Vector3 from, out Vector3 pos)
    {
        pos = default;
        float best = float.MaxValue;
        foreach (var p in people)
        {
            if (!p.fare) continue;
            Vector3 fp = SidewalkPoint(p.block, p.u, FareInset, out _, out _);
            float d = Shapes.Flat(fp - from).sqrMagnitude;
            if (d < best) { best = d; pos = fp; }
        }
        return best < float.MaxValue;
    }

    /// <summary>Where every waiting fare stands, for the minimap.</summary>
    public void GetFares(List<Vector3> into)
    {
        foreach (var p in people)
            if (p.fare) into.Add(SidewalkPoint(p.block, p.u, FareInset, out _, out _));
    }

    /// <summary>If a fare is within radius, they get in: true, and a new fare appears somewhere else.</summary>
    public bool TryPickUp(Vector3 carPos, float radius)
    {
        foreach (var p in people)
        {
            if (!p.fare) continue;
            if (Shapes.Flat(SidewalkPoint(p.block, p.u, FareInset, out _, out _) - carPos).sqrMagnitude > radius * radius) continue;
            Respawn(p, nearOk: false);
            return true;
        }
        return false;
    }

    /// <summary>The passenger gets out at this spot and walks off. Reuses the walker farthest from the car.</summary>
    public void DropOff(Vector2Int block, float u)
    {
        Person far = null;
        float best = -1f;
        foreach (var p in people)
        {
            if (p.fare) continue;
            float d = Shapes.Flat(p.body.position - car.position).sqrMagnitude;
            if (d > best) { best = d; far = p; }
        }
        if (far == null) return;
        far.block = block;
        far.u = u;
        far.pause = 0.5f;
    }

    // ------------------------------------------------------------------ Grid helpers

    public Vector2Int BlockAt(Vector3 pos) => new(Mathf.FloorToInt(pos.x / pitch), Mathf.FloorToInt(pos.z / pitch));

    /// <summary>Length of one side of the loop around a block, inset metres in from the curb (negative = out into the road).</summary>
    public float SideLength(float inset) => blockSize - 2f * inset;

    /// <summary>
    /// Point u metres along the loop around a block (anticlockwise from its south-west corner), inset metres in
    /// from the curb. Also gives the walking direction and the direction out towards the road.
    /// </summary>
    public Vector3 SidewalkPoint(Vector2Int b, float u, float inset, out Vector3 along, out Vector3 outward)
    {
        float len = SideLength(inset);
        u = Mathf.Repeat(u, 4f * len);
        int side = Mathf.Min(3, (int)(u / len));
        float t = u - side * len;
        // Block b covers b * pitch + roadWidth .. (b + 1) * pitch on both axes.
        float x0 = b.x * pitch + roadWidth + inset, z0 = b.y * pitch + roadWidth + inset;
        float x1 = (b.x + 1) * pitch - inset, z1 = (b.y + 1) * pitch - inset;
        switch (side)
        {
            case 0: along = Vector3.right; outward = Vector3.back; return new Vector3(x0 + t, SidewalkTop, z0);
            case 1: along = Vector3.forward; outward = Vector3.right; return new Vector3(x1, SidewalkTop, z0 + t);
            case 2: along = Vector3.left; outward = Vector3.forward; return new Vector3(x1 - t, SidewalkTop, z1);
            default: along = Vector3.back; outward = Vector3.left; return new Vector3(x0, SidewalkTop, z1 - t);
        }
    }

    // ------------------------------------------------------------------ Movement

    void Update()
    {
        if (car == null) return;
        float dt = Time.deltaTime, now = Time.time;

        foreach (var p in people)
        {
            if (Shapes.Flat(p.body.position - car.position).sqrMagnitude > despawnDistance * despawnDistance)
                Respawn(p, nearOk: false);

            if (p.fare)
            {
                // Waiting at the curb, facing the road, hopping a little to get noticed.
                Vector3 pos = SidewalkPoint(p.block, p.u, FareInset, out _, out Vector3 outward);
                float hop = Mathf.Abs(Mathf.Sin(now * 4f + p.phase)) * 0.15f;
                p.body.SetPositionAndRotation(pos + Vector3.up * hop, Quaternion.LookRotation(outward));
                if (p.light) p.light.position = pos + Vector3.up * (Height + 0.45f + hop);
                continue;
            }

            p.timer -= dt;
            if (p.timer <= 0f)
            {
                p.timer = Random.Range(4f, 12f);
                float r = Random.value;
                if (r < 0.35f) p.dir = -p.dir;                         // pace back the other way
                else if (r < 0.55f) p.pause = Random.Range(1f, 3f);    // stand around for a bit
            }

            bool moving = p.pause <= 0f;
            if (moving) p.u += p.dir * p.speed * dt;
            else p.pause -= dt;

            Vector3 at = SidewalkPoint(p.block, p.u, WalkInset, out Vector3 along, out _);
            float stride = p.u * 3.5f;   // tied to distance walked, so feet don't slide
            float bob = moving ? Mathf.Abs(Mathf.Sin(stride)) * 0.05f : 0f;
            p.body.SetPositionAndRotation(at + Vector3.up * bob, Quaternion.LookRotation(along * p.dir));

            // Legs swing opposite each other while walking and settle back when standing.
            float swing = moving ? Mathf.Cos(stride) * 28f : 0f;
            p.legL.localRotation = Quaternion.Slerp(p.legL.localRotation, Quaternion.Euler(swing, 0f, 0f), 15f * dt);
            p.legR.localRotation = Quaternion.Slerp(p.legR.localRotation, Quaternion.Euler(-swing, 0f, 0f), 15f * dt);
        }
    }

    void Respawn(Person p, bool nearOk)
    {
        Vector2Int carBlock = BlockAt(car.position);
        int r = spawnRadiusBlocks;
        Vector2Int off;
        do off = new Vector2Int(Random.Range(-r, r + 1), Random.Range(-r, r + 1));
        while (!nearOk && Mathf.Max(Mathf.Abs(off.x), Mathf.Abs(off.y)) < 2);

        p.block = carBlock + off;
        p.u = Random.Range(0f, 4f * SideLength(p.fare ? FareInset : WalkInset));
        p.dir = Random.value < 0.5f ? -1f : 1f;
        p.speed = Random.Range(minWalkSpeed, maxWalkSpeed);
        p.timer = Random.Range(2f, 12f);
        p.pause = 0f;
        p.phase = Random.Range(0f, 10f);
        // Put the body there now so the distance check doesn't fire again before the next Update.
        p.body.position = SidewalkPoint(p.block, p.u, WalkInset, out _, out _);
    }

    /// <summary>Root at the feet. Torso, arms and head are one mesh (one object); the legs are separate so they can swing.</summary>
    void BuildPerson(Person p)
    {
        var root = new GameObject(p.fare ? "Fare" : "Walker").transform;
        root.SetParent(transform, false);
        p.body = root;

        var body = new GameObject("Body");
        body.transform.SetParent(root, false);
        body.AddComponent<MeshFilter>().sharedMesh = p.fare ? fareMesh : walkerMesh;
        // Submeshes: 0 clothes, 1 skin, 2 eyes.
        var skin = skins[Random.Range(0, skins.Length)];
        body.AddComponent<MeshRenderer>().sharedMaterials =
            new[] { clothes[Random.Range(0, clothes.Length)], skin, eyeMat };

        var legMat = pants[Random.Range(0, pants.Length)];
        p.legL = Leg(root, -0.11f, legMat);
        p.legR = Leg(root, 0.11f, legMat);
    }

    Transform Leg(Transform root, float x, Material mat)
    {
        var go = new GameObject("Leg");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(x, HipHeight, 0f);   // pivot at the hip, mesh hangs down
        go.AddComponent<MeshFilter>().sharedMesh = legMesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        return go.transform;
    }

    Material[] Tinted(Material lit, Color[] colors, string name)
    {
        var mats = new Material[colors.Length];
        for (int i = 0; i < mats.Length; i++)
        {
            mats[i] = Shapes.Tinted(lit, colors[i], 0.15f, $"{name} {i}");
            owned.Add(mats[i]);
        }
        return mats;
    }

    // ------------------------------------------------------------------ Box meshes

    /// <summary>Torso, arms, head and eyes facing +Z. A fare has the right arm raised to hail a taxi.</summary>
    static Mesh BodyMesh(bool hailing)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var clothesTris = new List<int>();
        var skinTris = new List<int>();
        var eyeTris = new List<int>();

        Shapes.Box(verts, normals, clothesTris, new Vector3(0f, 0.94f, 0f), new Vector3(0.48f, 0.72f, 0.26f));       // torso
        Shapes.Box(verts, normals, clothesTris, new Vector3(-0.315f, 0.935f, 0f), new Vector3(0.13f, 0.61f, 0.14f)); // left arm
        var shoulder = new Vector3(0.315f, 1.24f, 0f);
        var raise = hailing ? Quaternion.AngleAxis(150f, Vector3.forward) : Quaternion.identity;
        Shapes.Box(verts, normals, clothesTris, new Vector3(0.315f, 0.935f, 0f), new Vector3(0.13f, 0.61f, 0.14f), raise, shoulder);
        Shapes.Box(verts, normals, skinTris, new Vector3(0f, 1.49f, 0f), new Vector3(0.38f, 0.38f, 0.38f));         // head
        Shapes.Box(verts, normals, eyeTris, new Vector3(-0.085f, 1.52f, 0.19f), new Vector3(0.06f, 0.06f, 0.02f));
        Shapes.Box(verts, normals, eyeTris, new Vector3(0.085f, 1.52f, 0.19f), new Vector3(0.06f, 0.06f, 0.02f));

        var m = new Mesh { name = hailing ? "Fare Body" : "Walker Body" };
        m.SetVertices(verts);
        m.SetNormals(normals);
        m.subMeshCount = 3;
        m.SetTriangles(clothesTris, 0);
        m.SetTriangles(skinTris, 1);
        m.SetTriangles(eyeTris, 2);
        m.RecalculateBounds();
        return m;
    }

    static Mesh LegMesh()
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>();
        Shapes.Box(verts, normals, tris, new Vector3(0f, -HipHeight / 2f, 0f), new Vector3(0.17f, HipHeight, 0.18f));
        var m = new Mesh { name = "Leg" };
        m.SetVertices(verts);
        m.SetNormals(normals);
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        return m;
    }

    Transform FareLight(Mesh beacon)
    {
        var root = new GameObject("Fare Light").transform;
        root.SetParent(transform, false);

        var ball = new GameObject("Ball");
        ball.transform.SetParent(root, false);
        ball.transform.localScale = Vector3.one * 0.45f;
        ball.AddComponent<MeshFilter>().sharedMesh = sphere;
        Glow(ball.AddComponent<MeshRenderer>());

        // A thin fading column so fares can be spotted from down the street.
        var column = new GameObject("Column");
        column.transform.SetParent(root, false);
        column.transform.localScale = new Vector3(0.3f, 22f, 0.3f);
        column.AddComponent<MeshFilter>().sharedMesh = beacon;
        Glow(column.AddComponent<MeshRenderer>());
        return root;
    }

    void Glow(MeshRenderer mr)
    {
        mr.sharedMaterial = fareMat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }
}
