using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// AI traffic and live traffic light lenses. Cars (sedans, SUVs and detailed vans) drive in the right-hand
/// lanes of the road grid and pick a random way at each intersection. They obey the visible traffic lights
/// (each intersection lets north-south and east-west go in turn, with yellow and all-red clearance), keep
/// their distance to the car in front (the player too) and wait for oncoming cars before turning left.
/// Each car is a physics body held on its lane, so you can shove it. Knock it far enough off its lane and it
/// tumbles free until out of sight. Respawning is biased ahead of the player so long straight drives stay
/// lively. Creates itself at startup in any scene that has a CityStreamer.
/// </summary>
public class Traffic : MonoBehaviour
{
    public int count = 48;
    [Tooltip("Cars appear on roads up to this many blocks from the player.")]
    public int spawnRadiusBlocks = 4;
    public float despawnDistance = 230f;
    [Tooltip("Cruising speed range in m/s (11-14 m/s is about 40-50 km/h).")]
    public Vector2 cruiseSpeed = new(11f, 14f);
    public float acceleration = 3f;
    [Tooltip("Comfortable braking, used to plan stops (m/s²).")]
    public float comfortBrake = 4.5f;

    [Header("Traffic lights")]
    public float greenTime = 9f;
    [Tooltip("All directions red (starting with yellow) between greens, so the intersection can clear.")]
    public float clearTime = 2.5f;
    [Tooltip("Draw glowing signal lenses on intersections within this many metres of the player.")]
    public float lightDrawRadius = 185f;

    const float MaxBrake = 9f;
    const float RightTurnSpeed = 5.5f, LeftTurnSpeed = 7.5f;
    const float LookAhead = 40f;      // how far ahead cars watch for others
    const float MinGap = 2.5f;        // bumper to bumper when queuing
    const float StopLineBack = 4.4f;  // from the intersection edge back past the crosswalk
    const float KnockDistance = 2.2f; // pushed this far off the lane = crashed
    const float KnockAngle = 40f;
    const float Mass = 1200f;

    enum LightState { Red, Yellow, Green }

    static readonly Vector3[] Dirs = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
    static readonly Vector2Int[] Steps = { new(0, 1), new(1, 0), new(0, -1), new(-1, 0) };

    static readonly Color[] Paints =
    {
        new(0.92f, 0.92f, 0.9f), new(0.62f, 0.64f, 0.67f), new(0.08f, 0.08f, 0.09f), new(0.12f, 0.2f, 0.45f),
        new(0.6f, 0.08f, 0.08f), new(0.14f, 0.32f, 0.2f), new(0.72f, 0.66f, 0.52f), new(0.45f, 0.65f, 0.85f),
    };

    static readonly Color[] Accents =
    {
        new(0.86f, 0.88f, 0.9f), new(0.16f, 0.46f, 0.72f), new(0.88f, 0.44f, 0.12f), new(0.2f, 0.22f, 0.25f),
    };

    class Car
    {
        public Rigidbody rb;
        public MeshRenderer renderer;
        public Material[] mats, brakeMats;
        public float halfLength, cruise, speed;

        // Current piece of road: a quadratic Bezier p0-p1-p2 (straight pieces have p1 halfway).
        // Driving along a block: towards intersection `node`. Turning: through `node`.
        public Vector2Int node;
        public int dir, nextDir;
        public bool turning;
        public Vector3 p0, p1, p2;
        public float s, length;

        public bool placed, crashed, braking, blocked, brakeLightsOn;
        public float crashTime, stuck;

        public Vector3 Point()
        {
            float t = Mathf.Clamp01(s / length), u = 1f - t;
            return u * u * p0 + 2f * u * t * p1 + t * t * p2;
        }

        public Vector3 Tangent()
        {
            float t = Mathf.Clamp01(s / length);
            Vector3 d = 2f * (1f - t) * (p1 - p0) + 2f * t * (p2 - p1);
            return d.sqrMagnitude > 1e-6f ? d.normalized : Dirs[dir];
        }
    }

    readonly List<Car> cars = new();
    readonly List<Object> owned = new();
    CityStreamer city;
    Transform player;
    Rigidbody playerBody;
    Camera cam;
    float pitch, roadWidth, blockSize, lane;
    Vector2Int nodeShift;   // how far node indices have moved through origin shifts, so lights keep their timing

