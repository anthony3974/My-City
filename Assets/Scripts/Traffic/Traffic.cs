using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// AI traffic. Cars drive in the right-hand lanes of the road grid and pick a random way at each intersection.
/// They stop for traffic lights (not drawn, each intersection lets one direction go at a time), keep their
/// distance to the car in front (the player too) and wait for oncoming cars before turning left.
/// Each car is a physics body held on its lane, so you can shove it. Knock it far enough off its lane and it
/// tumbles free, until it is out of sight and gets reused. Cars that end up far from the player are moved
/// to a road near them again. Creates itself at startup in any scene that has a CityStreamer.
/// </summary>
public class Traffic : MonoBehaviour
{
    public int count = 28;
    [Tooltip("Cars appear on roads up to this many blocks from the player.")]
    public int spawnRadiusBlocks = 5;
    public float despawnDistance = 300f;
    [Tooltip("Cruising speed range in m/s (11-14 m/s is about 40-50 km/h).")]
    public Vector2 cruiseSpeed = new(11f, 14f);
    public float acceleration = 3f;
    [Tooltip("Comfortable braking, used to plan stops (m/s²).")]
    public float comfortBrake = 4.5f;

    [Header("Traffic lights")]
    public float greenTime = 9f;
    [Tooltip("All directions red between greens, so the intersection can clear.")]
    public float clearTime = 2.5f;

    const float MaxBrake = 9f;
    const float RightTurnSpeed = 5.5f, LeftTurnSpeed = 7.5f;
    const float LookAhead = 40f;      // how far ahead cars watch for others
    const float MinGap = 2.5f;        // bumper to bumper when queuing
    const float StopLineBack = 4.4f;  // from the intersection edge back past the crosswalk
    const float KnockDistance = 2.2f; // pushed this far off the lane = crashed
    const float KnockAngle = 40f;
    const float Mass = 1200f;

    static readonly Vector3[] Dirs = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
    static readonly Vector2Int[] Steps = { new(0, 1), new(1, 0), new(0, -1), new(-1, 0) };