    // Batched glowing lenses for nearby traffic lights (3 draw calls total: red, yellow, green).
    Material[] lightMats;
    Mesh[] lightMeshes;
    readonly List<Vector3>[] lightVerts = { new(), new(), new() };
    readonly List<Vector3>[] lightNormals = { new(), new(), new() };
    readonly List<Color>[] lightColors = { new(), new(), new() };
    readonly List<int>[] lightTris = { new(), new(), new() };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        var streamer = FindFirstObjectByType<CityStreamer>();
        if (streamer == null || streamer.car == null) return;
        var t = new GameObject("Traffic").AddComponent<Traffic>();
        t.city = streamer;
        t.player = streamer.car;
    }

    void Start()
    {
        var s = city.settings;
        pitch = s.Pitch;
        roadWidth = s.roadWidth;
        blockSize = s.blockSize;
        lane = roadWidth / 4f;   // lane centre, measured from the road's centre line
        playerBody = player.GetComponent<Rigidbody>();
        city.OriginShifted += OnOriginShifted;

        BuildCars();
        BuildTrafficLightBatches();
        foreach (var c in cars) Respawn(c, initial: true);
    }

    void OnDestroy()
    {
        if (city != null) city.OriginShifted -= OnOriginShifted;
        foreach (var o in owned) Destroy(o);
    }

    void OnOriginShifted(Vector3 delta)
    {
        var shift = new Vector2Int(Mathf.RoundToInt(delta.x / pitch), Mathf.RoundToInt(delta.z / pitch));
        nodeShift += shift;
        foreach (var c in cars)
        {
            c.node += shift;
            c.p0 += delta; c.p1 += delta; c.p2 += delta;
            c.rb.position += delta;
            c.rb.transform.position += delta;
        }
    }

    /// <summary>Positions of all traffic cars, for the minimap.</summary>
    public void GetCars(List<Vector3> into)
    {
        foreach (var c in cars)
            if (c.placed) into.Add(c.rb.position);
    }

    /// <summary>Number of active traffic cars within radius metres of the player (for metrics).</summary>
    public int CountWithin(float radius)
    {
        if (player == null) return 0;
        float r2 = radius * radius;
        Vector3 pp = player.position;
        int n = 0;
        foreach (var c in cars)
            if (c.placed && Shapes.Flat(c.rb.position - pp).sqrMagnitude <= r2) n++;
        return n;
    }

    /// <summary>Number of active traffic cars in a forward corridor ahead of the player (for metrics).</summary>
    public int CountAhead(float range, float halfWidth)
    {
        if (player == null) return 0;
        Vector3 pp = player.position;
        Vector3 fwd = PlayerTravelDir();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        int n = 0;
        foreach (var c in cars)
        {
            if (!c.placed) continue;
            Vector3 rel = Shapes.Flat(c.rb.position - pp);
            float along = Vector3.Dot(rel, fwd);
            if (along > 5f && along <= range && Mathf.Abs(Vector3.Dot(rel, right)) <= halfWidth) n++;
        }
        return n;
    }

    Vector3 PlayerTravelDir()
    {
        if (playerBody != null)
        {
            Vector3 v = Shapes.Flat(playerBody.linearVelocity);
            if (v.sqrMagnitude > 16f) return v.normalized;
        }
        Vector3 f = Shapes.Flat(player.forward);
        return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
    }

    // ------------------------------------------------------------------ Road grid

    Vector3 Center(Vector2Int n) => new(n.x * pitch + roadWidth * 0.5f, 0f, n.y * pitch + roadWidth * 0.5f);
    static Vector3 Right(int d) => Dirs[(d + 1) & 3];
    static bool IsLeft(int from, int to) => to == ((from + 3) & 3);
    static bool IsRight(int from, int to) => to == ((from + 1) & 3);

    void EnterSegment(Car c, Vector2Int node, int dir)
    {
        c.node = node;
        c.dir = dir;
        c.turning = false;
        Vector3 d = Dirs[dir], r = Right(dir) * lane;
        c.p0 = Center(node - Steps[dir]) + d * (roadWidth * 0.5f) + r;
        c.p2 = Center(node) - d * (roadWidth * 0.5f) + r;
        c.p1 = (c.p0 + c.p2) * 0.5f;
        c.length = blockSize;
        c.s = 0f;

        float roll = Random.value;
        c.nextDir = roll < 0.55f ? dir : roll < 0.775f ? (dir + 1) & 3 : (dir + 3) & 3;
    }

    void EnterTurn(Car c)
    {
        c.turning = true;
        int d = c.dir, nd = c.nextDir;
        Vector3 ctr = Center(c.node);
        c.p0 = ctr - Dirs[d] * (roadWidth * 0.5f) + Right(d) * lane;
        c.p2 = ctr + Dirs[nd] * (roadWidth * 0.5f) + Right(nd) * lane;
        // A turn bends around the point where the two lanes cross.
        c.p1 = nd == d ? (c.p0 + c.p2) * 0.5f : ctr + (Right(d) + Right(nd)) * lane;
        c.s = 0f;

        float len = 0f;
        Vector3 prev = c.p0;
        for (int i = 1; i <= 12; i++)
        {
            float t = i / 12f, u = 1f - t;
            Vector3 p = u * u * c.p0 + 2f * u * t * c.p1 + t * t * c.p2;
            len += Vector3.Distance(prev, p);
            prev = p;
        }
        c.length = len;
    }

    void Advance(Car c, float dist)
    {
        c.s += dist;
        while (c.s >= c.length)
        {
            float over = c.s - c.length;
            if (c.turning) EnterSegment(c, c.node + Steps[c.nextDir], c.nextDir);
            else EnterTurn(c);
            c.s = over;
        }
    }

    float TurnSpeed(Car c) =>
        IsRight(c.dir, c.nextDir) ? RightTurnSpeed : IsLeft(c.dir, c.nextDir) ? LeftTurnSpeed : c.cruise;

    LightState LightAt(Vector2Int node, int dir)
    {
        Vector2Int a = node - nodeShift;
        float cycle = 2f * (greenTime + clearTime);
        float yellowTime = Mathf.Min(1.6f, clearTime * 0.65f);
        float offset = CityGenerator.Hash(city.settings.seed + 7, a.x, a.y) % 1000 / 1000f * cycle;
        float t = Mathf.Repeat(Time.time + offset, cycle);
        if ((dir & 1) == 0)
        {
            if (t < greenTime) return LightState.Green;
            if (t < greenTime + yellowTime) return LightState.Yellow;
            return LightState.Red;
        }
        float ewStart = greenTime + clearTime;
        if (t >= ewStart && t < ewStart + greenTime) return LightState.Green;
        if (t >= ewStart + greenTime && t < ewStart + greenTime + yellowTime) return LightState.Yellow;
        return LightState.Red;
    }

    /// <summary>Whether the light at this intersection is green for traffic heading this way.</summary>
    bool Green(Vector2Int node, int dir) => LightAt(node, dir) == LightState.Green;

    /// <summary>A car coming the other way that a left turn would cut across.</summary>
    bool Oncoming(Car c)
    {
        int opp = (c.dir + 2) & 3;
        foreach (var o in cars)
        {
            if (o == c || !o.placed || o.crashed || o.node != c.node || o.dir != opp) continue;
            if (IsLeft(o.dir, o.nextDir)) continue;   // two left turns pass in front of each other
            if (o.turning || o.length - o.s < 30f) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ Driving

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        foreach (var c in cars)
        {
            if (!c.placed || c.crashed) continue;
            Plan(c, dt);
            Advance(c, c.speed * dt);
            Hold(c);
        }
    }

    /// <summary>Picks this step's speed: the slowest of what the road, the lights and the car ahead allow.</summary>
    void Plan(Car c, float dt)
    {
        float limit = c.cruise;
        float toEnd = c.length - c.s;

        if (c.turning)
        {
            limit = Mathf.Min(limit, TurnSpeed(c));
        }
        else
        {
            limit = Mathf.Min(limit, Approach(toEnd, TurnSpeed(c)));

            float toStop = toEnd - StopLineBack - c.halfLength;
            bool hold = !Green(c.node, c.dir) || (IsLeft(c.dir, c.nextDir) && Oncoming(c));
            // Too close to stop when the light changes: carry on through.
            bool canStop = toStop > -0.5f && toStop > c.speed * c.speed / (2f * MaxBrake) - 1f;
            if (hold && canStop) limit = Mathf.Min(limit, Approach(toStop, 0f));
        }

        float gap = Gap(c, out float aheadSpeed);
        c.blocked = false;
        if (gap < LookAhead)
        {
            float room = gap - MinGap;
            limit = Mathf.Min(limit, room <= 0f ? 0f : Approach(room, aheadSpeed));
            c.blocked = room < 1.5f && aheadSpeed < 0.5f;
        }

        c.braking = limit < c.speed - 0.3f || c.speed < 0.3f;
        c.speed = Mathf.MoveTowards(c.speed, limit, (limit > c.speed ? acceleration : MaxBrake) * dt);
        c.stuck = c.blocked && c.speed < 0.2f ? c.stuck + dt : 0f;
    }

    /// <summary>Fastest speed now that still lets the car slow to `target` within `dist` metres.</summary>
    float Approach(float dist, float target) =>
        dist <= 0f ? target : Mathf.Sqrt(target * target + 2f * comfortBrake * dist);

    /// <summary>Bumper-to-bumper distance to the nearest car in this car's lane ahead, and that car's speed.</summary>
    float Gap(Car c, out float aheadSpeed)
    {
        Vector3 pos = c.rb.position, fwd = c.Tangent(), right = Vector3.Cross(Vector3.up, fwd);
        float best = float.MaxValue, bestSpeed = 0f;

        foreach (var o in cars)
        {
            if (o == c || !o.placed) continue;
            Check(o.rb.position, o.rb.linearVelocity, o.halfLength);
        }
        if (playerBody) Check(playerBody.position, playerBody.linearVelocity, 2.1f);
        aheadSpeed = bestSpeed;
        return best;

        void Check(Vector3 p, Vector3 v, float half)
        {
            Vector3 rel = p - pos;
            float ahead = Vector3.Dot(rel, fwd);
            if (ahead <= 0f || ahead > LookAhead + 6f) return;
            if (Mathf.Abs(Vector3.Dot(rel, right)) > 2.2f) return;
            float g = ahead - c.halfLength - half;
            if (g < best)
            {
                best = g;
                bestSpeed = Mathf.Max(0f, Vector3.Dot(v, fwd));
            }
        }
    }

    /// <summary>Steers the body towards where it should be on the lane, or lets it go if it was knocked off.</summary>
    void Hold(Car c)
    {
        Vector3 target = c.Point(), tangent = c.Tangent();
        Vector3 err = target - c.rb.position;
        err.y = 0f;
        float yawErr = Vector3.SignedAngle(c.rb.rotation * Vector3.forward, tangent, Vector3.up);
        if (err.magnitude > KnockDistance || Mathf.Abs(yawErr) > KnockAngle)
        {
            Crash(c);
            return;
        }
        c.rb.linearVelocity = tangent * c.speed + err * 6f;
        c.rb.angularVelocity = new Vector3(0f, yawErr * Mathf.Deg2Rad * 8f, 0f);
    }

    void Crash(Car c)
    {
        c.crashed = true;
        c.crashTime = Time.time;
        c.braking = false;
        c.rb.constraints = RigidbodyConstraints.None;
        c.rb.useGravity = true;
        c.rb.linearDamping = 0.3f;
        c.rb.angularDamping = 0.5f;
    }

    // ------------------------------------------------------------------ Recycling and lights

    void Update()
    {
        if (player == null) return;
        if (cam == null) cam = Camera.main;
        Vector3 pp = player.position;
        Vector3 travel = PlayerTravelDir();
        bool movingFast = playerBody != null && Shapes.Flat(playerBody.linearVelocity).sqrMagnitude > 36f;

        foreach (var c in cars)
        {
            Vector3 rel = Shapes.Flat(c.rb.position - pp);
            float d = rel.magnitude;
            // Recycle cars that fall well behind the player while moving so traffic stays dense ahead.
            bool farBehind = movingFast && Vector3.Dot(rel, travel) < -130f && !Visible(c.rb.position);
            bool reuse = !c.placed || d > despawnDistance || farBehind
                || (c.crashed && (d > 70f || Time.time - c.crashTime > 30f) && !Visible(c.rb.position))
                || (c.stuck > 10f && !Visible(c.rb.position));
            if (reuse) Respawn(c, initial: false);

            bool brakeLights = c.braking && !c.crashed;
            if (brakeLights != c.brakeLightsOn)
            {
                c.brakeLightsOn = brakeLights;
                c.renderer.sharedMaterials = brakeLights ? c.brakeMats : c.mats;
            }
        }

        DrawTrafficLights();
    }

    bool Visible(Vector3 p)
    {
        if (cam == null) return false;
        Vector3 v = cam.WorldToViewportPoint(p + Vector3.up);
        return v.z > 0f && v.z < 350f && v.x > -0.15f && v.x < 1.15f && v.y > -0.15f && v.y < 1.15f;
    }

    void Respawn(Car c, bool initial)
    {
        Vector3 pp = player.position;
        Vector3 travel = PlayerTravelDir();
        var home = new Vector2Int(Mathf.FloorToInt(pp.x / pitch), Mathf.FloorToInt(pp.z / pitch));
        var aheadCenter = new Vector2Int(
            Mathf.FloorToInt((pp.x + travel.x * pitch * 2.1f) / pitch),
            Mathf.FloorToInt((pp.z + travel.z * pitch * 2.1f) / pitch));

        // Bias two-thirds of respawns toward a window centred ahead of the player so long straight drives have traffic ahead.
        bool biasAhead = !initial && Random.value < 0.7f;
        Vector2Int origin = biasAhead ? aheadCenter : home;
        int r = biasAhead ? Mathf.Max(2, spawnRadiusBlocks - 1) : spawnRadiusBlocks;

        for (int attempt = 0; attempt < 36; attempt++)
        {
            var node = origin + new Vector2Int(Random.Range(-r, r + 1), Random.Range(-r, r + 1));
            EnterSegment(c, node, Random.Range(0, 4));
            c.s = Random.Range(0f, c.length * 0.75f);
            Vector3 p = c.Point();

            float d = Shapes.Flat(p - pp).magnitude;
            if (d < (initial ? 25f : 65f) || d > despawnDistance - 25f) continue;
            // Allow visible forward spawns past 130 m (where fog blends them in smoothly) so straight avenues stay populated.
            if (!initial && d < 130f && Visible(p)) continue;
            if (Crowded(c, p)) continue;
            Place(c);
            return;
        }
        // Nowhere free right now. Park it out of the way; it tries again next frame.
        c.placed = false;
        c.rb.position = c.rb.transform.position = new Vector3(0f, -50f, 0f);
        c.rb.isKinematic = true;
    }

    bool Crowded(Car c, Vector3 p)
    {
        foreach (var o in cars)
            if (o != c && o.placed && Shapes.Flat(o.rb.position - p).sqrMagnitude < 14f * 14f) return true;
        return false;
    }

    void Place(Car c)
    {
        Vector3 p = c.Point(), t = c.Tangent();
        var q = Quaternion.LookRotation(t);
        c.placed = true;
        c.crashed = false;
        c.stuck = 0f;
        c.speed = c.cruise * 0.75f;

        var rb = c.rb;
        rb.isKinematic = false;
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX |
                         RigidbodyConstraints.FreezeRotationZ;
        rb.linearDamping = 0f;
        rb.angularDamping = 0.05f;
        rb.position = p;
        rb.rotation = q;
        rb.transform.SetPositionAndRotation(p, q);
        rb.linearVelocity = t * c.speed;
        rb.angularVelocity = Vector3.zero;
    }

    // ------------------------------------------------------------------ Visible traffic light lenses

    void BuildTrafficLightBatches()
    {
        var glow = Resources.Load<Material>("Glow");
        if (glow == null) return;

        Color[] colors =
        {
            new(1f, 0.16f, 0.12f, 1f),   // Red
            new(1f, 0.78f, 0.12f, 1f),   // Yellow
            new(0.18f, 1f, 0.36f, 1f),   // Green
        };
        string[] names = { "Traffic Light Red", "Traffic Light Yellow", "Traffic Light Green" };

        lightMats = new Material[3];
        lightMeshes = new Mesh[3];
        for (int i = 0; i < 3; i++)
        {
            var m = new Material(glow) { name = names[i] };
            m.SetColor("_BaseColor", colors[i]);
            lightMats[i] = m;
            owned.Add(m);

            var mesh = new Mesh { name = names[i] + " Batch" };
            mesh.MarkDynamic();
            lightMeshes[i] = mesh;
            owned.Add(mesh);
        }
    }

    void DrawTrafficLights()
    {
        if (lightMats == null || player == null) return;

        for (int i = 0; i < 3; i++)
        {
            lightVerts[i].Clear();
            lightNormals[i].Clear();
            lightColors[i].Clear();
            lightTris[i].Clear();
        }

        Vector3 pp = player.position;
        int cx = Mathf.FloorToInt(pp.x / pitch), cz = Mathf.FloorToInt(pp.z / pitch);
        int rangeBlocks = Mathf.CeilToInt(lightDrawRadius / pitch);
        float maxDist2 = lightDrawRadius * lightDrawRadius;
        float halfRoad = roadWidth * 0.5f;
        float frontOff = CityLook.TrafficLightHousingDepth * 0.5f + 0.025f;
        const float lensHalf = 0.125f;

        for (int ix = cx - rangeBlocks; ix <= cx + rangeBlocks; ix++)
        {
            for (int iz = cz - rangeBlocks; iz <= cz + rangeBlocks; iz++)
            {
                var node = new Vector2Int(ix, iz);
                Vector3 ctr = Center(node);
                if (Shapes.Flat(ctr - pp).sqrMagnitude > maxDist2) continue;

                for (int dir = 0; dir < 4; dir++)
                {
                    LightState state = LightAt(node, dir);
                    int idx = (int)state;
                    float yOff = state == LightState.Red ? CityLook.TrafficLightLensSpacing
                        : state == LightState.Green ? -CityLook.TrafficLightLensSpacing : 0f;

                    Vector3 fwd = Dirs[dir];
                    Vector3 right = Right(dir);
                    Vector3 pos = ctr + fwd * (halfRoad + CityLook.TrafficLightInset - frontOff)
                        + right * lane
                        + Vector3.up * (CityLook.TrafficLightHousingY + yOff);

                    AddLensQuad(idx, pos, right, -fwd, lensHalf);
                }
            }
        }

        for (int i = 0; i < 3; i++)
        {
            if (lightTris[i].Count == 0) continue;
            var mesh = lightMeshes[i];
            mesh.Clear();
            mesh.SetVertices(lightVerts[i]);
            mesh.SetNormals(lightNormals[i]);
            mesh.SetColors(lightColors[i]);
            mesh.SetTriangles(lightTris[i], 0);
            mesh.RecalculateBounds();
            Graphics.DrawMesh(mesh, Matrix4x4.identity, lightMats[i], 0, null, 0, null, ShadowCastingMode.Off, false);
        }
    }

    void AddLensQuad(int idx, Vector3 center, Vector3 right, Vector3 normal, float half)
    {
        var v = lightVerts[idx];
        var n = lightNormals[idx];
        var col = lightColors[idx];
        var t = lightTris[idx];
        int b = v.Count;

        Vector3 r = right * half, u = Vector3.up * half;
        v.Add(center - r - u);
        v.Add(center - r + u);
        v.Add(center + r + u);
        v.Add(center + r - u);
        for (int k = 0; k < 4; k++) { n.Add(normal); col.Add(Color.white); }
        t.Add(b); t.Add(b + 1); t.Add(b + 2);
        t.Add(b); t.Add(b + 2); t.Add(b + 3);
    }

    // ------------------------------------------------------------------ Building the cars

    void BuildCars()
    {
        // Reuse the player car's materials (by part name), so no new shaders are needed in the build.
        Material PartMaterial(string part)
        {
            foreach (var r in player.GetComponentsInChildren<Renderer>(true))
                if (r.name == part) return r.sharedMaterial;
            return null;
        }
        var glass = PartMaterial("Cabin");
        var trim = PartMaterial("FrontBumper") ?? PartMaterial("Tire");
        var head = PartMaterial("Headlight");
        var tail = PartMaterial("Taillight");
        var lit = glass ?? trim;
        if (lit == null) lit = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        glass ??= Tint(lit, new Color(0.1f, 0.12f, 0.15f), 0.9f, "Traffic Glass");
        trim ??= Tint(lit, new Color(0.06f, 0.06f, 0.06f), 0.2f, "Traffic Trim");
        head ??= Tint(lit, new Color(1f, 0.95f, 0.75f), 0.9f, "Traffic Headlight");
        tail ??= Tint(lit, new Color(0.5f, 0.04f, 0.04f), 0.9f, "Traffic Taillight");

        // Brake lights: the unlit glow material, so they look lit.
        Material brake;
        var glow = Resources.Load<Material>("Glow");
        if (glow != null)
        {
            brake = new Material(glow) { name = "Traffic Brake Light" };
            brake.SetColor("_BaseColor", new Color(1f, 0.12f, 0.06f, 1f));
            owned.Add(brake);
        }
        else brake = Tint(lit, new Color(1f, 0.15f, 0.1f), 0.9f, "Traffic Brake Light");

        var paints = new Material[Paints.Length];
        for (int i = 0; i < paints.Length; i++) paints[i] = Tint(lit, Paints[i], 0.65f, $"Traffic Paint {i}");
        var accents = new Material[Accents.Length];
        for (int i = 0; i < accents.Length; i++) accents[i] = Tint(lit, Accents[i], 0.55f, $"Traffic Accent {i}");

        var sedan = SedanMesh();
        var suv = SuvMesh();
        var van = VanMesh();
        owned.Add(sedan);
        owned.Add(suv);
        owned.Add(van);

        for (int i = 0; i < count; i++)
        {
            float roll = Random.value;
            int kind = roll < 0.55f ? 0 : roll < 0.78f ? 1 : 2;   // 55% sedan, 23% SUV, 22% detailed van
            var go = new GameObject(kind == 2 ? "Traffic Van" : kind == 1 ? "Traffic SUV" : "Traffic Car");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = kind == 2 ? van : kind == 1 ? suv : sedan;
            var mr = go.AddComponent<MeshRenderer>();
            var paint = paints[Random.Range(0, paints.Length)];
            var accent = accents[Random.Range(0, accents.Length)];
            // Submeshes: 0 paint, 1 glass, 2 trim and tyres, 3 headlights, 4 tail lights, 5 accent/livery & hubcaps.
            var mats = new[] { paint, glass, trim, head, tail, accent };
            var brakeMats = new[] { paint, glass, trim, head, brake, accent };
            mr.sharedMaterials = mats;

            var col = go.AddComponent<BoxCollider>();
            col.center = kind == 2 ? new Vector3(0f, 1.12f, 0f) : kind == 1 ? new Vector3(0f, 0.92f, 0f) : new Vector3(0f, 0.8f, 0f);
            col.size = kind == 2 ? new Vector3(2.02f, 2.16f, 4.95f) : kind == 1 ? new Vector3(1.96f, 1.72f, 4.45f) : new Vector3(1.9f, 1.5f, 4.2f);

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = kind == 2 ? Mass * 1.25f : Mass;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.centerOfMass = new Vector3(0f, 0.5f, 0f);

            cars.Add(new Car
            {
                rb = rb,
                renderer = mr,
                mats = mats,
                brakeMats = brakeMats,
                halfLength = kind == 2 ? 2.5f : kind == 1 ? 2.25f : 2.15f,
                cruise = Random.Range(cruiseSpeed.x, cruiseSpeed.y),
            });
        }
    }

    Material Tint(Material source, Color c, float smoothness, string name)
    {
        var m = Shapes.Tinted(source, c, smoothness, name);
        owned.Add(m);
        return m;
    }

    static Mesh SedanMesh()
    {
        var b = new CarMeshBuilder();
        b.Box(0, new Vector3(0f, 0.7f, 0f), new Vector3(1.9f, 0.55f, 4.2f));           // body
        b.Box(0, new Vector3(0f, 0.99f, 1.25f), new Vector3(1.3f, 0.05f, 1.25f));      // raised hood crease
        b.Box(0, new Vector3(0f, 1.51f, -0.22f), new Vector3(1.72f, 0.08f, 1.84f));    // roof
        b.Box(1, new Vector3(0f, 1.225f, -0.2f), new Vector3(1.64f, 0.5f, 2.0f));      // cabin glass
        b.Box(2, new Vector3(0f, 0.5f, 2.12f), new Vector3(1.95f, 0.24f, 0.15f));      // front bumper
        b.Box(2, new Vector3(0f, 0.5f, -2.12f), new Vector3(1.95f, 0.24f, 0.15f));     // rear bumper
        b.Box(2, new Vector3(0f, 0.78f, 2.115f), new Vector3(0.76f, 0.18f, 0.04f));    // front grille
        b.Box(5, new Vector3(0f, 0.78f, 2.135f), new Vector3(0.72f, 0.03f, 0.02f));    // grille trim bar
        b.Box(5, new Vector3(0f, 0.66f, -2.115f), new Vector3(0.42f, 0.12f, 0.03f));   // rear plate recess
        for (int sx = -1; sx <= 1; sx += 2)
        {
            // Cabin pillars and side mirrors.
            b.Box(0, new Vector3(0.83f * sx, 1.23f, -0.22f), new Vector3(0.06f, 0.49f, 0.14f));    // B-pillar
            b.Box(0, new Vector3(0.83f * sx, 1.23f, -1.04f), new Vector3(0.06f, 0.49f, 0.28f));    // C-pillar
            b.Box(2, new Vector3(0.99f * sx, 1.06f, 0.62f), new Vector3(0.16f, 0.1f, 0.12f));      // wing mirror
            // Lower side rocker trim & wheel arch brows.
            b.Box(2, new Vector3(0.955f * sx, 0.48f, 0f), new Vector3(0.03f, 0.09f, 1.85f));
            b.Box(3, new Vector3(0.64f * sx, 0.8f, 2.115f), new Vector3(0.38f, 0.15f, 0.04f));
            b.Box(4, new Vector3(0.65f * sx, 0.8f, -2.115f), new Vector3(0.36f, 0.13f, 0.04f));
            for (int sz = -1; sz <= 1; sz += 2)
            {
                b.Box(2, new Vector3(0.955f * sx, 0.72f, 1.35f * sz), new Vector3(0.05f, 0.06f, 0.82f));
                b.Wheel(2, 5, new Vector3(0.86f * sx, 0.35f, 1.35f * sz), 0.35f, 0.26f);
            }
        }
        return b.Build("Traffic Sedan");
    }

    static Mesh SuvMesh()
    {
        var b = new CarMeshBuilder();
        b.Box(0, new Vector3(0f, 0.78f, 0f), new Vector3(1.94f, 0.62f, 4.42f));         // lower body
        b.Box(0, new Vector3(0f, 1.72f, -0.35f), new Vector3(1.82f, 0.09f, 2.55f));     // long roof
        b.Box(1, new Vector3(0f, 1.38f, -0.35f), new Vector3(1.74f, 0.62f, 2.66f));     // cabin glass
        b.Box(2, new Vector3(0f, 0.52f, 2.23f), new Vector3(1.98f, 0.28f, 0.16f));      // front bumper
        b.Box(2, new Vector3(0f, 0.52f, -2.23f), new Vector3(1.98f, 0.28f, 0.16f));     // rear bumper
        b.Box(2, new Vector3(0f, 0.88f, 2.225f), new Vector3(0.9f, 0.26f, 0.05f));      // SUV front grille
        b.Box(5, new Vector3(0f, 0.84f, 2.25f), new Vector3(0.84f, 0.04f, 0.02f));
        b.Box(5, new Vector3(0f, 0.94f, 2.25f), new Vector3(0.84f, 0.04f, 0.02f));
        b.Box(5, new Vector3(0f, 0.76f, -2.225f), new Vector3(0.46f, 0.14f, 0.03f));    // tailgate plate
        for (int sx = -1; sx <= 1; sx += 2)
        {
            // Roof rails.
            b.Box(2, new Vector3(0.76f * sx, 1.81f, -0.4f), new Vector3(0.06f, 0.07f, 2.2f));
            // Window pillars and mirrors.
            b.Box(0, new Vector3(0.88f * sx, 1.38f, 0.05f), new Vector3(0.06f, 0.6f, 0.14f));
            b.Box(0, new Vector3(0.88f * sx, 1.38f, -0.82f), new Vector3(0.06f, 0.6f, 0.14f));
            b.Box(0, new Vector3(0.88f * sx, 1.38f, -1.58f), new Vector3(0.08f, 0.6f, 0.24f));
            b.Box(2, new Vector3(1.03f * sx, 1.16f, 0.85f), new Vector3(0.18f, 0.12f, 0.12f));
            // Protective lower side cladding + accent stripe.
            b.Box(2, new Vector3(0.98f * sx, 0.54f, 0f), new Vector3(0.04f, 0.16f, 1.9f));
            b.Box(5, new Vector3(0.985f * sx, 0.78f, 0f), new Vector3(0.02f, 0.06f, 1.88f));
            b.Box(3, new Vector3(0.68f * sx, 0.9f, 2.225f), new Vector3(0.36f, 0.18f, 0.04f));
            b.Box(4, new Vector3(0.72f * sx, 0.96f, -2.225f), new Vector3(0.28f, 0.26f, 0.04f));
            for (int sz = -1; sz <= 1; sz += 2)
            {
                b.Box(2, new Vector3(0.985f * sx, 0.78f, 1.44f * sz), new Vector3(0.06f, 0.08f, 0.88f));
                b.Wheel(2, 5, new Vector3(0.88f * sx, 0.39f, 1.44f * sz), 0.39f, 0.28f);
            }
        }
        return b.Build("Traffic SUV");
    }

    static Mesh VanMesh()
    {
        var b = new CarMeshBuilder();
        // Stepped van body: lower body, slightly recessed upper cargo box, and distinct front cab nose.
        b.Box(0, new Vector3(0f, 0.76f, -0.05f), new Vector3(2.0f, 0.68f, 4.82f));      // main lower body
        b.Box(0, new Vector3(0f, 0.82f, 2.22f), new Vector3(1.94f, 0.54f, 0.48f));      // stepped front nose
        b.Box(0, new Vector3(0f, 1.66f, -0.45f), new Vector3(1.96f, 1.12f, 3.92f));     // cargo box and cab
        b.Box(0, new Vector3(0f, 2.25f, -0.45f), new Vector3(1.86f, 0.08f, 3.8f));      // subtle raised roof crown

        // Windscreen with frame & roof visor overhang.
        b.Box(1, new Vector3(0f, 1.62f, 1.53f), new Vector3(1.76f, 0.68f, 0.06f));
        b.Box(2, new Vector3(0f, 1.99f, 1.58f), new Vector3(1.88f, 0.06f, 0.16f));      // sun visor over windscreen

        // Front radiator grille with horizontal slat accents.
        b.Box(2, new Vector3(0f, 0.84f, 2.47f), new Vector3(0.98f, 0.34f, 0.05f));
        b.Box(5, new Vector3(0f, 0.76f, 2.50f), new Vector3(0.9f, 0.035f, 0.02f));
        b.Box(5, new Vector3(0f, 0.84f, 2.50f), new Vector3(0.9f, 0.035f, 0.02f));
        b.Box(5, new Vector3(0f, 0.92f, 2.50f), new Vector3(0.9f, 0.035f, 0.02f));

        // Roof rack rails + crossbars + compact roof vent/AC unit for silhouette depth.
        b.Box(2, new Vector3(0f, 2.34f, -1.2f), new Vector3(0.72f, 0.14f, 0.78f));      // roof AC box
        b.Box(5, new Vector3(0f, 2.34f, -1.2f), new Vector3(0.62f, 0.06f, 0.80f));      // vent slat band
        for (int rz = 0; rz < 3; rz++)
            b.Box(2, new Vector3(0f, 2.34f, 0.75f - rz * 0.72f), new Vector3(1.66f, 0.04f, 0.06f));

        // Rear cargo double doors: vertical center seam, small rear windows, door handles, high brake light.
        b.Box(2, new Vector3(0f, 1.38f, -2.43f), new Vector3(0.04f, 1.56f, 0.04f));     // center door seam
        b.Box(4, new Vector3(0f, 2.16f, -2.43f), new Vector3(0.44f, 0.08f, 0.04f));     // top brake bar
        b.Box(5, new Vector3(0f, 0.84f, -2.47f), new Vector3(0.46f, 0.14f, 0.03f));     // rear plate

        // Front and rear heavy-duty step bumpers.
        b.Box(2, new Vector3(0f, 0.48f, 2.48f), new Vector3(2.06f, 0.26f, 0.16f));
        b.Box(2, new Vector3(0f, 0.48f, -2.49f), new Vector3(2.06f, 0.26f, 0.20f));

        for (int sx = -1; sx <= 1; sx += 2)
        {
            // Roof rack side rails.
            b.Box(2, new Vector3(0.82f * sx, 2.34f, 0.05f), new Vector3(0.05f, 0.06f, 1.75f));

            // Cab side window + mirror on stalk.
            b.Box(1, new Vector3(0.99f * sx, 1.66f, 1.02f), new Vector3(0.04f, 0.54f, 0.76f));
            b.Box(2, new Vector3(1.10f * sx, 1.52f, 1.38f), new Vector3(0.10f, 0.26f, 0.10f));   // tall van mirror
            b.Box(2, new Vector3(1.03f * sx, 1.52f, 1.38f), new Vector3(0.14f, 0.04f, 0.04f));   // mirror arm

            // Cargo side pattern design: two-tone livery stripes + vertical panel ribs + lower rub strip.
            b.Box(5, new Vector3(0.995f * sx, 1.24f, -0.68f), new Vector3(0.03f, 0.20f, 3.22f)); // main side stripe
            b.Box(5, new Vector3(0.995f * sx, 1.44f, -0.68f), new Vector3(0.03f, 0.05f, 3.22f)); // thin upper pinstripe
            b.Box(2, new Vector3(1.015f * sx, 0.62f, -0.05f), new Vector3(0.04f, 0.12f, 2.15f)); // lower rub rail
            // Recessed/raised vertical body panel ribs along the cargo box for depth.
            for (int rib = 0; rib < 4; rib++)
                b.Box(0, new Vector3(0.995f * sx, 1.76f, 0.25f - rib * 0.78f), new Vector3(0.04f, 0.54f, 0.08f));

            // Rear door window + handle + tall vertical taillights + recessed front headlights.
            b.Box(1, new Vector3(0.45f * sx, 1.76f, -2.425f), new Vector3(0.64f, 0.44f, 0.04f));
            b.Box(2, new Vector3(0.14f * sx, 1.24f, -2.44f), new Vector3(0.05f, 0.18f, 0.05f));
            b.Box(3, new Vector3(0.72f * sx, 0.88f, 2.475f), new Vector3(0.36f, 0.18f, 0.05f));
            b.Box(4, new Vector3(0.84f * sx, 1.04f, -2.465f), new Vector3(0.20f, 0.36f, 0.05f));

            // Fender arch flares + wheels with contrasting hubcaps.
            for (int sz = -1; sz <= 1; sz += 2)
            {
                b.Box(2, new Vector3(1.015f * sx, 0.78f, 1.56f * sz), new Vector3(0.06f, 0.09f, 0.92f));
                b.Wheel(2, 5, new Vector3(0.9f * sx, 0.38f, 1.56f * sz), 0.38f, 0.28f);
            }
        }
        return b.Build("Traffic Van");
    }

    /// <summary>Flat-shaded boxes and wheels in one mesh, with six submeshes.</summary>
    class CarMeshBuilder
    {
        readonly List<Vector3> verts = new(), normals = new();
        readonly List<Color> colors = new();
        readonly List<int>[] tris = { new(), new(), new(), new(), new(), new() };

        public void Box(int sub, Vector3 c, Vector3 size)
        {
            Shapes.Box(verts, normals, tris[sub], c, size);
            while (colors.Count < verts.Count) colors.Add(Color.white);
        }

        /// <summary>A wheel with its axle along X, plus contrasting hubcaps on both faces.</summary>
        public void Wheel(int tireSub, int hubSub, Vector3 c, float radius, float width, int sides = 10)
        {
            Vector3 hw = Vector3.right * (width / 2f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 r0 = new(0f, Mathf.Sin(a0) * radius, Mathf.Cos(a0) * radius);
                Vector3 r1 = new(0f, Mathf.Sin(a1) * radius, Mathf.Cos(a1) * radius);
                Quad(tireSub, c + r0 - hw, c + r0 + hw, c + r1 + hw, c + r1 - hw, (r0 + r1).normalized);
                Tri(tireSub, c + hw, c + r0 + hw, c + r1 + hw, Vector3.right);
                Tri(tireSub, c - hw, c + r0 - hw, c + r1 - hw, Vector3.left);
            }
            // Small hubcap disc/bar protruding slightly from the outer tyre face so wheels read clearly.
            float hubR = radius * 0.55f;
            Box(hubSub, c, new Vector3(width + 0.02f, hubR * 1.15f, hubR * 0.34f));
            Box(hubSub, c, new Vector3(width + 0.02f, hubR * 0.34f, hubR * 1.15f));
        }

        void Quad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            Tri(sub, a, b, c, n);
            Tri(sub, a, c, d, n);
        }

        void Tri(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 n)
        {
            int i = verts.Count;
            verts.Add(a);
            // Unity draws a face when Cross(b - a, c - a) points out of it.
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) > 0f) { verts.Add(b); verts.Add(c); }
            else { verts.Add(c); verts.Add(b); }
            for (int k = 0; k < 3; k++) { normals.Add(n); colors.Add(Color.white); }
            tris[sub].Add(i); tris[sub].Add(i + 1); tris[sub].Add(i + 2);
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetColors(colors);   // the glow brake-light shader multiplies by vertex colour
            m.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) m.SetTriangles(tris[i], i);
            m.RecalculateBounds();
            return m;
        }
    }
}