    static readonly Color[] Paints =
    {
        new(0.92f, 0.92f, 0.9f), new(0.62f, 0.64f, 0.67f), new(0.08f, 0.08f, 0.09f), new(0.12f, 0.2f, 0.45f),
        new(0.6f, 0.08f, 0.08f), new(0.14f, 0.32f, 0.2f), new(0.72f, 0.66f, 0.52f), new(0.45f, 0.65f, 0.85f),
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

    /// <summary>Whether the light at this intersection is green for traffic heading this way.</summary>
    bool Green(Vector2Int node, int dir)
    {
        Vector2Int a = node - nodeShift;
        float cycle = 2f * (greenTime + clearTime);
        float offset = CityGenerator.Hash(city.settings.seed + 7, a.x, a.y) % 1000 / 1000f * cycle;
        float t = Mathf.Repeat(Time.time + offset, cycle);
        return (dir & 1) == 0 ? t < greenTime : t >= greenTime + clearTime && t < 2f * greenTime + clearTime;
    }

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

        foreach (var c in cars)
        {
            float d = Flat(c.rb.position - pp).magnitude;
            bool reuse = !c.placed || d > despawnDistance
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
    }

    bool Visible(Vector3 p)
    {
        if (cam == null) return false;
        Vector3 v = cam.WorldToViewportPoint(p + Vector3.up);
        return v.z > 0f && v.z < 350f && v.x > -0.15f && v.x < 1.15f && v.y > -0.15f && v.y < 1.15f;
    }

    static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    void Respawn(Car c, bool initial)
    {
        Vector3 pp = player.position;
        var home = new Vector2Int(Mathf.FloorToInt(pp.x / pitch), Mathf.FloorToInt(pp.z / pitch));
        int r = spawnRadiusBlocks;

        for (int attempt = 0; attempt < 30; attempt++)
        {
            var node = home + new Vector2Int(Random.Range(-r, r + 1), Random.Range(-r, r + 1));
            EnterSegment(c, node, Random.Range(0, 4));
            c.s = Random.Range(0f, c.length * 0.6f);
            Vector3 p = c.Point();

            float d = Flat(p - pp).magnitude;
            if (d < (initial ? 30f : 90f) || d > despawnDistance - 40f) continue;
            if (!initial && d < 180f && Visible(p)) continue;
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
            if (o != c && o.placed && Flat(o.rb.position - p).sqrMagnitude < 15f * 15f) return true;
        return false;
    }

    void Place(Car c)
    {
        Vector3 p = c.Point(), t = c.Tangent();
        var q = Quaternion.LookRotation(t);
        c.placed = true;
        c.crashed = false;
        c.stuck = 0f;
        c.speed = c.cruise * 0.7f;

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

        var sedan = SedanMesh();
        var van = VanMesh();
        owned.Add(sedan);
        owned.Add(van);

        for (int i = 0; i < count; i++)
        {
            bool isVan = Random.value < 0.2f;
            var go = new GameObject(isVan ? "Traffic Van" : "Traffic Car");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = isVan ? van : sedan;
            var mr = go.AddComponent<MeshRenderer>();
            var paint = paints[Random.Range(0, paints.Length)];
            // Submeshes: 0 paint, 1 glass, 2 trim and tyres, 3 headlights, 4 tail lights.
            var mats = new[] { paint, glass, trim, head, tail };
            var brakeMats = new[] { paint, glass, trim, head, brake };
            mr.sharedMaterials = mats;

            var col = go.AddComponent<BoxCollider>();
            col.center = isVan ? new Vector3(0f, 1.12f, 0f) : new Vector3(0f, 0.8f, 0f);
            col.size = isVan ? new Vector3(2f, 2.14f, 4.9f) : new Vector3(1.9f, 1.5f, 4.2f);   // bottom just off the road

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mass;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.centerOfMass = new Vector3(0f, 0.5f, 0f);

            cars.Add(new Car
            {
                rb = rb,
                renderer = mr,
                mats = mats,
                brakeMats = brakeMats,
                halfLength = isVan ? 2.45f : 2.15f,
                cruise = Random.Range(cruiseSpeed.x, cruiseSpeed.y),
            });
        }
    }

    Material Tint(Material source, Color c, float smoothness, string name)
    {
        var m = new Material(source) { name = name };
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smoothness);
        owned.Add(m);
        return m;
    }

    static Mesh SedanMesh()
    {
        var b = new CarMeshBuilder();
        b.Box(0, new Vector3(0f, 0.7f, 0f), new Vector3(1.9f, 0.55f, 4.2f));          // body
        b.Box(0, new Vector3(0f, 1.51f, -0.25f), new Vector3(1.72f, 0.08f, 1.8f));    // roof
        b.Box(1, new Vector3(0f, 1.225f, -0.2f), new Vector3(1.65f, 0.5f, 2f));       // cabin glass
        b.Box(2, new Vector3(0f, 0.5f, 2.12f), new Vector3(1.95f, 0.25f, 0.15f));     // bumpers
        b.Box(2, new Vector3(0f, 0.5f, -2.12f), new Vector3(1.95f, 0.25f, 0.15f));
        for (int sx = -1; sx <= 1; sx += 2)
        {
            b.Box(3, new Vector3(0.62f * sx, 0.8f, 2.115f), new Vector3(0.4f, 0.15f, 0.04f));
            b.Box(4, new Vector3(0.65f * sx, 0.8f, -2.115f), new Vector3(0.35f, 0.12f, 0.04f));
            for (int sz = -1; sz <= 1; sz += 2)
                b.Wheel(2, new Vector3(0.86f * sx, 0.35f, 1.35f * sz), 0.35f, 0.26f);
        }
        return b.Build("Traffic Sedan");
    }

    static Mesh VanMesh()
    {
        var b = new CarMeshBuilder();
        b.Box(0, new Vector3(0f, 0.75f, 0f), new Vector3(2f, 0.7f, 4.9f));            // lower body
        b.Box(0, new Vector3(0f, 1.65f, -0.45f), new Vector3(2f, 1.1f, 4f));          // cargo box and cab
        b.Box(1, new Vector3(0f, 1.62f, 1.56f), new Vector3(1.8f, 0.7f, 0.06f));      // windscreen
        for (int sx = -1; sx <= 1; sx += 2)
        {
            b.Box(1, new Vector3(1.005f * sx, 1.72f, 1.05f), new Vector3(0.02f, 0.5f, 0.75f));   // side windows
            b.Box(3, new Vector3(0.65f * sx, 0.88f, 2.465f), new Vector3(0.4f, 0.16f, 0.04f));
            b.Box(4, new Vector3(0.78f * sx, 0.95f, -2.465f), new Vector3(0.22f, 0.3f, 0.04f));
            for (int sz = -1; sz <= 1; sz += 2)
                b.Wheel(2, new Vector3(0.9f * sx, 0.38f, 1.6f * sz), 0.38f, 0.28f);
        }
        b.Box(2, new Vector3(0f, 0.5f, 2.47f), new Vector3(2.05f, 0.28f, 0.15f));     // bumpers
        b.Box(2, new Vector3(0f, 0.5f, -2.47f), new Vector3(2.05f, 0.28f, 0.15f));
        return b.Build("Traffic Van");
    }

    /// <summary>Flat-shaded boxes and wheels in one mesh, with five submeshes.</summary>
    class CarMeshBuilder
    {
        readonly List<Vector3> verts = new(), normals = new();
        readonly List<Color> colors = new();
        readonly List<int>[] tris = { new(), new(), new(), new(), new() };

        public void Box(int sub, Vector3 c, Vector3 size)
        {
            Vector3 h = size / 2f;
            for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 n = Vector3.zero, u = Vector3.zero, v = Vector3.zero;
                    n[axis] = sign;
                    u[(axis + 1) % 3] = h[(axis + 1) % 3];
                    v[(axis + 2) % 3] = h[(axis + 2) % 3];
                    Vector3 f = c + n * h[axis];
                    Quad(sub, f - u - v, f - u + v, f + u + v, f + u - v, n);
                }
        }

        /// <summary>A wheel with its axle along X.</summary>
        public void Wheel(int sub, Vector3 c, float radius, float width, int sides = 10)
        {
            Vector3 hw = Vector3.right * (width / 2f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 r0 = new(0f, Mathf.Sin(a0) * radius, Mathf.Cos(a0) * radius);
                Vector3 r1 = new(0f, Mathf.Sin(a1) * radius, Mathf.Cos(a1) * radius);
                Quad(sub, c + r0 - hw, c + r0 + hw, c + r1 + hw, c + r1 - hw, (r0 + r1).normalized);
                Tri(sub, c + hw, c + r0 + hw, c + r1 + hw, Vector3.right);
                Tri(sub, c - hw, c + r0 - hw, c + r1 - hw, Vector3.left);
            }
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
